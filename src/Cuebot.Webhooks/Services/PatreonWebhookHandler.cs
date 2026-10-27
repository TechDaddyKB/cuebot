using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Patreon (patreon.com/portal → My Webhooks). Each delivery carries <c>X-Patreon-Event</c> (e.g.
/// <c>members:pledge:create</c>) and <c>X-Patreon-Signature</c>, the hex HMAC-MD5 of the raw body keyed with the
/// webhook's secret. The body is a JSON:API document: <c>data</c> is the member (or post), <c>included</c> holds the
/// user, tiers and campaign. Patreon sends no event id, so dedupe uses the event name + a hash of the body.
/// </summary>
public sealed class PatreonWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "patreon";

    public override string Service => Route;
    public override string DisplayName => "Patreon";
    public WebhookServiceSettings Settings => SettingsFile.Value.Patreon;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Patreon webhook secret";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.CheckHmacMd5Hex(Settings.Secret.Trim(), delivery.Body, delivery.Header("X-Patreon-Signature"));

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var topic = delivery.Header("X-Patreon-Event").Trim();
        var e = Map(topic, JsonReaders.ParseBody(delivery.BodyText), isTest: false);
        return e is null ? [] : [e with { Keys = [$"{topic}:{WebhookVerification.BodyHash(delivery.Body)}"] }];
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var sample) && Map(sample.Topic, JsonReaders.ParseBody(sample.Json), isTest: true) is { } e ? [e] : null;

    /// <summary>The bot event type for a Patreon event name, or null when Cuebot ignores it.</summary>
    internal static string? EventType(string topic, JsonElement member) => topic.ToLowerInvariant() switch
    {
        // A new paying patron also gets members:pledge:create, so members:create is a follow only for free members, and
        // for gifted / free-trial memberships, which arrive as active patrons with no pledge event.
        "members:create" => IsPatron(member) && !IsGiftOrTrial(member) ? null : PatreonEventTypes.FollowCreated,
        "members:delete" => PatreonEventTypes.FollowDeleted,
        "members:update" => PatreonEventTypes.MemberUpdated,
        "members:pledge:create" => PatreonEventTypes.PledgeCreated,
        "members:pledge:update" => PatreonEventTypes.PledgeUpdated,
        "members:pledge:delete" => PatreonEventTypes.PledgeDeleted,
        "posts:publish" => PatreonEventTypes.PostPublished,
        _ => null,
    };

    private static bool IsPatron(JsonElement member) =>
        member.Path("attributes.patron_status") is { } s && JsonReaders.Text(s) == "active_patron";

    private static bool IsGiftOrTrial(JsonElement member) =>
        member.Child("attributes") is { } attributes && (attributes.Bool("is_gifted") || attributes.Bool("is_free_trial"));

    /// <summary>Post URLs are site-relative (<c>/posts/…</c>); make them absolute.</summary>
    internal static string AbsoluteUrl(string url) => url.StartsWith('/') ? "https://www.patreon.com" + url : url;

    internal static WebhookEvent? Map(string topic, JsonElement root, bool isTest)
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

        if (type == PatreonEventTypes.PostPublished)
        {
            var title = data.PathStr("attributes.title");
            Common(a, "", 0, "", title, isTest);
            a["title"] = title;
            a["url"] = AbsoluteUrl(data.PathStr("attributes.url"));
            return new WebhookEvent(type, [], a);
        }

        var userRef = data.Path("relationships.user.data");
        var user = Included(userRef);
        a["user.id"] = userRef.Str("id");
        a["user.type"] = userRef.Str("type") is { Length: > 0 } ut ? ut : "user";
        if (user?.Child("attributes") is { } userAttributes) JsonReaders.Flatten(userAttributes, "user.Attributes", a, maxDepth: 2);

        var tiers = data.Path("relationships.currently_entitled_tiers.data") is { ValueKind: JsonValueKind.Array } refs
            ? refs.EnumerateArray().ToArray()
            : [];
        // Tiers referenced but missing from "included" keep their ids and add no title.
        var tierTitles = new List<(string Title, double Cents)>();
        for (var i = 0; i < tiers.Length; i++)
        {
            a[$"entitledTiers[{i}].id"] = tiers[i].Str("id");
            a[$"entitledTiers[{i}].type"] = tiers[i].Str("type");
            if (Included(tiers[i])?.Child("attributes") is { } tierAttributes)
            {
                JsonReaders.Flatten(tierAttributes, $"entitledTiers[{i}].Attributes", a, maxDepth: 1);
                if (tierAttributes.Str("title") is { Length: > 0 } t) tierTitles.Add((t, tierAttributes.Number("amount_cents")));
            }
        }
        // Members of a paid tier are usually entitled to the campaign's Free tier too; name only the paid one(s).
        if (tierTitles.Any(t => t.Cents > 0)) tierTitles.RemoveAll(t => t.Cents <= 0);

        var name = data.PathStr("attributes.full_name") is { Length: > 0 } n ? n : user.Child("attributes").Str("full_name");
        var cents = data.PathNumber("attributes.currently_entitled_amount_cents") is > 0 and var c ? c
            : data.PathNumber("attributes.will_pay_amount_cents");
        // Patreon reports amounts in the campaign's currency but does not name it in the member resource.
        // attributes.note is the creator's private note about the member, never a message from them: not published.
        Common(a, name.Length > 0 ? name : "A patron", cents / 100.0, "", "", isTest);
        a["note"] = data.PathStr("attributes.note");
        a["tier"] = string.Join(", ", tierTitles.Select(t => t.Title));
        a["patronStatus"] = data.PathStr("attributes.patron_status");
        a["isFreeTrial"] = data.Child("attributes") is { } m1 && m1.Bool("is_free_trial");
        a["isGifted"] = data.Child("attributes") is { } m2 && m2.Bool("is_gifted");
        return new WebhookEvent(type, [], a);
    }

    internal sealed record PatreonSample(string Topic, string Json);

    private const string MemberJson = """
        {"data":{"attributes":{"campaign_lifetime_support_cents":0,"currently_entitled_amount_cents":__CENTS__,"email":"patron@example.com",
          "full_name":"Jane Patron","is_follower":false,"last_charge_date":"2026-10-27T12:00:00.000+00:00","last_charge_status":"Paid",
          "lifetime_support_cents":__CENTS__,"next_charge_date":"2026-11-27T00:00:00.000+00:00","note":"","patron_status":"__STATUS__",
          "pledge_cadence":1,"pledge_relationship_start":"2026-10-27T12:00:00.000+00:00","will_pay_amount_cents":__CENTS__},
          "id":"03ca69c3-ebea-4b9a-8fac-e4a837873254",
          "relationships":{"campaign":{"data":{"id":"123456","type":"campaign"}},
            "currently_entitled_tiers":{"data":[__TIERS__]},
            "user":{"data":{"id":"98765432","type":"user"}}},"type":"member"},
         "included":[{"attributes":{"created_at":"2020-01-01T00:00:00.000+00:00","creation_name":"streams","is_monthly":true,"patron_count":42},"id":"123456","type":"campaign"},
          {"attributes":{"amount_cents":500,"created_at":"2020-01-01T00:00:00.000+00:00","description":"Thanks for the support!","discord_role_ids":null,
            "edited_at":"2020-01-01T00:00:00.000+00:00","image_url":null,"patron_count":10,"post_count":3,"published":true,
            "published_at":"2020-01-01T00:00:00.000+00:00","remaining":null,"requires_shipping":false,"title":"Supporter",
            "unpublished_at":null,"url":"/join/streams/checkout?rid=7654321","user_limit":null},"id":"7654321","type":"tier"},
          {"attributes":{"about":null,"created":"2019-05-01T00:00:00.000+00:00","first_name":"Jane","full_name":"Jane Patron","hide_pledges":false,
            "image_url":"https://c8.patreon.com/2/200/98765432","is_creator":false,"last_name":"Patron","like_count":0,
            "social_connections":{"discord":null,"twitch":null},"thumb_url":"https://c8.patreon.com/2/200/98765432","url":"https://www.patreon.com/user?u=98765432","vanity":null},
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
    };
}
