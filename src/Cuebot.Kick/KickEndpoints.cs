namespace Cuebot.Kick;

/// <summary>Base addresses of the services the module talks to. Tests point them at fakes.</summary>
internal sealed record KickEndpoints(Uri Id, Uri Api, Uri Web, Uri Pusher)
{
    public const int DefaultRedirectPort = 47582;

    /// <summary>Public Pusher app key and cluster used by the kick.com web client (unofficial; see PROTOCOL.md).</summary>
    public const string PusherAppKey = "32cbd69e4b950bf97679";
    public const string PusherCluster = "us2";

    public static KickEndpoints Production { get; } = new(
        new Uri("https://id.kick.com/"),
        new Uri("https://api.kick.com/"),
        new Uri("https://kick.com/"),
        new Uri($"wss://ws-{PusherCluster}.pusher.com/app/{PusherAppKey}?protocol=7&client=js&version=8.4.0&flash=false"));

    public static string RedirectUri(int port) => $"http://localhost:{port}/callback";
}
