using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Elgato.Common;
using static Cuebot.Elgato.Common.Steps;
using E = Cuebot.Elgato.CameraHub.CameraHubEvents;

namespace Cuebot.Elgato.CameraHub;

/// <summary>Camera Hub triggers that direct webcam control can produce. Type ids are stored in actions.json and must never change.</summary>
internal sealed class CameraHubTriggers(CameraHubService cameraHub) : ITriggerProvider
{
    public const string Category = "Elgato Camera Hub";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor[] WebcamFilter() =>
        [
            new("device", "Webcams")
            {
                Help = "Comma-separated webcam names; leave empty for any webcam",
                Suggestions = () => cameraHub.Webcams.Select(w => w.Name).Distinct(),
            },
        ];
        bool Device(BotEvent e, ParameterValues s) => MatchesList(s["device"], e.Text("deviceId"), e.Text("deviceName"));
        EventTriggerType W(string type, string name) => Trigger(Category, type, "Webcam " + name, WebcamFilter(), Device);

        return
        [
            Trigger(Category, E.Connected, "Connected"),
            Trigger(Category, E.Disconnected, "Disconnected"),
            W(E.WebcamConnected, "Connected"),
            W(E.WebcamDisconnected, "Disconnected"),
            W(E.AntiFlickerChanged, "Anti Flicker Changed"),
            W(E.AutoExposureDisabled, "Auto Exposure Disabled"),
            W(E.AutoExposureEnabled, "Auto Exposure Enabled"),
            W(E.AutoFocusDisabled, "Auto Focus Disabled"),
            W(E.AutoFocusEnabled, "Auto Focus Enabled"),
            W(E.AutoWhiteBalanceDisabled, "Auto White Balance Disabled"),
            W(E.AutoWhiteBalanceEnabled, "Auto White Balance Enabled"),
            W(E.BrightnessChanged, "Brightness Changed"),
            W(E.ContrastChanged, "Contrast Changed"),
            W(E.DeviceOrientationChanged, "Device Orientation Changed"),
            W(E.Flipped, "Flipped"),
            W(E.FocusChanged, "Focus Changed"),
            W(E.LiveShutterSpeedChanged, "Live Shutter Speed Changed"),
            W(E.LiveWhiteBalanceChanged, "Live White Balance Changed"),
            W(E.Mirrored, "Mirrored"),
            W(E.PanChanged, "Pan Changed"),
            W(E.SaturationChanged, "Saturation Changed"),
            W(E.SharpnessChanged, "Sharpness Changed"),
            W(E.ShutterSpeedChanged, "Shutter Speed Changed"),
            W(E.TiltChanged, "Tilt Changed"),
            W(E.WhiteBalanceChanged, "White Balance Changed"),
            W(E.ZoomChanged, "Zoom Changed"),
        ];
    }
}

/// <summary>Camera Hub webcam sub-actions backed by UVC controls. Type ids are stored in actions.json and must never change.</summary>
internal static class CameraHubSubActions
{
    public const string Category = "Elgato Camera Hub";

    private static readonly (string Id, string Name, WebcamControl Control, string Label, string Default, string Help)[] Numbers =
    [
        ("elgato.cameraHub.webcam.setBrightness", "Webcam Set Brightness", WebcamControl.Brightness, "Brightness (0-100 %)", "50", ""),
        ("elgato.cameraHub.webcam.setContrast", "Webcam Set Contrast", WebcamControl.Contrast, "Contrast (0-100 %)", "50", ""),
        ("elgato.cameraHub.webcam.setSaturation", "Webcam Set Saturation", WebcamControl.Saturation, "Saturation (0-100 %)", "50", ""),
        ("elgato.cameraHub.webcam.setSharpness", "Webcam Set Sharpness", WebcamControl.Sharpness, "Sharpness", "2", "In the camera's own steps (Facecam: 0-4)"),
        ("elgato.cameraHub.webcam.setFocus", "Webcam Set Focus", WebcamControl.Focus, "Focus (0-100 %)", "50", "Turns off auto focus first on most cameras"),
        ("elgato.cameraHub.webcam.setPan", "Webcam Set Pan", WebcamControl.Pan, "Pan (0-100 %)", "50", "Only moves while zoomed in"),
        ("elgato.cameraHub.webcam.setTilt", "Webcam Set Tilt", WebcamControl.Tilt, "Tilt (0-100 %)", "50", "Only moves while zoomed in"),
        ("elgato.cameraHub.webcam.setWhiteBalance", "Webcam Set White Balance", WebcamControl.WhiteBalance, "White balance (K)", "5000",
            "Kelvin, 2800-12500 on Facecam; needs auto white balance off"),
        ("elgato.cameraHub.webcam.setZoom", "Webcam Set Zoom", WebcamControl.Zoom, "Zoom (100-400 %)", "100", "100 % = widest"),
    ];

    private static readonly (string Id, string Name, WebcamControl Control)[] Switches =
    [
        ("elgato.cameraHub.webcam.setAutoWhiteBalance", "Webcam Set Auto White Balance", WebcamControl.AutoWhiteBalance),
        ("elgato.cameraHub.webcam.setAutoFocus", "Webcam Set Auto Focus", WebcamControl.AutoFocus),
        ("elgato.cameraHub.webcam.setAutoExposure", "Webcam Set Auto Exposure", WebcamControl.AutoExposure),
    ];

    public static readonly string[] TypeIds =
        [.. Numbers.Select(n => n.Id), .. Switches.Select(s => s.Id), "elgato.cameraHub.webcam.setAntiFlicker", "elgato.cameraHub.webcam.getSettings"];

    public static IReadOnlyList<ISubActionHandler> Create(CameraHubService cameraHub)
    {
        ParameterDescriptor Device() => new("device", "Webcam")
        {
            Help = "Webcam name (comma-separated for several, All for every webcam); empty = the first webcam",
            Suggestions = () => cameraHub.Webcams.Select(w => w.Name).Distinct(),
        };

        var handlers = new List<ISubActionHandler>();
        foreach (var n in Numbers)
        {
            var parameters = new List<ParameterDescriptor>
            {
                Device(),
                new("value", n.Label, ParameterKind.Number) { DefaultValue = n.Default, Help = n.Help.Length > 0 ? n.Help : null },
                Adjustment(),
            };
            handlers.Add(Make(n.Id, n.Name, parameters, async (ctx, v, ct) =>
            {
                ctx.Args["webcamValue"] = await cameraHub.SetAsync(v["device"], n.Control, Number(v["value"], n.Label), Bool(v["adjustment"]), ct)
                    .ConfigureAwait(false);
            }));
        }
        foreach (var s in Switches)
        {
            handlers.Add(Make(s.Id, s.Name, [Device(), Switch("Enabled", "Disabled", key: "mode", label: "Mode")], async (ctx, v, ct) =>
                ctx.Args["webcamValue"] = await cameraHub.SetSwitchAsync(v["device"], s.Control, ParseSwitch(v["mode"]), ct).ConfigureAwait(false)));
        }
        handlers.Add(Make("elgato.cameraHub.webcam.setAntiFlicker", "Webcam Set Anti Flicker",
            [Device(), new("antiFlicker", "Anti-flicker", ParameterKind.Choice) { DefaultValue = "60 Hz", Choices = AntiFlicker.Names }],
            async (ctx, v, ct) =>
            {
                await cameraHub.SetAsync(v["device"], WebcamControl.AntiFlicker, AntiFlicker.Parse(v["antiFlicker"]), false, ct).ConfigureAwait(false);
                ctx.Args["webcamValue"] = v["antiFlicker"];
            }));
        handlers.Add(Make("elgato.cameraHub.webcam.getSettings", "Webcam Get Settings", [Device()], (ctx, v, _) =>
        {
            var webcam = cameraHub.Resolve(v["device"])[0];
            foreach (var (key, value) in CameraHubScript.Describe(webcam)) ctx.Args[key] = value;
            return Task.CompletedTask;
        }));
        return handlers;
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) => new ElgatoSubAction(typeId, Category, name, parameters, run);
}

public static class CameraHubScript
{
    /// <summary>A webcam's settings under Streamer.bot's trigger variable names.</summary>
    public static Dictionary<string, object?> Describe(Webcam webcam)
    {
        var args = new Dictionary<string, object?> { ["deviceId"] = webcam.Id, ["deviceName"] = webcam.Name, ["devicePath"] = webcam.Device.Path };
        void Put(string name, WebcamControl control, Func<int, object?>? map = null)
        {
            if (webcam.Display(control) is { } value) args[name] = map is null ? value : map(value);
        }
        Put("brightness", WebcamControl.Brightness);
        Put("contrast", WebcamControl.Contrast);
        Put("saturation", WebcamControl.Saturation);
        Put("sharpness", WebcamControl.Sharpness);
        Put("focus", WebcamControl.Focus);
        Put("pan", WebcamControl.Pan);
        Put("tilt", WebcamControl.Tilt);
        Put("whiteBalance", WebcamControl.WhiteBalance);
        Put("zoomValue", WebcamControl.Zoom);
        Put("zoomPercent", WebcamControl.Zoom, v => $"{v}%");
        Put("autoFocus", WebcamControl.AutoFocus, v => v != 0);
        Put("autoWhiteBalance", WebcamControl.AutoWhiteBalance, v => v != 0);
        Put("autoExposure", WebcamControl.AutoExposure, v => v != 0);
        Put("shutterSpeed", WebcamControl.Exposure, v => WebcamUnits.ShutterSpeed(v));
        Put("antiFlicker", WebcamControl.AntiFlicker, v => AntiFlicker.Name(v));
        Put("mirrored", WebcamControl.Mirror, v => v != 0);
        Put("flipped", WebcamControl.Flip, v => v != 0);
        if (webcam.Has(WebcamControl.Zoom)) args["maximumZoom"] = WebcamUnits.MaxZoomPercent;
        return args;
    }
}
