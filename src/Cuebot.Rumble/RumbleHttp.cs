using System.Net;

namespace Cuebot.Rumble;

/// <summary>
/// The module's own HTTP client. It does not share core's client because Rumble's chat sign-in is a cookie that
/// must go only to the requests that need it: this client keeps no cookie jar (cookies are set per request), so
/// the session never leaks into Live Stream API calls or other modules' requests.
/// </summary>
internal static class RumbleHttp
{
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var version = typeof(RumbleHttp).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Cuebot/{version}");
        return client;
    }
}
