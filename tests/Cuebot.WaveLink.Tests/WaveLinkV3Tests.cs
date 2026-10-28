using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.WaveLink.Tests.Support;
using Ids = Cuebot.WaveLink.Tests.Support.FakeWaveLink.V3Ids;

namespace Cuebot.WaveLink.Tests;

public sealed class WaveLinkV3Tests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private WaveLinkService _wl = null!;
    private FakeWaveLink _app = null!;

    public async Task InitializeAsync()
    {
        _env.Network.Listen(1884, FakeWaveLink.V3);
        _wl = _env.Service();
        await _wl.ConnectAsync();
        _app = await _env.Network.AppAsync(1884);
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
    public void Snapshot_maps_channels_mixes_devices_and_scales_levels_to_percent()
    {
        var s = _wl.Snapshot;
        Assert.Equal(["Wave:3", "Music", "Game"], s.Channels.Select(c => c.Name));
        var music = s.FindChannel("music");
        Assert.Equal(50, music.Volume);
        Assert.Equal("Spotify", Assert.Single(music.Apps).Name);
        var stream = music.Mix(Ids.Stream)!;
        Assert.True(stream.Muted);
        Assert.Equal(30, stream.Volume);
        Assert.Equal("Stream Mix", stream.MixName);
        Assert.Equal(["Personal Mix", "Stream Mix"], s.Mixes.Select(m => m.Name));
        Assert.Equal(85, s.FindMix("Stream Mix").Volume);

        var input = s.FindInputDevice("").Primary!;   // the only device
        Assert.Equal(50, input.Gain);
        Assert.Equal(20, input.GainDb);
        Assert.Equal(25, input.MicPcMix);
        Assert.True(input.ClipGuard);
        Assert.False(input.LowCut);

        Assert.Equal(Ids.Headphones, s.MainOutputDevice!.Id);
        Assert.Equal(Ids.Personal, s.MonitoredMixId);
        Assert.Equal(70, s.FindOutputDevice("Headphones (Wave:3)").Primary!.Volume);
    }

    [Fact]
    public async Task Subscribes_to_focused_app_but_not_level_meters_by_default()
    {
        var p = await _app.NextCallAsync("setSubscription");
        Assert.True(p!["focusedAppChanged"]!["isEnabled"]!.GetValue<bool>());
        Assert.False(p["levelMeterChanged"]!["isEnabled"]!.GetValue<bool>());

        _wl.Settings.LevelMeters = true;
        _wl.Settings.LevelMeterType = "channel";
        await _wl.ApplySubscriptionsAsync();
        p = await _app.NextCallAsync("setSubscription", 1);
        Assert.True(p!["levelMeterChanged"]!["isEnabled"]!.GetValue<bool>());
        Assert.Equal("channel", p["levelMeterChanged"]!["type"]!.GetValue<string>());
        Assert.Equal("all", p["levelMeterChanged"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Partial_channel_update_fires_mute_and_volume_events_with_mix_variables()
    {
        _app.Notify("channelChanged", new JsonObject
        {
            ["id"] = Ids.Game,
            ["mixes"] = new JsonArray(new JsonObject { ["mixId"] = Ids.Stream, ["isMuted"] = true }),
        });
        var mute = await _env.WaitForEventAsync(WaveLinkEvents.ChannelMuteChanged);
        Assert.Equal("Game", mute.Args["waveLinkChannelName"]);
        Assert.Equal("Stream Mix", mute.Args["waveLinkMixName"]);
        Assert.Equal(true, mute.Args["waveLinkMuted"]);
        Assert.Equal("mix", mute.Args["waveLinkScope"]);
        Assert.Equal("channelChanged", mute.Args["waveLinkEvent"]);
        Assert.Equal("3", mute.Args["waveLinkApi"]);
        Assert.Equal(2, mute.Args["waveLinkChannelMixesCount"]);
        Assert.Equal(true, mute.Args["waveLinkChannelMix.1.muted"]);
        // The partial update kept the rest of the channel.
        Assert.Equal(50, _wl.Snapshot.FindChannel("Game").Mix(Ids.Stream)!.Volume);
        Assert.Single(_env.Events(WaveLinkEvents.ChannelUpdated));
        Assert.Empty(_env.Events(WaveLinkEvents.ChannelVolumeChanged));

        var trigger = Trigger(WaveLinkEvents.ChannelMuteChanged);
        Assert.True(Matches(trigger, mute, new() { ["channel"] = "game", ["mix"] = "Stream Mix", ["stateFilter"] = "Muted" }));
        Assert.False(Matches(trigger, mute, new() { ["mix"] = "Personal Mix" }));
        Assert.False(Matches(trigger, mute, new() { ["stateFilter"] = "Not muted" }));
        // The legacy "Input mute changed" trigger listens to the same event but only for Wave Link 1.x/2.x.
        Assert.False(Matches(Trigger("wavelink.legacy.inputMuteChanged"), mute));

        _app.Notify("channelChanged", new JsonObject { ["id"] = Ids.Mic, ["level"] = 0.42 });
        var volume = await _env.WaitForEventAsync(WaveLinkEvents.ChannelVolumeChanged);
        Assert.Equal(42.0, volume.Args["waveLinkChannelVolume"]);
        Assert.Equal(80.0, volume.Args["waveLinkPreviousVolume"]);
        Assert.Equal("channel", volume.Args["waveLinkScope"]);
    }

    [Fact]
    public async Task Effects_mixes_devices_and_outputs_map_to_their_events()
    {
        _app.Notify("channelChanged", new JsonObject
        {
            ["id"] = Ids.Mic,
            ["effects"] = new JsonArray(new JsonObject { ["id"] = "fx-eq", ["isEnabled"] = false }),
        });
        var fx = await _env.WaitForEventAsync(WaveLinkEvents.ChannelEffectsChanged);
        Assert.Equal("EQ", fx.Args["waveLinkEffectName"]);
        Assert.Equal(false, fx.Args["waveLinkEffectEnabled"]);

        _app.Notify("mixChanged", new JsonObject { ["id"] = Ids.Stream, ["isMuted"] = true });
        var mix = await _env.WaitForEventAsync(WaveLinkEvents.MixUpdated);
        Assert.Equal("Stream Mix", mix.Args["waveLinkMixName"]);
        Assert.Equal(true, mix.Args["waveLinkMixMuted"]);
        Assert.Equal(true, mix.Args["waveLinkMuteChanged"]);
        Assert.True(Matches(Trigger(WaveLinkEvents.MixUpdated), mix, new() { ["mix"] = "stream mix" }));

        _app.Notify("inputDeviceChanged", new JsonObject
        {
            ["id"] = Ids.Wave3,
            ["inputs"] = new JsonArray(new JsonObject { ["id"] = Ids.Wave3Input, ["gain"] = new JsonObject { ["value"] = 0.75 } }),
        });
        var gain = await _env.WaitForEventAsync(WaveLinkEvents.InputGainChanged);
        Assert.Equal(75.0, gain.Args["waveLinkInputDeviceGain"]);
        Assert.Equal(30.0, gain.Args["waveLinkInputDeviceGainDb"]);   // max 40 dB kept from the full device
        Assert.Equal(50.0, gain.Args["waveLinkPreviousGain"]);

        _app.Notify("inputDeviceChanged", new JsonObject
        {
            ["id"] = Ids.Wave3,
            ["inputs"] = new JsonArray(new JsonObject
            {
                ["id"] = Ids.Wave3Input,
                ["dspEffects"] = new JsonArray(new JsonObject { ["id"] = "lowcut", ["isEnabled"] = true }),
            }),
        });
        var dsp = await _env.WaitForEventAsync(WaveLinkEvents.InputDspEffectsChanged);
        Assert.Equal(true, dsp.Args["waveLinkInputDeviceLowCutFilter"]);
        Assert.Equal("Low Cut Filter", dsp.Args["waveLinkEffectName"]);
        Assert.Equal(2, _env.Events(WaveLinkEvents.InputDeviceUpdated).Length);

        _app.Notify("outputDeviceChanged", new JsonObject
        {
            ["id"] = Ids.Speakers,
            ["outputs"] = new JsonArray(new JsonObject { ["id"] = Ids.SpeakersOut, ["mixId"] = Ids.Personal }),
        });
        var outMix = await _env.WaitForEventAsync(WaveLinkEvents.OutputMixChanged);
        Assert.Equal("Speakers (Realtek)", outMix.Args["waveLinkOutputDeviceName"]);
        Assert.Equal("Personal Mix", outMix.Args["waveLinkOutputMixName"]);
        Assert.Equal(Ids.Stream, outMix.Args["waveLinkPreviousMixId"]);
        Assert.True(Matches(Trigger(WaveLinkEvents.OutputMixChanged), outMix, new() { ["outputDevice"] = "Speakers (Realtek)", ["mix"] = "Personal Mix" }));
        Assert.Single(_env.Events(WaveLinkEvents.OutputDeviceUpdated));

        var outputs = FakeWaveLink.V3OutputDevices();
        outputs["mainOutput"] = new JsonObject { ["outputDeviceId"] = Ids.Speakers, ["outputId"] = Ids.SpeakersOut };
        _app.Notify("outputDevicesChanged", outputs);
        var main = await _env.WaitForEventAsync(WaveLinkEvents.MainOutputChanged);
        Assert.Equal("Speakers (Realtek)", main.Args["waveLinkOutputDeviceName"]);
        Assert.Equal(Ids.Headphones, main.Args["waveLinkPreviousOutputDeviceId"]);
        Assert.Equal(Ids.Stream, _wl.Snapshot.MonitoredMixId);
    }

    [Fact]
    public async Task List_notifications_replace_the_lists()
    {
        var channels = FakeWaveLink.V3Channels();
        channels.RemoveAt(2);
        _app.Notify("channelsChanged", new JsonObject { ["channels"] = channels });
        var changed = await _env.WaitForEventAsync(WaveLinkEvents.ChannelsChanged);
        Assert.Equal(2, changed.Args["waveLinkChannelsCount"]);
        Assert.Equal(2, _wl.Snapshot.Channels.Count);

        _app.Notify("mixesChanged", new JsonObject { ["mixes"] = FakeWaveLink.V3Mixes() });
        await _env.WaitForEventAsync(WaveLinkEvents.MixesChanged);
        _app.Notify("inputDevicesChanged", new JsonObject { ["inputDevices"] = new JsonArray() });
        Assert.Equal(0, (await _env.WaitForEventAsync(WaveLinkEvents.InputDevicesChanged)).Args["waveLinkInputDevicesCount"]);
    }

    [Fact]
    public async Task Focused_app_and_create_profile_requests_become_events()
    {
        _app.Notify("focusedAppChanged", new JsonObject { ["id"] = "chrome.exe", ["name"] = "Google Chrome", ["channel"] = new JsonObject { ["id"] = Ids.Music } });
        var focused = await _env.WaitForEventAsync(WaveLinkEvents.FocusedAppChanged);
        Assert.Equal("Google Chrome", focused.Args["waveLinkAppName"]);
        Assert.Equal("Music", focused.Args["waveLinkChannelName"]);
        Assert.True(Matches(Trigger(WaveLinkEvents.FocusedAppChanged), focused, new() { ["app"] = "google chrome" }));

        // Add application to channel defaults to the focused app.
        var app = await _wl.AddAppToChannelAsync("Game");
        Assert.Equal("chrome.exe", app.Id);
        var call = await _app.NextCallAsync("addToChannel");
        Assert.Equal("chrome.exe", call!["appId"]!.GetValue<string>());
        Assert.Equal(Ids.Game, call["channelId"]!.GetValue<string>());

        _app.Notify("createProfileRequested", new JsonObject { ["deviceType"] = "StreamDeckPlus", ["mixes"] = new JsonArray("Personal Mix", "Stream Mix") });
        var profile = await _env.WaitForEventAsync(WaveLinkEvents.CreateProfileRequested);
        Assert.Equal("StreamDeckPlus", profile.Args["waveLinkDeviceType"]);
        Assert.Equal("Personal Mix, Stream Mix", profile.Args["waveLinkProfileMixes"]);
        Assert.Equal(2, profile.Args["waveLinkProfileMixesCount"]);
    }

    [Fact]
    public async Task Channel_commands_send_partial_setChannel_and_fire_one_event_even_when_echoed()
    {
        Assert.True(await _wl.SetChannelMuteAsync("Music", "", SwitchMode.Toggle));
        var p = await _app.NextCallAsync("setChannel");
        Assert.Equal(Ids.Music, p!["id"]!.GetValue<string>());
        Assert.True(p["isMuted"]!.GetValue<bool>());
        Assert.Single(_env.Events(WaveLinkEvents.ChannelMuteChanged));
        Assert.True(_wl.Snapshot.FindChannel("Music").Muted);
        // Wave Link echoes the change: no second event.
        _app.Notify("channelChanged", new JsonObject { ["id"] = Ids.Music, ["isMuted"] = true });
        _app.Notify("mixChanged", new JsonObject { ["id"] = Ids.Personal, ["name"] = "Personal Mix" });
        await Wait.UntilAsync(() => _env.Events(WaveLinkEvents.ChannelUpdated).Length >= 1, "update");
        await Task.Delay(50);
        Assert.Single(_env.Events(WaveLinkEvents.ChannelMuteChanged));

        Assert.False(await _wl.SetChannelMuteAsync("Music", "Stream Mix", SwitchMode.Toggle));   // was muted in the stream mix
        p = await _app.NextCallAsync("setChannel", 1);
        Assert.Equal(Ids.Stream, p!["mixes"]![0]!["mixId"]!.GetValue<string>());
        Assert.False(p["mixes"]![0]!["isMuted"]!.GetValue<bool>());

        Assert.Equal(25, await _wl.SetChannelVolumeAsync("Game", "Personal Mix", 25));
        p = await _app.NextCallAsync("setChannel", 2);
        Assert.Equal(0.25, p!["mixes"]![0]!["level"]!.GetValue<double>(), 3);
        Assert.Equal(100, await _wl.SetChannelVolumeAsync("Game", "", 10, relative: true));   // 100 + 10 is clamped
        p = await _app.NextCallAsync("setChannel", 3);
        Assert.Equal(1.0, p!["level"]!.GetValue<double>(), 3);

        Assert.False(await _wl.SetChannelEffectAsync("Wave:3", "EQ", SwitchMode.Toggle));
        p = await _app.NextCallAsync("setChannel", 4);
        Assert.Equal("fx-eq", p!["effects"]![0]!["id"]!.GetValue<string>());

        await Assert.ThrowsAsync<WaveLinkRequestException>(() => _wl.SetChannelMuteAsync("Discord", "", SwitchMode.On));
        await Assert.ThrowsAsync<WaveLinkUnsupportedException>(() => _wl.SetFilterBypassAsync("Music", "", SwitchMode.On));
        await Assert.ThrowsAsync<WaveLinkUnsupportedException>(() => _wl.SwitchMonitoringAsync());
    }

    [Fact]
    public async Task Mix_input_and_output_commands_use_the_documented_shapes()
    {
        Assert.Equal(75, await _wl.SetMixVolumeAsync("Stream Mix", -10, relative: true));
        var p = await _app.NextCallAsync("setMix");
        Assert.Equal(Ids.Stream, p!["id"]!.GetValue<string>());
        Assert.Equal(0.75, p["level"]!.GetValue<double>(), 3);
        Assert.True(await _wl.SetMixMuteAsync("Personal Mix", SwitchMode.On));

        Assert.True(await _wl.SetInputMuteAsync("", SwitchMode.Toggle));
        p = await _app.NextCallAsync("setInputDevice");
        Assert.Equal(Ids.Wave3, p!["id"]!.GetValue<string>());
        Assert.Equal(Ids.Wave3Input, p["inputs"]![0]!["id"]!.GetValue<string>());
        Assert.True(p["inputs"]![0]!["isMuted"]!.GetValue<bool>());

        Assert.False(await _wl.SetInputClipGuardAsync("Wave:3", SwitchMode.Toggle));
        p = await _app.NextCallAsync("setInputDevice", 1);
        Assert.Equal("clipguard", p!["inputs"]![0]!["dspEffects"]![0]!["id"]!.GetValue<string>());
        Assert.True(await _wl.SetInputLowCutAsync("Wave:3", SwitchMode.On));
        Assert.Equal(60, await _wl.SetInputGainAsync("Wave:3", 60));
        p = await _app.NextCallAsync("setInputDevice", 3);
        Assert.Equal(0.6, p!["inputs"]![0]!["gain"]!["value"]!.GetValue<double>(), 3);
        Assert.True(await _wl.SetInputGainLockAsync("Wave:3", SwitchMode.On));
        Assert.Equal(35, await _wl.SetMicPcMixAsync("Wave:3", 10, relative: true));
        Assert.Contains(_env.Events(WaveLinkEvents.InputGainChanged), e => (double?)e.Args["waveLinkInputDeviceGain"] == 60);

        await _wl.SetMainOutputAsync("Speakers (Realtek)");
        p = await _app.NextCallAsync("setOutputDevice");
        Assert.Equal(Ids.Speakers, p!["mainOutput"]!["outputDeviceId"]!.GetValue<string>());
        Assert.Equal(Ids.SpeakersOut, p["mainOutput"]!["outputId"]!.GetValue<string>());
        Assert.Single(_env.Events(WaveLinkEvents.MainOutputChanged));

        Assert.Equal(40, await _wl.SetOutputVolumeAsync("Headphones (Wave:3)", 40));
        p = await _app.NextCallAsync("setOutputDevice", 1);
        Assert.Equal(Ids.Headphones, p!["outputDevice"]!["id"]!.GetValue<string>());
        Assert.Equal(0.4, p["outputDevice"]!["outputs"]![0]!["level"]!.GetValue<double>(), 3);

        // Speakers are the main output now and play the stream mix; monitor the personal mix instead.
        Assert.Equal("Personal Mix", (await _wl.SetMonitoredMixAsync("Personal Mix")).Name);
        p = await _app.NextCallAsync("setOutputDevice", 2);
        Assert.Equal(Ids.Speakers, p!["outputDevice"]!["id"]!.GetValue<string>());
        Assert.Equal(Ids.Personal, p["outputDevice"]!["outputs"]![0]!["mixId"]!.GetValue<string>());
        Assert.Equal(Ids.Personal, _wl.Snapshot.MonitoredMixId);
        Assert.Single(_env.Events(WaveLinkEvents.OutputMixChanged));

        await _wl.AddOutputToMixAsync("Headphones (Wave:3)", "Stream Mix");
        p = await _app.NextCallAsync("setOutputDevice", 3);
        Assert.Equal(Ids.Stream, p!["outputDevice"]!["outputs"]![0]!["mixId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Wave_link_errors_surface_as_request_exceptions()
    {
        _app.Errors["setMix"] = "Invalid mix";
        var ex = await Assert.ThrowsAsync<WaveLinkRequestException>(() => _wl.SetMixMuteAsync("Stream Mix", SwitchMode.On));
        Assert.Contains("Invalid mix", ex.Message);
        Assert.False(_wl.Snapshot.FindMix("Stream Mix").Muted);   // nothing applied locally
        Assert.Empty(_env.Events(WaveLinkEvents.MixUpdated));
    }

    [Fact]
    public async Task Level_meters_are_ignored_until_enabled_then_throttled_into_level_events()
    {
        JsonObject Meters(double left) => new()
        {
            ["channels"] = new JsonArray(
                new JsonObject { ["id"] = Ids.Mic, ["levelLeftPercentage"] = left, ["levelRightPercentage"] = left / 2 },
                new JsonObject { ["id"] = Ids.Music, ["levelLeftPercentage"] = 10, ["levelRightPercentage"] = 12 }),
            ["mixes"] = new JsonArray(new JsonObject { ["id"] = Ids.Stream, ["levelLeftPercentage"] = 5, ["levelRightPercentage"] = 5 }),
            ["inputDevices"] = new JsonArray(),
            ["outputDevices"] = new JsonArray(),
        };
        _app.Notify("levelMeterChanged", Meters(50));
        _app.Notify("channelChanged", new JsonObject { ["id"] = Ids.Mic, ["level"] = 0.5 });
        await _env.WaitForEventAsync(WaveLinkEvents.ChannelVolumeChanged);
        Assert.Empty(_env.Events(WaveLinkEvents.LevelMeter));

        _wl.Settings.LevelMeters = true;
        _wl.Settings.LevelMeterRate = 5;
        for (var i = 0; i < 30; i++) _app.Notify("levelMeterChanged", Meters(40 + i));
        var level = await _env.WaitForEventAsync(WaveLinkEvents.LevelMeter);
        Assert.Equal(3, level.Args["waveLinkLevelsCount"]);
        Assert.Equal("Wave:3", level.Args["waveLinkLevelName"]);   // the loudest
        Assert.Equal("channel", level.Args["waveLinkLevelType"]);
        await Task.Delay(300);
        // 30 messages in a burst: the leading flush and at most a couple of trailing ones at 5/s.
        Assert.InRange(_env.Events(WaveLinkEvents.LevelMeter).Length, 1, 3);
        var last = _env.Events(WaveLinkEvents.LevelMeter).Last();
        Assert.Contains("\"name\":\"Wave:3\"", (string)last.Args["waveLinkLevels"]!);

        var trigger = Trigger(WaveLinkEvents.LevelMeter);
        var args = new Dictionary<string, object?>();
        Assert.True(trigger.Matches(level, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["levelType"] = "Mix" }), args));
        Assert.Equal("Stream Mix", args["waveLinkLevelName"]);
        Assert.False(trigger.Matches(level, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["levelName"] = "Music", ["minLevel"] = "50" }), args));
    }
}
