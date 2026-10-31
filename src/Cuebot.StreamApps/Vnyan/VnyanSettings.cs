namespace Cuebot.StreamApps;

/// <summary>VNyan connection settings. Its HTTP API has no password.</summary>
public sealed class VnyanSettings
{
    /// <summary>The PC running VNyan (its API listens on every network card).</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>VNyan's HTTP API port (8069 unless it was changed in VNyan).</summary>
    public int Port { get; set; } = 8069;
}
