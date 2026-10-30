using Cuebot.Core.Storage;
namespace Cuebot.Rumble;

/// <summary>Persisted Rumble configuration and chat sign-in state (rumble.json).</summary>
public sealed class RumbleSettings
{
    /// <summary>
    /// The private Live Stream API URL including its key (rumble.com → Account → Live Stream API). Treat it like
    /// a password: it is never logged.
    /// </summary>
    [Secret]
    public string LiveStreamApiUrl { get; set; } = "";

    /// <summary>Seconds between Live Stream API polls. Values below Rumble's minimum of 5 are raised to 5.</summary>
    public int PollSeconds { get; set; } = 10;

    public bool AutoConnect { get; set; } = true;

    /// <summary>Value of Rumble's <c>u_s</c> session cookie, used only to send chat. Never the password.</summary>
    [Secret]
    public string SessionCookie { get; set; } = "";

    /// <summary>Username the session belongs to; empty when not signed in.</summary>
    public string SignedInUser { get; set; } = "";
}
