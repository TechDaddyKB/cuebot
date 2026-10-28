using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Common;
using Cuebot.Elgato.Lights;
using Cuebot.Elgato.Studio;

namespace Cuebot.Elgato;

/// <summary>Elgato light and webcam functions for user scripts.</summary>
internal sealed class ElgatoScriptFunctions(ElgatoLightsService lights, CameraHubService cameraHub) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("elgato.lights.list", "Every known light: id, name, product, address, online, on, brightness, temperature (K), hue, saturation", [],
            (_, _, _) => Task.FromResult<object?>(lights.Lights.Select(l => l.ToArgs()).ToArray())),
        new("elgato.lights.isRunning", "True while lights are being polled", [], (_, _, _) => Task.FromResult<object?>(lights.IsRunning)),
        new("elgato.lights.start", "Start discovery and polling", [], async (_, _, _) =>
        {
            await lights.StartPollingAsync().ConfigureAwait(false);
            return true;
        }),
        new("elgato.lights.discover", "Send an mDNS query now; returns the lights that were added", [], async (_, _, ct) =>
            (await lights.DiscoverAsync(ct).ConfigureAwait(false)).Select(l => l.ToArgs()).ToArray()),
        new("elgato.lights.add", "Add a light by IP address or host[:port]; returns it", ["address"], async (_, a, ct) =>
            (await lights.AddLightAsync(a.GetString("address"), ct).ConfigureAwait(false)).ToArgs()),
        new("elgato.lights.setPower", "Power On, Off or Toggle (device: name, serial, IP or All); returns the lights", ["device", "state"], async (_, a, ct) =>
            Result(await lights.SetPowerAsync(a.GetString("device"), Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false))),
        new("elgato.lights.setBrightness", "Brightness 0-100 (adjust: true adds to the current value)", ["device", "brightness", "adjust"], async (_, a, ct) =>
            Result(await lights.SetBrightnessAsync(a.GetString("device"), Num(a, "brightness"), a.GetBool("adjust"), ct).ConfigureAwait(false))),
        new("elgato.lights.setTemperature", "Temperature in Kelvin 2900-7000 (adjust: true adds Kelvin)", ["device", "temperature", "adjust"], async (_, a, ct) =>
            Result(await lights.SetTemperatureAsync(a.GetString("device"), Num(a, "temperature"), a.GetBool("adjust"), ct).ConfigureAwait(false))),
        new("elgato.lights.setColor", "Hue 0-360 and saturation 0-100 (Light Strip)", ["device", "hue", "saturation"], async (_, a, ct) =>
            Result(await lights.SetColorAsync(a.GetString("device"), Num(a, "hue"), Num(a, "saturation"), ct).ConfigureAwait(false))),
        new("elgato.lights.identify", "Blink the light(s)", ["device"], async (_, a, ct) =>
        {
            await lights.IdentifyAsync(a.GetString("device"), ct).ConfigureAwait(false);
            return true;
        }),
        new("elgato.lights.rename", "Set a light's display name", ["device", "name"], async (_, a, ct) =>
        {
            await lights.RenameAsync(a.GetString("device"), a.GetString("name"), ct).ConfigureAwait(false);
            return true;
        }),

        new("elgato.cameraHub.isAvailable", "True when webcam control works on this system (Linux V4L2)", [],
            (_, _, _) => Task.FromResult<object?>(cameraHub.IsAvailable)),
        new("elgato.cameraHub.webcams", "Every webcam with its settings (Streamer.bot variable names)", [],
            (_, _, _) => Task.FromResult<object?>(cameraHub.Webcams.Select(CameraHubScript.Describe).ToArray())),
        new("elgato.cameraHub.set", "Set a control: brightness, contrast, saturation, sharpness, focus, pan, tilt, whiteBalance, zoom, autoFocus, "
                                    + "autoWhiteBalance, autoExposure, antiFlicker, mirror, flip; returns the new value", ["device", "control", "value", "adjust"],
            async (_, a, ct) =>
            {
                var control = Enum.TryParse<WebcamControl>(a.GetString("control"), ignoreCase: true, out var c)
                    ? c
                    : throw new ArgumentException($"Unknown control '{a.GetString("control")}'.");
                var value = control == WebcamControl.AntiFlicker && a.GetElement("value") is { ValueKind: JsonValueKind.String } s
                    ? AntiFlicker.Parse(s.GetString() ?? "")
                    : Num(a, "value");
                return await cameraHub.SetAsync(a.GetString("device"), control, value, a.GetBool("adjust"), ct).ConfigureAwait(false);
            }),

        new("elgato.studio.isRunning", "True while the Elgato Studio app is running on this PC (it has no control API)", [],
            (_, _, _) => Task.FromResult<object?>(ElgatoAppPresence.IsRunning(ElgatoAppPresence.StudioProcesses))),
    ];

    private static object Result(IReadOnlyList<ElgatoLight> changed) => changed.Select(l => l.ToArgs()).ToArray();

    private static double Num(JsonElement args, string name) => args.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s => Steps.Number(s.GetString() ?? "", name),
        _ => throw new ArgumentException($"{name} must be a number."),
    };
}
