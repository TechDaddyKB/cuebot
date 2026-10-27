using System.Net;
using System.Text.Json;

namespace Cuebot.Kick.Api;

/// <summary>
/// UNOFFICIAL: kick.com's own website API (<c>/api/v2/channels/{slug}</c>), used only to find the internal channel
/// and chatroom ids the real-time websocket needs (the official API does not expose them). kick.com sits behind
/// Cloudflare, which sometimes blocks non-browser requests; callers fall back to ids entered by the user.
/// </summary>
internal sealed class KickWebClient(HttpClient http, KickEndpoints endpoints)
{
    internal const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public async Task<KickChannelIds> GetChannelIdsAsync(string slug, CancellationToken ct)
    {
        slug = NormalizeSlug(slug);
        if (slug.Length == 0) throw new KickApiException("No Kick channel name to look up");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoints.Web, "api/v2/channels/" + Uri.EscapeDataString(slug)));
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new KickApiException($"Kick channel '{slug}' was not found", 404);
        if (!response.IsSuccessStatusCode)
            throw new KickApiException(
                $"kick.com refused the channel lookup ({(int)response.StatusCode}); it is probably blocking automated requests. " +
                "Enter the channel and chatroom ids in Kick settings (see the help there).", (int)response.StatusCode);
        try
        {
            return ParseChannelIds(body);
        }
        catch (JsonException)
        {
            throw new KickApiException("kick.com answered the channel lookup with a page instead of data (bot protection). " +
                                       "Enter the channel and chatroom ids in Kick settings.");
        }
    }

    internal static KickChannelIds ParseChannelIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ids = new KickChannelIds(root.Long("id"), root.Prop("chatroom").Long("id"),
            root.Long("user_id") is > 0 and var uid ? uid : root.Prop("user").Long("id"), root.Str("slug"));
        if (ids.ChatroomId <= 0) throw new KickApiException("kick.com returned no chatroom for this channel");
        return ids;
    }

    public static string NormalizeSlug(string slug)
    {
        slug = slug.Trim().TrimEnd('/').TrimStart('@');
        var slash = slug.LastIndexOf('/');
        if (slash >= 0) slug = slug[(slash + 1)..];
        return slug.ToLowerInvariant();
    }
}
