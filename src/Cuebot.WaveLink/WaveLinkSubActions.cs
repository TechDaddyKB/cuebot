using Cuebot.Core.Actions;
using Cuebot.WaveLink.Common;
using Cuebot.WaveLink.Model;
using Cuebot.WaveLink.Protocol;
using static Cuebot.WaveLink.Common.Steps;

namespace Cuebot.WaveLink;

/// <summary>
/// Every Wave Link sub-action, as a table: Streamer.bot's 21 Wave Link 3 sub-actions (plus Mute mix and Set main output)
/// and its 16 Wave Link 1.x/2.x ones. Both sets run on either API generation where the operation exists there.
/// Type ids are stored in actions.json and must never change.
/// </summary>
internal static class WaveLinkSubActions
{
    public const string Category = WaveLinkTriggers.Category;
    public const string LegacyCategory = WaveLinkTriggers.LegacyCategory;

    public static readonly string[] TypeIds =
    [
        // Wave Link 3: channels
        "wavelink.addAppToChannel", "wavelink.adjustChannelVolume", "wavelink.getChannelInfo", "wavelink.muteChannel",
        "wavelink.setChannelEffectState", "wavelink.setChannelMixMute", "wavelink.setChannelVolume",
        // inputs
        "wavelink.getInputDeviceInfo", "wavelink.muteInputDevice", "wavelink.setInputClipGuard", "wavelink.setInputGain",
        "wavelink.setInputGainLock", "wavelink.setInputLowCut", "wavelink.setMicPcBalance",
        // mixes
        "wavelink.adjustMixVolume", "wavelink.getMixInfo", "wavelink.setMixVolume", "wavelink.muteMix",
        // outputs
        "wavelink.addOutputToMix", "wavelink.getSelectedOutput", "wavelink.setMonitoredMix", "wavelink.setOutputVolume", "wavelink.setMainOutput",
        // Wave Link 1.x/2.x
        "wavelink.legacy.getFilterState", "wavelink.legacy.getInputInfo", "wavelink.legacy.getMicrophoneInfo", "wavelink.legacy.getOutputVolumes",
        "wavelink.legacy.getSelectedOutput", "wavelink.legacy.muteInput", "wavelink.legacy.muteMicrophone", "wavelink.legacy.muteOutput",
        "wavelink.legacy.setFilterBypass", "wavelink.legacy.setFilterState", "wavelink.legacy.setInputVolume", "wavelink.legacy.setMicrophoneBalance",
        "wavelink.legacy.setMicrophoneGain", "wavelink.legacy.setMicrophoneOutputVolume", "wavelink.legacy.setOutputMonitorDevice",
        "wavelink.legacy.setOutputVolume",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(WaveLinkService wl)
    {
        WaveLinkSnapshot S() => wl.Snapshot;
        WaveLinkSnapshot Live() => wl.IsConnected ? wl.Snapshot : throw new WaveLinkNotConnectedException();

        ParameterDescriptor Channel(string label = "Channel") => new("channel", label)
        {
            Help = "Channel name or id (Wave Link 1.x/2.x: the input)",
            Suggestions = () => S().Channels.Select(c => c.Name),
        };
        ParameterDescriptor Mix(string help, string label = "Mix") => new("mix", label) { Help = help, Suggestions = () => S().Mixes.Select(m => m.Name) };
        ParameterDescriptor Mixer() => new("mix", "Mixer", ParameterKind.Choice)
        {
            DefaultValue = "Both",
            Choices = ["Both", "Local", "Stream"],
            Help = "Local = Monitor Mix",
        };
        ParameterDescriptor OutputMixer() => new("mix", "Mixer", ParameterKind.Choice) { DefaultValue = "Local", Choices = ["Local", "Stream"], Help = "Local = Monitor Mix" };
        ParameterDescriptor Input(string label = "Input device") => new("inputDevice", label)
        {
            Help = "Input device name or id; leave empty when there is only one (Wave Link 1.x/2.x: the microphone)",
            Suggestions = () => S().InputDevices.Select(d => d.Name),
        };
        ParameterDescriptor Output(string label = "Output device") => new("outputDevice", label)
        {
            Help = "Output device name or id",
            Suggestions = () => S().OutputDevices.Select(d => d.Name),
        };
        ParameterDescriptor Effect(string label = "Effect") => new("effect", label)
        {
            Help = "Effect (Wave Link 1.x/2.x: filter) name or id",
            Suggestions = () => S().Channels.SelectMany(c => c.Effects).Select(e => e.Name).Distinct(),
        };
        ParameterDescriptor Volume(string label = "Volume (0-100)", string defaultValue = "100") => new("volume", label, ParameterKind.Number) { DefaultValue = defaultValue };
        ParameterDescriptor Amount() => new("amount", "Change (+/-)", ParameterKind.Number) { DefaultValue = "5", Help = "Percentage points to add (negative to lower)" };
        ParameterDescriptor Relative() => new("relative", "Relative change", ParameterKind.Boolean) { DefaultValue = "false", Help = "Add the value to the current one" };

        static bool IsTrue(string v) => v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || v.Trim() == "1";

        void ChannelArgs(ActionRunContext ctx, string channel)
        {
            var s = wl.Snapshot;
            var c = s.FindChannel(channel);
            var a = WaveLinkArgs.New();
            WaveLinkArgs.Channel(a, c, s);
            foreach (var (k, v) in a) ctx.Args[k] = v;
        }

        return
        [
            // ---- Wave Link 3: channels ----
            Make("wavelink.addAppToChannel", "Add application to channel",
                [Channel(), new("app", "Application") { Help = "Application name or id; leave empty for the focused application" }],
                async (ctx, v, ct) =>
                {
                    var app = await wl.AddAppToChannelAsync(Require(v["channel"], "Channel"), v["app"], ct).ConfigureAwait(false);
                    ctx.Args["waveLinkAppId"] = app.Id;
                    ctx.Args["waveLinkAppName"] = app.Name;
                }),
            Make("wavelink.adjustChannelVolume", "Adjust channel volume", [Channel(), Mix("Mix to adjust; leave empty for the channel's own volume"), Amount()],
                async (ctx, v, ct) => ctx.Args["waveLinkChannelVolume"] = await wl.SetChannelVolumeAsync(Require(v["channel"], "Channel"), v["mix"],
                    Number(v["amount"], "Change"), true, ct).ConfigureAwait(false)),
            Make("wavelink.getChannelInfo", "Get channel information", [Channel()], (ctx, v, _) =>
            {
                Live();
                ChannelArgs(ctx, Require(v["channel"], "Channel"));
                return Task.CompletedTask;
            }),
            Make("wavelink.muteChannel", "Mute channel", [Channel(), Switch("Muted", "Not Muted")],
                async (ctx, v, ct) => ctx.Args["waveLinkChannelMuted"] = await wl.SetChannelMuteAsync(Require(v["channel"], "Channel"), "",
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setChannelEffectState", "Set channel effect state", [Channel(), Effect(), Switch("Enabled", "Disabled")],
                async (ctx, v, ct) => ctx.Args["waveLinkEffectEnabled"] = await wl.SetChannelEffectAsync(Require(v["channel"], "Channel"),
                    Require(v["effect"], "Effect"), ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setChannelMixMute", "Set channel mix mute", [Channel(), Mix("Mix which should be affected"), Switch("Muted", "Not Muted")],
                async (ctx, v, ct) => ctx.Args["waveLinkMixMuted"] = await wl.SetChannelMuteAsync(Require(v["channel"], "Channel"), Require(v["mix"], "Mix"),
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setChannelVolume", "Set channel volume", [Channel(), Mix("Mix to set; leave empty for the channel's own volume"), Volume()],
                async (ctx, v, ct) => ctx.Args["waveLinkChannelVolume"] = await wl.SetChannelVolumeAsync(Require(v["channel"], "Channel"), v["mix"],
                    Number(v["volume"], "Volume", 100), false, ct).ConfigureAwait(false)),

            // ---- Wave Link 3: inputs ----
            Make("wavelink.getInputDeviceInfo", "Get input device information", [Input()], (ctx, v, _) =>
            {
                var s = Live();
                var device = s.FindInputDevice(v["inputDevice"]);
                var a = WaveLinkArgs.New();
                WaveLinkArgs.InputDevice(a, device);
                // Streamer.bot also reports the channel the device feeds (same name).
                if (s.Channels.FirstOrDefault(c => string.Equals(c.Name, device.Name, StringComparison.OrdinalIgnoreCase)
                        || device.Inputs.Any(i => i.Id == c.Id || string.Equals(i.Name, c.Name, StringComparison.OrdinalIgnoreCase))) is { } channel)
                {
                    a["waveLinkInputChannelId"] = channel.Id;
                    a["waveLinkInputChannelName"] = channel.Name;
                    a["waveLinkInputChannelMuted"] = channel.Muted;
                    a["waveLinkInputChannelVolume"] = channel.Volume;
                }
                foreach (var (k, val) in a) ctx.Args[k] = val;
                return Task.CompletedTask;
            }),
            Make("wavelink.muteInputDevice", "Mute input device", [Input(), Switch("Muted", "Not Muted")],
                async (ctx, v, ct) => ctx.Args["waveLinkInputDeviceMuted"] = await wl.SetInputMuteAsync(v["inputDevice"], ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setInputClipGuard", "Set input Clipguard state", [Input(), Switch("Enabled", "Disabled")],
                async (ctx, v, ct) => ctx.Args["waveLinkInputDeviceClipGuard"] = await wl.SetInputClipGuardAsync(v["inputDevice"], ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setInputGain", "Set input gain",
                [Input(), new("gain", "Gain (0-100)", ParameterKind.Number) { DefaultValue = "50", Help = "Percent of the device's gain range" }, Relative()],
                async (ctx, v, ct) => ctx.Args["waveLinkInputDeviceGain"] = await wl.SetInputGainAsync(v["inputDevice"], Number(v["gain"], "Gain"),
                    IsTrue(v["relative"]), ct).ConfigureAwait(false)),
            Make("wavelink.setInputGainLock", "Set input gain lock state", [Input(), Switch("Locked", "Unlocked")],
                async (ctx, v, ct) => ctx.Args["waveLinkInputDeviceGainLocked"] = await wl.SetInputGainLockAsync(v["inputDevice"], ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setInputLowCut", "Set input low cut filter", [Input(), Switch("Enabled", "Disabled")],
                async (ctx, v, ct) => ctx.Args["waveLinkInputDeviceLowCutFilter"] = await wl.SetInputLowCutAsync(v["inputDevice"], ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("wavelink.setMicPcBalance", "Set Mic/PC balance",
                [Input(), new("balance", "Balance (0-100)", ParameterKind.Number) { DefaultValue = "50", Help = "0 = all mic, 100 = all PC" }, Relative()],
                async (ctx, v, ct) => ctx.Args["waveLinkInputDeviceMicPcMix"] = await wl.SetMicPcMixAsync(v["inputDevice"], Number(v["balance"], "Balance"),
                    IsTrue(v["relative"]), ct).ConfigureAwait(false)),

            // ---- Wave Link 3: mixes ----
            Make("wavelink.adjustMixVolume", "Adjust mix volume", [Mix("Mix to adjust"), Amount()],
                async (ctx, v, ct) => ctx.Args["waveLinkMixVolume"] = await wl.SetMixVolumeAsync(Require(v["mix"], "Mix"), Number(v["amount"], "Change"), true, ct).ConfigureAwait(false)),
            Make("wavelink.getMixInfo", "Get mix information", [Mix("Mix to read")], (ctx, v, _) =>
            {
                var a = WaveLinkArgs.New();
                WaveLinkArgs.Mix(a, Live().FindMix(Require(v["mix"], "Mix")));
                foreach (var (k, val) in a) ctx.Args[k] = val;
                return Task.CompletedTask;
            }),
            Make("wavelink.setMixVolume", "Set mix volume", [Mix("Mix to set"), Volume()],
                async (ctx, v, ct) => ctx.Args["waveLinkMixVolume"] = await wl.SetMixVolumeAsync(Require(v["mix"], "Mix"), Number(v["volume"], "Volume", 100), false, ct).ConfigureAwait(false)),
            Make("wavelink.muteMix", "Mute mix", [Mix("Mix to mute"), Switch("Muted", "Not Muted")],
                async (ctx, v, ct) => ctx.Args["waveLinkMixMuted"] = await wl.SetMixMuteAsync(Require(v["mix"], "Mix"), ParseSwitch(v["state"]), ct).ConfigureAwait(false)),

            // ---- Wave Link 3: outputs ----
            Make("wavelink.addOutputToMix", "Add output to device mix", [Output(), Mix("Mix the output device should play")],
                async (ctx, v, ct) =>
                {
                    await wl.AddOutputToMixAsync(Require(v["outputDevice"], "Output device"), Require(v["mix"], "Mix"), ct).ConfigureAwait(false);
                    ctx.Args["waveLinkOutputMixName"] = wl.Snapshot.FindMix(v["mix"]).Name;
                }),
            Make("wavelink.getSelectedOutput", "Get selected output device", [], (ctx, _, _) =>
            {
                var s = Live();
                var device = s.MainOutputDevice;
                ctx.Args["waveLinkOutputDeviceId"] = device?.Id ?? s.MainOutput?.OutputDeviceId ?? "";
                ctx.Args["waveLinkOutputDeviceName"] = device?.Name ?? "";
                ctx.Args["waveLinkOutputId"] = s.MainOutput?.OutputId ?? "";
                ctx.Args["waveLinkOutputMixId"] = s.MonitoredMixId;
                ctx.Args["waveLinkOutputMixName"] = s.MonitoredMixId.Length > 0 ? s.MixName(s.MonitoredMixId) : "";
                return Task.CompletedTask;
            }),
            Make("wavelink.setMonitoredMix", "Set monitored mix", [Mix("Mix to monitor", "Monitored mix")],
                async (ctx, v, ct) => ctx.Args["waveLinkMixName"] = (await wl.SetMonitoredMixAsync(Require(v["mix"], "Monitored mix"), ct).ConfigureAwait(false)).Name),
            Make("wavelink.setOutputVolume", "Set output volume", [Output(), Volume(), Relative()],
                async (ctx, v, ct) => ctx.Args["waveLinkOutputVolume"] = await wl.SetOutputVolumeAsync(Require(v["outputDevice"], "Output device"),
                    Number(v["volume"], "Volume", 100), IsTrue(v["relative"]), ct).ConfigureAwait(false)),
            Make("wavelink.setMainOutput", "Set main output device", [Output()],
                async (ctx, v, ct) => ctx.Args["waveLinkOutputDeviceName"] = (await wl.SetMainOutputAsync(Require(v["outputDevice"], "Output device"), ct).ConfigureAwait(false)).Name),

            // ---- Wave Link 1.x/2.x ----
            Legacy("wavelink.legacy.getFilterState", "Get filter state", [Channel("Input"), Effect("Filter")], (ctx, v, _) =>
            {
                var c = Live().FindChannel(Require(v["channel"], "Input"));
                var f = WaveLinkSnapshot.Find(c.Effects, Require(v["effect"], "Filter"), x => x.Id, x => x.Name, $"filter on input '{c.Name}'");
                ctx.Args["filter.active"] = f.Enabled;
                ctx.Args["filter.id"] = f.Id;
                ctx.Args["filter.name"] = f.Name;
                ctx.Args["filter.pluginId"] = f.PluginId;
                ctx.Args["input.identifier"] = c.Id;
                ctx.Args["input.name"] = c.Name;
                return Task.CompletedTask;
            }),
            Legacy("wavelink.legacy.getInputInfo", "Get input information", [Channel("Input")], (ctx, v, _) =>
            {
                var c = Live().FindChannel(Require(v["channel"], "Input"));
                ctx.Args["input.identifier"] = c.Id;
                ctx.Args["input.name"] = c.Name;
                foreach (var (prefix, id) in new[] { ("local", WaveLinkLegacyProtocol.LocalMixer), ("stream", WaveLinkLegacyProtocol.StreamMixer) })
                {
                    // Wave Link 3 channels have user-named mixes: local = the first, stream = the second.
                    var mix = c.Mix(id) ?? (c.Mixes.Count > (prefix == "local" ? 0 : 1) ? c.Mixes[prefix == "local" ? 0 : 1] : null);
                    ctx.Args[$"input.{prefix}.muted"] = mix?.Muted ?? false;
                    ctx.Args[$"input.{prefix}.volume"] = (int)Math.Round(mix?.Volume ?? 0);
                    ctx.Args[$"input.{prefix}.filterBypass"] = mix?.FilterBypassed ?? false;
                }
                return Task.CompletedTask;
            }),
            Legacy("wavelink.legacy.getMicrophoneInfo", "Get microphone information", [Input("Microphone")], (ctx, v, _) =>
            {
                var device = Live().FindInputDevice(v["inputDevice"]);
                var a = WaveLinkArgs.New();
                WaveLinkArgs.InputDevice(a, device);
                var input = device.Primary;
                a["microphone.identifier"] = device.Id;
                a["microphone.name"] = device.Name;
                a["microphone.gain"] = input?.Gain;
                a["microphone.outputVolume"] = input?.OutputVolume;
                a["microphone.balance"] = input?.MicPcMix;
                a["microphone.lowCut"] = input?.LowCut;
                a["microphone.clipGuard"] = input?.ClipGuard;
                a["microphone.gainLocked"] = input?.GainLocked;
                a["microphone.muted"] = input?.Muted ?? false;
                wl.AddMicrophoneArgs(a, device.Id);
                foreach (var (k, val) in a) ctx.Args[k] = val;
                return Task.CompletedTask;
            }),
            Legacy("wavelink.legacy.getOutputVolumes", "Get output volumes", [], (ctx, _, _) =>
            {
                var s = Live();
                var local = s.MixById(WaveLinkLegacyProtocol.LocalMixer) ?? (s.Mixes.Count > 0 ? s.Mixes[0] : null);
                var stream = s.MixById(WaveLinkLegacyProtocol.StreamMixer) ?? (s.Mixes.Count > 1 ? s.Mixes[1] : null);
                ctx.Args["localVolume"] = (int)Math.Round(local?.Volume ?? 0);
                ctx.Args["streamVolume"] = (int)Math.Round(stream?.Volume ?? 0);
                return Task.CompletedTask;
            }),
            Legacy("wavelink.legacy.getSelectedOutput", "Get selected output", [], (ctx, _, _) =>
            {
                var s = Live();
                ctx.Args["output.identifier"] = s.MainOutputDevice?.Id ?? s.MainOutput?.OutputDeviceId ?? "";
                ctx.Args["output.name"] = s.MainOutputDevice?.Name ?? "";
                return Task.CompletedTask;
            }),
            Legacy("wavelink.legacy.muteInput", "Mute input", [Channel("Input"), Mixer(), Switch("Mute", "Unmute")],
                async (ctx, v, ct) => ctx.Args["input.muted"] = await wl.SetChannelMuteAsync(Require(v["channel"], "Input"), v["mix"],
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.muteMicrophone", "Mute microphone", [Input("Microphone"), Switch("Mute", "Unmute")],
                async (ctx, v, ct) => ctx.Args["microphone.muted"] = await wl.SetInputMuteAsync(v["inputDevice"], ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.muteOutput", "Mute output", [OutputMixer(), Switch("Mute", "Unmute")],
                async (ctx, v, ct) => ctx.Args["output.muted"] = await wl.SetMixMuteAsync(Require(v["mix"], "Mixer"), ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setFilterBypass", "Set filter bypass state", [Channel("Input"), Mixer(), Switch("Bypassed", "Not Bypassed")],
                async (ctx, v, ct) => ctx.Args["input.filterBypass"] = await wl.SetFilterBypassAsync(Require(v["channel"], "Input"), v["mix"],
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setFilterState", "Set filter state", [Channel("Input"), Effect("Filter"), Switch("Enabled", "Disabled")],
                async (ctx, v, ct) => ctx.Args["filter.active"] = await wl.SetChannelEffectAsync(Require(v["channel"], "Input"), Require(v["effect"], "Filter"),
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setInputVolume", "Set input volume", [Channel("Input"), Mixer(), Volume(), Relative()],
                async (ctx, v, ct) => ctx.Args["input.volume"] = await wl.SetChannelVolumeAsync(Require(v["channel"], "Input"), v["mix"],
                    Number(v["volume"], "Volume", 100), IsTrue(v["relative"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setMicrophoneBalance", "Set microphone balance",
                [Input("Microphone"), new("balance", "Balance (0-100)", ParameterKind.Number) { DefaultValue = "50" }, Relative()],
                async (ctx, v, ct) => ctx.Args["microphone.balance"] = await wl.SetMicPcMixAsync(v["inputDevice"], Number(v["balance"], "Balance"),
                    IsTrue(v["relative"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setMicrophoneGain", "Set microphone gain",
                [Input("Microphone"), new("gain", "Gain", ParameterKind.Number) { DefaultValue = "50", Help = "Wave Link 1.x/2.x: the microphone's gain value; Wave Link 3: percent" }, Relative()],
                async (ctx, v, ct) => ctx.Args["microphone.gain"] = await wl.SetInputGainAsync(v["inputDevice"], Number(v["gain"], "Gain"),
                    IsTrue(v["relative"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setMicrophoneOutputVolume", "Set microphone output volume",
                [Input("Microphone"), Volume("Output volume", "50"), Relative()],
                async (ctx, v, ct) => ctx.Args["microphone.outputVolume"] = await wl.SetMicOutputVolumeAsync(v["inputDevice"], Number(v["volume"], "Output volume"),
                    IsTrue(v["relative"]), ct).ConfigureAwait(false)),
            Legacy("wavelink.legacy.setOutputMonitorDevice", "Set output monitor device", [Output()],
                async (ctx, v, ct) => ctx.Args["output.name"] = (await wl.SetMainOutputAsync(Require(v["outputDevice"], "Output device"), ct).ConfigureAwait(false)).Name),
            Legacy("wavelink.legacy.setOutputVolume", "Set output volume", [OutputMixer(), Volume(), Relative()],
                async (ctx, v, ct) => ctx.Args["output.volume"] = await wl.SetMixVolumeAsync(Require(v["mix"], "Mixer"), Number(v["volume"], "Volume", 100),
                    IsTrue(v["relative"]), ct).ConfigureAwait(false)),
        ];
    }

    private static WaveLinkSubAction Make(string id, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) => new(id, Category, name, parameters, run);

    private static WaveLinkSubAction Legacy(string id, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) => new(id, LegacyCategory, name, parameters, run);
}
