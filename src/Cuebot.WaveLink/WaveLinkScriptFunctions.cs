using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.WaveLink.Common;

namespace Cuebot.WaveLink;

/// <summary>
/// Wave Link functions for user scripts. Streamer.bot's WaveLink* C# methods map here (WaveLinkInputMute →
/// wavelink.setChannelMute, WaveLinkSetOutputVolume → wavelink.setMixVolume, WaveLinkMicrophoneSetVolume →
/// wavelink.setInputGain, WaveLinkInputFilterEnable → wavelink.setChannelEffect, ...).
/// </summary>
internal sealed class WaveLinkScriptFunctions(WaveLinkService wl) : IScriptFunctionProvider
{
    private static SwitchMode State(JsonElement a, string fallback = "Toggle") => Steps.ParseSwitch(a.GetString("state", fallback));

    private static double Number(JsonElement a, string name) =>
        a.GetElement(name) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble()
        : double.TryParse(a.GetString(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
        : throw new ArgumentException($"{name} must be a number");

    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("wavelink.isConnected", "True while Wave Link is connected", [], (_, _, _) => Task.FromResult<object?>(wl.IsConnected)),
        new("wavelink.connect", "Look for Wave Link and connect (returns at once; keeps trying in the background)", [], async (_, _, ct) =>
        {
            await wl.ConnectAsync(ct).ConfigureAwait(false);
            return wl.IsAvailable;
        }),
        new("wavelink.disconnect", "Disconnect from Wave Link", [], async (_, _, _) =>
        {
            await wl.DisconnectAsync().ConfigureAwait(false);
            return true;
        }),
        new("wavelink.getStatus", "Connection state, detected API (\"3\" or \"legacy\"), port and counts", [], (_, _, _) =>
        {
            var s = wl.Snapshot;
            return Task.FromResult<object?>(new
            {
                isConnected = wl.IsConnected, isAvailable = wl.IsAvailable, status = wl.StatusMessage,
                api = wl.Api switch { WaveLinkApi.V3 => "3", WaveLinkApi.Legacy => "legacy", _ => "" },
                port = wl.Port, interfaceRevision = wl.InterfaceRevision,
                channels = s.Channels.Count, mixes = s.Mixes.Count, inputDevices = s.InputDevices.Count, outputDevices = s.OutputDevices.Count,
            });
        }),
        new("wavelink.getChannels", "Channels (1.x/2.x: inputs) with mute, volume, per-mix state and effects", [], (_, _, _) =>
            Task.FromResult<object?>(wl.Snapshot.Channels.Select(c => new
            {
                id = c.Id, name = c.Name, type = c.Type, muted = c.Muted, volume = c.Volume,
                mixes = c.Mixes.Select(m => new { id = m.MixId, name = m.MixName, muted = m.Muted, volume = m.Volume, filterBypassed = m.FilterBypassed }).ToArray(),
                effects = c.Effects.Select(e => new { id = e.Id, name = e.Name, enabled = e.Enabled }).ToArray(),
                apps = c.Apps.Select(a => new { id = a.Id, name = a.Name }).ToArray(),
            }).ToArray())),
        new("wavelink.getMixes", "Mixes (1.x/2.x: Monitor Mix and Stream Mix)", [], (_, _, _) =>
            Task.FromResult<object?>(wl.Snapshot.Mixes.Select(m => new { id = m.Id, name = m.Name, muted = m.Muted, volume = m.Volume }).ToArray())),
        new("wavelink.getInputDevices", "Input devices (1.x/2.x: microphones) with their inputs", [], (_, _, _) =>
            Task.FromResult<object?>(wl.Snapshot.InputDevices.Select(d => new
            {
                id = d.Id, name = d.Name, type = d.Type,
                inputs = d.Inputs.Select(i => new
                {
                    id = i.Id, name = i.Name, muted = i.Muted, gain = i.Gain, gainDb = i.GainDb, gainLocked = i.GainLocked, micPcMix = i.MicPcMix,
                    lowCut = i.LowCut, clipGuard = i.ClipGuard,
                }).ToArray(),
            }).ToArray())),
        new("wavelink.getOutputDevices", "Output devices, the main one and the monitored mix", [], (_, _, _) =>
        {
            var s = wl.Snapshot;
            return Task.FromResult<object?>(new
            {
                main = s.MainOutputDevice?.Name, monitoredMix = s.MonitoredMixId.Length > 0 ? s.MixName(s.MonitoredMixId) : null,
                devices = s.OutputDevices.Select(d => new
                {
                    id = d.Id, name = d.Name,
                    outputs = d.Outputs.Select(o => new { id = o.Id, name = o.Name, muted = o.Muted, volume = o.Volume, mixId = o.MixId }).ToArray(),
                }).ToArray(),
            });
        }),
        new("wavelink.setChannelMute", "Mute a channel; mix optional (empty = whole channel / both mixes); state Muted, NotMuted or Toggle", ["channel", "mix", "state"],
            async (_, a, ct) => await wl.SetChannelMuteAsync(a.GetString("channel"), a.GetString("mix"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.setChannelVolume", "Set a channel's volume 0-100 (mix optional; relative: true adds)", ["channel", "volume", "mix", "relative"],
            async (_, a, ct) => await wl.SetChannelVolumeAsync(a.GetString("channel"), a.GetString("mix"), Number(a, "volume"), a.GetBool("relative"), ct).ConfigureAwait(false)),
        new("wavelink.setChannelEffect", "Enable/disable a channel effect (1.x/2.x: an input filter)", ["channel", "effect", "state"],
            async (_, a, ct) => await wl.SetChannelEffectAsync(a.GetString("channel"), a.GetString("effect"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.setFilterBypass", "Wave Link 1.x/2.x: bypass an input's filters (mix optional)", ["channel", "mix", "state"],
            async (_, a, ct) => await wl.SetFilterBypassAsync(a.GetString("channel"), a.GetString("mix"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.addAppToChannel", "Wave Link 3: route an application (empty = focused app) to a channel", ["channel", "app"],
            async (_, a, ct) => (await wl.AddAppToChannelAsync(a.GetString("channel"), a.GetString("app"), ct).ConfigureAwait(false)).Name),
        new("wavelink.setMixMute", "Mute a mix; state Muted, NotMuted or Toggle", ["mix", "state"],
            async (_, a, ct) => await wl.SetMixMuteAsync(a.GetString("mix"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.setMixVolume", "Set a mix's volume 0-100 (relative: true adds)", ["mix", "volume", "relative"],
            async (_, a, ct) => await wl.SetMixVolumeAsync(a.GetString("mix"), Number(a, "volume"), a.GetBool("relative"), ct).ConfigureAwait(false)),
        new("wavelink.setInputMute", "Mute an input device / microphone", ["device", "state"],
            async (_, a, ct) => await wl.SetInputMuteAsync(a.GetString("device"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.setInputGain", "Set an input's gain (Wave Link 3: 0-100 percent; 1.x/2.x: the microphone's value)", ["device", "gain", "relative"],
            async (_, a, ct) => await wl.SetInputGainAsync(a.GetString("device"), Number(a, "gain"), a.GetBool("relative"), ct).ConfigureAwait(false)),
        new("wavelink.setMicPcBalance", "Set the Mic/PC balance 0-100", ["device", "balance", "relative"],
            async (_, a, ct) => await wl.SetMicPcMixAsync(a.GetString("device"), Number(a, "balance"), a.GetBool("relative"), ct).ConfigureAwait(false)),
        new("wavelink.setInputLowCut", "Turn an input's low cut filter on/off", ["device", "state"],
            async (_, a, ct) => await wl.SetInputLowCutAsync(a.GetString("device"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.setInputClipGuard", "Turn an input's Clipguard on/off", ["device", "state"],
            async (_, a, ct) => await wl.SetInputClipGuardAsync(a.GetString("device"), State(a), ct).ConfigureAwait(false)),
        new("wavelink.setMainOutput", "Select the main (monitoring) output device; returns its name", ["device"],
            async (_, a, ct) => (await wl.SetMainOutputAsync(a.GetString("device"), ct).ConfigureAwait(false)).Name),
        new("wavelink.setOutputVolume", "Wave Link 3: set an output device's volume 0-100", ["device", "volume", "relative"],
            async (_, a, ct) => await wl.SetOutputVolumeAsync(a.GetString("device"), Number(a, "volume"), a.GetBool("relative"), ct).ConfigureAwait(false)),
        new("wavelink.setMonitoredMix", "Monitor a mix; returns its name", ["mix"],
            async (_, a, ct) => (await wl.SetMonitoredMixAsync(a.GetString("mix"), ct).ConfigureAwait(false)).Name),
        new("wavelink.addOutputToMix", "Wave Link 3: play a mix on an output device", ["device", "mix"], async (_, a, ct) =>
        {
            await wl.AddOutputToMixAsync(a.GetString("device"), a.GetString("mix"), ct).ConfigureAwait(false);
            return true;
        }),
        new("wavelink.switchMonitoring", "Wave Link 1.x/2.x: toggle monitoring between Monitor and Stream mix; returns the new mix", [],
            async (_, _, ct) => (await wl.SwitchMonitoringAsync(ct).ConfigureAwait(false)).Name),
    ];
}
