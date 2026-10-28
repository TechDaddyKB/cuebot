using System.Globalization;
using Cuebot.Controllers.Device;

namespace Cuebot.Controllers;

/// <summary>One axis in a snapshot: normalised value (deadzone applied) and the raw reading.</summary>
public sealed record AxisSnapshot(string Name, double Value, short Raw);

/// <summary>A connected controller with its live state, as seen by the UI, scripts and sub-actions.</summary>
public sealed record ControllerSnapshot(
    ControllerDeviceInfo Device,
    int Slot,
    DateTimeOffset ConnectedAt,
    IReadOnlyList<string> HeldButtons,
    IReadOnlyList<AxisSnapshot> Axes,
    IReadOnlyList<int> Hats)
{
    public string Name => Device.Name;

    /// <summary>"#1 Xbox Series X Controller".</summary>
    public string DisplayName => $"#{Slot} {Device.Name}";

    /// <summary>The trigger/sub-action variables describing this controller (controllerName, controllerGuid, ...).</summary>
    public Dictionary<string, object?> ToArgs() => ControllerState.DeviceArgs(Device, Slot);

    /// <summary>Everything, for scripts: identity, battery, held buttons, axis values and hat directions.</summary>
    public Dictionary<string, object?> Describe()
    {
        var d = ToArgs();
        d["controllerConnectedAt"] = ConnectedAt.ToString("O", CultureInfo.InvariantCulture);
        d["heldButtons"] = string.Join(",", HeldButtons);
        d["axes"] = Axes.ToDictionary(a => a.Name, a => (object?)a.Value);
        d["hats"] = Hats.Select((h, i) => (Name: ControllerNames.Hat(i), Dir: ControllerNames.HatDirection(h)))
            .ToDictionary(x => x.Name, x => (object?)x.Dir);
        return d;
    }
}

/// <summary>Mutable live state of one controller. Owned by <see cref="ControllerService"/> (accessed under its lock).</summary>
internal sealed class ControllerState
{
    public ControllerState(ControllerDeviceInfo device, int slot, DateTimeOffset connectedAt)
    {
        Device = device;
        Slot = slot;
        ConnectedAt = connectedAt;
        Resize(device);
    }

    public ControllerDeviceInfo Device { get; private set; }
    public int Slot { get; }
    public DateTimeOffset ConnectedAt { get; }

    public bool[] Buttons { get; private set; } = [];
    public DateTimeOffset[] PressedAt { get; private set; } = [];
    public short[] RawAxes { get; private set; } = [];

    /// <summary>Current normalised value of each axis (deadzone applied).</summary>
    public double[] Axes { get; private set; } = [];

    /// <summary>Last value published in an axis event (what the next event reports as axisPrevious).</summary>
    public double[] PublishedAxes { get; private set; } = [];

    public byte[] Hats { get; private set; } = [];

    /// <summary>The synthesised D-pad hat (gamepads).</summary>
    public byte DPadHat { get; set; }

    public bool IsGamepad => Device.IsGamepad;

    public void Update(ControllerDeviceInfo device)
    {
        var relayout = device.IsGamepad != Device.IsGamepad || device.Buttons != Device.Buttons || device.Axes != Device.Axes
                       || device.Hats != Device.Hats;
        Device = device;
        if (relayout) Resize(device);
    }

    private void Resize(ControllerDeviceInfo device)
    {
        var buttons = device.IsGamepad ? ControllerNames.GamepadButtons.Length : Math.Clamp(device.Buttons, 0, 256);
        var axes = device.IsGamepad ? ControllerNames.GamepadAxes.Length : Math.Clamp(device.Axes, 0, 64);
        Buttons = new bool[buttons];
        PressedAt = new DateTimeOffset[buttons];
        RawAxes = new short[axes];
        Axes = new double[axes];
        PublishedAxes = new double[axes];
        Hats = new byte[device.IsGamepad ? 0 : Math.Clamp(device.Hats, 0, 16)];
        DPadHat = 0;
    }

    /// <summary>Grows the arrays when a device reports an index beyond what it announced.</summary>
    public void EnsureButton(int index)
    {
        if (index < Buttons.Length || index > 255) return;
        var size = index + 1;
        Buttons = [.. Buttons, .. new bool[size - Buttons.Length]];
        PressedAt = [.. PressedAt, .. new DateTimeOffset[size - PressedAt.Length]];
    }

    public void EnsureAxis(int index)
    {
        if (index < Axes.Length || index > 63) return;
        var extra = index + 1 - Axes.Length;
        RawAxes = [.. RawAxes, .. new short[extra]];
        Axes = [.. Axes, .. new double[extra]];
        PublishedAxes = [.. PublishedAxes, .. new double[extra]];
    }

    public void EnsureHat(int index)
    {
        if (index < Hats.Length || index > 15) return;
        Hats = [.. Hats, .. new byte[index + 1 - Hats.Length]];
    }

    public string ButtonName(int index) => ControllerNames.Button(IsGamepad, index);

    public string ButtonLabel(int index) => ControllerNames.ButtonLabel(Device.Type, ButtonName(index));

    public IReadOnlyList<string> HeldButtons() =>
        Buttons.Select((down, i) => (down, i)).Where(x => x.down).Select(x => ButtonName(x.i)).ToArray();

    public ControllerSnapshot Snapshot() => new(Device, Slot, ConnectedAt, HeldButtons(),
        Axes.Select((v, i) => new AxisSnapshot(ControllerNames.Axis(IsGamepad, i), v, RawAxes[i])).ToArray(),
        IsGamepad ? [DPadHat] : Hats.Select(h => (int)h).ToArray());

    public static Dictionary<string, object?> DeviceArgs(ControllerDeviceInfo d, int slot) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["controllerName"] = d.Name,
        ["controllerId"] = (long)d.Id,
        ["controllerGuid"] = d.Guid,
        ["controllerIndex"] = slot,
        ["controllerType"] = d.Type,
        ["controllerIsGamepad"] = d.IsGamepad,
        ["controllerPlayerIndex"] = d.PlayerIndex,
        ["controllerVendorId"] = d.VendorId.ToString("x4", CultureInfo.InvariantCulture),
        ["controllerProductId"] = d.ProductId.ToString("x4", CultureInfo.InvariantCulture),
        ["controllerSerial"] = d.Serial,
        ["controllerCanRumble"] = d.CanRumble,
        ["controllerHasLed"] = d.HasRgbLed,
        ["batteryState"] = d.Power.ToString(),
        ["batteryPercent"] = d.BatteryPercent,
    };
}

/// <summary>
/// "Which controller" filters: empty or Any = every controller; otherwise comma-separated entries, each one of
/// a slot ("#1" or "1"), a GUID, a type ("PS5", "Xbox One", "Joystick") or part of the name ("DualSense").
/// </summary>
public static class ControllerFilter
{
    public static bool IsAny(string filter) =>
        string.IsNullOrWhiteSpace(filter) || string.Equals(filter.Trim(), "Any", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(filter.Trim(), "All", StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string filter, string name, string guid, int slot, string type)
    {
        if (IsAny(filter)) return true;
        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.TrimStart('#');
            if (int.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n == slot) return true;
                continue;
            }
            if (string.Equals(raw, guid, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, type, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains(raw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool Matches(string filter, IReadOnlyDictionary<string, object?> args)
    {
        string Arg(string key) => args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        _ = int.TryParse(Arg("controllerIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot);
        return Matches(filter, Arg("controllerName"), Arg("controllerGuid"), slot, Arg("controllerType"));
    }

    public static bool Matches(string filter, ControllerSnapshot c) =>
        Matches(filter, c.Device.Name, c.Device.Guid, c.Slot, c.Device.Type);
}
