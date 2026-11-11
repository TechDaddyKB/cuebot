namespace Cuebot.StreamApps;

/// <summary>RahiTuber settings ("Control States via HTTP" in its Preferences > Integration; no password).</summary>
public sealed class RahituberSettings
{
    /// <summary>The PC running RahiTuber (it listens on every network card, so another PC works too).</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The HTTP Port next to "Control States via HTTP" (8000 unless changed there).</summary>
    public int Port { get; set; } = 8000;

    /// <summary>
    /// Optional: the folder with RahiTuber's program and its config.xml. Cuebot then reads (never writes) the loaded
    /// avatar's states and RahiTuber_Log.txt. Empty = not used.
    /// </summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// The least time between two requests for the same state, in milliseconds, so quick presses are not swallowed by
    /// RahiTuber's 0.2 s repeat guard. 0 = no spacing.
    /// </summary>
    public int SameStateGapMs { get; set; } = 250;
}
