namespace Cuebot.Obs;

/// <summary>Connection settings persisted in obs.json.</summary>
public sealed class ObsSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;

    /// <summary>obs-websocket server password; empty when authentication is disabled in OBS.</summary>
    public string Password { get; set; } = "";

    /// <summary>Connect on startup and keep reconnecting while OBS is unreachable.</summary>
    public bool AutoConnect { get; set; }
}
