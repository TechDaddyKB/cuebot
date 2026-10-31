namespace Cuebot.StreamApps;

/// <summary>Warudo connection settings (its Stream Deck service; no password).</summary>
public sealed class WarudoSettings
{
    /// <summary>
    /// Warudo only listens on this PC, on "localhost": that can be IPv6 (::1) only, e.g. under Proton, so the default
    /// is the name rather than 127.0.0.1.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Warudo's Stream Deck service port (fixed at 19069 in Warudo).</summary>
    public int Port { get; set; } = 19069;

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when Warudo closes or is not listening yet.</summary>
    public bool AutoReconnect { get; set; } = true;
}
