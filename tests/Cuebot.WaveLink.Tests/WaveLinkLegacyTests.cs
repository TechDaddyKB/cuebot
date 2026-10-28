using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.WaveLink.Tests.Support;
using Ids = Cuebot.WaveLink.Tests.Support.FakeWaveLink.LegacyIds;

namespace Cuebot.WaveLink.Tests;

public sealed class WaveLinkLegacyTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private WaveLinkService _wl = null!;
    private FakeWaveLink _app = null!;

    public async Task InitializeAsync()
    {
        _env.Network.Listen(1824, s => FakeWaveLink.Legacy(s));
        _wl = _env.Service();
        await _wl.ConnectAsync();
        _app = await _env.Network.AppAsync(1824);
        await Wait.UntilAsync(() => _wl.IsConnected, "connected");
    }

    public async Task DisposeAsync()
    {
        await _wl.DisposeAsync();
        _env.Dispose();
    }

    private ITriggerType Trigger(string id) => new WaveLinkTriggers(_wl).GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string>? settings = null) =>
        trigger.EventTypes.Contains(e.Type)
        && trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    [Fact]
    public void Inputs_become_channels_with_a_monitor_and_a_stream_mix()
    {
        var s = _wl.Snapshot;
        Assert.Equal(["Monitor Mix", "Stream Mix"], s.Mixes.Select(m => m.Name));
        Assert.Equal(80, s.FindMix("Monitor Mix").Volume);
        var music = s.FindChannel("Music");
        Assert.Equal(50, music.Mix(Ids.Local)!.Volume);
        Assert.True(music.Mix(Ids.Stream)!.Muted);
        Assert.False(music.Muted);   // muted in one mix only
        var mic = s.FindChannel(Ids.Mic);
        Assert.Equal("NVIDIA Broadcast", Assert.Single(mic.Effects).Name);
        Assert.Equal("nvafx", mic.Effects[0].PluginId);

        var micDevice = Assert.Single(s.InputDevices);
        Assert.Equal("Wave", micDevice.Type);
        Assert.Equal(75, micDevice.Primary!.Gain);
        Assert.Equal(50, micDevice.Primary.MicPcMix);
        Assert.True(micDevice.Primary.ClipGuard);
        Assert.Equal(["Headset", "Speakers"], s.OutputDevices.Select(d => d.Name));
        Assert.Equal("Headset", s.MainOutputDevice!.Name);
        Assert.Equal(Ids.Local, s.MonitoredMixId);
        Assert.Equal(["getApplicationInfo", "getMicrophoneConfig", "getSwitchState", "getOutputConfig", "getOutputs", "getInputConfigs"],
            _app.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task Input_mute_fires_the_shared_channel_trigger_and_the_legacy_one()
    {
        _app.Notify("inputMuteChanged", new JsonObject { ["identifier"] = Ids.Mic, ["mixerID"] = Ids.Stream, ["value"] = true });
        var e = await _env.WaitForEventAsync(WaveLinkEvents.ChannelMuteChanged);
        Assert.Equal("InputMuteChanged", e.Args["waveLinkLegacyEvent"]);
        Assert.Equal("legacy", e.Args["waveLinkApi"]);
        Assert.Equal("Wave:3", e.Args["waveLinkChannelName"]);
        Assert.Equal("Stream Mix", e.Args["waveLinkMixName"]);
        Assert.Equal(true, e.Args["waveLinkMuted"]);
        Assert.Equal(Ids.Mic, e.Args["input.identifier"]);
        Assert.Equal(true, e.Args["input.stream.muted"]);
        Assert.Equal(false, e.Args["input.local.muted"]);
        Assert.Equal("Stream", e.Args["mixer"]);

        Assert.True(Matches(Trigger(WaveLinkEvents.ChannelMuteChanged), e, new() { ["mix"] = "Stream Mix", ["stateFilter"] = "Muted" }));
        Assert.True(Matches(Trigger("wavelink.legacy.inputMuteChanged"), e, new() { ["channel"] = "wave:3" }));
        Assert.False(Matches(Trigger("wavelink.legacy.inputVolumeChanged"), e));

        // A repeated notification with the same state is not a change.
        _app.Notify("inputMuteChanged", new JsonObject { ["identifier"] = Ids.Mic, ["mixerID"] = Ids.Stream, ["value"] = true });
        _app.Notify("inputVolumeChanged", new JsonObject { ["identifier"] = Ids.Mic, ["mixerID"] = Ids.Local, ["value"] = 64 });
        var volume = await _env.WaitForEventAsync(WaveLinkEvents.ChannelVolumeChanged);
        Assert.Single(_env.Events(WaveLinkEvents.ChannelMuteChanged));
        Assert.Equal(64.0, volume.Args["waveLinkVolume"]);
        Assert.Equal(64, volume.Args["input.local.volume"]);
        Assert.True(Matches(Trigger("wavelink.legacy.inputVolumeChanged"), volume));
        Assert.True(Matches(Trigger(WaveLinkEvents.ChannelVolumeChanged), volume, new() { ["mix"] = "Monitor Mix" }));
    }

    [Fact]
    public async Task Every_legacy_notification_maps_to_its_event()
    {
        async Task<BotEvent> Expect(string method, JsonObject p, string type, string legacy)
        {
            _app.Notify(method, p);
            return await _env.WaitForEventAsync(type, e => (string?)e.Args.GetValueOrDefault("waveLinkLegacyEvent") == legacy);
        }

        var name = await Expect("inputNameChanged", new() { ["identifier"] = Ids.Music, ["value"] = "Spotify" }, WaveLinkEvents.ChannelUpdated, "InputNameChanged");
        Assert.Equal("Spotify", name.Args["waveLinkChannelName"]);
        Assert.Equal("Music", name.Args["waveLinkPreviousName"]);

        var added = await Expect("filterAdded", new() { ["identifier"] = Ids.Music, ["filterID"] = "F2", ["name"] = "Reverb", ["isActive"] = false, ["pluginID"] = "vst.reverb" },
            WaveLinkEvents.ChannelEffectsChanged, "FilterAdded");
        Assert.Equal("Reverb", added.Args["filter.name"]);
        var changed = await Expect("filterChanged", new() { ["identifier"] = Ids.Music, ["filterID"] = "F2", ["value"] = true },
            WaveLinkEvents.ChannelEffectsChanged, "FilterChanged");
        Assert.Equal(true, changed.Args["filter.active"]);
        await Expect("filterBypassStateChanged", new() { ["identifier"] = Ids.Music, ["mixerID"] = Ids.Local, ["value"] = true },
            WaveLinkEvents.ChannelEffectsChanged, "FilterBypassStateChanged");
        Assert.True(_wl.Snapshot.FindChannel("Spotify").Mix(Ids.Local)!.FilterBypassed);
        await Expect("filterRemoved", new() { ["identifier"] = Ids.Music, ["filterID"] = "F2" }, WaveLinkEvents.ChannelEffectsChanged, "FilterDeleted");
        Assert.Empty(_wl.Snapshot.FindChannel("Spotify").Effects);

        var gain = await Expect("microphoneConfigChanged", new() { ["identifier"] = Ids.Mic, ["property"] = "Microphone Gain", ["value"] = 60 },
            WaveLinkEvents.InputGainChanged, "MicrophoneGainChanged");
        Assert.Equal(60.0, gain.Args["microphone.gain"]);
        Assert.True(Matches(Trigger("wavelink.legacy.microphoneGainChanged"), gain));
        Assert.True(Matches(Trigger("wavelink.legacy.microphoneSettingChanged"), gain));
        Assert.True(Matches(Trigger(WaveLinkEvents.InputGainChanged), gain, new() { ["inputDevice"] = "Wave:3" }));
        await Expect("microphoneConfigChanged", new() { ["identifier"] = Ids.Mic, ["property"] = "Microphone Output Volume", ["value"] = 70 },
            WaveLinkEvents.InputDeviceUpdated, "MicrophoneOutputVolumeChanged");
        await Expect("microphoneConfigChanged", new() { ["identifier"] = Ids.Mic, ["property"] = "Microphone DirectMonitor Volume", ["value"] = 0.25 },
            WaveLinkEvents.InputDeviceUpdated, "MicrophoneBalanceChanged");
        Assert.Equal(25, _wl.Snapshot.InputDevices[0].Primary!.MicPcMix);
        await Expect("microphoneConfigChanged", new() { ["identifier"] = Ids.Mic, ["property"] = "Microphone Mute", ["boolValue"] = true },
            WaveLinkEvents.InputDeviceUpdated, "MicrophoneMuteChanged");
        Assert.True(_wl.Snapshot.InputDevices[0].Primary!.Muted);
        var lowCut = await Expect("microphoneConfigChanged", new() { ["identifier"] = Ids.Mic, ["property"] = "Microphone LowCut", ["value"] = true },
            WaveLinkEvents.InputDspEffectsChanged, "MicrophoneSettingChanged");
        Assert.Equal(true, lowCut.Args["waveLinkInputDeviceLowCutFilter"]);

        var outMute = await Expect("outputMuteChanged", new() { ["mixerID"] = Ids.Stream, ["value"] = true }, WaveLinkEvents.MixUpdated, "OutputMuteChanged");
        Assert.Equal("Stream Mix", outMute.Args["waveLinkMixName"]);
        Assert.True(Matches(Trigger("wavelink.legacy.outputMuteChanged"), outMute, new() { ["mix"] = "Stream Mix" }));
        Assert.False(Matches(Trigger("wavelink.legacy.outputVolumeChanged"), outMute));
        var outVolume = await Expect("outputVolumeChanged", new() { ["identifier"] = "", ["mixerID"] = Ids.Local, ["value"] = 55 },
            WaveLinkEvents.MixUpdated, "OutputVolumeChanged");
        Assert.Equal(55, outVolume.Args["localVolume"]);

        var switched = await Expect("outputSwitched", new() { ["value"] = "StreamMix" }, WaveLinkEvents.OutputMixChanged, "OutputSwitched");
        Assert.Equal("Stream Mix", switched.Args["waveLinkOutputMixName"]);
        Assert.Equal(Ids.Stream, switched.Args["mixerOutput"]);   // Streamer.bot's Output Switched name
        Assert.Equal(Ids.Stream, _wl.Snapshot.MonitoredMixId);
        var selected = await Expect("selectedOutputChanged", new() { ["identifier"] = Ids.Speakers, ["mixerID"] = Ids.Local, ["value"] = "" },
            WaveLinkEvents.MainOutputChanged, "SelectedOutputChanged");
        Assert.Equal("Speakers", selected.Args["output.name"]);
        Assert.Equal("Speakers", _wl.Snapshot.MainOutputDevice!.Name);
    }

    [Fact]
    public async Task Inputs_changed_reloads_the_inputs()
    {
        var calls = _app.CallsTo("getInputConfigs").Count;
        _app.Methods["getInputConfigs"] = _ =>
        {
            var inputs = FakeWaveLink.LegacyInputs();
            inputs.RemoveAt(1);
            return inputs;
        };
        _app.Notify("inputsChanged", new JsonObject());
        var e = await _env.WaitForEventAsync(WaveLinkEvents.ChannelsChanged);
        Assert.Equal(1, e.Args["waveLinkChannelsCount"]);
        Assert.Equal(calls + 1, _app.CallsTo("getInputConfigs").Count);
    }

    [Fact]
    public async Task Commands_use_setInputConfig_setOutputConfig_and_setMicrophoneConfig()
    {
        // Both mixes: one call per mixer.
        Assert.True(await _wl.SetChannelMuteAsync("Wave:3", "Both", SwitchMode.Toggle));
        var first = await _app.NextCallAsync("setInputConfig");
        var second = await _app.NextCallAsync("setInputConfig", 1);
        Assert.Equal("Mute", first!["property"]!.GetValue<string>());
        Assert.Equal(Ids.Mic, first["identifier"]!.GetValue<string>());
        Assert.Equal(Ids.Local, first["mixerID"]!.GetValue<string>());
        Assert.Equal(Ids.Stream, second!["mixerID"]!.GetValue<string>());
        Assert.True(first["value"]!.GetValue<bool>());
        Assert.False(first["forceLink"]!.GetValue<bool>());
        Assert.Equal(2, _env.Events(WaveLinkEvents.ChannelMuteChanged).Length);
        Assert.True(_wl.Snapshot.FindChannel("Wave:3").Muted);

        Assert.Equal(40, await _wl.SetChannelVolumeAsync("Music", "Local", -10, relative: true));
        var volume = await _app.NextCallAsync("setInputConfig", 2);
        Assert.Equal("Volume", volume!["property"]!.GetValue<string>());
        Assert.Equal(40, volume["value"]!.GetValue<int>());

        Assert.False(await _wl.SetChannelEffectAsync("Wave:3", "NVIDIA Broadcast", SwitchMode.Off));
        var filter = await _app.NextCallAsync("setFilter");
        Assert.Equal("F-NV-1", filter!["filterID"]!.GetValue<string>());
        Assert.True(await _wl.SetFilterBypassAsync("Music", "Stream", SwitchMode.On));
        Assert.Equal(Ids.Stream, (await _app.NextCallAsync("setFilterBypass"))!["mixerID"]!.GetValue<string>());

        Assert.Equal(90, await _wl.SetMixVolumeAsync("Monitor Mix", 90));
        var output = await _app.NextCallAsync("setOutputConfig");
        Assert.Equal("Output Level", output!["property"]!.GetValue<string>());
        Assert.Equal(Ids.Local, output["mixerID"]!.GetValue<string>());
        Assert.True(await _wl.SetMixMuteAsync("stream", SwitchMode.On));
        Assert.Equal("Output Mute", (await _app.NextCallAsync("setOutputConfig", 1))!["property"]!.GetValue<string>());
        await Assert.ThrowsAsync<ArgumentException>(() => _wl.SetMixMuteAsync("Both", SwitchMode.On));

        Assert.True(await _wl.SetInputMuteAsync("Wave:3", SwitchMode.On));
        var mute = await _app.NextCallAsync("setMicrophoneConfig");
        Assert.Equal("Microphone Mute", mute!["property"]!.GetValue<string>());
        Assert.True(mute["boolValue"]!.GetValue<bool>());
        Assert.True(await _wl.SetInputLowCutAsync("", SwitchMode.Toggle));
        Assert.Equal("Microphone LowCut", (await _app.NextCallAsync("setMicrophoneConfig", 1))!["property"]!.GetValue<string>());
        Assert.Equal(80, await _wl.SetInputGainAsync("", 5, relative: true));
        var gain = await _app.NextCallAsync("setMicrophoneConfig", 2);
        Assert.Equal("Microphone Gain", gain!["property"]!.GetValue<string>());
        Assert.Equal(80, gain["value"]!.GetValue<double>());
        Assert.Equal(30, await _wl.SetMicPcMixAsync("", 30));
        Assert.Equal(0.3, (await _app.NextCallAsync("setMicrophoneConfig", 3))!["value"]!.GetValue<double>(), 3);
        Assert.Equal(60, await _wl.SetMicOutputVolumeAsync("", 60));
        Assert.Equal("Microphone Output Volume", (await _app.NextCallAsync("setMicrophoneConfig", 4))!["property"]!.GetValue<string>());

        await _wl.SetMainOutputAsync("Speakers");
        var selected = await _app.NextCallAsync("setSelectedOutput");
        Assert.Equal(Ids.Speakers, selected!["identifier"]!.GetValue<string>());
        Assert.Equal("Speakers", selected["name"]!.GetValue<string>());
        Assert.Equal(Ids.Local, selected["mixerID"]!.GetValue<string>());

        // Monitoring the mix already monitored does nothing; the other one switches.
        await _wl.SetMonitoredMixAsync("Monitor Mix");
        Assert.Empty(_app.CallsTo("switchOutput"));
        Assert.Equal("Stream Mix", (await _wl.SwitchMonitoringAsync()).Name);
        await _app.NextCallAsync("switchOutput");
        Assert.Equal(Ids.Stream, _wl.Snapshot.MonitoredMixId);
        Assert.Single(_env.Events(WaveLinkEvents.OutputMixChanged));

        await Assert.ThrowsAsync<WaveLinkUnsupportedException>(() => _wl.AddAppToChannelAsync("Music"));
        await Assert.ThrowsAsync<WaveLinkUnsupportedException>(() => _wl.SetOutputVolumeAsync("Headset", 50));
        await Assert.ThrowsAsync<WaveLinkUnsupportedException>(() => _wl.AddOutputToMixAsync("Headset", "Stream Mix"));
    }

    [Fact]
    public async Task Real_time_levels_map_inputs_to_channels_and_outputs_to_mixes()
    {
        _wl.Settings.LevelMeters = true;
        _app.Notify("realTimeChanges", new JsonObject
        {
            ["mixerList"] = new JsonArray(new JsonObject
            {
                ["identifier"] = Ids.Mic,
                ["localMixer"] = new JsonObject { ["levelLeft"] = 0.5, ["levelRight"] = 0.4 },
                ["streamMixer"] = new JsonObject { ["levelLeft"] = 0.45, ["levelRight"] = 0.4 },
            }),
            ["localMixer"] = new JsonObject { ["levelLeft"] = 0.2, ["levelRight"] = 0.2 },
            ["streamMixer"] = new JsonObject { ["levelLeft"] = 0.3, ["levelRight"] = 0.25 },
            ["microphoneLevel"] = new JsonObject { ["levelLeft"] = 0.6, ["levelRight"] = 0.6 },
        });
        var e = await _env.WaitForEventAsync(WaveLinkEvents.LevelMeter);
        Assert.Equal(5, e.Args["waveLinkLevelsCount"]);
        Assert.Equal("input", e.Args["waveLinkLevelType"]);
        Assert.Equal(60.0, e.Args["waveLinkLevelLeft"]);

        var args = new Dictionary<string, object?>();
        var input = Trigger("wavelink.legacy.inputLevelMeterChanged");
        Assert.True(input.Matches(e, new TriggerDefinition(), ParameterValues.From(input.Parameters, new Dictionary<string, string>()), args));
        Assert.Equal("Wave:3", args["waveLinkLevelName"]);
        Assert.Equal(Ids.Local, args["waveLinkLevelSubId"]);
        var output = Trigger("wavelink.legacy.outputLevelMeterChanged");
        Assert.True(output.Matches(e, new TriggerDefinition(), ParameterValues.From(output.Parameters, new Dictionary<string, string> { ["levelName"] = "Stream Mix" }), args));
        Assert.Equal(30.0, args["waveLinkLevelLeft"]);
    }
}
