using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cuebot.YouTube.Auth;

namespace Cuebot.YouTube.Api;

/// <summary>
/// Transport for the YouTube Data API v3: bearer auth (one refresh-and-retry on 401), quota accounting, and
/// Google's error format turned into <see cref="YouTubeApiException"/> (quotaExceeded marks the tracker exhausted).
/// </summary>
internal sealed class YouTubeApiClient(HttpClient http, Uri apiBase, QuotaTracker quota)
{
    public QuotaTracker Quota => quota;

    public async Task<JsonElement> SendAsync(AccountTokens account, HttpMethod method, string resource,
        IEnumerable<KeyValuePair<string, string?>> query, object? body, int cost, CancellationToken ct)
    {
        var uri = BuildUri(resource, query);
        var json = body is null ? null : JsonSerializer.Serialize(body);
        var token = await account.GetAccessTokenAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                await account.RefreshAsync(token, ct).ConfigureAwait(false);
                token = await account.GetAccessTokenAsync(ct).ConfigureAwait(false);
                continue;
            }

            quota.Spend(cost);
            var parsed = JsonElementExtensions.Parse(text);
            if (response.IsSuccessStatusCode) return parsed;

            var error = ParseError(parsed, (int)response.StatusCode);
            if (error.IsQuotaExceeded) quota.MarkExhausted();
            throw error;
        }
    }

    internal Uri BuildUri(string resource, IEnumerable<KeyValuePair<string, string?>> query)
    {
        var parts = query.Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}");
        var text = string.Join('&', parts);
        return new Uri(apiBase, text.Length > 0 ? $"{resource}?{text}" : resource);
    }

    internal static YouTubeApiException ParseError(JsonElement body, int status)
    {
        var error = body.Prop("error");
        var first = error.Items("errors").FirstOrDefault();
        var reason = first.Str("reason");
        var message = error.Str("message");
        if (message.Length == 0) message = first.Str("message");
        if (message.Length == 0) message = $"HTTP {status}";
        var friendly = reason switch
        {
            "quotaExceeded" or "dailyLimitExceeded" => "The YouTube API daily quota is exhausted (resets at midnight Pacific time)",
            "liveChatEnded" => "The live chat has ended",
            "liveChatNotFound" => "The live chat was not found",
            "liveChatDisabled" => "Live chat is disabled for this broadcast",
            "rateLimitExceeded" => "YouTube says Cuebot is sending requests too quickly",
            "insufficientPermissions" or "forbidden" => $"YouTube refused the request: {message}",
            _ => $"YouTube API error: {message}",
        };
        return new YouTubeApiException(friendly, status, reason);
    }
}
