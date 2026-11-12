using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Webhooks.Services;

/// <summary>The API refused the token (HTTP 401/403) even after a refresh: the user has to reconnect.</summary>
public sealed class PatreonAuthException(string message) : Exception(message);

/// <summary>A campaign as the API describes it.</summary>
public sealed record PatreonCampaignInfo(string Id, string Name, string Currency, int PatronCount, string Vanity, string Url);

/// <summary>A tier as the API describes it.</summary>
public sealed record PatreonTierInfo(string Id, string Title, int AmountCents, bool Published);

/// <summary>One member from the member list.</summary>
public sealed record PatreonMemberInfo(
    string MemberId, string UserId, string Name, string Status, int AmountCents, int Cadence, bool IsGifted, bool IsFreeTrial,
    double LifetimeCents, DateTimeOffset? Since, IReadOnlyList<string> TierIds, string TwitchUserId, string TwitchUrl,
    string YouTubeUserId, string DiscordUserId, string ImageUrl);

/// <summary>One pledge history event (<c>pledge_upgrade</c>, <c>pledge_downgrade</c>, <c>pledge_start</c>...).</summary>
public sealed record PatreonPledgeEvent(string Type, int AmountCents, string Currency, string TierTitle, DateTimeOffset? Date);

/// <summary>A post's details (is it for paying patrons, which tiers).</summary>
public sealed record PatreonPostInfo(string Id, string Title, string Url, bool IsPaid, bool IsPublic, IReadOnlyList<string> TierIds);

/// <summary>A webhook made by this client, with its health.</summary>
public sealed record PatreonWebhookInfo(string Id, string Uri, bool Paused, int ConsecutiveFailures, DateTimeOffset? LastAttemptedAt,
    IReadOnlyList<string> Triggers, string Secret);

/// <summary>New tokens from a refresh.</summary>
public sealed record PatreonTokens(string AccessToken, string RefreshToken, DateTimeOffset? ExpiresAt);

/// <summary>
/// The few Patreon API v2 calls Cuebot makes with the creator's own access token (docs.patreon.com). Every call sends
/// a User-Agent, as Patreon asks. A 401 is retried once after refreshing the token (when the client id, secret and
/// refresh token are known); after that it throws <see cref="PatreonAuthException"/> instead of retrying, because
/// Patreon blocks clients that keep sending failing requests. Tokens are never logged.
/// </summary>
public sealed class PatreonApi(HttpClient http, Func<PatreonOptions> options, Action<PatreonTokens> tokensRefreshed, string? baseUrl = null)
{
    public const string DefaultBaseUrl = "https://www.patreon.com";

    /// <summary>The webhook triggers Cuebot asks for when it creates the webhook.</summary>
    public static readonly string[] WebhookTriggers =
    [
        "members:create", "members:update", "members:delete", "members:pledge:create", "members:pledge:update", "members:pledge:delete",
        "posts:publish", "posts:update", "posts:delete",
    ];

    private readonly string _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

    internal string UserAgent { get; init; } = "Cuebot (+https://github.com/TechDaddyKB/cuebot)";

    // ---- campaign ----

    /// <summary>The token owner's campaign id (GET /identity?include=campaign), or "" when they have none.</summary>
    public async Task<string> CampaignIdAsync(CancellationToken ct)
    {
        var doc = await GetAsync("/api/oauth2/v2/identity", [("include", "campaign"), ("fields[user]", "full_name")], ct).ConfigureAwait(false);
        return doc.Path("data.relationships.campaign.data.id") is { } id ? JsonReaders.Text(id) : "";
    }

    /// <summary>The campaign and its tiers.</summary>
    public async Task<(PatreonCampaignInfo Campaign, IReadOnlyList<PatreonTierInfo> Tiers)> CampaignAsync(string campaignId, CancellationToken ct)
    {
        var doc = await GetAsync($"/api/oauth2/v2/campaigns/{Uri.EscapeDataString(campaignId)}",
        [
            ("include", "tiers"),
            ("fields[campaign]", "creation_name,currency,patron_count,vanity,url"),
            ("fields[tier]", "title,amount_cents,published"),
        ], ct).ConfigureAwait(false);
        var data = doc.Child("data") ?? throw new FormatException("No campaign in Patreon's answer");
        var a = data.Child("attributes");
        var campaign = new PatreonCampaignInfo(data.Str("id"), a.Str("creation_name"), a.Str("currency").ToUpperInvariant(),
            (int)a.Number("patron_count"), a.Str("vanity"), a.Str("url"));
        var tiers = doc.Items("included").Where(i => i.Str("type") == "tier")
            .Select(t => new PatreonTierInfo(t.Str("id"), t.PathStr("attributes.title"), (int)t.PathNumber("attributes.amount_cents"),
                t.Path("attributes.published") is not { ValueKind: JsonValueKind.False }))
            .OrderBy(t => t.AmountCents).ToArray();
        return (campaign, tiers);
    }

    // ---- members ----

    /// <summary>Every member of the campaign, following the cursor (up to 1000 a page).</summary>
    public async Task<IReadOnlyList<PatreonMemberInfo>> MembersAsync(string campaignId, CancellationToken ct, int pageSize = 1000)
    {
        var members = new List<PatreonMemberInfo>();
        string? cursor = null;
        for (var page = 0; page < 200; page++)
        {
            List<(string, string)> query =
            [
                ("include", "currently_entitled_tiers,user"),
                ("fields[member]", "full_name,patron_status,currently_entitled_amount_cents,will_pay_amount_cents,pledge_cadence,is_gifted,is_free_trial,campaign_lifetime_support_cents,pledge_relationship_start"),
                ("fields[user]", "social_connections,full_name,image_url"),
                ("page[count]", pageSize.ToString(CultureInfo.InvariantCulture)),
            ];
            if (cursor is not null) query.Add(("page[cursor]", cursor));
            var doc = await GetAsync($"/api/oauth2/v2/campaigns/{Uri.EscapeDataString(campaignId)}/members", query, ct).ConfigureAwait(false);
            var users = doc.Items("included").Where(i => i.Str("type") == "user").ToDictionary(u => u.Str("id"), StringComparer.Ordinal);
            foreach (var m in doc.Items("data")) members.Add(Member(m, users));
            cursor = doc.Path("meta.pagination.cursors.next") is { ValueKind: JsonValueKind.String } next ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return members;
    }

    private static PatreonMemberInfo Member(JsonElement m, IReadOnlyDictionary<string, JsonElement> users)
    {
        var a = m.Child("attributes");
        var userId = m.PathStr("relationships.user.data.id");
        JsonElement? user = users.TryGetValue(userId, out var u) ? u : null;
        var social = user.Path("attributes.social_connections");
        var cents = a.Number("currently_entitled_amount_cents") is > 0 and var c ? c : a.Number("will_pay_amount_cents");
        var tierIds = m.Path("relationships.currently_entitled_tiers.data") is { ValueKind: JsonValueKind.Array } refs
            ? refs.EnumerateArray().Select(t => t.Str("id")).Where(id => id.Length > 0).ToArray()
            : [];
        var name = a.Str("full_name") is { Length: > 0 } n ? n : user.PathStr("attributes.full_name");
        return new PatreonMemberInfo(m.Str("id"), userId, name, a.Str("patron_status"), (int)cents, Math.Max(1, (int)a.Number("pledge_cadence")),
            a is { } x && x.Bool("is_gifted"), a is { } y && y.Bool("is_free_trial"), a.Number("campaign_lifetime_support_cents"),
            PatreonTime(a.Str("pledge_relationship_start")), tierIds,
            social.PathStr("twitch.user_id"), social.PathStr("twitch.url"), social.PathStr("youtube.user_id"), social.PathStr("discord.user_id"),
            user.PathStr("attributes.image_url"));
    }

    /// <summary>The member's pledge history, newest first.</summary>
    public async Task<IReadOnlyList<PatreonPledgeEvent>> PledgeHistoryAsync(string memberId, CancellationToken ct)
    {
        var doc = await GetAsync($"/api/oauth2/v2/members/{Uri.EscapeDataString(memberId)}",
            [("include", "pledge_history"), ("fields[pledge-event]", "type,amount_cents,currency_code,tier_title,date")], ct).ConfigureAwait(false);
        return doc.Items("included").Where(i => i.Str("type") == "pledge-event")
            .Select(p => new PatreonPledgeEvent(p.PathStr("attributes.type"), (int)p.PathNumber("attributes.amount_cents"),
                p.PathStr("attributes.currency_code").ToUpperInvariant(), p.PathStr("attributes.tier_title"), PatreonTime(p.PathStr("attributes.date"))))
            .OrderByDescending(p => p.Date ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    // ---- posts ----

    public async Task<PatreonPostInfo> PostAsync(string postId, CancellationToken ct)
    {
        var doc = await GetAsync($"/api/oauth2/v2/posts/{Uri.EscapeDataString(postId)}",
            [("fields[post]", "title,url,is_paid,is_public,tiers,published_at")], ct).ConfigureAwait(false);
        var data = doc.Child("data") ?? throw new FormatException("No post in Patreon's answer");
        var a = data.Child("attributes");
        // "tiers" is a list of tier ids in the post's attributes.
        var tiers = a.Child("tiers") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(JsonReaders.Text).Where(t => t.Length > 0).ToArray()
            : [];
        return new PatreonPostInfo(data.Str("id"), a.Str("title"), a.Str("url"), a is { } p && p.Bool("is_paid"), a is { } q && q.Bool("is_public"), tiers);
    }

    // ---- webhooks ----

    /// <summary>The webhooks made by this client (webhooks made by hand in the portal are not listed by Patreon).</summary>
    public async Task<IReadOnlyList<PatreonWebhookInfo>> WebhooksAsync(CancellationToken ct)
    {
        var doc = await GetAsync("/api/oauth2/v2/webhooks",
            [("fields[webhook]", "last_attempted_at,num_consecutive_times_failed,paused,secret,triggers,uri")], ct).ConfigureAwait(false);
        return doc.Items("data").Select(Webhook).ToArray();
    }

    /// <summary>Creates the webhook for <paramref name="uri"/> with <see cref="WebhookTriggers"/>; the answer carries its secret.</summary>
    public async Task<PatreonWebhookInfo> CreateWebhookAsync(string campaignId, string uri, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "webhook",
                ["attributes"] = new JsonObject { ["triggers"] = new JsonArray([.. WebhookTriggers.Select(t => (JsonNode)t)]), ["uri"] = uri },
                ["relationships"] = new JsonObject
                {
                    ["campaign"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "campaign", ["id"] = campaignId } },
                },
            },
        };
        var doc = await SendAsync(HttpMethod.Post, "/api/oauth2/v2/webhooks", [], body, ct).ConfigureAwait(false);
        return Webhook(doc.Child("data") ?? throw new FormatException("No webhook in Patreon's answer"));
    }

    /// <summary>Un-pauses a webhook; Patreon then sends the deliveries it held back.</summary>
    public async Task<PatreonWebhookInfo> ResumeWebhookAsync(string webhookId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["data"] = new JsonObject { ["type"] = "webhook", ["id"] = webhookId, ["attributes"] = new JsonObject { ["paused"] = false } },
        };
        var doc = await SendAsync(HttpMethod.Patch, $"/api/oauth2/v2/webhooks/{Uri.EscapeDataString(webhookId)}", [], body, ct).ConfigureAwait(false);
        return Webhook(doc.Child("data") ?? throw new FormatException("No webhook in Patreon's answer"));
    }

    private static PatreonWebhookInfo Webhook(JsonElement w)
    {
        var a = w.Child("attributes");
        var triggers = a.Child("triggers") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(JsonReaders.Text).ToArray() : [];
        return new PatreonWebhookInfo(w.Str("id"), a.Str("uri"), a is { } p && p.Bool("paused"), (int)a.Number("num_consecutive_times_failed"),
            PatreonTime(a.Str("last_attempted_at")), triggers, a.Str("secret"));
    }

    // ---- tokens ----

    /// <summary>True when a refresh is possible (client id, client secret and refresh token are all known).</summary>
    public bool CanRefresh
    {
        get
        {
            var o = options();
            return o.ClientId.Trim().Length > 0 && o.ClientSecret.Trim().Length > 0 && o.RefreshToken.Trim().Length > 0;
        }
    }

    /// <summary>POST /api/oauth2/token with grant_type=refresh_token; saves the new tokens through the callback.</summary>
    public async Task<PatreonTokens> RefreshAsync(CancellationToken ct)
    {
        var o = options();
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/api/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = o.RefreshToken.Trim(),
                ["client_id"] = o.ClientId.Trim(),
                ["client_secret"] = o.ClientSecret.Trim(),
            }),
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            throw new PatreonAuthException("Patreon refused the refresh token. Paste new tokens from your Patreon client and connect again.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Patreon answered {(int)response.StatusCode} to the token refresh");
        var doc = JsonReaders.ParseBody(text);
        var access = doc.Str("access_token");
        if (access.Length == 0) throw new FormatException("No access token in Patreon's answer");
        var expires = doc.Number("expires_in") is > 0 and var s ? DateTimeOffset.UtcNow.AddSeconds(s) : (DateTimeOffset?)null;
        var tokens = new PatreonTokens(access, doc.Str("refresh_token") is { Length: > 0 } r ? r : o.RefreshToken, expires);
        tokensRefreshed(tokens);
        return tokens;
    }

    // ---- plumbing ----

    private Task<JsonElement> GetAsync(string path, IReadOnlyList<(string Key, string Value)> query, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, query, null, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyList<(string Key, string Value)> query, JsonNode? body,
        CancellationToken ct)
    {
        var token = options().AccessToken.Trim();
        if (token.Length == 0) throw new PatreonAuthException("No Patreon access token. Paste your creator's access token and connect.");
        var url = _base + path + (query.Count == 0 ? "" : "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/vnd.api+json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && CanRefresh)
            {
                token = (await RefreshAsync(ct).ConfigureAwait(false)).AccessToken;
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PatreonAuthException(response.StatusCode == HttpStatusCode.Forbidden
                    ? "Patreon refused the request (403): the token lacks the permission for it."
                    : "Patreon refused the access token (401). Paste new tokens from your Patreon client and connect again.");
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Patreon answered {(int)response.StatusCode} {response.ReasonPhrase}");
            return JsonReaders.ParseBody(text.Length == 0 ? "{}" : text);
        }
    }

    internal static DateTimeOffset? PatreonTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
