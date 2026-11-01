namespace Cuebot.StreamApps;

/// <summary>Animaze connection settings (its API, off until turned on in Animaze; no password).</summary>
public sealed class AnimazeSettings
{
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The port in Animaze's Settings > Animaze API (9000 unless changed there).</summary>
    public int Port { get; set; } = 9000;

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when Animaze closes or its API is not on yet.</summary>
    public bool AutoReconnect { get; set; } = true;
}
