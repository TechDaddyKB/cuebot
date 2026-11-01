namespace Cuebot.StreamApps;

/// <summary>RahiTuber settings ("Control States via HTTP" in its Preferences > Integration; no password).</summary>
public sealed class RahituberSettings
{
    /// <summary>The PC running RahiTuber (it listens on every network card, so another PC works too).</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The HTTP Port next to "Control States via HTTP" (8000 unless changed there).</summary>
    public int Port { get; set; } = 8000;
}
