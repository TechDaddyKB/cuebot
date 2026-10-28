namespace Cuebot.StreamApps;

/// <summary>T.I.T.S. (Twitch Integrated Throwing System) connection settings; its public API has no credentials.</summary>
public sealed class TitsSettings
{
    /// <summary>The PC running T.I.T.S.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>T.I.T.S.'s API port (fixed at 42069 in its documentation).</summary>
    public int Port { get; set; } = 42069;

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when T.I.T.S. closes or is not running yet.</summary>
    public bool AutoReconnect { get; set; } = true;
}
