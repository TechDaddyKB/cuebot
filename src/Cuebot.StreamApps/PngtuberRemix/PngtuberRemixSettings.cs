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

    /// <summary>
    /// Seconds between re-reads of the avatar states (the sprites every 5th time), so a state changed with a PNGTuber
    /// Remix hotkey is noticed; 0 = off (then a ping every 15 s checks the connection). PNGTuber Remix pushes nothing.
    /// </summary>
    public double PollSeconds { get; set; } = 2;

    /// <summary>The most items one throw sends; a bigger amount (e.g. %bits%) is lowered to this.</summary>
    public int MaxThrowAmount { get; set; } = 50;
}
