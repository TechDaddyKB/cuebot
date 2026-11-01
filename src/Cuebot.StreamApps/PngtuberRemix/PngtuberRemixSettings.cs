namespace Cuebot.StreamApps;

/// <summary>PNGTuber Remix connection settings (its WebSocket API; no password).</summary>
public sealed class PngtuberRemixSettings
{
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The port in PNGTuber Remix's WebSocket tab (9321 unless changed there).</summary>
    public int Port { get; set; } = 9321;

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when PNGTuber Remix closes or its WebSocket is not started yet.</summary>
    public bool AutoReconnect { get; set; } = true;
}
