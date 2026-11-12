using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Variables;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Patreon (patreon.com/portal → My Webhooks). Each delivery carries <c>X-Patreon-Event</c> (e.g.
/// <c>members:pledge:create</c>) and <c>X-Patreon-Signature</c>, the hex HMAC-MD5 of the raw body keyed with the
/// webhook's secret. The body is a JSON:API document: <c>data</c> is the member (or post), <c>included</c> holds the
/// user, tiers and campaign. Patreon sends no event id, so dedupe uses the event name + a hash of the body; a new pledge
/// also by member + pledge start, and members:create (which Patreon may send more than once per member, with a
/// different body) by member for <see cref="MemberCreateWindow"/>.
/// </summary>
public sealed class PatreonWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "patreon";

    /// <summary>A second members:create for the same member within this time is a duplicate.</summary>
    public static readonly TimeSpan MemberCreateWindow = TimeSpan.FromMinutes(10);

    public override string Service => Route;
    public override string DisplayName => "Patreon";
    public WebhookServiceSettings Settings => SettingsFile.Value.Patreon;
    public PatreonOptions Options => SettingsFile.Value.PatreonOptions;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Patreon webhook secret";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    /// <summary>The API connection, campaign, member list and totals (the app shares one; tests make their own).</summary>
    public PatreonTracker Tracker { get; internal set; } = new(settings, log);

    /// <summary>Clock for the stale check (tests replace it).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    private PatreonContext Context(bool isTest) =>
        new(Tracker.Currency, Clock.GetUtcNow(), isTest ? 0 : Options.StaleHours, Tracker.FindTier);

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.CheckHmacMd5Hex(Settings.Secret.Trim(), delivery.Body, delivery.Header("X-Patreon-Signature"));

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var topic = delivery.Header("X-Patreon-Event").Trim();
        var e = Map(topic, JsonReaders.ParseBody(delivery.BodyText), isTest: false, Context(isTest: false));
        if (e is null) return [];
        var id = WebhookFilters.Text(e.Args, "id");
        List<string> keys = [$"{topic}:{WebhookVerification.BodyHash(delivery.Body)}"];
        // A re-sent new pledge names the same pledge start; a later new pledge of the same member has a later one.
        if (topic.Equals("members:pledge:create", StringComparison.OrdinalIgnoreCase) && id.Length > 0
            && WebhookFilters.Text(e.Args, "attributes.pledge_relationship_start") is { Length: > 0 } start)
            keys.Add($"{topic}:{id}:{start}");
        var window = topic.Equals("members:create", StringComparison.OrdinalIgnoreCase) && id.Length > 0 ? [$"members:create:{id}"] : Array.Empty<string>();
        return [e with { Keys = keys, WindowKeys = window, Window = MemberCreateWindow }];
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var sample) && Map(sample.Topic, JsonReaders.ParseBody(sample.Json), isTest: true, Context(isTest: true)) is { } e ? [e] : null;

    private protected override Task EnrichAsync(WebhookEvent e, CancellationToken ct) => Tracker.EnrichAsync(e, ct);

    private protected override IReadOnlyList<BotEvent> BeforePublish(WebhookEvent e)
    {
        Tracker.Record(e.Type, e.Args, e.State as PatreonDelivery);
        return [];
    }

    /// <summary>The bot event type for a Patreon event name, or null when Cuebot ignores it.</summary>
    internal static string? EventType(string topic, JsonElement member) => topic.ToLowerInvariant() switch
    {
        // A new paying patron also gets members:pledge:create, so members:create is a "free member joined" only for free
        // members, and for gifted / free-trial memberships, which arrive as active patrons with no pledge event. Patreon
        // documents that a gift gets no pledge event; for a free trial that is still to be confirmed with a live delivery.
        "members:create" => IsPatron(member) && !IsGiftOrTrial(member) ? null : PatreonEventTypes.FollowCreated,
        "members:delete" => PatreonEventTypes.FollowDeleted,
        "members:update" => PatreonEventTypes.MemberUpdated,
        "members:pledge:create" => PatreonEventTypes.PledgeCreated,
        "members:pledge:update" => PatreonEventTypes.PledgeUpdated,
        "members:pledge:delete" => PatreonEventTypes.PledgeDeleted,
        "posts:publish" => PatreonEventTypes.PostPublished,
        "posts:update" => PatreonEventTypes.PostUpdated,
        "posts:delete" => PatreonEventTypes.PostDeleted,
        // pledges:* are API v1 triggers (kept until 2027-01-20 for creators with an extension); Cuebot reads members:pledge:*.
        _ => null,
    };

    private static bool IsPatron(JsonElement member) =>
        member.Path("attributes.patron_status") is { } s && JsonReaders.Text(s) == "active_patron";

    private static bool IsGiftOrTrial(JsonElement member) =>
        member.Child("attributes") is { } attributes && (attributes.Bool("is_gifted") || attributes.Bool("is_free_trial"));

    /// <summary>Post URLs are site-relative (<c>/posts/…</c>); make them absolute.</summary>
    internal static string AbsoluteUrl(string url) => url.StartsWith('/') ? "https://www.patreon.com" + url : url;

    /// <summary>Streamer.bot's <c>user.attributes.social_connections.*</c> names.</summary>
    private static readonly string[] SocialConnections =
        ["deviantart", "discord.url", "discord.user_id", "facebook", "google", "instagram", "reddit", "spotify", "twitch", "twitter", "vimeo", "youtube"];

    internal const string AnonymousName = "An anonymous patron";

    internal static WebhookEvent? Map(string topic, JsonElement root, bool isTest) =>
        Map(topic, root, isTest, new PatreonContext("", DateTimeOffset.UtcNow, 0, null));

    internal static WebhookEvent? Map(string topic, JsonElement root, bool isTest, PatreonContext context)
    {
        var data = root.Child("data") ?? throw new FormatException("No data in the Patreon payload");
        var type = EventType(topic, data);
        if (type is null) return null;

        var a = NewArgs();
        a["eventType"] = topic;
        a["id"] = data.Str("id");
        if (data.Child("attributes") is { } attributes) JsonReaders.Flatten(attributes, "attributes", a, maxDepth: 2);

        var included = root.Items("included").ToArray();
        JsonElement? Included(JsonElement? reference) =>
            reference is { } r ? included.FirstOrDefault(i => i.Str("type") == r.Str("type") && i.Str("id") == r.Str("id")) is { ValueKind: JsonValueKind.Object } found ? found : null : null;

        // The campaign in "included": its currency (added to the API on 2026-03-09) and patron count.
        var campaignRef = data.Path("relationships.campaign.data");
        var anyCampaign = included.FirstOrDefault(i => i.Str("type") == "campaign");
        var campaign = Included(campaignRef) ?? (anyCampaign.ValueKind == JsonValueKind.Object ? anyCampaign : null);
        var campaignCurrency = campaign.PathStr("attributes.currency").Trim().ToUpperInvariant();
        var currency = campaignCurrency.Length > 0 ? campaignCurrency : context.Currency;

        if (type is PatreonEventTypes.PostPublished or PatreonEventTypes.PostUpdated or PatreonEventTypes.PostDeleted)
        {
            var title = data.PathStr("attributes.title");
            Common(a, "", 0, "", title, isTest);
            a["title"] = title;
            a["url"] = AbsoluteUrl(data.PathStr("attributes.url"));
            a["isPaid"] = data.Child("attributes") is { } pa && pa.Bool("is_paid");
            a["isPublic"] = data.Child("attributes") is { } pb && pb.Bool("is_public");
            a["tierIds"] = new List<string>();
            a["tierNames"] = "";
            return new WebhookEvent(type, [], a)
            {
                State = new PatreonDelivery(campaignRef.Str("id"), campaignCurrency, (int)campaign.PathNumber("attributes.patron_count"), [], null),
            };
        }

        var userRef = data.Path("relationships.user.data");
        var user = Included(userRef);
        a["user.id"] = userRef.Str("id");
        a["user.type"] = userRef.Str("type") is { Length: > 0 } ut ? ut : "user";
        if (user?.Child("attributes") is { } userAttributes) JsonReaders.Flatten(userAttributes, "user.Attributes", a, maxDepth: 2);
        StreamerBotArgs.ToTime(a, "user.attributes.created");
        // Streamer.bot lists every social connection, null when not linked; Patreon may leave them out.
        foreach (var network in SocialConnections)
        {
            var connection = "user.attributes.social_connections." + network;
            if (!a.Keys.Any(k => k.StartsWith(connection + ".", StringComparison.OrdinalIgnoreCase))) StreamerBotArgs.Placeholder(a, connection, null);
        }

        var tiers = data.Path("relationships.currently_entitled_tiers.data") is { ValueKind: JsonValueKind.Array } refs
            ? refs.EnumerateArray().ToArray()
            : [];
        // Tiers referenced but missing from "included" keep their ids and take a title only from the tiers Cuebot knows.
        var tierTitles = new List<(string Id, string Title, double Cents)>();
        var deliveredTiers = new List<PatreonTier>();
        for (var i = 0; i < tiers.Length; i++)
        {
            var tierId = tiers[i].Str("id");
            a[$"entitledTiers[{i}].id"] = tierId;
            a[$"entitledTiers[{i}].type"] = tiers[i].Str("type");
            if (Included(tiers[i])?.Child("attributes") is { } tierAttributes)
            {
                JsonReaders.Flatten(tierAttributes, $"entitledTiers[{i}].Attributes", a, maxDepth: 1);
                foreach (var time in (string[])["created_at", "edited_at", "published_at"]) StreamerBotArgs.ToTime(a, $"entitledTiers[{i}].attributes.{time}");
                if (tierAttributes.Str("title") is { Length: > 0 } t)
                {
                    tierTitles.Add((tierId, t, tierAttributes.Number("amount_cents")));
                    deliveredTiers.Add(new PatreonTier { Id = tierId, Title = t, AmountCents = (int)tierAttributes.Number("amount_cents") });
                }
            }
            else if (context.Tier?.Invoke(tierId) is { Title.Length: > 0 } known)
            {
                tierTitles.Add((tierId, known.Title, known.AmountCents));
            }
            // Streamer.bot's Pledge Updated: the tier the patron is now entitled to (its title, else its id).
            if (type == PatreonEventTypes.PledgeUpdated)
                a[$"entitledTiers[{i}].updatedToTier"] = Included(tiers[i])?.Child("attributes").Str("title") is { Length: > 0 } title
                    ? title
                    : tiers[i].Str("id");
        }
        // Streamer.bot's Pledge Updated also names the new tier at the top level (null when there is none).
        if (type == PatreonEventTypes.PledgeUpdated) a["updatedToTier"] = a.GetValueOrDefault("entitledTiers[0].updatedToTier");
        // Members of a paid tier are usually entitled to the campaign's Free tier too; name only the paid one(s).
        if (tierTitles.Any(t => t.Cents > 0)) tierTitles.RemoveAll(t => t.Cents <= 0);

        // A patron who hides their identity (2026-05-26) arrives with a null name, picture and link on the user.
        var userName = user.PathStr("attributes.full_name");
        var memberName = data.PathStr("attributes.full_name");
        var isAnonymous = user?.Child("attributes") is { } ua && ua.TryGetProperty("full_name", out var fullName) && fullName.ValueKind == JsonValueKind.Null;
        var name = isAnonymous ? AnonymousName : memberName.Length > 0 ? memberName : userName;

        // Amounts are in the campaign's currency. currently_entitled covers the current period; will_pay is the next charge.
        // For a yearly pledge (pledge_cadence 12) that is the yearly charge: %amount% is the monthly value.
        var cents = data.PathNumber("attributes.currently_entitled_amount_cents") is > 0 and var c ? c
            : data.PathNumber("attributes.will_pay_amount_cents");
        var cadence = Math.Max(1, (int)data.PathNumber("attributes.pledge_cadence"));
        var charge = cents / 100.0;
        var monthly = Math.Round(charge / cadence, 2);
        // attributes.note is the creator's private note about the member, never a message from them: not published.
        Common(a, name.Length > 0 ? name : "A patron", monthly, currency, "", isTest);
        a["chargeAmount"] = (decimal)charge;
        a["formattedCharge"] = Money.Format(charge, currency);
        a["pledgeCadence"] = cadence;
        a["monthlyAmount"] = (decimal)monthly;
        a["lifetimeSupport"] = (decimal)(data.PathNumber("attributes.campaign_lifetime_support_cents") / 100.0);
        a["note"] = data.PathStr("attributes.note");
        a["tier"] = string.Join(", ", tierTitles.Select(t => t.Title));
        a["tiers"] = tierTitles.Select(t => t.Title).ToList();
        a["tierIds"] = tiers.Select(t => t.Str("id")).Where(id => id.Length > 0).ToList();
        a["patronStatus"] = data.PathStr("attributes.patron_status");
        a["isFreeTrial"] = data.Child("attributes") is { } m1 && m1.Bool("is_free_trial");
        a["isGifted"] = data.Child("attributes") is { } m2 && m2.Bool("is_gifted");
        a["isAnonymous"] = isAnonymous;
        a["privateName"] = isAnonymous ? memberName : "";
        a["avatarUrl"] = isAnonymous ? "" : user.PathStr("attributes.image_url");
        a["profileUrl"] = isAnonymous ? "" : user.PathStr("attributes.url");

        if (type == PatreonEventTypes.PledgeUpdated)
        {
            // Filled in after the duplicate check from the pledge history or the member list (see PatreonTracker.EnrichAsync).
            a["direction"] = isTest ? "upgrade" : "";
            a["previousAmount"] = isTest ? 3m : null;
            a["previousTier"] = "";
        }

        // A paused webhook, once resumed, sends its backlog at once: a new pledge that started and was last charged
        // long ago is marked stale, so alerts can skip it (it still updates the member list and totals).
        var started = PatreonApi.PatreonTime(data.PathStr("attributes.pledge_relationship_start"));
        var charged = PatreonApi.PatreonTime(data.PathStr("attributes.last_charge_date"));
        var newest = started > charged || charged is null ? started : charged;
        var age = newest is { } n ? context.Now - n : (TimeSpan?)null;
        a["ageHours"] = age is { } ag ? Math.Round(Math.Max(0, ag.TotalHours), 1) : null;
        a["isStale"] = type == PatreonEventTypes.PledgeCreated && context.StaleHours > 0 && age is { } old && old > TimeSpan.FromHours(context.StaleHours);

        var social = user.Path("attributes.social_connections");
        var member = new PatreonPatron
        {
            MemberId = data.Str("id"), UserId = userRef.Str("id"), Name = isAnonymous ? "" : name, Status = data.PathStr("attributes.patron_status"),
            AmountCents = (int)cents, Cadence = cadence, IsGifted = (bool)a["isGifted"]!, IsFreeTrial = (bool)a["isFreeTrial"]!,
            TierIds = [.. (List<string>)a["tierIds"]!], Tier = (string)a["tier"]!, Since = started,
            TwitchUserId = isAnonymous ? "" : social.PathStr("twitch.user_id"), TwitchLogin = isAnonymous ? "" : PatreonTracker.TwitchLogin(social.PathStr("twitch.url")),
            YouTubeUserId = isAnonymous ? "" : social.PathStr("youtube.user_id"), DiscordUserId = isAnonymous ? "" : social.PathStr("discord.user_id"),
        };
        return new WebhookEvent(type, [], a)
        {
            State = new PatreonDelivery(campaignRef.Str("id"), campaignCurrency, (int)campaign.PathNumber("attributes.patron_count"), deliveredTiers, member),
        };
    }

    internal sealed record PatreonSample(string Topic, string Json);

    private const string MemberJson = """
        {"data":{"attributes":{"campaign_lifetime_support_cents":__CENTS__,"currently_entitled_amount_cents":__CENTS__,"email":"patron@example.com",
          "full_name":"Jane Patron","is_free_trial":false,"is_gifted":false,"last_charge_date":"2026-10-27T12:00:00.000+00:00","last_charge_status":"Paid",
          "next_charge_date":"2026-11-27T00:00:00.000+00:00","note":"","patron_status":"__STATUS__",
          "pledge_cadence":1,"pledge_relationship_start":"2026-10-27T12:00:00.000+00:00","will_pay_amount_cents":__CENTS__},
          "id":"03ca69c3-ebea-4b9a-8fac-e4a837873254",
          "relationships":{"campaign":{"data":{"id":"123456","type":"campaign"}},
            "currently_entitled_tiers":{"data":[__TIERS__]},
            "user":{"data":{"id":"98765432","type":"user"}}},"type":"member"},
         "included":[{"attributes":{"created_at":"2020-01-01T00:00:00.000+00:00","creation_name":"streams","currency":"USD","is_monthly":true,"patron_count":42,"vanity":"streams"},"id":"123456","type":"campaign"},
          {"attributes":{"amount_cents":500,"created_at":"2020-01-01T00:00:00.000+00:00","description":"Thanks for the support!","discord_role_ids":null,
            "edited_at":"2020-01-01T00:00:00.000+00:00","image_url":null,"patron_count":10,"post_count":3,"published":true,
            "published_at":"2020-01-01T00:00:00.000+00:00","remaining":null,"requires_shipping":false,"title":"Supporter",
            "unpublished_at":null,"url":"/join/streams/checkout?rid=7654321","user_limit":null},"id":"7654321","type":"tier"},
          {"attributes":{"about":null,"created":"2019-05-01T00:00:00.000+00:00","first_name":"Jane","full_name":"Jane Patron","hide_pledges":false,
            "image_url":"https://c8.patreon.com/2/200/98765432","is_creator":false,"last_name":"Patron","like_count":0,
            "social_connections":{"discord":null,"twitch":null},"thumb_url":"https://c8.patreon.com/2/200/98765432","url":"https://www.patreon.com/user?u=98765432"},
           "id":"98765432","type":"user"}],
         "links":{"self":"https://www.patreon.com/api/oauth2/v2/members/03ca69c3-ebea-4b9a-8fac-e4a837873254"}}
        """;

    private static string Member(int cents, string status, bool tier) => MemberJson
        .Replace("__CENTS__", cents.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__STATUS__", status, StringComparison.Ordinal)
        .Replace("__TIERS__", tier ? """{"id":"7654321","type":"tier"}""" : "", StringComparison.Ordinal);

    internal static readonly IReadOnlyDictionary<string, PatreonSample> Samples = new Dictionary<string, PatreonSample>(StringComparer.OrdinalIgnoreCase)
    {
        ["pledgeCreated"] = new("members:pledge:create", Member(500, "active_patron", tier: true)),
        ["pledgeUpdated"] = new("members:pledge:update", Member(500, "active_patron", tier: true)),
        ["pledgeDeleted"] = new("members:pledge:delete", Member(0, "former_patron", tier: false)),
        ["followCreated"] = new("members:create", Member(0, "", tier: false).Replace("\"patron_status\":\"\"", "\"patron_status\":null", StringComparison.Ordinal)),
        ["followDeleted"] = new("members:delete", Member(0, "", tier: false).Replace("\"patron_status\":\"\"", "\"patron_status\":null", StringComparison.Ordinal)),
        ["postPublished"] = new("posts:publish", """
            {"data":{"attributes":{"title":"New video is up!","url":"/posts/new-video-is-up-123","published_at":"2026-10-27T12:00:00.000+00:00","is_paid":false,"is_public":true},
             "id":"123","type":"post"}}
            """),
        ["postUpdated"] = new("posts:update", """
            {"data":{"attributes":{"title":"New video is up! (now with chapters)","url":"/posts/new-video-is-up-123","published_at":"2026-10-27T12:00:00.000+00:00","is_paid":false,"is_public":true},
             "id":"123","type":"post"}}
            """),
        ["postDeleted"] = new("posts:delete", """
            {"data":{"attributes":{"title":"New video is up!","url":"/posts/new-video-is-up-123"},"id":"123","type":"post"}}
            """),
    };
}

/// <summary>What the mapping needs beyond the delivery: fallbacks from the tracker and the stale setting.</summary>
/// <param name="Currency">The campaign's currency when the delivery does not name it ("" if unknown).</param>
/// <param name="StaleHours">0 = never mark a delivery stale.</param>
/// <param name="Tier">A tier Cuebot knows by id (for tiers missing from "included"), or null.</param>
internal sealed record PatreonContext(string Currency, DateTimeOffset Now, int StaleHours, Func<string, PatreonTier?>? Tier);
