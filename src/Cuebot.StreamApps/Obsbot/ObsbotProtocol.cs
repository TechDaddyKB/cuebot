namespace Cuebot.StreamApps.Obsbot;

/// <summary>One camera slot of OBSBOT Center's DeviceInfo reply.</summary>
public sealed record ObsbotDevice(int Index, bool Connected, string Name);

/// <summary>One preset position slot (Tiny/Meet PresetPositionInfo reply).</summary>
public sealed record ObsbotPreset(int Index, bool Exists, string Name);

/// <summary>What Cuebot last heard from OBSBOT Center. Null = not reported yet.</summary>
public sealed record ObsbotState
{
    public static ObsbotState Empty { get; } = new();

    public IReadOnlyList<ObsbotDevice> Devices { get; init; } = [];
    public int? SelectedDevice { get; init; }
    public bool? Awake { get; init; }

    /// <summary>The type number of the selected device as Center reports it (0 Tiny, 1 Tiny 4K, 2 Meet, 3 Meet 4K; newer models undocumented).</summary>
    public int? DeviceType { get; init; }

    public int? Zoom { get; init; }

    /// <summary>0 = 86°, 1 = 78°, 2 = 65°; -1 = custom (zoom between presets; seen from real devices, not in the sheet).</summary>
    public int? Fov { get; init; }

    public int? Pitch { get; init; }
    public int? Yaw { get; init; }
    public bool? AiLocked { get; init; }
    public IReadOnlyList<ObsbotPreset> Presets { get; init; } = [];
    public int? VirtualBackground { get; init; }
    public int? AutoFraming { get; init; }

    public string SelectedName => SelectedDevice is { } i ? Devices.FirstOrDefault(d => d.Index == i)?.Name ?? "" : "";

    public static string FovLabel(int? fov) => fov switch { 0 => "86°", 1 => "78°", 2 => "65°", -1 => "custom", _ => "" };
}

/// <summary>
/// OBSBOT Center's OSC commands, from OBSBOT's official "OBSBOT Center OSC definition" (obsbot.com/explore/obsbot-center/osc,
/// revision 2026-05-08). Most commands take an optional first int selecting the device (0-3; omitted = the device selected in
/// Center) followed by the value(s). Every argument is an int unless noted. A Tail 2 / Tail Air reached directly follows
/// its own sheet (OBSBOT Tail 2 / Tail Air OSC definitions): the general commands without a device index, its own
/// /OBSBOT/Camera/Tail2|TailAir/ commands, and nothing of Center's PC, Tiny or Meet commands.
/// </summary>
public static class ObsbotProtocol
{
    public const string General = "/OBSBOT/WebCam/General/";
    public const string Tiny = "/OBSBOT/WebCam/Tiny/";
    public const string Meet = "/OBSBOT/WebCam/Meet/";

    /// <summary>Tail commands through OBSBOT Center.</summary>
    public const string Tail = "/OBSBOT/Camera/Tail/";

    /// <summary>Tail commands of a Tail 2 / Tail Air's own OSC server.</summary>
    public const string Tail2 = "/OBSBOT/Camera/Tail2/";
    public const string TailAir = "/OBSBOT/Camera/TailAir/";

    /// <summary>The port a Tail's own OSC server listens on (TCP and UDP).</summary>
    public const int TailHardwarePort = 57110;

    public static bool IsDirect(ObsbotSeries series) => series is ObsbotSeries.Tail2Direct or ObsbotSeries.TailAirDirect;

    /// <summary>The prefix of Tail commands for this series: Center's /Camera/Tail/ or the camera's own.</summary>
    public static string TailPrefix(ObsbotSeries series) => series switch
    {
        ObsbotSeries.Tail2Direct => Tail2,
        ObsbotSeries.TailAirDirect => TailAir,
        _ => Tail,
    };

    /// <summary>Tail series of any kind (through Center or direct).</summary>
    public static bool IsTail(ObsbotSeries series) => series is ObsbotSeries.Tail || IsDirect(series);

    private static readonly HashSet<string> HardwareGeneral = new(StringComparer.Ordinal)
    {
        "Connected", "Disconnected", "ResetGimbal", "SetZoom", "SetZoomSpeed", "SetZoomMax", "SetZoomMin", "SetGimbalUp",
        "SetGimbalDown", "SetGimbalLeft", "SetGimbalRight", "SetGimMotorDegree", "SetMirror", "SetAutoFocus", "SetManualFocus",
        "SetAutoExposure", "SetExposureCompensate", "SetShutterSpeed", "SetISO", "SetAutoWhiteBalance", "SetColorTemperature",
        "GetDeviceInfo", "GetZoomInfo", "GetGimbalPosInfo",
    };

    private static readonly HashSet<string> Tail2Commands = new(StringComparer.Ordinal)
        { "SetAiEnable", "SetAiMode", "SetTrackingSpeed", "SetAutoZoom", "SetOnlyMe", "SetRecording", "Snapshot", "TriggerPreset" };

    private static readonly HashSet<string> TailAirCommands = new(StringComparer.Ordinal)
        { "SetAiMode", "SetTrackingSpeed", "SetRecording", "Snapshot", "TriggerPreset" };

    /// <summary>Whether a camera reached directly has this address in its OSC definition (always true through Center).</summary>
    public static bool Supports(ObsbotSeries series, string address)
    {
        if (!IsDirect(series)) return true;
        if (address.StartsWith(General, StringComparison.Ordinal)) return HardwareGeneral.Contains(address[General.Length..]);
        if (address.StartsWith(Tail2, StringComparison.Ordinal)) return series == ObsbotSeries.Tail2Direct && Tail2Commands.Contains(address[Tail2.Length..]);
        if (address.StartsWith(TailAir, StringComparison.Ordinal)) return series == ObsbotSeries.TailAirDirect && TailAirCommands.Contains(address[TailAir.Length..]);
        return false;
    }

    /// <summary>Gimbal limits (pan°, pitch°) and the highest zoom speed: the Tail 2's own sheet differs from Center's.</summary>
    public static (int Pan, int Pitch, int ZoomSpeed) Limits(ObsbotSeries series) =>
        series == ObsbotSeries.Tail2Direct ? (159, 62, 10) : (129, 59, 11);

    public const string Connected = General + "Connected";
    public const string ConnectedResp = General + "ConnectedResp";
    public const string Disconnected = General + "Disconnected";
    public const string DeviceInfo = General + "DeviceInfo";
    public const string ZoomInfo = General + "ZoomInfo";
    public const string GimbalPosInfo = General + "GetGimbalPosInfoResp";
    public const string AiTrackingInfo = Tiny + "AiTrackingInfo";
    public const string TinyPresetInfo = Tiny + "PresetPositionInfo";
    public const string MeetPresetInfo = Meet + "PresetPositionInfo";

    /// <summary>Not in any OBSBOT sheet: Bitfocus Companion's module reports Tail 2 hardware sending it.</summary>
    public const string GeneralPresetInfo = General + "PresetPositionInfo";
    public const string VirtualBackgroundInfo = Meet + "VirtualBackgroundInfo";
    public const string AutoFramingInfo = Meet + "AutoFramingInfo";

    /// <summary>Argument counts of each reply without the optional leading device index.</summary>
    private static readonly Dictionary<string, int> ReplyArgs = new(StringComparer.Ordinal)
    {
        [DeviceInfo] = 11, [ZoomInfo] = 2, [GimbalPosInfo] = 3, [AiTrackingInfo] = 1, [TinyPresetInfo] = 6,
        [MeetPresetInfo] = 6, [GeneralPresetInfo] = 6, [VirtualBackgroundInfo] = 1, [AutoFramingInfo] = 1,
    };

    /// <summary>A command whose value(s) may be preceded by a device index (null = the selected device).</summary>
    public static OscMessage Command(string address, int? device, params object[] values) =>
        new(address, device is { } d ? [d, .. values] : values);

    /// <summary>The "hello" every client sends first; Center answers ConnectedResp plus DeviceInfo.</summary>
    public static OscMessage Hello() => new(Connected, 0);

    public static OscMessage Goodbye() => new(Disconnected, 0);

    /// <summary>
    /// State queries for <paramref name="series"/> (Auto asks everything the Tiny and Meet families answer). A camera
    /// reached directly also gets the hello each time: Bitfocus Companion's module notes Tail 2 hardware ignores the
    /// Get* queries but answers every Connected with its full state.
    /// </summary>
    public static IEnumerable<OscMessage> Queries(ObsbotSeries series)
    {
        if (IsDirect(series)) yield return Hello();
        yield return new OscMessage(General + "GetDeviceInfo", 0);
        yield return new OscMessage(General + "GetZoomInfo", 0);
        yield return new OscMessage(General + "GetGimbalPosInfo", 0);
        if (series is ObsbotSeries.Tiny or ObsbotSeries.Auto)
        {
            yield return new OscMessage(Tiny + "GetAiTrackingInfo", 0);
            yield return new OscMessage(Tiny + "GetPresetPositionInfo", 0);
        }
        if (series is ObsbotSeries.Meet or ObsbotSeries.Auto)
        {
            yield return new OscMessage(Meet + "GetVirtualBackgroundInfo", 0);
            yield return new OscMessage(Meet + "GetAutoFramingInfo", 0);
        }
        if (series == ObsbotSeries.Meet) yield return new OscMessage(Meet + "GetPresetPositionInfo", 0);
    }

    /// <summary>The series a device name suggests ("OBSBOT Tail Air" → Tail, "Meet 2" → Meet, otherwise Tiny).</summary>
    public static ObsbotSeries Guess(string deviceName, int? deviceType = null)
    {
        if (deviceName.Contains("Tail", StringComparison.OrdinalIgnoreCase)) return ObsbotSeries.Tail;
        if (deviceName.Contains("Meet", StringComparison.OrdinalIgnoreCase)) return ObsbotSeries.Meet;
        if (deviceName.Length == 0 && deviceType is 2 or 3) return ObsbotSeries.Meet;
        return ObsbotSeries.Tiny;
    }

    /// <summary>Folds a reply into the state; returns the same instance for messages that are not state replies.</summary>
    public static ObsbotState Apply(ObsbotState state, OscMessage message, ObsbotSeries series = ObsbotSeries.Auto)
    {
        if (!ReplyArgs.TryGetValue(message.Address, out var expected)) return state;
        if (message.Address == GimbalPosInfo)
        {
            // The sheets say roll, pitch, yaw (no device index on this reply). Bitfocus Companion's module reports real
            // devices sending two values: OBSBOT Center [yaw, pitch], Tail 2 hardware [pitch, yaw].
            if (message.Args.Count == 2)
                return series == ObsbotSeries.Tail2Direct
                    ? state with { Pitch = message.Int(0) ?? state.Pitch, Yaw = message.Int(1) ?? state.Yaw }
                    : state with { Yaw = message.Int(0) ?? state.Yaw, Pitch = message.Int(1) ?? state.Pitch };
            var g = message.Args.Count == expected + 1 ? new OscMessage(message.Address, message.Args.Skip(1).ToArray()) : message;
            return state with { Pitch = g.Int(1) ?? state.Pitch, Yaw = g.Int(2) ?? state.Yaw };
        }
        var a = message.Args.Count == expected + 1 ? new OscMessage(message.Address, message.Args.Skip(1).ToArray()) : message;
        switch (message.Address)
        {
            case DeviceInfo:
                var devices = new List<ObsbotDevice>();
                for (var i = 0; i < 4 && i * 2 + 1 < a.Args.Count; i++)
                    devices.Add(new ObsbotDevice(i, a.Int(i * 2) == 1, a.Str(i * 2 + 1) ?? ""));
                return state with
                {
                    Devices = devices,
                    SelectedDevice = a.Int(8) ?? state.SelectedDevice,
                    Awake = a.Int(9) is { } run ? run == 1 : state.Awake,
                    DeviceType = a.Int(10) ?? state.DeviceType,
                };
            case ZoomInfo:
                return state with { Zoom = a.Int(0) ?? state.Zoom, Fov = a.Int(1) ?? state.Fov };
            case AiTrackingInfo:
                return state with { AiLocked = a.Int(0) is { } l ? l == 1 : state.AiLocked };
            case TinyPresetInfo or MeetPresetInfo or GeneralPresetInfo:
                var presets = new List<ObsbotPreset>();
                for (var i = 0; i < 3 && i * 2 + 1 < a.Args.Count; i++)
                    presets.Add(new ObsbotPreset(i, a.Int(i * 2) == 1, a.Str(i * 2 + 1) ?? ""));
                return state with { Presets = presets };
            case VirtualBackgroundInfo:
                return state with { VirtualBackground = a.Int(0) ?? state.VirtualBackground };
            case AutoFramingInfo:
                return state with { AutoFraming = a.Int(0) ?? state.AutoFraming };
            default:
                return state;
        }
    }

    // ---- Value tables (labels shown in sub-actions) ----

    public static readonly string[] Views = ["86°", "78°", "65°"];

    public static readonly string[] TinyTrackingModes = ["Headroom", "Standard", "Motion"];

    public static readonly string[] VirtualBackgrounds = ["Disable", "Blur", "Green screen", "Replacement"];

    public static readonly string[] AutoFramingModes = ["Disable", "Single", "Group"];

    public const string TinyAiModesHelp =
        "Tiny 2: 0 no tracking, 1 normal, 2 upper body, 3 close-up, 4 headless, 5 lower body, 6 desk, 7 whiteboard, 8 hand, 9 group " +
        "(Tiny 2 Lite and Tiny SE only 0, 1, 2, 3, 9). Tiny 3 / Tiny 3 Lite: 0 human single, 1 human group, 2 voice tracking, 3 desk, 4 hand, 5 whiteboard.";

    public const string TailAiModesHelp =
        "Tail Air: 0 no tracking, 1 normal, 2 upper body, 3 close-up, 4 headless, 5 lower body, 6 animal, 7 group " +
        "(direct to the camera: 0, 1, 2, 3, 6, 7). Tail 2 / Tail 2s: 0 human single, 1 human group, 2 animal normal, 3 animal close-up " +
        "(direct to the camera: 0 single, 1 multi).";

    public const string TrackingModesHelp = "Tiny 2 / Tiny 2 Lite / Tiny SE only have Standard and Motion.";

    public const string TrackingSpeedHelp =
        "Tiny 3 / Tiny 3 Lite and Tail Air: 0 slow, 1 standard, 2 fast. Tail 2 / Tail 2s: 0 super lazy, 1 lazy, 2 slow, 3 fast, 4 crazy, 5 auto " +
        "(direct to a Tail 2: 5 = custom).";

    /// <summary>The valid AI mode numbers per command address (Center's Tiny and Tail tables, the Tail sheets direct).</summary>
    public static int[] AiModes(ObsbotSeries series) => series switch
    {
        ObsbotSeries.Tail2Direct => [0, 1],
        ObsbotSeries.TailAirDirect => [0, 1, 2, 3, 6, 7],
        ObsbotSeries.Tail => [0, 1, 2, 3, 4, 5, 6, 7],
        _ => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
    };

    /// <summary>The highest preset tracking speed: Center's Tiny command 0-2, Center's Tail command 0-5, Tail Air 0-2, Tail 2 0-5.</summary>
    public static int MaxTrackingSpeed(ObsbotSeries series) => series is ObsbotSeries.Tail or ObsbotSeries.Tail2Direct ? 5 : 2;

    /// <summary>Image settings sub-action: label → (address, value count, floats?).</summary>
    public static readonly IReadOnlyDictionary<string, (string Address, int Values, bool Floats, string Help)> ImageSettings =
        new Dictionary<string, (string, int, bool, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto focus"] = (General + "SetAutoFocus", 1, false, "1 auto focus, 0 manual (Tiny and Meet series)"),
            ["Manual focus"] = (General + "SetManualFocus", 1, false, "0-100"),
            ["Tail focus mode"] = (Tail + "SetFocusMode", 2, false, "mode (0 AF-S, 1 AF-C, 2 MF), manual focus 0-100 (used with MF)"),
            ["Auto exposure"] = (General + "SetAutoExposure", 1, false, "1 auto, 0 manual"),
            ["Exposure compensation"] = (General + "SetExposureCompensate", 1, false, "-30..30 = -3.0..3.0 EV in steps such as -3, 0, 3, 7, 10"),
            ["Shutter speed"] = (General + "SetShutterSpeed", 1, false,
                "denominator, e.g. 100 for 1/100 (6400 ... 3); the sheet types it as an int, so 12.5, 6.25 and 2.5 are sent rounded"),
            ["ISO"] = (General + "SetISO", 1, false, "100-6400"),
            ["Auto white balance"] = (General + "SetAutoWhiteBalance", 1, false, "1 auto, 0 manual"),
            ["Color temperature"] = (General + "SetColorTemperature", 1, false, "2000-10000 K (within the camera's range)"),
            ["White balance shift"] = (General + "SetWhiteBalanceShift", 2, true, "blue-amber, green-magenta: -7.0..7.0 in 0.25 steps (Tiny 3, Tiny 3 Lite, Meet Flip)"),
            ["Red/blue gain"] = (General + "SetRBGain", 2, false, "red gain, blue gain: 0-255 each (Tiny 3, Tiny 3 Lite, Meet Flip)"),
        };
}
