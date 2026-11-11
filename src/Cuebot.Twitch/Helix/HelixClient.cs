using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;

namespace Cuebot.Twitch.Helix;

/// <summary>
/// Thin Helix transport: authenticated JSON requests with readable errors. A 401 triggers one token refresh and a
/// single retry; an exhausted rate-limit bucket (429 with Ratelimit-Remaining 0) waits for Ratelimit-Reset and a 503
/// retries once, as Twitch's API guide asks.
/// </summary>
internal sealed class HelixClient(HttpClient http, Uri helixBase, ITwitchTokenSource tokens, TimeProvider? time = null)
{
    /// <summary>The longest Cuebot waits for the rate-limit bucket to refill before reporting the 429.</summary>
    internal static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan UnavailableRetryDelay = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Subscribes the WebSocket session to one event type. An already existing subscription is not an error.</summary>
    public async Task CreateEventSubSubscriptionAsync(EventSubSubscriptionSpec spec, string userId, string sessionId, CancellationToken ct)
    {
        var body = new
        {
            type = spec.Type,
            version = spec.Version,
            condition = spec.BuildCondition(userId),
            transport = new { method = "websocket", session_id = sessionId },
        };
        using var response = await SendAsync(HttpMethod.Post, "eventsub/subscriptions", body, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) return;
        await EnsureSuccessAsync(response, $"Subscribing to {spec.Type}", ct).ConfigureAwait(false);
    }

    /// <summary>One of this app's EventSub subscriptions, as Get EventSub Subscriptions lists it.</summary>
    public sealed record EventSubSubscriptionInfo(string Id, string Type, string Status, string Method, string SessionId);

    /// <summary>Every EventSub subscription this app (client id and token) created, all pages.</summary>
    public async Task<IReadOnlyList<EventSubSubscriptionInfo>> ListEventSubSubscriptionsAsync(CancellationToken ct)
    {
        var list = new List<EventSubSubscriptionInfo>();
        string? cursor = null;
        do
        {
            var query = cursor is null ? null : new[] { KeyValuePair.Create("after", cursor) };
            var json = await RequestAsync(HttpMethod.Get, "eventsub/subscriptions", query, null, "Listing EventSub subscriptions", ct).ConfigureAwait(false);
            foreach (var item in json.Items("data"))
            {
                var transport = item.Child("transport");
                list.Add(new EventSubSubscriptionInfo(item.Str("id"), item.Str("type"), item.Str("status"),
                    transport?.Str("method") ?? "", transport?.Str("session_id") ?? ""));
            }
            cursor = json.Child("pagination") is { } page ? page.Str("cursor") : null;
            if (cursor is { Length: 0 }) cursor = null;
        }
        while (cursor is not null && !ct.IsCancellationRequested);
        return list;
    }

    public async Task DeleteEventSubSubscriptionAsync(string id, CancellationToken ct) =>
        await RequestAsync(HttpMethod.Delete, "eventsub/subscriptions", [KeyValuePair.Create("id", id)], null, "Deleting an EventSub subscription", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Sends a chat message and returns its id; throws when Twitch drops it (e.g. AutoMod, duplicate, rate limit).
    /// </summary>
    public async Task<string> SendChatMessageAsync(string broadcasterId, string senderId, string message, CancellationToken ct,
        string? replyToMessageId = null)
    {
        object body = string.IsNullOrEmpty(replyToMessageId)
            ? new { broadcaster_id = broadcasterId, sender_id = senderId, message }
            : new { broadcaster_id = broadcasterId, sender_id = senderId, message, reply_parent_message_id = replyToMessageId };
        var json = await RequestAsync(HttpMethod.Post, "chat/messages", null, body, "Sending chat message", ct).ConfigureAwait(false);
        var result = json.Items("data").FirstOrDefault();
        if (result.ValueKind == JsonValueKind.Object && !result.Bool("is_sent"))
        {
            var reason = result.Child("drop_reason") is { } drop ? drop.Str("message") : "";
            throw new TwitchApiException($"Twitch did not send the message: {(reason.Length > 0 ? reason : "dropped")}");
        }
        return result.ValueKind == JsonValueKind.Object ? result.Str("message_id") : "";
    }

    /// <summary>
    /// Sends one Helix request and returns the JSON body (default for an empty body such as 204). Throws a
    /// <see cref="TwitchApiException"/> describing <paramref name="operation"/> on any error status.
    /// </summary>
    public async Task<JsonElement> RequestAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? query,
        object? body, string operation, CancellationToken ct)
    {
        using var response = await SendAsync(method, WithQuery(path, query), body, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, operation, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static string WithQuery(string path, IEnumerable<KeyValuePair<string, string>>? query)
    {
        if (query is null) return path;
        var builder = new StringBuilder(path);
        var first = true;
        foreach (var (key, value) in query)
        {
            builder.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return builder.ToString();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = tokens.AccessToken;
        var response = await SendOnceAsync(method, path, body, token, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            if (!await tokens.TryRefreshAsync(token, ct).ConfigureAwait(false))
                throw new TwitchApiException("Twitch sign-in has expired. Please sign in again.", 401);
            response = await SendOnceAsync(method, path, body, tokens.AccessToken, ct).ConfigureAwait(false);
        }
        if (RetryDelay(response) is not { } delay) return response;
        response.Dispose();
        await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        return await SendOnceAsync(method, path, body, tokens.AccessToken, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// How long to wait before the one retry, or null for none: a 503 retries after a second; a 429 from an empty
    /// rate-limit bucket waits until it refills (other 429s are endpoint limits, such as EventSub's, that waiting
    /// does not fix).
    /// </summary>
    internal TimeSpan? RetryDelay(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return UnavailableRetryDelay;
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return null;
        if (Header(response, "Ratelimit-Remaining") is not { } remaining || remaining != 0) return null;
        if (Header(response, "Ratelimit-Reset") is not { } reset) return null;
        var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - _time.GetUtcNow();
        if (wait > MaxRateLimitWait) return null;
        return wait < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : wait;
    }

    private static long? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var value) ? value : null;

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(helixBase, path));
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Client-Id", tokens.ClientId);
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string message;
        try
        {
            message = JsonSerializer.Deserialize<JsonElement>(text).Str("message");
        }
        catch (JsonException)
        {
            message = "";
        }
        var status = (int)response.StatusCode;
        var hint = status switch
        {
            401 => " Sign in to Twitch again.",
            403 => " Cuebot is not authorized for this; check the scopes on the Twitch Settings tab and sign in again.",
            429 => " Twitch rate limit reached; try again shortly.",
            _ => "",
        };
        throw new TwitchApiException($"{operation} failed ({status}){(message.Length > 0 ? $": {message}" : "")}.{hint}".Replace("..", ".", StringComparison.Ordinal),
            status);
    }
}
