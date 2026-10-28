using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;
using static Cuebot.StreamApps.Common.Steps;
using P = Cuebot.StreamApps.Obsbot.ObsbotProtocol;

namespace Cuebot.StreamApps.Obsbot;

/// <summary>OBSBOT triggers (Cuebot extras: Streamer.bot documents none for OBSBOT). Type ids must never change.</summary>
internal sealed class ObsbotTriggers : ITriggerProvider
{
    public const string Category = "OBSBOT";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, ObsbotService.ConnectedEvent, "Connected"),
        Trigger(Category, ObsbotService.DisconnectedEvent, "Disconnected"),
        Trigger(Category, ObsbotService.DeviceChangedEvent, "Selected camera changed"),
        Trigger(Category, ObsbotService.SleepStateChangedEvent, "Camera slept or woke",
            [new("stateFilter", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Awake", "Asleep"] }],
            (e, s) => s["stateFilter"] switch
            {
                "Awake" => e.Flag("obsbot.awake") == true,
                "Asleep" => e.Flag("obsbot.awake") == false,
                _ => true,
            }),
        Trigger(Category, ObsbotService.ZoomChangedEvent, "Zoom changed"),
        Trigger(Category, ObsbotService.GimbalMovedEvent, "Gimbal moved"),
        Trigger(Category, ObsbotService.AiTrackingChangedEvent, "AI tracking locked/unlocked",
            [new("stateFilter", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Locked", "Unlocked"] }],
            (e, s) => s["stateFilter"] switch
            {
                "Locked" => e.Flag("obsbot.aiLocked") == true,
                "Unlocked" => e.Flag("obsbot.aiLocked") == false,
                _ => true,
            }),
        Trigger(Category, ObsbotService.MessageEvent, "OSC message",
            [new("address", "Addresses") { Help = "Comma-separated OSC addresses; empty for any message that is not a state reply" }],
            (e, s) => MatchesList(s["address"], e.Text("obsbot.address"))),
    ];
}

/// <summary>OBSBOT sub-actions: one per OBSBOT Center OSC command family. Type ids are stored in actions.json and must never change.</summary>
internal static class ObsbotSubActions
{
    public static readonly string[] TypeIds =
    [
        "obsbot.selectDevice", "obsbot.wakeSleep", "obsbot.resetGimbal", "obsbot.setZoom", "obsbot.zoomLimit", "obsbot.moveGimbal",
        "obsbot.setGimbalPosition", "obsbot.setView", "obsbot.setMirror", "obsbot.recording", "obsbot.snapshot", "obsbot.setImage",
        "obsbot.aiLock", "obsbot.triggerPreset", "obsbot.setAiMode", "obsbot.setTrackingMode", "obsbot.setTrackingSpeed",
        "obsbot.setAxisLock", "obsbot.setVirtualBackground", "obsbot.setAutoFraming", "obsbot.standardMode", "obsbot.getState",
        "obsbot.sendOsc",
    ];

    private static readonly string[] Directions = ["Up", "Down", "Left", "Right", "Stop"];
    private static readonly string[] SeriesChoices = ["Auto", "Tiny", "Tail"];

    public static IReadOnlyList<ISubActionHandler> Create(ObsbotService obsbot)
    {
        ParameterDescriptor Device() => new("device", "Camera")
        {
            Help = "1-4 (OBSBOT Center's device order); empty = the camera selected in OBSBOT Center",
            Suggestions = () => obsbot.Current.Devices.Where(d => d.Connected).Select(d => (d.Index + 1).ToString(CultureInfo.InvariantCulture)),
        };
        ParameterDescriptor Series() => new("series", "Camera series", ParameterKind.Choice)
        {
            DefaultValue = "Auto", Choices = SeriesChoices, Help = "Auto uses the setting on the OBSBOT card (or guesses from the camera name)",
        };
        Task Send(string address, ParameterValues v, CancellationToken ct, params object[] values) =>
            obsbot.SendAsync(P.Command(address, DeviceIndex(v["device"]), values), ct);
        ObsbotSeries SeriesOf(ParameterValues v) => v["series"] switch
        {
            "Tiny" => ObsbotSeries.Tiny,
            "Tail" => ObsbotSeries.Tail,
            "Meet" => ObsbotSeries.Meet,
            _ => obsbot.Series,
        };

        return
        [
            Make("obsbot.selectDevice", "Select camera",
                [new("device", "Camera") { DefaultValue = "1", Help = "1-4 in OBSBOT Center's device list" }],
                (_, v, ct) => obsbot.SendAsync(new OscMessage(P.General + "SelectDevice", DeviceIndex(v["device"]) ?? 0), ct)),
            Make("obsbot.wakeSleep", "Wake / sleep camera", [Switch("Wake", "Sleep"), Device()], async (ctx, v, ct) =>
            {
                var mode = ParseWake(v["state"]);
                var awake = mode == SwitchMode.Toggle
                    ? !((obsbot.Current.Awake ?? (await obsbot.RefreshAsync(ct).ConfigureAwait(false)).Awake) ?? true)
                    : mode == SwitchMode.On;
                await Send(P.General + "WakeSleep", v, ct, awake ? 1 : 0).ConfigureAwait(false);
                ctx.Args["obsbot.awake"] = awake;
            }),
            Make("obsbot.resetGimbal", "Reset gimbal", [Device()], (_, v, ct) => Send(P.General + "ResetGimbal", v, ct, 0)),
            Make("obsbot.setZoom", "Set zoom",
                [
                    new("zoom", "Zoom (0-100)", ParameterKind.Number) { DefaultValue = "0", Help = "Percent of the zoom range" },
                    new("speed", "Speed (optional, 0-11)") { Help = "Empty = instant; 0 default speed, 1-11 faster (newer models only)" },
                    Device(),
                ],
                (_, v, ct) =>
                {
                    var zoom = Range(v["zoom"], "Zoom", 0, 100);
                    return OptionalNumber(v["speed"], "Speed") is { } speed
                        ? Send(P.General + "SetZoomSpeed", v, ct, zoom, (int)Math.Clamp(Math.Round(speed), 0, 11))
                        : Send(P.General + "SetZoom", v, ct, zoom);
                }),
            Make("obsbot.zoomLimit", "Zoom to maximum / minimum",
                [new("limit", "Zoom", ParameterKind.Choice) { DefaultValue = "Max", Choices = ["Max", "Min"] }, Device()],
                (_, v, ct) => Send(P.General + (v["limit"] == "Min" ? "SetZoomMin" : "SetZoomMax"), v, ct, 0)),
            Make("obsbot.moveGimbal", "Move gimbal",
                [
                    new("direction", "Direction", ParameterKind.Choice) { DefaultValue = "Up", Choices = Directions },
                    new("speed", "Speed (1-100)", ParameterKind.Number) { DefaultValue = "50" },
                    new("duration", "Duration (ms, optional)") { Help = "Stop after this long; empty = keep moving until a Stop" },
                    Device(),
                ],
                async (_, v, ct) =>
                {
                    var direction = v["direction"];
                    if (direction == "Stop")
                    {
                        await StopGimbalAsync().ConfigureAwait(false);
                        return;
                    }
                    var address = P.General + "SetGimbal" + (Directions.Contains(direction) ? direction : throw new ArgumentException($"Unknown direction '{direction}'."));
                    await Send(address, v, ct, Range(v["speed"], "Speed", 1, 100)).ConfigureAwait(false);
                    if (OptionalNumber(v["duration"], "Duration") is { } ms and > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(ms, 60_000)), ct).ConfigureAwait(false);
                        await Send(address, v, CancellationToken.None, 0).ConfigureAwait(false);
                    }

                    async Task StopGimbalAsync()
                    {
                        await Send(P.General + "SetGimbalUp", v, ct, 0).ConfigureAwait(false);
                        await Send(P.General + "SetGimbalLeft", v, ct, 0).ConfigureAwait(false);
                    }
                }),
            Make("obsbot.setGimbalPosition", "Set gimbal position",
                [
                    new("pan", "Pan (°, -129..129)", ParameterKind.Number) { DefaultValue = "0" },
                    new("pitch", "Pitch (°, -59..59)", ParameterKind.Number) { DefaultValue = "0" },
                    new("speed", "Speed (0-90)", ParameterKind.Number) { DefaultValue = "50" },
                    new("precise", "Send decimals", ParameterKind.Boolean) { DefaultValue = "False", Help = "Use SetGimMotorDegreeEx (floats) instead of whole degrees" },
                    Device(),
                ],
                (_, v, ct) =>
                {
                    var speed = Math.Clamp(Number(v["speed"], "Speed"), 0, 90);
                    var pan = Math.Clamp(Number(v["pan"], "Pan"), -129, 129);
                    var pitch = Math.Clamp(Number(v["pitch"], "Pitch"), -59, 59);
                    return v.GetBool("precise")
                        ? Send(P.General + "SetGimMotorDegreeEx", v, ct, (float)speed, (float)pan, (float)pitch)
                        : Send(P.General + "SetGimMotorDegree", v, ct, (int)Math.Round(speed), (int)Math.Round(pan), (int)Math.Round(pitch));
                }),
            Make("obsbot.setView", "Set field of view",
                [new("view", "Field of view", ParameterKind.Choice) { DefaultValue = P.Views[0], Choices = P.Views }, Device()],
                (_, v, ct) => Send(P.General + "SetView", v, ct, Index(P.Views, v["view"], "Field of view"))),
            Make("obsbot.setMirror", "Set mirror",
                [new("state", "Mirror", ParameterKind.Choice) { DefaultValue = "On", Choices = ["On", "Off"] }, Device()],
                (_, v, ct) => Send(P.General + "SetMirror", v, ct, ParseSwitch(v["state"]) == SwitchMode.Off ? 0 : 1)),
            Make("obsbot.recording", "Recording",
                [
                    new("target", "Recording", ParameterKind.Choice) { DefaultValue = "PC recording", Choices = ["PC recording", "High-fps recording", "Camera recording (Tail)"] },
                    new("state", "State", ParameterKind.Choice) { DefaultValue = "Start", Choices = ["Start", "Stop"] },
                    Device(),
                ],
                (_, v, ct) =>
                {
                    var on = ParseSwitch(v["state"]) == SwitchMode.Off ? 0 : 1;
                    return v["target"] switch
                    {
                        "High-fps recording" => obsbot.SendAsync(new OscMessage(P.General + "SetHighfpsRecording", on), ct),
                        "Camera recording (Tail)" => Send(P.Tail + "SetRecording", v, ct, on),
                        _ => obsbot.SendAsync(new OscMessage(P.General + "SetPCRecording", on), ct),
                    };
                }),
            Make("obsbot.snapshot", "Take snapshot",
                [new("target", "Snapshot", ParameterKind.Choice) { DefaultValue = "PC snapshot", Choices = ["PC snapshot", "Camera snapshot (Tail)"] }, Device()],
                (_, v, ct) => v["target"] == "Camera snapshot (Tail)"
                    ? Send(P.Tail + "Snapshot", v, ct, 1)
                    : obsbot.SendAsync(new OscMessage(P.General + "PCSnapshot", 1), ct)),
            Make("obsbot.setImage", "Set image setting",
                [
                    new("setting", "Setting", ParameterKind.Choice) { DefaultValue = "Auto exposure", Choices = [.. P.ImageSettings.Keys] },
                    new("value", "Value") { Help = string.Join("; ", P.ImageSettings.Select(kv => $"{kv.Key}: {kv.Value.Help}")) },
                    new("value2", "Second value") { Help = "Only for Tail focus mode, White balance shift and Red/blue gain" },
                    Device(),
                ],
                (_, v, ct) =>
                {
                    if (!P.ImageSettings.TryGetValue(v["setting"], out var setting)) throw new ArgumentException($"Unknown image setting '{v["setting"]}'.");
                    var values = new List<object> { ImageValue(v["value"], "Value", setting.Floats) };
                    if (setting.Values == 2) values.Add(v["value2"].Trim().Length == 0 && setting.Address.EndsWith("SetFocusMode", StringComparison.Ordinal)
                        ? 0 : ImageValue(v["value2"], "Second value", setting.Floats));
                    return Send(setting.Address, v, ct, [.. values]);
                }),
            Make("obsbot.aiLock", "AI tracking lock (Tiny)", [Switch("Lock", "Unlock"), Device()], async (ctx, v, ct) =>
            {
                var mode = ParseLock(v["state"]);
                var locked = mode == SwitchMode.Toggle
                    ? !((obsbot.Current.AiLocked ?? (await obsbot.RefreshAsync(ct).ConfigureAwait(false)).AiLocked) ?? false)
                    : mode == SwitchMode.On;
                await Send(P.Tiny + "ToggleAILock", v, ct, locked ? 1 : 0).ConfigureAwait(false);
                ctx.Args["obsbot.aiLocked"] = locked;
            }),
            Make("obsbot.triggerPreset", "Go to preset position",
                [new("preset", "Preset", ParameterKind.Choice) { DefaultValue = "1", Choices = ["1", "2", "3"] }, Series(), Device()],
                (_, v, ct) => Send((SeriesOf(v) == ObsbotSeries.Tail ? P.Tail : P.Tiny) + "TriggerPreset", v, ct, Range(v["preset"], "Preset", 1, 3) - 1)),
            Make("obsbot.setAiMode", "Set AI mode",
                [new("mode", "AI mode", ParameterKind.Number) { DefaultValue = "1", Help = P.TinyAiModesHelp + " " + P.TailAiModesHelp }, Series(), Device()],
                (_, v, ct) => Send((SeriesOf(v) == ObsbotSeries.Tail ? P.Tail : P.Tiny) + "SetAiMode", v, ct, Range(v["mode"], "AI mode", 0, 9))),
            Make("obsbot.setTrackingMode", "Set tracking mode (Tiny)",
                [new("mode", "Tracking mode", ParameterKind.Choice) { DefaultValue = "Standard", Choices = P.TinyTrackingModes }, Device()],
                (_, v, ct) => Send(P.Tiny + "SetTrackingMode", v, ct, Index(P.TinyTrackingModes, v["mode"], "Tracking mode"))),
            Make("obsbot.setTrackingSpeed", "Set tracking speed",
                [
                    new("speed", "Speed", ParameterKind.Number) { DefaultValue = "1", Help = P.TrackingSpeedHelp + " Pan/tilt axes (Tail 2 only): 1-10." },
                    new("axis", "Axis", ParameterKind.Choice) { DefaultValue = "Both", Choices = ["Both", "Pan", "Tilt"] },
                    Series(),
                    Device(),
                ],
                (_, v, ct) => v["axis"] switch
                {
                    "Pan" => Send(P.Tail + "SetPanTrackingSpeed", v, ct, Range(v["speed"], "Speed", 1, 10)),
                    "Tilt" => Send(P.Tail + "SetTiltTrackingSpeed", v, ct, Range(v["speed"], "Speed", 1, 10)),
                    _ => Send((SeriesOf(v) == ObsbotSeries.Tail ? P.Tail : P.Tiny) + "SetTrackingSpeed", v, ct, Range(v["speed"], "Speed", 0, 5)),
                }),
            Make("obsbot.setAxisLock", "Lock pan / tilt axis (Tail 2)",
                [
                    new("axis", "Axis", ParameterKind.Choice) { DefaultValue = "Pan", Choices = ["Pan", "Tilt"] },
                    new("state", "State", ParameterKind.Choice) { DefaultValue = "Lock", Choices = ["Lock", "Unlock"] },
                    Device(),
                ],
                (_, v, ct) => Send(P.Tail + (v["axis"] == "Tilt" ? "SetTiltAxisLock" : "SetPanAxisLock"), v, ct, ParseLock(v["state"]) == SwitchMode.Off ? 0 : 1)),
            Make("obsbot.setVirtualBackground", "Set virtual background (Meet)",
                [new("mode", "Background", ParameterKind.Choice) { DefaultValue = "Blur", Choices = P.VirtualBackgrounds }, Device()],
                (_, v, ct) => Send(P.Meet + "SetVirtualBackground", v, ct, Index(P.VirtualBackgrounds, v["mode"], "Background"))),
            Make("obsbot.setAutoFraming", "Set auto framing (Meet)",
                [new("mode", "Auto framing", ParameterKind.Choice) { DefaultValue = "Single", Choices = P.AutoFramingModes }, Device()],
                (_, v, ct) => Send(P.Meet + "SetAutoFraming", v, ct, Index(P.AutoFramingModes, v["mode"], "Auto framing"))),
            Make("obsbot.standardMode", "Standard mode (Meet)", [Device()], (_, v, ct) => Send(P.Meet + "SetStandardMode", v, ct, 0)),
            Make("obsbot.getState", "Get camera state", [], async (ctx, _, ct) =>
            {
                ctx.Args["obsbot.isConnected"] = obsbot.IsConnected;
                var state = obsbot.CanSend ? await obsbot.RefreshAsync(ct).ConfigureAwait(false) : ObsbotState.Empty;
                foreach (var (key, value) in ObsbotService.StateArgs(state)) ctx.Args[key] = value;
            }),
            Make("obsbot.sendOsc", "Send OSC message",
                [
                    new("address", "Address") { Help = "e.g. /OBSBOT/WebCam/General/SetZoom" },
                    new("args", "Arguments") { Help = "Space- or comma-separated (12 → int, 1.5 → float, true/false, other text → string) or a JSON array" },
                ],
                (_, v, ct) => obsbot.SendAsync(new OscMessage(Require(v["address"], "Address"), ParseArgs(v["args"])), ct)),
        ];
    }

    /// <summary>"1"-"4" → 0-3; empty → null (the camera selected in Center).</summary>
    internal static int? DeviceIndex(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16
            ? n - 1
            : throw new ArgumentException($"Camera must be a number from 1 to 4 (got '{text}').");
    }

    /// <summary>OSC arguments from text: a JSON array, or tokens (ints, floats, true/false, strings).</summary>
    internal static object?[] ParseArgs(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return [];
        if (t.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(t);
            return doc.RootElement.EnumerateArray().Select(FromJson).ToArray();
        }
        return t.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(FromToken).ToArray();
    }

    internal static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var i) && !e.GetRawText().Contains('.') => i,
        JsonValueKind.Number => (float)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.String => e.GetString(),
        _ => e.GetRawText(),
    };

    private static object FromToken(string token)
    {
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
        if (bool.TryParse(token, out var b)) return b;
        return token.Trim('"');
    }

    private static SwitchMode ParseWake(string value) => value.Trim().ToLowerInvariant() switch
    {
        "wake" or "awake" => SwitchMode.On,
        "sleep" or "asleep" => SwitchMode.Off,
        var other => ParseSwitch(other),
    };

    private static SwitchMode ParseLock(string value) => value.Trim().ToLowerInvariant() switch
    {
        "lock" or "locked" => SwitchMode.On,
        "unlock" or "unlocked" => SwitchMode.Off,
        var other => ParseSwitch(other),
    };

    private static double Number(string text, string label) => OptionalNumber(text, label) ?? throw new ArgumentException($"{label} is required.");

    private static int Range(string text, string label, int min, int max)
    {
        var n = (int)Math.Round(Number(text, label));
        return n >= min && n <= max ? n : throw new ArgumentException($"{label} must be between {min} and {max} (got {n}).");
    }

    private static object ImageValue(string text, string label, bool floats) =>
        floats ? (object)(float)Number(text, label) : (int)Math.Round(Number(text, label));

    private static int Index(string[] choices, string value, string label)
    {
        var i = Array.FindIndex(choices, c => string.Equals(c, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : int.TryParse(value, out var n) && n >= 0 && n < choices.Length ? n : throw new ArgumentException($"Unknown {label.ToLowerInvariant()} '{value}'.");
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new StreamAppSubAction(typeId, ObsbotTriggers.Category, name, parameters, run);
}

/// <summary>OBSBOT functions for user scripts.</summary>
internal sealed class ObsbotScriptFunctions(ObsbotService obsbot) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("obsbot.isConnected", "True while OBSBOT Center answers (UDP) or the TCP connection is up", [], (_, _, _) => Task.FromResult<object?>(obsbot.IsConnected)),
        new("obsbot.connect", "Connect to OBSBOT Center (keeps retrying in the background)", [], async (_, _, ct) =>
        {
            await obsbot.ConnectAsync(ct).ConfigureAwait(false);
            return true;
        }),
        new("obsbot.disconnect", "Disconnect from OBSBOT Center", [], async (_, _, _) =>
        {
            await obsbot.DisconnectAsync().ConfigureAwait(false);
            return true;
        }),
        new("obsbot.getState", "Fresh camera state: zoom, fov, pitch, yaw, AI lock, presets, devices", [], async (_, _, ct) =>
            ObsbotService.StateArgs(await obsbot.RefreshAsync(ct).ConfigureAwait(false))),
        new("obsbot.send", "Send any OSC message, e.g. {address:'/OBSBOT/WebCam/General/SetZoom', args:[50]}", ["address", "args"], async (_, a, ct) =>
        {
            var args = a.GetElement("args") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(ObsbotSubActions.FromJson).ToArray() : [];
            await obsbot.SendAsync(new OscMessage(Require(a.GetString("address"), "address"), args), ct).ConfigureAwait(false);
            return true;
        }),
        new("obsbot.setZoom", "Zoom 0-100 (device 1-4 optional)", ["zoom", "device"], async (_, a, ct) =>
        {
            await obsbot.SendAsync(P.Command(P.General + "SetZoom", ObsbotSubActions.DeviceIndex(a.GetString("device")),
                (int)Math.Clamp(Math.Round(Num(a, "zoom")), 0, 100)), ct).ConfigureAwait(false);
            return true;
        }),
        new("obsbot.setGimbal", "Point the gimbal: pan (-129..129), pitch (-59..59), speed (0-90)", ["pan", "pitch", "speed", "device"], async (_, a, ct) =>
        {
            await obsbot.SendAsync(P.Command(P.General + "SetGimMotorDegree", ObsbotSubActions.DeviceIndex(a.GetString("device")),
                (int)Math.Clamp(Math.Round(Num(a, "speed", 50)), 0, 90), (int)Math.Clamp(Math.Round(Num(a, "pan")), -129, 129),
                (int)Math.Clamp(Math.Round(Num(a, "pitch")), -59, 59)), ct).ConfigureAwait(false);
            return true;
        }),
        new("obsbot.triggerPreset", "Go to preset 1-3", ["preset", "device"], async (_, a, ct) =>
        {
            var series = obsbot.Series == ObsbotSeries.Tail ? P.Tail : P.Tiny;
            await obsbot.SendAsync(P.Command(series + "TriggerPreset", ObsbotSubActions.DeviceIndex(a.GetString("device")),
                (int)Math.Clamp(Math.Round(Num(a, "preset", 1)), 1, 3) - 1), ct).ConfigureAwait(false);
            return true;
        }),
    ];

    private static double Num(JsonElement a, string name, double fallback = 0) =>
        a.GetElement(name) is { } e
            ? e.ValueKind == JsonValueKind.Number ? e.GetDouble()
            : double.TryParse(e.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"{name} must be a number")
            : fallback;
}
