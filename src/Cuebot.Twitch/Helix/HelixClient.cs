using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;

namespace Cuebot.Twitch.Helix;

/// <summary>The few Helix endpoints Cuebot uses. A 401 triggers one token refresh and a single retry.</summary>
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

    /// <summary>Sends a chat message; throws when Twitch drops it (e.g. AutoMod, duplicate, rate limit).</summary>
    public async Task SendChatMessageAsync(string broadcasterId, string senderId, string message, CancellationToken ct)
    {
        var body = new { broadcaster_id = broadcasterId, sender_id = senderId, message };
        using var response = await SendAsync(HttpMethod.Post, "chat/messages", body, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Sending chat message", ct).ConfigureAwait(false);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        var result = json.Items("data").FirstOrDefault();
        if (result.ValueKind == JsonValueKind.Object && !result.Bool("is_sent"))
        {
            var reason = result.Child("drop_reason") is { } drop ? drop.Str("message") : "";
            throw new TwitchApiException($"Twitch did not send the message: {(reason.Length > 0 ? reason : "dropped")}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var token = tokens.AccessToken;
        var response = await SendOnceAsync(method, path, body, token, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        if (!await tokens.TryRefreshAsync(token, ct).ConfigureAwait(false))
            throw new TwitchApiException("Twitch sign-in has expired. Please sign in again.", 401);
        return await SendOnceAsync(method, path, body, tokens.AccessToken, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(helixBase, path)) { Content = JsonContent.Create(body) };
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
        throw new TwitchApiException(
            $"{operation} failed ({(int)response.StatusCode}){(message.Length > 0 ? $": {message}" : "")}",
            (int)response.StatusCode);
    }
}
