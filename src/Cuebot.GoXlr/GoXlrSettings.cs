namespace Cuebot.GoXlr;

/// <summary>
/// Persisted GoXLR configuration (goxlr.json). The GoXLR Utility has no credentials: it listens on localhost:14564 unless
/// its "Allow network access" option is on.
/// </summary>
public sealed class GoXlrSettings
{
    /// <summary>Connect when Cuebot starts.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Keep looking (with backoff) while the utility is not running, and reconnect when it closes.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Where the GoXLR Utility runs (another PC needs "Allow network access" turned on in the utility).</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>The utility's HTTP port (its <c>--http-port</c>; 14564 by default).</summary>
    public int Port { get; set; } = GoXlrDefaults.Port;

    /// <summary>The serial number of the GoXLR that sub-actions control; empty = the first one the utility reports.</summary>
    public string Serial { get; set; } = "";
}
