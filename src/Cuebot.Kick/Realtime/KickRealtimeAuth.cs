using System.Net.Http.Json;
using System.Text.Json;
using Cuebot.Kick.Api;

namespace Cuebot.Kick.Realtime;

/// <summary>Where to connect for real-time events and the connection token to present there.</summary>
internal sealed record KickRealtimeCredentials(Uri Url, string Token);

/// <summary>
/// UNOFFICIAL: how kick.com's own web client finds its real-time server (a Centrifugo instance) and gets an anonymous
/// connection token for it. Public chat channels need no further authorization. See PROTOCOL.md.
/// </summary>
internal sealed class KickRealtimeAuth(HttpClient http, KickEndpoints endpoints)
{
    /// <summary>Used when the discovery call fails or names no Centrifugo server (the address seen in Oct 2026).</summary>
    public static readonly Uri FallbackUrl = new("wss://realtime.us-west-2.platform.kick.com/connection/websocket");

    /// <summary>A fresh client id plus the server and token for it. <paramref name="channelId"/> may be 0.</summary>
    public async Task<KickRealtimeCredentials> GetCredentialsAsync(string clientId, long channelId, CancellationToken ct)
    {
        Uri url;
        try
        {
            url = await DiscoverAsync(clientId, channelId, ct).ConfigureAwait(false) ?? FallbackUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KickApiException && !ct.IsCancellationRequested)
        {
            url = FallbackUrl;
        }
        return new KickRealtimeCredentials(url, await GetTokenAsync(clientId, ct).ConfigureAwait(false));
    }

    /// <summary>A new connection token for <paramref name="clientId"/> (also used to refresh an expiring one).</summary>
    public async Task<string> GetTokenAsync(string clientId, CancellationToken ct)
    {
        using var doc = await PostAsync("v1/realtime/auth/connection", new { client_id = clientId }, ct).ConfigureAwait(false);
        return doc.RootElement.Prop("data").Str("token") is { Length: > 0 } token
            ? token
            : throw new KickApiException("Kick's real-time service returned no connection token");
    }

    private async Task<Uri?> DiscoverAsync(string clientId, long channelId, CancellationToken ct)
    {
        var path = channelId > 0 ? $"v1/realtime/channels/{channelId}/chat/connection" : "v1/realtime/connection";
        var body = new
        {
            client = new { id = clientId, type = "web" },
            capabilities = new { accepted_providers = new[] { new { provider = "centrifugo" } } },
        };
        using var doc = await PostAsync(path, body, ct).ConfigureAwait(false);
        if (doc.RootElement.Prop("data").Prop("connections") is not { ValueKind: JsonValueKind.Array } connections) return null;
        foreach (var connection in connections.EnumerateArray())
        {
            if (connection.Str("provider") == "centrifugo" &&
                Uri.TryCreate(connection.Prop("credentials").Str("url"), UriKind.Absolute, out var url) &&
                url.Scheme is "wss" or "ws")
                return url;
        }
        return null;
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoints.RealtimeApi, path)) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("User-Agent", KickWebClient.BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new KickApiException($"Kick's real-time service refused {path} ({(int)response.StatusCode})", (int)response.StatusCode);
        return JsonDocument.Parse(text);
    }
}
