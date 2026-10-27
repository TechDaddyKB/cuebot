namespace Cuebot.YouTube.Auth;

/// <summary>OAuth scopes Cuebot requests.</summary>
internal static class YouTubeScopes
{
    /// <summary>
    /// Manage the YouTube account: read and post live chat, moderate, update broadcasts and videos. It is the only
    /// YouTube management scope Google allows for "TVs and Limited Input devices" clients (device flow).
    /// </summary>
    public const string Manage = "https://www.googleapis.com/auth/youtube";

    /// <summary>Same access over SSL-only endpoints; requested additionally by the browser (loopback) sign-in.</summary>
    public const string ForceSsl = "https://www.googleapis.com/auth/youtube.force-ssl";

    public static IReadOnlyList<string> Device { get; } = [Manage];
    public static IReadOnlyList<string> Loopback { get; } = [Manage, ForceSsl];
}
