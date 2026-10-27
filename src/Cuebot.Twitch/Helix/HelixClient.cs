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
/// single retry.
/// </summary>
internal sealed class HelixClient(HttpClient http, Uri helixBase, ITwitchTokenSource tokens)
{
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
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        if (!await tokens.TryRefreshAsync(token, ct).ConfigureAwait(false))
            throw new TwitchApiException("Twitch sign-in has expired. Please sign in again.", 401);
        return await SendOnceAsync(method, path, body, tokens.AccessToken, ct).ConfigureAwait(false);
    }

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
