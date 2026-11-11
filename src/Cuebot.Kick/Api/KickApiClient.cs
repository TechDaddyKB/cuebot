using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cuebot.Kick.Auth;

namespace Cuebot.Kick.Api;

/// <summary>
/// The official Kick public API (api.kick.com/public/v1, docs.kick.com). Every call uses a user access token and
/// retries once after refreshing when Kick answers 401.
/// </summary>
internal sealed class KickApiClient(HttpClient http, KickEndpoints endpoints, IKickTokenSource tokens)
{
    public const int MaxChatLength = 500;

    // ---- users & channels ----

    /// <summary>The user the token belongs to (GET /users without ids).</summary>
    public async Task<KickUser> GetCurrentUserAsync(KickAccount account, CancellationToken ct)
    {
        using var doc = await SendAsync(account, HttpMethod.Get, "public/v1/users", null, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseUser).FirstOrDefault()
               ?? throw new KickApiException("Kick returned no user for this sign-in");
    }

    public async Task<IReadOnlyList<KickUser>> GetUsersAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var query = string.Join('&', ids.Select(id => "id=" + id.ToString(CultureInfo.InvariantCulture)));
        if (query.Length == 0) return [];
        using var doc = await ReadPublicAsync("public/v1/users?" + query, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseUser).ToArray();
    }

    /// <summary>The broadcaster's own channel (no parameters), or the channel with <paramref name="slug"/>.</summary>
    public async Task<KickChannel?> GetChannelAsync(string? slug, CancellationToken ct)
    {
        using var doc = string.IsNullOrWhiteSpace(slug)
            ? await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, "public/v1/channels", null, ct).ConfigureAwait(false)
            : await ReadPublicAsync("public/v1/channels?slug=" + Uri.EscapeDataString(slug.Trim()), ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseChannel).FirstOrDefault();
    }

    /// <summary>
    /// The broadcaster's ingest address and stream key from GET /channels (filled only with <c>streamkey:read</c>), or
    /// null when Kick did not include them.
    /// </summary>
    public async Task<KickStreamCredentials?> GetStreamCredentialsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, "public/v1/channels", null, ct).ConfigureAwait(false);
        var stream = doc.RootElement.Items("data").FirstOrDefault().Prop("stream");
        var (url, key) = (stream.Str("url"), stream.Str("key"));
        return url.Length > 0 && key.Length > 0 ? new KickStreamCredentials(url, key) : null;
    }

    public async Task<KickChannel?> GetChannelByIdAsync(long broadcasterUserId, CancellationToken ct)
    {
        using var doc = await ReadPublicAsync("public/v1/channels?broadcaster_user_id=" + broadcasterUserId.ToString(CultureInfo.InvariantCulture), ct)
            .ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseChannel).FirstOrDefault();
    }

    /// <summary>PATCH /channels: sets the stream title and/or category of the broadcaster's channel.</summary>
    public async Task UpdateChannelAsync(string? title, long? categoryId, CancellationToken ct)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(title)) body["stream_title"] = title.Trim();
        if (categoryId is > 0) body["category_id"] = categoryId.Value;
        if (body.Count == 0) throw new ArgumentException("Set a title or a category");
        using var _ = await SendAsync(KickAccount.Broadcaster, HttpMethod.Patch, "public/v1/channels", body, ct).ConfigureAwait(false);
    }

    /// <summary>PATCH /channels with <c>custom_tags</c>: replaces the channel's tags (an empty list clears them).</summary>
    public async Task SetTagsAsync(IReadOnlyList<string> tags, CancellationToken ct)
    {
        var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var body = new Dictionary<string, object> { ["custom_tags"] = clean };
        using var _ = await SendAsync(KickAccount.Broadcaster, HttpMethod.Patch, "public/v1/channels", body, ct).ConfigureAwait(false);
    }

    /// <summary>How long the category list from /public/v2/categories is reused before it is read again.</summary>
    internal static readonly TimeSpan CategoryListLifetime = TimeSpan.FromHours(12);
    private const int CategoryPageSize = 1000;
    private const int MaxCategoryPages = 50;
    private const int MaxSearchResults = 25;
    private readonly SemaphoreSlim _categoryGate = new(1, 1);
    private IReadOnlyList<KickCategory>? _categories;
    private DateTimeOffset _categoriesAt;

    /// <summary>
    /// Searches categories by name. Kick deprecated the keyword search (GET /public/v1/categories?q=) in January 2026, and
    /// its replacement (/public/v2/categories?name=) only matches whole names, so Cuebot reads the full list from v2 (a
    /// few pages, kept for <see cref="CategoryListLifetime"/>) and searches it here: exact names first, then names that
    /// start with the text, then names that contain it. The v1 search is the fallback while it still answers.
    /// </summary>
    public async Task<IReadOnlyList<KickCategory>> SearchCategoriesAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        IReadOnlyList<KickCategory> all;
        try
        {
            all = await AllCategoriesAsync(ct).ConfigureAwait(false);
        }
        catch (KickApiException)
        {
            using var doc = await ReadPublicAsync("public/v1/categories?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
            return doc.RootElement.Items("data").Select(ParseCategory).ToArray();
        }
        return RankCategories(all, query);
    }

    internal static IReadOnlyList<KickCategory> RankCategories(IEnumerable<KickCategory> all, string query) => all
        .Select(c => (Category: c, Rank: string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase) ? 0
            : c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
            : c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3))
        .Where(x => x.Rank < 3)
        .OrderBy(x => x.Rank).ThenBy(x => x.Category.Name.Length).ThenBy(x => x.Category.Name, StringComparer.OrdinalIgnoreCase)
        .Take(MaxSearchResults)
        .Select(x => x.Category)
        .ToArray();

    /// <summary>Every category, from /public/v2/categories (cursor pages of 1000), cached.</summary>
    internal async Task<IReadOnlyList<KickCategory>> AllCategoriesAsync(CancellationToken ct)
    {
        if (_categories is { } cached && DateTimeOffset.UtcNow - _categoriesAt < CategoryListLifetime) return cached;
        await _categoryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_categories is { } again && DateTimeOffset.UtcNow - _categoriesAt < CategoryListLifetime) return again;
            var list = new List<KickCategory>();
            var cursor = "";
            for (var page = 0; page < MaxCategoryPages; page++)
            {
                var path = "public/v2/categories?limit=" + CategoryPageSize.ToString(CultureInfo.InvariantCulture)
                           + (cursor.Length > 0 ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
                using var doc = await ReadPublicAsync(path, ct).ConfigureAwait(false);
                list.AddRange(doc.RootElement.Items("data").Select(ParseCategory).Where(c => c.Id > 0));
                cursor = doc.RootElement.Prop("pagination").Str("next_cursor");
                if (cursor.Length == 0) break;
            }
            if (list.Count == 0) throw new KickApiException("Kick returned no categories");
            _categories = list;
            _categoriesAt = DateTimeOffset.UtcNow;
            return list;
        }
        finally
        {
            _categoryGate.Release();
        }
    }

    public async Task<KickCategory?> GetCategoryAsync(long id, CancellationToken ct)
    {
        using var doc = await ReadPublicAsync("public/v1/categories/" + id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return doc.RootElement.Prop("data") is { } data ? ParseCategory(data) : null;
    }

    // ---- live streams ----

    /// <summary>GET /public/v1/users/livestreams: the live streams of these users (users who are offline are left out).</summary>
    public async Task<IReadOnlyList<KickLivestream>> GetLivestreamsForUsersAsync(IEnumerable<long> userIds, CancellationToken ct)
    {
        var query = string.Join('&', userIds.Where(id => id > 0).Distinct().Select(id => "user_id=" + id.ToString(CultureInfo.InvariantCulture)));
        if (query.Length == 0) return [];
        using var doc = await ReadPublicAsync("public/v1/users/livestreams?" + query, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseLivestream).ToArray();
    }

    /// <summary>GET /public/v2/livestreams: live streams, optionally in one category or language (one page, oldest first).</summary>
    public async Task<(IReadOnlyList<KickLivestream> Items, string NextCursor)> GetLivestreamsAsync(long? categoryId, string? languageCode, int limit,
        string? cursor, CancellationToken ct)
    {
        var query = new List<string> { "limit=" + Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture) };
        if (categoryId is > 0) query.Add("category_id=" + categoryId.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(languageCode)) query.Add("language_code=" + Uri.EscapeDataString(languageCode.Trim().ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add("cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await ReadPublicAsync("public/v2/livestreams?" + string.Join('&', query), ct).ConfigureAwait(false);
        return (doc.RootElement.Items("data").Select(ParseLivestream).ToArray(), doc.RootElement.Prop("pagination").Str("next_cursor"));
    }

    internal static KickLivestream ParseLivestream(JsonElement l)
    {
        var user = l.Prop("broadcaster_user");
        var start = l.Str("started_at");
        return new KickLivestream(
            user.Long("id"),
            user.Str("username"),
            l.Prop("channel").Str("slug"),
            user.Str("profile_picture"),
            l.Str("title"),
            l.Prop("category") is { } cat && cat.Long("id") > 0 ? ParseCategory(cat) : null,
            (int)l.Long("viewer_count"),
            DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && t.Year > 2000 ? t : null,
            l.Str("language_code"),
            l.Bool("has_mature_content"),
            l.Items("tags").Select(x => x.AsString()).ToArray(),
            l.Str("thumbnail"));
    }

    // ---- KICKs ----

    public const int MaxLeaderboardEntries = 100;

    /// <summary>GET /public/v1/kicks/leaderboard (<c>kicks:read</c>): the top gifters of the broadcaster's channel.</summary>
    public async Task<KickLeaderboard> GetKicksLeaderboardAsync(int top, CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get,
            "public/v1/kicks/leaderboard?top=" + Math.Clamp(top, 1, MaxLeaderboardEntries).ToString(CultureInfo.InvariantCulture), null, ct).ConfigureAwait(false);
        var data = doc.RootElement.Prop("data");
        KickLeaderboardEntry[] Entries(string name) => data.Items(name)
            .Select(e => new KickLeaderboardEntry((int)e.Long("rank"), e.Long("user_id"), e.Str("username"), e.Long("gifted_amount")))
            .OrderBy(e => e.Rank)
            .ToArray();
        return new KickLeaderboard(Entries("week"), Entries("month"), Entries("lifetime"));
    }

    // ---- ads ----

    public const int MinAdBreakSeconds = 7;
    public const int MaxAdBreakSeconds = 90;

    /// <summary>
    /// POST /public/v1/ads/ad-break (<c>ads:write</c>): starts an ad break of 7 to 90 seconds. <paramref name="requestId"/>
    /// is the UUID Kick uses to recognise a retried request.
    /// </summary>
    public async Task<KickAdBreak> CreateAdBreakAsync(int seconds, Guid requestId, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["break_duration_seconds"] = Math.Clamp(seconds, MinAdBreakSeconds, MaxAdBreakSeconds),
            ["id"] = requestId.ToString("D"),
        };
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Post, "public/v1/ads/ad-break", body, ct).ConfigureAwait(false);
        var data = doc.RootElement.Prop("data");
        return new KickAdBreak(data.Str("id") is { Length: > 0 } id ? id : requestId.ToString("D"), data.Str("ad_break_id"),
            (int)data.Long("remaining_ad_breaks"));
    }

    /// <summary>GET /public/v1/ads/ad-break-status (<c>ads:read</c> or <c>ads:write</c>).</summary>
    public async Task<KickAdStatus> GetAdStatusAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, "public/v1/ads/ad-break-status", null, ct).ConfigureAwait(false);
        var d = doc.RootElement.Prop("data");
        var limits = d.Prop("limits");
        return new KickAdStatus(d.Bool("opted_in"), d.Bool("ads_blocked"), d.Bool("auto_ads_active"), (int)d.Long("remaining_ad_breaks"),
            (int)limits.Long("max"), (int)limits.Long("period_seconds"),
            d.Items("ad_breaks").Select(b => new KickAdBreakItem(b.Str("id"), b.Str("ad_break_id"), (int)b.Long("break_duration_seconds"),
                b.Str("status"), b.Str("source"),
                DateTimeOffset.TryParse(b.Str("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null)).ToArray());
    }

    /// <summary>POST /public/v1/ads/enroll (<c>ads:write</c>): enrols the broadcaster's channel in Kick's ads.</summary>
    public async Task EnrollInAdsAsync(CancellationToken ct)
    {
        using var _ = await SendAsync(KickAccount.Broadcaster, HttpMethod.Post, "public/v1/ads/enroll", new Dictionary<string, object>(), ct)
            .ConfigureAwait(false);
    }

    // ---- chat ----

    /// <summary>
    /// POST /chat. As <c>user</c> the message goes to <paramref name="broadcasterUserId"/>'s chat from the token's
    /// account; as <c>bot</c> Kick posts as the app's bot into the token's own channel.
    /// </summary>
    public async Task<KickChatSendResult> SendChatAsync(KickAccount account, string content, long broadcasterUserId,
        bool asBotType, string? replyToMessageId, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["content"] = content, ["type"] = asBotType ? "bot" : "user" };
        if (!asBotType) body["broadcaster_user_id"] = broadcasterUserId;
        if (!string.IsNullOrWhiteSpace(replyToMessageId)) body["reply_to_message_id"] = replyToMessageId.Trim();
        using var doc = await SendAsync(account, HttpMethod.Post, "public/v1/chat", body, ct).ConfigureAwait(false);
        var data = doc.RootElement.Prop("data");
        return new KickChatSendResult(data.Str("message_id"), data?.Prop("is_sent") is null || data.Bool("is_sent"));
    }

    /// <summary>DELETE /chat/{message_id}.</summary>
    public async Task DeleteChatMessageAsync(KickAccount account, string messageId, CancellationToken ct)
    {
        using var _ = await SendAsync(account, HttpMethod.Delete, "public/v1/chat/" + Uri.EscapeDataString(messageId.Trim()), null, ct)
            .ConfigureAwait(false);
    }

    // ---- moderation ----

    /// <summary>POST /moderation/bans: permanent ban without <paramref name="durationMinutes"/>, timeout with it (1-10080).</summary>
    public async Task BanAsync(KickAccount account, long broadcasterUserId, long userId, int? durationMinutes, string? reason,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["broadcaster_user_id"] = broadcasterUserId, ["user_id"] = userId };
        if (durationMinutes is { } minutes) body["duration"] = Math.Clamp(minutes, 1, 10080);
        if (!string.IsNullOrWhiteSpace(reason)) body["reason"] = reason.Trim().Length > 100 ? reason.Trim()[..100] : reason.Trim();
        using var _ = await SendAsync(account, HttpMethod.Post, "public/v1/moderation/bans", body, ct).ConfigureAwait(false);
    }

    /// <summary>DELETE /moderation/bans: lifts a ban or a timeout.</summary>
    public async Task UnbanAsync(KickAccount account, long broadcasterUserId, long userId, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["broadcaster_user_id"] = broadcasterUserId, ["user_id"] = userId };
        using var _ = await SendAsync(account, HttpMethod.Delete, "public/v1/moderation/bans", body, ct).ConfigureAwait(false);
    }

    // ---- channel rewards ----

    public const int MaxRewardTitleLength = 50;
    public const int MaxRewardDescriptionLength = 200;
    public const int MaxRedemptionIdsPerCall = 25;

    /// <summary>GET /channels/rewards: every reward of the broadcaster's channel (up to 50, enabled and disabled).</summary>
    public async Task<IReadOnlyList<KickReward>> GetRewardsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, "public/v1/channels/rewards", null, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseReward).ToArray();
    }

    /// <summary>POST /channels/rewards (title and cost are required). Needs <c>channel:rewards:write</c>.</summary>
    public async Task<KickReward> CreateRewardAsync(KickRewardUpdate reward, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reward.Title)) throw new ArgumentException("A reward needs a title");
        if (reward.Cost is not > 0) throw new ArgumentException("A reward needs a cost of at least 1");
        var body = RewardBody(reward with { IsPaused = null });
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Post, "public/v1/channels/rewards", body, ct).ConfigureAwait(false);
        return doc.RootElement.Prop("data") is { } data ? ParseReward(data) : throw new KickApiException("Kick returned no reward");
    }

    /// <summary>PATCH /channels/rewards/{id}. Kick only lets the app that created a reward change it.</summary>
    public async Task<KickReward?> UpdateRewardAsync(string rewardId, KickRewardUpdate update, CancellationToken ct)
    {
        var body = RewardBody(update);
        if (body.Count == 0) throw new ArgumentException("Nothing to change on the reward");
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Patch, "public/v1/channels/rewards/" + Uri.EscapeDataString(rewardId.Trim()),
            body, ct).ConfigureAwait(false);
        return doc.RootElement.Prop("data") is { } data ? ParseReward(data) : null;
    }

    /// <summary>DELETE /channels/rewards/{id}. Kick only lets the app that created a reward delete it.</summary>
    public async Task DeleteRewardAsync(string rewardId, CancellationToken ct)
    {
        using var _ = await SendAsync(KickAccount.Broadcaster, HttpMethod.Delete, "public/v1/channels/rewards/" + Uri.EscapeDataString(rewardId.Trim()),
            null, ct).ConfigureAwait(false);
    }

    /// <summary>GET /channels/rewards/redemptions (one page per call; Kick's default status is pending).</summary>
    public async Task<(IReadOnlyList<KickRedemption> Items, string NextCursor)> GetRedemptionsAsync(string? rewardId, string? status, string? cursor,
        CancellationToken ct)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(rewardId)) query.Add("reward_id=" + Uri.EscapeDataString(rewardId.Trim()));
        if (!string.IsNullOrWhiteSpace(status)) query.Add("status=" + Uri.EscapeDataString(status.Trim().ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add("cursor=" + Uri.EscapeDataString(cursor));
        var path = "public/v1/channels/rewards/redemptions" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var items = new List<KickRedemption>();
        foreach (var group in doc.RootElement.Items("data"))
        {
            var reward = group.Prop("reward");
            foreach (var r in group.Items("redemptions"))
            {
                var at = r.Str("redeemed_at");
                items.Add(new KickRedemption(r.Str("id"), reward.Str("id"), reward.Str("title"), r.Prop("redeemer").Long("user_id"), r.Str("status"),
                    r.Str("user_input"),
                    DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null));
            }
        }
        return (items, doc.RootElement.Prop("pagination").Str("next_cursor"));
    }

    /// <summary>
    /// POST /channels/rewards/redemptions/accept or /reject, 25 ids per call. Returns the redemptions Kick could not
    /// update (for example already handled, or a reward another app owns).
    /// </summary>
    public async Task<IReadOnlyList<KickRedemptionFailure>> UpdateRedemptionsAsync(IEnumerable<string> redemptionIds, bool accept, CancellationToken ct)
    {
        var ids = redemptionIds.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) throw new ArgumentException("No redemption id given");
        var failures = new List<KickRedemptionFailure>();
        foreach (var chunk in ids.Chunk(MaxRedemptionIdsPerCall))
        {
            using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Post,
                "public/v1/channels/rewards/redemptions/" + (accept ? "accept" : "reject"), new Dictionary<string, object> { ["ids"] = chunk }, ct)
                .ConfigureAwait(false);
            failures.AddRange(doc.RootElement.Items("data").Select(f => new KickRedemptionFailure(f.Str("id"), f.Str("reason"))));
        }
        return failures;
    }

    private static Dictionary<string, object> RewardBody(KickRewardUpdate r)
    {
        var body = new Dictionary<string, object>();
        if (r.Title is { } title)
        {
            title = title.Trim();
            if (title.Length == 0) throw new ArgumentException("The reward title is empty");
            body["title"] = title.Length > MaxRewardTitleLength ? title[..MaxRewardTitleLength] : title;
        }
        if (r.Description is { } description)
            body["description"] = description.Trim().Length > MaxRewardDescriptionLength ? description.Trim()[..MaxRewardDescriptionLength] : description.Trim();
        if (r.Cost is { } cost) body["cost"] = cost >= 1 ? cost : throw new ArgumentException("The reward cost must be at least 1");
        if (r.BackgroundColor is { } color) body["background_color"] = NormalizeColor(color);
        if (r.IsEnabled is { } enabled) body["is_enabled"] = enabled;
        if (r.IsPaused is { } paused) body["is_paused"] = paused;
        if (r.IsUserInputRequired is { } input) body["is_user_input_required"] = input;
        if (r.SkipsRequestQueue is { } skip) body["should_redemptions_skip_request_queue"] = skip;
        return body;
    }

    /// <summary>"#RRGGBB" (accepts "RRGGBB", "#RGB" and any case).</summary>
    internal static string NormalizeColor(string color)
    {
        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) throw new ArgumentException($"'{color}' is not a colour like #00E701");
        return "#" + hex.ToUpperInvariant();
    }

    internal static KickReward ParseReward(JsonElement r) => new(
        r.Str("id"), r.Str("title"), r.Str("description"), (int)r.Long("cost"), r.Str("background_color"), r.Bool("is_enabled"),
        r.Bool("is_paused"), r.Bool("is_user_input_required"), r.Bool("should_redemptions_skip_request_queue"));

    // ---- webhooks (used with the relay) ----

    /// <summary>GET /public-key: the PEM key Kick signs webhooks with. Needs no token.</summary>
    public async Task<string> GetWebhookPublicKeyAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(endpoints.Api, "public/v1/public-key"), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new KickApiException($"Could not fetch Kick's webhook key ({(int)response.StatusCode})", (int)response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Prop("data").Str("public_key") is { Length: > 0 } key ? key : throw new KickApiException("Kick returned no webhook key");
    }

    public async Task<IReadOnlyList<KickEventSubscription>> ListEventSubscriptionsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, "public/v1/events/subscriptions", null, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data")
            .Select(s => new KickEventSubscription(s.Str("id"), s.Str("event"), (int)s.Long("version"), s.Str("method")))
            .ToArray();
    }

    /// <summary>POST /events/subscriptions with method "webhook" (Kick delivers to the URL set in the app).</summary>
    public async Task<IReadOnlyList<string>> SubscribeWebhookEventsAsync(IEnumerable<(string Name, int Version)> events, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["method"] = "webhook",
            ["events"] = events.Select(e => new Dictionary<string, object> { ["name"] = e.Name, ["version"] = e.Version }).ToArray(),
        };
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Post, "public/v1/events/subscriptions", body, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data")
            .Where(r => r.Str("error").Length > 0)
            .Select(r => $"{r.Str("name")}: {r.Str("error")}")
            .ToArray();
    }

    /// <summary>DELETE /events/subscriptions?id=...: removes webhook subscriptions (for example on sign-out).</summary>
    public async Task DeleteEventSubscriptionsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        foreach (var chunk in ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).Chunk(50))
        {
            var query = string.Join('&', chunk.Select(id => "id=" + Uri.EscapeDataString(id)));
            using var _ = await SendAsync(KickAccount.Broadcaster, HttpMethod.Delete, "public/v1/events/subscriptions?" + query, null, ct)
                .ConfigureAwait(false);
        }
    }

    // ---- plumbing ----

    /// <summary>Sends a request; returns the parsed body (null for 204). Throws <see cref="KickApiException"/> on failure.</summary>
    private async Task<JsonDocument> SendAsync(KickAccount account, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(account, ct).ConfigureAwait(false);
        var (status, text) = await SendOnceAsync(method, path, body, token, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized && await tokens.TryRefreshAsync(account, token, ct).ConfigureAwait(false))
        {
            token = await tokens.GetAccessTokenAsync(account, ct).ConfigureAwait(false);
            (status, text) = await SendOnceAsync(method, path, body, token, ct).ConfigureAwait(false);
        }
        if ((int)status is < 200 or > 299) throw new KickApiException(Describe(status, text, account), (int)status);
        if (status == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}");
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    /// <summary>
    /// A GET of public data: with the broadcaster's token when signed in, otherwise with an app access token (client
    /// credentials), renewed once when Kick rejects it.
    /// </summary>
    private async Task<JsonDocument> ReadPublicAsync(string path, CancellationToken ct)
    {
        if (tokens.IsSignedIn(KickAccount.Broadcaster)) return await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var token = await tokens.GetAppTokenAsync(false, ct).ConfigureAwait(false);
        var (status, text) = await SendOnceAsync(HttpMethod.Get, path, null, token, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized)
        {
            token = await tokens.GetAppTokenAsync(true, ct).ConfigureAwait(false);
            (status, text) = await SendOnceAsync(HttpMethod.Get, path, null, token, ct).ConfigureAwait(false);
        }
        if ((int)status is < 200 or > 299) throw new KickApiException(Describe(status, text, KickAccount.Broadcaster), (int)status);
        try
        {
            return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private async Task<(HttpStatusCode, string)> SendOnceAsync(HttpMethod method, string path, object? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoints.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static string Describe(HttpStatusCode status, string body, KickAccount account)
    {
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.Str("message");
        }
        catch (JsonException)
        {
        }
        var reason = message.Length > 0 ? message : status.ToString();
        return status switch
        {
            HttpStatusCode.Unauthorized => $"Kick sign-in ({account}) is no longer valid. Please sign in again.",
            HttpStatusCode.Forbidden => $"Kick refused the request ({reason}). The {account} account may lack the permission or scope; sign in again to grant it.",
            HttpStatusCode.TooManyRequests => "Kick rate limit reached; try again shortly.",
            _ => $"Kick API error {(int)status}: {reason}",
        };
    }

    internal static KickUser ParseUser(JsonElement u) => new(u.Long("user_id"), u.Str("name"), u.Str("profile_picture"));

    internal static KickCategory ParseCategory(JsonElement c) => new(c.Long("id"), c.Str("name"), c.Str("thumbnail"));

    internal static KickChannel ParseChannel(JsonElement c)
    {
        var stream = c.Prop("stream");
        var start = stream.Str("start_time");
        return new KickChannel(
            c.Long("broadcaster_user_id"),
            c.Str("slug"),
            c.Str("stream_title"),
            c.Prop("category") is { } cat && cat.Long("id") > 0 ? ParseCategory(cat) : null,
            c.Str("channel_description"),
            stream.Bool("is_live"),
            (int)stream.Long("viewer_count"),
            DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && t.Year > 2000 ? t : null,
            stream.Str("language"),
            stream.Bool("is_mature"),
            stream.Items("custom_tags").Select(x => x.AsString()).ToArray(),
            (int)c.Long("active_subscribers_count"))
        {
            ActiveGiftedSubscribers = (int)c.Long("active_gifted_subscribers_count"),
            CanceledSubscribers = (int)c.Long("canceled_subscribers_count"),
            BannerPicture = c.Str("banner_picture"),
        };
    }
}
