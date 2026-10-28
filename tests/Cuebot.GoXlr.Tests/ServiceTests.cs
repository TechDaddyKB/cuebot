using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.GoXlr.Tests.Support;

namespace Cuebot.GoXlr.Tests;

/// <summary>The service against an in-process GoXLR Utility over a real websocket.</summary>
public sealed class ServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private FakeGoXlrUtility _fake = null!;
    private GoXlrService _goXlr = null!;

    public async Task InitializeAsync()
    {
        _fake = FakeGoXlrUtility.Start();
        _goXlr = _env.Service(_fake);
        await _goXlr.ConnectAsync();
        await Wait.UntilAsync(() => _goXlr.IsConnected, "connected");
    }

    public async Task DisposeAsync()
    {
        await _goXlr.DisposeAsync();
        await _fake.DisposeAsync();
        _env.Dispose();
    }

    private JsonNode Mixer(JsonNode status) => _fake.Mixer(status);

    [Fact]
    public async Task Connects_loads_the_status_and_publishes_connected()
    {
        Assert.Contains("/api/websocket", _fake.Paths);
        Assert.Equal("\"GetStatus\"", _fake.Requests.First());
        Assert.Equal(ConnectionState.Connected, _goXlr.State);
        Assert.Contains("GoXLR Utility 1.2.3", _goXlr.StatusMessage);
        Assert.Contains(FakeGoXlrUtility.Serial, _goXlr.StatusMessage);
        var connected = await _env.NextAsync(GoXlrEvents.Connected);
        Assert.Equal("1.2.3", connected.Args["goXlrDaemonVersion"]);
        Assert.Equal(1, connected.Args["goXlrDeviceCount"]);
        Assert.Equal("Music", _goXlr.Mixer().Faders["B"].Channel);
        Assert.Contains("\"daemon_version\":\"1.2.3\"", _goXlr.RawStatus);
        Assert.True((await _goXlr.PingAsync()) < TimeSpan.FromSeconds(5));
        Assert.Equal("\"Ping\"", _fake.Requests.Last());
    }

    [Fact]
    public async Task Commands_go_out_in_the_utilitys_format_and_patches_update_the_snapshot()
    {
        Assert.Equal(50, await _goXlr.SetVolumeAsync("Music", 50));
        Assert.Equal($$"""{"Command":["{{FakeGoXlrUtility.Serial}}",{"SetVolume":["Music",128]}]}""", _fake.Requests.Last());
        var changed = await _env.NextAsync(GoXlrEvents.VolumeChanged);
        Assert.Equal("Music", changed.Args["goXlrChannel"]);
        Assert.Equal(50, changed.Args["goXlrVolume"]);
        Assert.Equal(75, changed.Args["goXlrPreviousVolume"]);
        Assert.Equal(128, _goXlr.Mixer().Volume("Music"));

        Assert.Equal(40, await _goXlr.SetVolumeAsync("Music", -10, relative: true));

        // Mute by channel name: resolves to fader B.
        Assert.Equal("MutedToX", await _goXlr.SetFaderMuteAsync("music", GoXlrMuteAction.Toggle));
        Assert.EndsWith("""{"SetFaderMuteState":["B","MutedToX"]}]}""", _fake.Requests.Last());
        var mute = await _env.NextAsync(GoXlrEvents.FaderMuteChanged);
        Assert.Equal(true, mute.Args["goXlrMuted"]);
        Assert.Equal("B", mute.Args["goXlrFader"]);
        await Wait.UntilAsync(() => _goXlr.Mixer().Faders["B"].Muted, "mute in snapshot");
        Assert.Equal("Unmuted", await _goXlr.SetFaderMuteAsync("B", GoXlrMuteAction.Toggle));

        Assert.Equal("MutedToAll", await _goXlr.SetCoughMuteAsync(GoXlrMuteAction.MuteToAll));
        await _env.NextAsync(GoXlrEvents.CoughMuteChanged, e => (bool?)e.Args["goXlrMuted"] == true);

        Assert.True(await _goXlr.SetEffectAsync("hard tune", SwitchMode.Toggle));
        Assert.EndsWith("""{"SetHardTuneEnabled":true}]}""", _fake.Requests.Last());
        await _env.NextAsync(GoXlrEvents.EffectStateChanged, e => (string?)e.Args["goXlrEffect"] == "HardTune");

        Assert.Equal("Preset3", await _goXlr.SetEffectPresetAsync("chipmunk"));
        var preset = await _env.NextAsync(GoXlrEvents.EffectPresetChanged);
        Assert.Equal("Chipmunk", preset.Args["goXlrPresetName"]);

        await _goXlr.SetSamplerBankAsync("b");
        await _env.NextAsync(GoXlrEvents.SamplerBankChanged);
        await Wait.UntilAsync(() => _goXlr.Mixer().Sampler!.ActiveBank == "B", "bank");
        Assert.Equal(("B", "TopRight"), await _goXlr.PlaySampleAsync("", "top right"));
        await _env.NextAsync(GoXlrEvents.SamplePlaybackChanged, e => (bool?)e.Args["goXlrPlaying"] == true);
        await _goXlr.PlaySampleAsync("A", "TopLeft", index: 1);
        Assert.EndsWith("""{"PlaySampleByIndex":["A","TopLeft",1]}]}""", _fake.Requests.Last());

        Assert.True(await _goXlr.SetRoutingAsync("Music", "Chat Mic", SwitchMode.Toggle));
        await _env.NextAsync(GoXlrEvents.RoutingChanged);

        await _goXlr.SetGlobalColourAsync("#ff0080");
        Assert.EndsWith("""{"SetGlobalColour":"FF0080"}]}""", _fake.Requests.Last());
        await _goXlr.SetButtonColoursAsync("cough", "#00ff00");
        Assert.EndsWith("""{"SetButtonColours":["Cough","00FF00",null]}]}""", _fake.Requests.Last());
        await _goXlr.SetFaderChannelAsync("D", "Game");
        await _env.NextAsync(GoXlrEvents.FaderAssignmentChanged);
    }

    [Fact]
    public async Task Profile_load_that_replaces_whole_sections_fires_only_what_changed()
    {
        await _goXlr.LoadProfileAsync("gaming", remember: false);
        Assert.EndsWith("""{"LoadProfile":["Gaming",false]}]}""", _fake.Requests.Last());   // the listed spelling
        var profile = await _env.NextAsync(GoXlrEvents.ProfileChanged);
        Assert.Equal("Gaming", profile.Args["goXlrProfile"]);
        var fader = await _env.NextAsync(GoXlrEvents.FaderAssignmentChanged);
        Assert.Equal("Game", fader.Args["goXlrChannel"]);
        Assert.DoesNotContain(_env.Events, e => e.Type == GoXlrEvents.FaderMuteChanged);

        var error = await Assert.ThrowsAsync<GoXlrRequestException>(() => _goXlr.LoadProfileAsync("Nope"));
        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public async Task Hardware_changes_become_events()
    {
        await _fake.MutateAsync(s => Mixer(s)["button_down"]!["Fader1Mute"] = true);
        var pressed = await _env.NextAsync(GoXlrEvents.ButtonPressed);
        Assert.Equal("Fader1Mute", pressed.Args["goXlrButton"]);
        Assert.Equal("Fader 1 Mute", pressed.Args["goXlrButtonName"]);
        await _fake.MutateAsync(s => Mixer(s)["button_down"]!["Fader1Mute"] = false);
        await _env.NextAsync(GoXlrEvents.ButtonReleased);

        await _fake.MutateAsync(s => Mixer(s)["levels"]!["volumes"]!["Game"] = 0);
        var volume = await _env.NextAsync(GoXlrEvents.VolumeChanged, e => (string?)e.Args["goXlrChannel"] == "Game");
        Assert.Equal(0, volume.Args["goXlrVolume"]);
        Assert.Equal("", volume.Args["goXlrFader"]);

        // A second GoXLR is plugged in, then removed.
        await _fake.MutateAsync(s => s["mixers"]!["S999"] = Mixer(s).DeepClone());
        var added = await _env.NextAsync(GoXlrEvents.DeviceConnected);
        Assert.Equal("S999", added.Args["goXlrSerial"]);
        Assert.Equal(2, _goXlr.Status.Mixers.Count);
        Assert.Equal(FakeGoXlrUtility.Serial, _goXlr.Mixer().Serial);
        _env.Settings.Value.Serial = "S999";
        Assert.Equal("S999", _goXlr.Mixer().Serial);
        _env.Settings.Value.Serial = "";
        await _fake.MutateAsync(s => s["mixers"]!.AsObject().Remove("S999"));
        await _env.NextAsync(GoXlrEvents.DeviceDisconnected);
        Assert.Throws<GoXlrRequestException>(() => _goXlr.Mixer("S999"));
    }

    [Fact]
    public async Task A_patch_that_does_not_apply_reloads_the_status()
    {
        var requests = _fake.Requests.Count(r => r == "\"GetStatus\"");
        await _fake.BroadcastAsync($$$"""{"id":{{{FakeGoXlrUtility.PatchId}}},"data":{"Patch":[{"op":"replace","path":"/mixers/NOPE/profile_name","value":"x"}]}}""");
        await Wait.UntilAsync(() => _fake.Requests.Count(r => r == "\"GetStatus\"") > requests, "status reload");
        Assert.True(_goXlr.IsConnected);
    }

    [Fact]
    public async Task Errors_and_unknown_commands_surface()
    {
        var error = await Assert.ThrowsAsync<GoXlrRequestException>(() => _goXlr.CommandAsync(JsonNode.Parse("""{"DoTheThing":1}""")!));
        Assert.Contains("unknown variant", error.Message);
        var answer = await _goXlr.RequestAsync(JsonNode.Parse("\"Ping\"")!);
        Assert.Equal("Ok", answer.GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => _goXlr.SetVolumeAsync("Kazoo", 10));
        await Assert.ThrowsAsync<GoXlrRequestException>(() => _goXlr.SetFaderMuteAsync("Console", GoXlrMuteAction.Mute));
    }

    [Fact]
    public async Task Reconnects_after_the_utility_restarts()
    {
        var before = _fake.SessionsOpened;
        await _fake.CloseSessionsAsync();
        await _env.NextAsync(GoXlrEvents.Disconnected);
        await Wait.UntilAsync(() => _fake.SessionsOpened > before && _goXlr.IsConnected, "reconnected");
        Assert.Equal(2, _env.Events.Count(e => e.Type == GoXlrEvents.Connected));
    }
}

public sealed class ServiceLifecycleTests
{
    [Fact]
    public async Task Not_running_utility_reports_and_retries_and_commands_fail_cleanly()
    {
        using var env = new TestEnvironment();
        env.Settings.Value.Port = 1;   // nothing listens there
        await using var goXlr = env.Service();
        await Assert.ThrowsAsync<GoXlrNotConnectedException>(() => goXlr.SetVolumeAsync("Mic", 10));
        await goXlr.ConnectAsync();
        await Wait.UntilAsync(() => goXlr.StatusMessage.Contains("retrying", StringComparison.Ordinal), "retry status");
        Assert.Contains("not reachable", goXlr.StatusMessage);
        Assert.True(goXlr.IsRunning);
        await goXlr.DisconnectAsync();
        Assert.False(goXlr.IsRunning);
        Assert.Equal(ConnectionState.Disconnected, goXlr.State);

        env.Settings.Value.AutoReconnect = false;
        await goXlr.ConnectAsync();
        await Wait.UntilAsync(() => !goXlr.IsRunning, "gives up");
        Assert.Equal(ConnectionState.Disconnected, goXlr.State);
    }

    [Fact]
    public async Task Mini_has_no_effects_or_sampler()
    {
        using var env = new TestEnvironment();
        await using var fake = FakeGoXlrUtility.Start(mini: true);
        await using var goXlr = env.Service(fake);
        await goXlr.ConnectAsync();
        await Wait.UntilAsync(() => goXlr.IsConnected, "connected");
        Assert.Contains("GoXLR Mini", goXlr.StatusMessage);
        await Assert.ThrowsAsync<GoXlrRequestException>(() => goXlr.SetEffectAsync("FX", SwitchMode.On));
        await Assert.ThrowsAsync<GoXlrRequestException>(() => goXlr.PlaySampleAsync("", "TopLeft"));
        await Assert.ThrowsAsync<GoXlrRequestException>(() => goXlr.SetScribbleTextAsync("A", "hi"));
        Assert.Equal("MutedToX", await goXlr.SetCoughMuteAsync(GoXlrMuteAction.Mute));
    }

    [Fact]
    public async Task Auto_connect_is_off_by_default()
    {
        using var env = new TestEnvironment();
        await using var goXlr = env.Service();
        await goXlr.StartAsync();
        Assert.False(goXlr.IsRunning);
        Assert.False(env.Settings.Value.AutoConnect);
        Assert.Equal(14564, env.Settings.Value.Port);
    }
}
