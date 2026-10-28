using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Elgato.Common;
using static Cuebot.Elgato.Common.Steps;
using E = Cuebot.Elgato.Lights.LightEvents;

namespace Cuebot.Elgato.Lights;

/// <summary>Control Center triggers (category "Elgato Control Center"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class LightTriggers(ElgatoLightsService lights) : ITriggerProvider
{
    public const string Category = "Elgato Control Center";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor[] DeviceFilter() =>
        [
            new("device", "Devices")
            {
                Help = "Comma-separated light names, serial numbers or IP addresses; leave empty for any light",
                Suggestions = () => lights.Lights.Select(l => l.Name).Distinct(),
            },
        ];
        bool Device(BotEvent e, ParameterValues s) =>
            MatchesList(s["device"], e.Text("deviceId"), e.Text("deviceName"), e.Text("ipAddress"));
        EventTriggerType T(string type, string name) => Trigger(Category, type, name, DeviceFilter(), Device);

        return
        [
            T(E.BrightnessChanged, "Brightness Changed"),
            T(E.DeviceAdded, "Device Added"),
            T(E.DeviceConfigurationChanged, "Device Configuration Changed"),
            T(E.DevicePowerStateChanged, "Device Power State Changed"),
            T(E.DevicePoweredOff, "Device Powered Off"),
            T(E.DevicePoweredOn, "Device Powered On"),
            T(E.DeviceRemoved, "Device Removed"),
            T(E.DeviceRenamed, "Device Renamed"),
            T(E.TemperatureChanged, "Temperature Changed"),
            T(E.ColorChanged, "Color Changed (Light Strip)"),
            T(E.DeviceOnline, "Device Reachable"),
            T(E.DeviceOffline, "Device Unreachable"),
        ];
    }
}

/// <summary>Control Center sub-actions, as a table. Type ids are stored in actions.json and must never change.</summary>
internal static class LightSubActions
{
    public const string Category = "Elgato Control Center";

    public static readonly string[] TypeIds =
    [
        "elgato.controlCenter.setBrightness", "elgato.controlCenter.setPowerState", "elgato.controlCenter.setTemperature",
        "elgato.controlCenter.setColor", "elgato.controlCenter.identify", "elgato.controlCenter.getDevice",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(ElgatoLightsService lights)
    {
        ParameterDescriptor Device() => new("device", "Device")
        {
            DefaultValue = "All",
            Help = "Light name, serial number or IP address (comma-separated for several); All = every light",
            Suggestions = () => ["All", .. lights.Lights.Select(l => l.Name).Distinct()],
        };

        return
        [
            Make("elgato.controlCenter.setBrightness", "Set Brightness",
                [Device(), new("brightness", "Brightness (0-100 %)", ParameterKind.Number) { DefaultValue = "50" }, Adjustment()],
                async (ctx, v, ct) => Store(ctx, await lights.SetBrightnessAsync(v["device"], Number(v["brightness"], "Brightness"),
                    Bool(v["adjustment"]), ct).ConfigureAwait(false))),
            Make("elgato.controlCenter.setPowerState", "Set Power State",
                [Device(), new("state", "State", ParameterKind.Choice) { DefaultValue = "Toggle", Choices = ["Off", "On", "Toggle"] }],
                async (ctx, v, ct) => Store(ctx, await lights.SetPowerAsync(v["device"], ParseSwitch(v["state"]), ct).ConfigureAwait(false))),
            Make("elgato.controlCenter.setTemperature", "Set Temperature",
                [
                    Device(),
                    new("temperature", "Temperature (K)", ParameterKind.Number)
                    {
                        DefaultValue = "4500", Help = "2900-7000 Kelvin (or the API's 143-344); with Adjustment, Kelvin to add",
                    },
                    Adjustment(),
                ],
                async (ctx, v, ct) => Store(ctx, await lights.SetTemperatureAsync(v["device"], Number(v["temperature"], "Temperature"),
                    Bool(v["adjustment"]), ct).ConfigureAwait(false))),
            Make("elgato.controlCenter.setColor", "Set Color (Light Strip)",
                [
                    Device(),
                    new("hue", "Hue (0-360)", ParameterKind.Number) { DefaultValue = "0" },
                    new("saturation", "Saturation (0-100)", ParameterKind.Number) { DefaultValue = "100" },
                ],
                async (ctx, v, ct) => Store(ctx, await lights.SetColorAsync(v["device"], Number(v["hue"], "Hue"),
                    Number(v["saturation"], "Saturation"), ct).ConfigureAwait(false))),
            Make("elgato.controlCenter.identify", "Identify (blink)", [Device()],
                (_, v, ct) => lights.IdentifyAsync(v["device"], ct)),
            Make("elgato.controlCenter.getDevice", "Get Device Info", [Device()], (ctx, v, _) =>
            {
                Store(ctx, lights.Resolve(v["device"]));
                return Task.CompletedTask;
            }),
        ];
    }

    /// <summary>The (first) light's state as variables, plus elgatoLightCount.</summary>
    private static void Store(ActionRunContext ctx, IReadOnlyList<ElgatoLight> changed)
    {
        ctx.Args["elgatoLightCount"] = changed.Count;
        if (changed.Count == 0) return;
        foreach (var (key, value) in changed[0].ToArgs()) ctx.Args[key] = value;
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) => new ElgatoSubAction(typeId, Category, name, parameters, run);
}
