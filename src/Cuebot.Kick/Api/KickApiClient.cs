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
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, "public/v1/users?" + query, null, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseUser).ToArray();
    }

    /// <summary>The broadcaster's own channel (no parameters), or the channel with <paramref name="slug"/>.</summary>
    public async Task<KickChannel?> GetChannelAsync(string? slug, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(slug) ? "public/v1/channels" : "public/v1/channels?slug=" + Uri.EscapeDataString(slug.Trim());
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseChannel).FirstOrDefault();
    }

    public async Task<KickChannel?> GetChannelByIdAsync(long broadcasterUserId, CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get,
            "public/v1/channels?broadcaster_user_id=" + broadcasterUserId.ToString(CultureInfo.InvariantCulture), null, ct).ConfigureAwait(false);
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

    /// <summary>Searches categories by name (GET /public/v1/categories?q=, the documented keyword search).</summary>
    public async Task<IReadOnlyList<KickCategory>> SearchCategoriesAsync(string query, CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get,
            "public/v1/categories?q=" + Uri.EscapeDataString(query.Trim()), null, ct).ConfigureAwait(false);
        return doc.RootElement.Items("data").Select(ParseCategory).ToArray();
    }

    public async Task<KickCategory?> GetCategoryAsync(long id, CancellationToken ct)
    {
        using var doc = await SendAsync(KickAccount.Broadcaster, HttpMethod.Get,
            "public/v1/categories/" + id.ToString(CultureInfo.InvariantCulture), null, ct).ConfigureAwait(false);
        return doc.RootElement.Prop("data") is { } data ? ParseCategory(data) : null;
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
            (int)c.Long("active_subscribers_count"));
    }
}
