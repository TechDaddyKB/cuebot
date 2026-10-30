using Cuebot.Core.Auth;

namespace Cuebot.YouTube.Auth;

/// <summary>
/// The Google OAuth clients YouTube signs in with. Google refreshes a token only through the client that issued it,
/// and sign-in with a code needs a different client type ("TVs and Limited Input devices") than browser sign-in
/// ("Desktop app"), so each account records which one it used (<see cref="YouTubeAccount.SignedInWith"/>).
/// The user's own client, when entered, serves both kinds of sign-in, as before.
/// </summary>
internal sealed record YouTubeApps(AppCredentials Browser, AppCredentials Device)
{
    public const string BuiltInBrowser = "builtin";
    public const string BuiltInDevice = "builtin-device";

    public static YouTubeApps BuiltIn { get; } = new(BuiltInApps.Google, BuiltInApps.GoogleDevice);

    public static AppCredentials Own(YouTubeSettings s) => AppCredentials.Choose(s.ClientId, s.ClientSecret, AppCredentials.None);

    public AppCredentials ForBrowser(YouTubeSettings s) => AppCredentials.Choose(s.ClientId, s.ClientSecret, Browser);

    public AppCredentials ForDevice(YouTubeSettings s) => AppCredentials.Choose(s.ClientId, s.ClientSecret, Device);

    /// <summary>The client that issued <paramref name="account"/>'s tokens, which must also refresh them.</summary>
    public AppCredentials ForAccount(YouTubeSettings s, YouTubeAccount account) => account.SignedInWith switch
    {
        BuiltInBrowser => Browser,
        BuiltInDevice => Device,
        _ => Own(s),
    };

    public static string Marker(AppCredentials app, bool device) =>
        !app.IsBuiltIn ? "" : device ? BuiltInDevice : BuiltInBrowser;
}
