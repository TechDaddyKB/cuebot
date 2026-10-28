namespace Cuebot.Elgato;

/// <summary>Persisted Elgato configuration (elgato.json). None of the Elgato apps or lights use credentials.</summary>
public sealed class ElgatoSettings
{
    public ElgatoLightsSettings Lights { get; set; } = new();
    public CameraHubSettings CameraHub { get; set; } = new();
}

public sealed class ElgatoLightsSettings
{
    /// <summary>Start polling (and discovering) the lights when Cuebot starts.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Look for lights with multicast DNS (_elg._tcp) in addition to the addresses entered by hand.</summary>
    public bool Discovery { get; set; } = true;

    /// <summary>How often each light's power, brightness and temperature are read (lights have no push API).</summary>
    public int PollIntervalMs { get; set; } = 2000;

    /// <summary>How often the name (accessory-info) and power-on settings are re-read.</summary>
    public int InfoIntervalSeconds { get; set; } = 30;

    /// <summary>How often an mDNS query is sent while running.</summary>
    public int DiscoveryIntervalSeconds { get; set; } = 60;

    /// <summary>Every light Cuebot knows (found by discovery or added by hand); "Device Added" fires once per light.</summary>
    public List<KnownLight> Devices { get; set; } = [];

    /// <summary>Ids of discovered lights the user removed; discovery does not add them back.</summary>
    public List<string> Ignored { get; set; } = [];
}

public sealed class KnownLight
{
    /// <summary>The serial number (or host:port until the light has answered once).</summary>
    public string Id { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 9123;
    public string Name { get; set; } = "";
    public string ProductName { get; set; } = "";

    /// <summary>Entered by hand (kept at its address) rather than found by discovery.</summary>
    public bool Manual { get; set; }
}

public sealed class CameraHubSettings
{
    /// <summary>Start webcam control when Cuebot starts (Linux: V4L2; see PROTOCOL.md for Windows).</summary>
    public bool AutoConnect { get; set; }

    /// <summary>How often webcam controls are read to detect changes made elsewhere.</summary>
    public int PollIntervalMs { get; set; } = 2000;

    /// <summary>Only control webcams whose name contains one of these (comma-separated); empty = every webcam.</summary>
    public string NameFilter { get; set; } = "";
}
