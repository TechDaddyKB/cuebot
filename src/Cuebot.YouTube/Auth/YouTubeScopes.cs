namespace Cuebot.YouTube.Auth;

/// <summary>OAuth scopes Cuebot requests.</summary>
internal static class YouTubeScopes
{
    /// <summary>
    /// Manage the YouTube account: read and post live chat, moderate, update broadcasts and videos. Every call Cuebot
    /// makes accepts it, so it is the only YouTube scope requested (youtube.force-ssl would add nothing but a second
    /// sensitive scope for Google's verification). It is also the only YouTube management scope Google allows for
    /// "TVs and Limited Input devices" clients (device flow).
    /// </summary>
    public const string Manage = "https://www.googleapis.com/auth/youtube";

    public static IReadOnlyList<string> Device { get; } = [Manage];
    public static IReadOnlyList<string> Loopback { get; } = [Manage];
}
