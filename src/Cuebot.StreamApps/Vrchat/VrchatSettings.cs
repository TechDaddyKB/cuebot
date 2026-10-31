namespace Cuebot.StreamApps;

/// <summary>VRChat OSC settings (OSC has no password). VRChat listens on 9000 and sends to 9001 by default.</summary>
public sealed class VrchatSettings
{
    /// <summary>The PC running VRChat.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Where VRChat listens for OSC (its "in" port).</summary>
    public int SendPort { get; set; } = 9000;

    /// <summary>Where VRChat sends OSC (its "out" port); Cuebot listens here for avatar and parameter changes.</summary>
    public int ListenPort { get; set; } = 9001;

    public bool AutoConnect { get; set; }
}
