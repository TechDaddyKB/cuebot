namespace Cuebot.Controllers.Device;

/// <summary>Battery state as the device reports it.</summary>
public enum ControllerPower { Unknown, OnBattery, NoBattery, Charging, Charged, Error }

/// <summary>
/// A connected controller as the device layer sees it. <see cref="Id"/> is an instance id that is unique for this
/// connection only (it changes when the controller is unplugged and plugged back in); <see cref="Guid"/> identifies
/// the model and connection type and is what gamecontrollerdb mappings key on.
/// </summary>
public sealed record ControllerDeviceInfo
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public string Guid { get; init; } = "";

    /// <summary>True for devices with a gamepad mapping (standard button/axis layout); false for generic joysticks.</summary>
    public bool IsGamepad { get; init; }

    /// <summary>Gamepad family (<see cref="ControllerNames.TypeNames"/>), "Joystick" for unmapped devices.</summary>
    public string Type { get; init; } = "Unknown";

    /// <summary>Player index the driver assigned (XInput slot, PS LED), or -1.</summary>
    public int PlayerIndex { get; init; } = -1;

    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string Serial { get; init; } = "";

    /// <summary>Number of buttons / axes / hats. Gamepads always use the standard layout (see <see cref="ControllerNames"/>).</summary>
    public int Buttons { get; init; }
    public int Axes { get; init; }
    public int Hats { get; init; }

    public bool CanRumble { get; init; }
    public bool HasRgbLed { get; init; }
    public ControllerPower Power { get; init; }

    /// <summary>Battery charge 0-100, or -1 when unknown.</summary>
    public int BatteryPercent { get; init; } = -1;
}

/// <summary>Something a controller did, raised by the backend on its own thread.</summary>
public abstract record ControllerRawEvent(uint DeviceId);

/// <summary>A controller appeared (also raised again for a known id when its mapping changed).</summary>
public sealed record DeviceAddedEvent(ControllerDeviceInfo Device) : ControllerRawEvent(Device.Id);

public sealed record DeviceRemovedEvent(uint Id) : ControllerRawEvent(Id);

/// <summary>Button index: a standard gamepad button for gamepads, the raw button number for joysticks.</summary>
public sealed record ButtonEvent(uint Id, int Button, bool Down) : ControllerRawEvent(Id);

/// <summary>Raw axis value -32768..32767 (gamepad triggers 0..32767). Index as for buttons.</summary>
public sealed record AxisEvent(uint Id, int Axis, short Value) : ControllerRawEvent(Id);

/// <summary>Hat bitmask: 1 up, 2 right, 4 down, 8 left (0 = centred).</summary>
public sealed record HatEvent(uint Id, int Hat, byte Value) : ControllerRawEvent(Id);

public sealed record BatteryEvent(uint Id, ControllerPower Power, int Percent) : ControllerRawEvent(Id);

/// <summary>Options read when the backend starts.</summary>
public sealed record ControllerBackendOptions
{
    /// <summary>A gamecontrollerdb.txt to load on top of the built-in mappings, or null.</summary>
    public string? MappingsFile { get; init; }

    /// <summary>Also report devices without a gamepad mapping (flight sticks, wheels, button boxes).</summary>
    public bool IncludeJoysticks { get; init; } = true;
}

/// <summary>Result of starting the backend.</summary>
public sealed record ControllerBackendStart(bool Ok, string Version, string? Error, int MappingsLoaded = 0);

/// <summary>
/// The device layer: one implementation talks to SDL3 on a dedicated thread, tests use a fake. Everything above it
/// (filters, deadzones, throttling, triggers) is device-independent.
/// </summary>
public interface IControllerBackend : IDisposable
{
    /// <summary>"SDL3", or a test name.</summary>
    string Name { get; }

    /// <summary>Raised on the backend thread. Handlers must be quick and must not call back into the backend synchronously.</summary>
    event Action<ControllerRawEvent>? EventReceived;

    /// <summary>Raised (on the backend thread) when the backend stopped by itself, with the reason.</summary>
    event Action<string>? Faulted;

    /// <summary>
    /// Starts watching. Completes after the library initialised and the controllers already plugged in were reported
    /// (or with <c>Ok = false</c> and the reason). Never throws.
    /// </summary>
    Task<ControllerBackendStart> StartAsync(ControllerBackendOptions options);

    /// <summary>Stops watching and releases every device. Safe to call when not running.</summary>
    void Stop();

    /// <summary>Rumbles (low- and high-frequency motor strength 0-65535). False when the device cannot rumble.</summary>
    Task<bool> RumbleAsync(uint id, ushort low, ushort high, uint durationMs);

    /// <summary>Sets the light bar / LED colour. False when the device has no RGB LED.</summary>
    Task<bool> SetLedAsync(uint id, byte red, byte green, byte blue);

    /// <summary>Loads more mappings from a gamecontrollerdb.txt; returns how many were added (or a negative number on error).</summary>
    Task<int> AddMappingsAsync(string file);
}
