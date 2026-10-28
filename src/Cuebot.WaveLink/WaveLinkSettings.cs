namespace Cuebot.WaveLink;

/// <summary>Persisted Wave Link configuration (wavelink.json). Wave Link has no credentials; it only listens on 127.0.0.1.</summary>
public sealed class WaveLinkSettings
{
    /// <summary>Connect when Cuebot starts.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Keep looking (with backoff) while Wave Link is not running, and reconnect when it closes.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Which API to look for; Auto scans the Wave Link 3 ports first, then the 1.x/2.x ports.</summary>
    public WaveLinkApiPreference Api { get; set; } = WaveLinkApiPreference.Auto;

    /// <summary>A fixed port (0 = detect: ws-info.json, then 1884-1893 for Wave Link 3 and 1824-1834 for 1.x/2.x).</summary>
    public int Port { get; set; }

    /// <summary>
    /// Publish "Level meter updated" events. Off by default: Wave Link sends meter values many times a second and most
    /// setups never use them. When on, Wave Link 3 is asked to send them and both APIs are throttled to <see cref="LevelMeterRate"/>.
    /// </summary>
    public bool LevelMeters { get; set; }

    /// <summary>Level meter events per second at most (1-30). Each event carries every meter that moved.</summary>
    public double LevelMeterRate { get; set; } = 10;

    /// <summary>
    /// Which meters (Wave Link 3 subscription filter): empty or "all", or one of channel, mix, input, output. Wave Link 1.x/2.x
    /// meters are filtered locally (its inputs count as channels, its Monitor/Stream outputs as mixes).
    /// </summary>
    public string LevelMeterType { get; set; } = "";

    /// <summary>Ask Wave Link 3 for focused-app notifications ("Focused app changed" and "Add application to channel").</summary>
    public bool FocusedApp { get; set; } = true;
}
