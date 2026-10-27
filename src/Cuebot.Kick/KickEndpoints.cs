namespace Cuebot.Kick;

/// <summary>Base addresses of the services the module talks to. Tests point them at fakes.</summary>
/// <param name="RealtimeApi">kick.com's web API that hands out real-time server addresses and tokens (unofficial).</param>
internal sealed record KickEndpoints(Uri Id, Uri Api, Uri Web, Uri RealtimeApi)
{
    public const int DefaultRedirectPort = 47582;

    public static KickEndpoints Production { get; } = new(
        new Uri("https://id.kick.com/"),
        new Uri("https://api.kick.com/"),
        new Uri("https://kick.com/"),
        new Uri("https://web.kick.com/api/"));

    public static string RedirectUri(int port) => $"http://localhost:{port}/callback";
}
