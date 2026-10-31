namespace Cuebot.StreamApps;

/// <summary>veadotube (mini or the full app) connection settings. Its API has no password.</summary>
public sealed class VeadotubeSettings
{
    /// <summary>
    /// Which veadotube to use: empty for the first one that is open, a name or instance id (e.g. "veadotube mini",
    /// "mini", "veado"), or an address such as 127.0.0.1:2424 to skip the instances folder (another PC).
    /// </summary>
    public string Instance { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when veadotube closes or is not open yet.</summary>
    public bool AutoReconnect { get; set; } = true;
}
