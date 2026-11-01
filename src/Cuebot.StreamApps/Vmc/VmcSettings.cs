namespace Cuebot.StreamApps;

/// <summary>VMC protocol settings (OSC over UDP, no password). 39539 is the usual VMC receiver port.</summary>
public sealed class VmcSettings
{
    /// <summary>The PC running the 3D VTuber app.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The app's VMC receiver port (VSeeFace, VMagicMirror, VNyan and Warudo default to 39539).</summary>
    public int Port { get; set; } = 39539;

    public bool AutoConnect { get; set; }
}
