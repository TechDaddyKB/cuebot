namespace Cuebot.Elgato.Lights;

/// <summary>Bot event types published by <see cref="ElgatoLightsService"/> (Streamer.bot's "Control Center" triggers).</summary>
public static class LightEvents
{
    public const string Prefix = "elgato.controlCenter.";
    public const string DeviceAdded = Prefix + "deviceAdded";
    public const string DeviceRemoved = Prefix + "deviceRemoved";
    public const string DeviceRenamed = Prefix + "deviceRenamed";
    public const string DeviceConfigurationChanged = Prefix + "deviceConfigurationChanged";
    public const string DevicePowerStateChanged = Prefix + "devicePowerStateChanged";
    public const string DevicePoweredOn = Prefix + "devicePoweredOn";
    public const string DevicePoweredOff = Prefix + "devicePoweredOff";
    public const string BrightnessChanged = Prefix + "brightnessChanged";
    public const string TemperatureChanged = Prefix + "temperatureChanged";

    // Cuebot extras.
    public const string ColorChanged = Prefix + "colorChanged";
    public const string DeviceOnline = Prefix + "deviceOnline";
    public const string DeviceOffline = Prefix + "deviceOffline";
}

/// <summary>A light Cuebot knows. Mutated only by <see cref="ElgatoLightsService"/> under its lock; read freely.</summary>
public sealed class ElgatoLight
{
    internal ElgatoLight(KnownLight known)
    {
        Known = known;
    }

    internal KnownLight Known { get; }

    /// <summary>Serial number (stable across IP changes), or host:port until the light has answered.</summary>
    public string Id => Known.Id;

    public string Address => Known.Address;

    public int Port => Known.Port;

    public bool IsManual => Known.Manual;

    /// <summary>The name set in Control Center (displayName), else the product name.</summary>
    public string Name => Info is { } info
        ? info.DisplayName.Length > 0 ? info.DisplayName : info.ProductName.Length > 0 ? info.ProductName : Address
        : Known.Name.Length > 0 ? Known.Name
        : Known.ProductName.Length > 0 ? Known.ProductName : Address;

    public string ProductName => Info?.ProductName is { Length: > 0 } product ? product : Known.ProductName;

    public AccessoryInfo? Info { get; internal set; }

    /// <summary>The first light of the accessory (lights[0]); null until the first answer.</summary>
    public LightState? State { get; internal set; }

    /// <summary>Number of lights in the accessory's "lights" array (1 for every current product).</summary>
    public int LightCount { get; internal set; } = 1;

    /// <summary>GET /elgato/lights/settings as compact JSON (power-on behaviour, transition times).</summary>
    public string? SettingsJson { get; internal set; }

    public bool IsOnline { get; internal set; }

    public DateTimeOffset? LastSeen { get; internal set; }

    public string? LastError { get; internal set; }

    internal int Failures { get; set; }

    internal DateTimeOffset NextInfo { get; set; } = DateTimeOffset.MinValue;

    internal Uri BaseUri => ElgatoLightClient.BaseUri(Address, Port);

    internal readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A plain snapshot for scripts and sub-action variables.</summary>
    public Dictionary<string, object?> ToArgs()
    {
        var args = new Dictionary<string, object?>
        {
            ["deviceId"] = Id,
            ["deviceName"] = Name,
            ["productName"] = ProductName,
            ["serialNumber"] = Info?.SerialNumber ?? "",
            ["firmwareVersion"] = Info?.FirmwareVersion ?? "",
            ["ipAddress"] = Address,
            ["port"] = Port,
            ["online"] = IsOnline,
        };
        if (State is { } s)
        {
            args["on"] = s.On;
            args["powerState"] = s.On ? "On" : "Off";
            args["brightness"] = s.Brightness;
            args["temperature"] = s.Kelvin;
            args["temperatureRaw"] = s.Temperature;
            args["hue"] = s.Hue;
            args["saturation"] = s.Saturation;
        }
        return args;
    }
}
