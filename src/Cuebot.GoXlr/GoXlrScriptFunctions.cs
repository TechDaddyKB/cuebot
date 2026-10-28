using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Scripting;
using Cuebot.GoXlr.Common;
using Cuebot.GoXlr.Model;

namespace Cuebot.GoXlr;

/// <summary>GoXLR functions for user scripts (goxlr.*).</summary>
internal sealed class GoXlrScriptFunctions(GoXlrService g) : IScriptFunctionProvider
{
    private static double Number(JsonElement a, string name, double? fallback = null) =>
        a.GetElement(name) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble()
        : double.TryParse(a.GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : fallback ?? throw new ArgumentException($"{name} must be a number");

    /// <summary>A JSON value passed either as an object/array/string or as a JSON string holding it.</summary>
    private static JsonNode Json(JsonElement a, string name)
    {
        var e = a.GetElement(name) ?? throw new ArgumentException($"{name} is required");
        if (e.ValueKind == JsonValueKind.String && e.GetString() is { } text && text.TrimStart() is ['{' or '[' or '"', ..])
            return JsonNode.Parse(text) ?? throw new ArgumentException($"{name} must not be null");
        return JsonNode.Parse(e.GetRawText()) ?? throw new ArgumentException($"{name} must not be null");
    }

    private static object Mixer(GoXlrMixer m) => new
    {
        serial = m.Serial, deviceType = m.DeviceType, profile = m.ProfileName, micProfile = m.MicProfileName, micType = m.MicType,
        faders = m.Faders.Values.Select(f => new { name = f.Name, channel = f.Channel, muteState = f.MuteState, muted = f.Muted, muteFunction = f.MuteFunction }).ToArray(),
        volumes = m.Volumes.ToDictionary(v => v.Key, v => Protocol.GoXlrNames.Percent(v.Value)),
        cough = new { muteState = m.Cough.MuteState, muted = m.Cough.Muted, isToggle = m.Cough.IsToggle },
        effects = m.Effects is { } e
            ? new { fx = e.FxEnabled, megaphone = e.MegaphoneEnabled, robot = e.RobotEnabled, hardTune = e.HardTuneEnabled, preset = e.ActivePreset, presetName = e.PresetName(e.ActivePreset) }
            : null,
        samplerBank = m.Sampler?.ActiveBank,
    };

    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("goxlr.isConnected", "True while the GoXLR Utility is connected", [], (_, _, _) => Task.FromResult<object?>(g.IsConnected)),
        new("goxlr.connect", "Connect to the GoXLR Utility (returns at once; keeps trying in the background)", [], async (_, _, ct) =>
        {
            await g.ConnectAsync(ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.disconnect", "Disconnect from the GoXLR Utility", [], async (_, _, _) =>
        {
            await g.DisconnectAsync().ConfigureAwait(false);
            return true;
        }),
        new("goxlr.getStatus", "Connection state, utility version and the connected GoXLRs", [], (_, _, _) =>
        {
            var s = g.Status;
            return Task.FromResult<object?>(new
            {
                isConnected = g.IsConnected, status = g.StatusMessage, url = g.Url.ToString(), daemonVersion = s.DaemonVersion,
                devices = s.Mixers.Select(Mixer).ToArray(), profiles = s.Profiles, micProfiles = s.MicProfiles, presets = s.Presets,
            });
        }),
        new("goxlr.getRawStatus", "The utility's whole status object as JSON text", [], (_, _, _) => Task.FromResult<object?>(g.RawStatus)),
        new("goxlr.setVolume", "Set a channel's volume in percent (relative: add to it); returns the new percent", ["channel", "volume", "relative"], async (_, a, ct) =>
            await g.SetVolumeAsync(a.GetString("channel"), Number(a, "volume"), a.GetBool("relative"), ct).ConfigureAwait(false)),
        new("goxlr.setFaderMute", "Mute a fader (A-D or the channel on it): Mute, Mute to all, Unmute, Toggle; returns the mute state", ["target", "state"], async (_, a, ct) =>
            await g.SetFaderMuteAsync(a.GetString("target"), GoXlrService.ParseMuteAction(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("goxlr.setFaderChannel", "Assign a channel to a fader", ["fader", "channel"], async (_, a, ct) =>
        {
            await g.SetFaderChannelAsync(a.GetString("fader"), a.GetString("channel"), ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.setCoughMute", "Cough / mic mute: Mute, Mute to all, Unmute, Toggle; returns the mute state", ["state"], async (_, a, ct) =>
            await g.SetCoughMuteAsync(GoXlrService.ParseMuteAction(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("goxlr.loadProfile", "Load a profile (remember: default true)", ["name", "remember"], async (_, a, ct) =>
        {
            await g.LoadProfileAsync(a.GetString("name"), a.GetBool("remember", true), ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.loadMicProfile", "Load a mic profile (remember: default true)", ["name", "remember"], async (_, a, ct) =>
        {
            await g.LoadMicProfileAsync(a.GetString("name"), a.GetBool("remember", true), ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.setEffect", "Turn FX, Megaphone, Robot or Hard Tune On/Off/Toggle; returns the new state", ["effect", "state"], async (_, a, ct) =>
            await g.SetEffectAsync(a.GetString("effect"), Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("goxlr.setEffectPreset", "Select effect preset 1-6 (or by name); returns Preset1..Preset6", ["preset"], async (_, a, ct) =>
            await g.SetEffectPresetAsync(a.GetString("preset"), ct).ConfigureAwait(false)),
        new("goxlr.setSamplerBank", "Select sampler bank A, B or C", ["bank"], async (_, a, ct) =>
        {
            await g.SetSamplerBankAsync(a.GetString("bank"), ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.playSample", "Play a sampler button's next sample (index: 1-based, optional; bank: empty = active)", ["bank", "button", "index"], async (_, a, ct) =>
        {
            int? index = a.GetElement("index") is null ? null : Math.Max(0, (int)Number(a, "index") - 1);
            var (bank, button) = await g.PlaySampleAsync(a.GetString("bank"), a.GetString("button"), index, ct).ConfigureAwait(false);
            return new { bank, button };
        }),
        new("goxlr.stopSample", "Stop a sampler button", ["bank", "button"], async (_, a, ct) =>
        {
            var (bank, button) = await g.StopSampleAsync(a.GetString("bank"), a.GetString("button"), ct).ConfigureAwait(false);
            return new { bank, button };
        }),
        new("goxlr.setRouting", "Route an input to an output: On/Off/Toggle; returns the new state", ["input", "output", "state"], async (_, a, ct) =>
            await g.SetRoutingAsync(a.GetString("input"), a.GetString("output"), Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("goxlr.setGlobalColour", "Set the global lighting colour (hex)", ["colour"], async (_, a, ct) =>
        {
            await g.SetGlobalColourAsync(a.GetString("colour"), ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.command", "Send a raw GoXLR Utility command, e.g. {\"SetFXEnabled\": true} (serial: optional)", ["command", "serial"], async (_, a, ct) =>
        {
            await g.CommandAsync(Json(a, "command"), a.GetString("serial"), ct).ConfigureAwait(false);
            return true;
        }),
        new("goxlr.request", "Send a raw DaemonRequest (\"GetStatus\", {\"Command\": [...]}, {\"Daemon\": ...}); returns the answer's data", ["request"], async (_, a, ct) =>
        {
            var answer = await g.RequestAsync(Json(a, "request"), ct).ConfigureAwait(false);
            return ScriptArgs.ToClr(answer);
        }),
    ];
}
