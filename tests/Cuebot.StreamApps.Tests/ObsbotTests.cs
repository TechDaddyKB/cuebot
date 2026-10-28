using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.StreamApps.Obsbot;
using Cuebot.StreamApps.Tests.Support;
using P = Cuebot.StreamApps.Obsbot.ObsbotProtocol;

namespace Cuebot.StreamApps.Tests;

public sealed class ObsbotProtocolTests
{
    [Fact]
    public void Commands_put_the_optional_device_index_first()
    {
        Assert.Equal([50], P.Command(P.General + "SetZoom", null, 50).Args);
        Assert.Equal([1, 50], P.Command(P.General + "SetZoom", 1, 50).Args);
        Assert.Equal([0, 20, -30, 10], P.Command(P.General + "SetGimMotorDegree", 0, 20, -30, 10).Args);
    }

    [Fact]
    public void Replies_fold_into_state_with_or_without_a_device_index()
    {
        var s = P.Apply(ObsbotState.Empty, new OscMessage(P.DeviceInfo, 1, "OBSBOT Tiny 2", 0, "", 1, "OBSBOT Tail Air", 0, "", 2, 1, 0));
        Assert.Equal(2, s.SelectedDevice);
        Assert.Equal("OBSBOT Tail Air", s.SelectedName);
        Assert.True(s.Awake);
        Assert.Equal(ObsbotSeries.Tail, P.Guess(s.SelectedName));

        s = P.Apply(s, new OscMessage(P.ZoomInfo, 0, 42, 1));            // leading device index
        Assert.Equal((42, 1), (s.Zoom!.Value, s.Fov!.Value));
        s = P.Apply(s, new OscMessage(P.ZoomInfo, 43, 2));
        Assert.Equal((43, 2), (s.Zoom!.Value, s.Fov!.Value));
        s = P.Apply(s, new OscMessage(P.GimbalPosInfo, 0, -12, 33));
        Assert.Equal((-12, 33), (s.Pitch!.Value, s.Yaw!.Value));
        s = P.Apply(s, new OscMessage(P.AiTrackingInfo, 1));
        Assert.True(s.AiLocked);
        s = P.Apply(s, new OscMessage(P.TinyPresetInfo, 1, "Desk", 0, "", 1, "Door"));
        Assert.Equal(["Desk", "", "Door"], s.Presets.Select(p => p.Name));
        Assert.Equal([true, false, true], s.Presets.Select(p => p.Exists));
        s = P.Apply(s, new OscMessage(P.VirtualBackgroundInfo, 2));
        s = P.Apply(s, new OscMessage(P.AutoFramingInfo, 1));
        Assert.Equal((2, 1), (s.VirtualBackground!.Value, s.AutoFraming!.Value));

        var unrelated = new OscMessage("/OBSBOT/Something/Else", 1);
        Assert.Same(s, P.Apply(s, unrelated));
        Assert.Equal("78°", ObsbotState.FovLabel(1));
    }

    [Fact]
    public void Series_guess_and_queries()
    {
        Assert.Equal(ObsbotSeries.Meet, P.Guess("OBSBOT Meet 4K"));
        Assert.Equal(ObsbotSeries.Tiny, P.Guess("OBSBOT Tiny 3 Lite"));
        Assert.Equal(ObsbotSeries.Meet, P.Guess("", deviceType: 3));
        Assert.Contains(P.Queries(ObsbotSeries.Tiny), q => q.Address == P.Tiny + "GetAiTrackingInfo");
        Assert.DoesNotContain(P.Queries(ObsbotSeries.Tail), q => q.Address.StartsWith(P.Meet, StringComparison.Ordinal));
        Assert.All(P.Queries(ObsbotSeries.Auto), q => Assert.Equal([0], q.Args));
    }

    [Fact]
    public void Free_form_arguments_parse_to_osc_types()
    {
        Assert.Equal([1, 2.5f, true, "x"], ObsbotSubActions.ParseArgs("1, 2.5 true x"));
        Assert.Equal([3, "two words", 1.5f, false, null], ObsbotSubActions.ParseArgs("""[3, "two words", 1.5, false, null]"""));
        Assert.Empty(ObsbotSubActions.ParseArgs(" "));
        Assert.Null(ObsbotSubActions.DeviceIndex(""));
        Assert.Equal(1, ObsbotSubActions.DeviceIndex("2"));
        Assert.Throws<ArgumentException>(() => ObsbotSubActions.DeviceIndex("zero"));
    }
}

public sealed class ObsbotServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private readonly TestEnvironment _env = new();
    private FakeObsbotCenter _center = null!;
    private ObsbotService _obsbot = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(ObsbotTransport transport, bool silent = false)
    {
        _center = new FakeObsbotCenter(transport) { Silent = silent };
        var s = _env.Settings.Value.Obsbot;
        s.Transport = transport;
        s.Port = _center.Port;
        s.ListenPort = 0;           // any free port; the fake answers the sender
        s.PollSeconds = 1;
        _obsbot = new ObsbotService(_env.Settings, _env.Bus, _env.Log, OscTransports.Create, TestTimings.Fast, Tick);
        await _obsbot.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_obsbot is not null) await _obsbot.DisposeAsync();
        if (_center is not null) await _center.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Udp_says_hello_connects_on_the_reply_and_polls_state()
    {
        await StartAsync(ObsbotTransport.Udp);
        Assert.Equal([0], (await _center.NextAsync(P.Connected)).Args);
        await _env.WaitForEventAsync(ObsbotService.ConnectedEvent);
        Assert.Equal(ConnectionState.Connected, _obsbot.State);
        Assert.True(_obsbot.IsConnected);

        await Wait.UntilAsync(() => _obsbot.Current.Zoom == 20 && _obsbot.Current.Presets.Count == 3, "polled state");
        Assert.Equal("OBSBOT Tiny 2", _obsbot.Current.SelectedName);
        Assert.Equal(ObsbotSeries.Tiny, _obsbot.Series);
        Assert.Equal((-10, 25), (_obsbot.Current.Pitch!.Value, _obsbot.Current.Yaw!.Value));

        _center.Zoom = 70;
        var zoom = await _env.WaitForEventAsync(ObsbotService.ZoomChangedEvent);
        Assert.Equal(70, zoom.Args["obsbot.zoom"]);
        Assert.Equal(20, zoom.Args["obsbot.oldZoom"]);
        Assert.Equal("UDP", zoom.Args["obsbot.transport"]);

        _center.Selected = 1;
        var device = await _env.WaitForEventAsync(ObsbotService.DeviceChangedEvent);
        Assert.Equal("OBSBOT Meet 2", device.Args["obsbot.deviceName"]);
    }

    [Fact]
    public async Task Udp_without_answers_keeps_trying_but_still_sends_commands()
    {
        await StartAsync(ObsbotTransport.Udp, silent: true);
        await _center.NextAsync(P.Connected);
        await _center.NextAsync(P.Connected);   // hello repeated while nobody answers
        Assert.Equal(ConnectionState.Connecting, _obsbot.State);
        Assert.Contains("waiting for OBSBOT Center", _obsbot.StatusMessage);
        Assert.False(_obsbot.IsConnected);
        Assert.True(_obsbot.CanSend);

        await _obsbot.SendAsync(new OscMessage(P.General + "ResetGimbal", 0));
        Assert.Equal(P.General + "ResetGimbal", (await _center.NextAsync(P.General + "ResetGimbal")).Address);

        _center.Silent = false;
        await _env.WaitForEventAsync(ObsbotService.ConnectedEvent);

        _center.Silent = true;   // Center quits: no answers for 3 poll ticks → lost
        await _env.WaitForEventAsync(ObsbotService.DisconnectedEvent);
        Assert.False(_obsbot.IsConnected);
        Assert.True(_obsbot.CanSend);
    }

    [Fact]
    public async Task Tcp_connects_frames_messages_and_reconnects()
    {
        await StartAsync(ObsbotTransport.Tcp);
        await _env.WaitForEventAsync(ObsbotService.ConnectedEvent);
        await _center.NextAsync(P.Connected);
        await Wait.UntilAsync(() => _obsbot.Current.AiLocked == false, "ai state");

        await _obsbot.SendAsync(P.Command(P.Tiny + "ToggleAILock", null, 1));
        Assert.Equal([1], (await _center.NextAsync(P.Tiny + "ToggleAILock")).Args);
        var ai = await _env.WaitForEventAsync(ObsbotService.AiTrackingChangedEvent);
        Assert.Equal(true, ai.Args["obsbot.aiLocked"]);

        await _center.PushAsync(new OscMessage("/OBSBOT/Camera/Tail/Custom", 5, "x"));
        var message = await _env.WaitForEventAsync(ObsbotService.MessageEvent);
        Assert.Equal("/OBSBOT/Camera/Tail/Custom", message.Args["obsbot.address"]);
        Assert.Equal(5, message.Args["obsbot.arg0"]);
        Assert.Equal("""[5,"x"]""", message.Args["obsbot.args"]);

        _center.DropClient();
        await _env.WaitForEventAsync(ObsbotService.DisconnectedEvent);
        await Wait.UntilAsync(() => _center.TcpClients == 2 && _obsbot.IsConnected, "reconnected");
    }

    [Fact]
    public async Task Sub_actions_send_the_documented_commands()
    {
        await StartAsync(ObsbotTransport.Udp);
        await _env.WaitForEventAsync(ObsbotService.ConnectedEvent);
        await Wait.UntilAsync(() => _obsbot.Current.Zoom is not null && _obsbot.Current.AiLocked is not null, "state");
        await using var runner = new ActionRunner(ObsbotSubActions.Create(_obsbot));
        _center.Clear();

        async Task<OscMessage> Run(string type, Dictionary<string, string> parameters, string expectAddress)
        {
            var (ok, _) = await runner.RunAsync((type, parameters));
            Assert.True(ok, runner.LastError);
            return await _center.NextAsync(expectAddress);
        }

        Assert.Equal([1, 55], (await Run("obsbot.setZoom", new() { ["zoom"] = "55", ["device"] = "2" }, P.General + "SetZoom")).Args);
        Assert.Equal([80, 4], (await Run("obsbot.setZoom", new() { ["zoom"] = "80", ["speed"] = "4" }, P.General + "SetZoomSpeed")).Args);
        Assert.Equal([0], (await Run("obsbot.zoomLimit", new() { ["limit"] = "Min" }, P.General + "SetZoomMin")).Args);
        Assert.Equal([30, -129, 20], (await Run("obsbot.setGimbalPosition",
            new() { ["pan"] = "-140", ["pitch"] = "20", ["speed"] = "30" }, P.General + "SetGimMotorDegree")).Args);   // pan clamped
        Assert.Equal([30f, 12.5f, -3f], (await Run("obsbot.setGimbalPosition",
            new() { ["pan"] = "12.5", ["pitch"] = "-3", ["speed"] = "30", ["precise"] = "True" }, P.General + "SetGimMotorDegreeEx")).Args);
        Assert.Equal([2], (await Run("obsbot.setView", new() { ["view"] = "65°" }, P.General + "SetView")).Args);
        Assert.Equal([1], (await Run("obsbot.recording", new() { ["state"] = "Start" }, P.General + "SetPCRecording")).Args);
        Assert.Equal([0, 0], (await Run("obsbot.recording", new() { ["target"] = "Camera recording (Tail)", ["state"] = "Stop", ["device"] = "1" }, P.Tail + "SetRecording")).Args);
        Assert.Equal([1], (await Run("obsbot.snapshot", [], P.General + "PCSnapshot")).Args);
        Assert.Equal([1], (await Run("obsbot.selectDevice", new() { ["device"] = "2" }, P.General + "SelectDevice")).Args);
        Assert.Equal([2], (await Run("obsbot.triggerPreset", new() { ["preset"] = "3" }, P.Tiny + "TriggerPreset")).Args);
        Assert.Equal([0], (await Run("obsbot.triggerPreset", new() { ["preset"] = "1", ["series"] = "Tail" }, P.Tail + "TriggerPreset")).Args);
        Assert.Equal([1], (await Run("obsbot.aiLock", new() { ["state"] = "Toggle" }, P.Tiny + "ToggleAILock")).Args);   // was unlocked
        Assert.Equal([2], (await Run("obsbot.setTrackingMode", new() { ["mode"] = "Motion" }, P.Tiny + "SetTrackingMode")).Args);
        Assert.Equal([7], (await Run("obsbot.setTrackingSpeed", new() { ["speed"] = "7", ["axis"] = "Tilt" }, P.Tail + "SetTiltTrackingSpeed")).Args);
        Assert.Equal([1], (await Run("obsbot.setAxisLock", new() { ["axis"] = "Pan", ["state"] = "Lock" }, P.Tail + "SetPanAxisLock")).Args);
        Assert.Equal([3], (await Run("obsbot.setVirtualBackground", new() { ["mode"] = "Replacement" }, P.Meet + "SetVirtualBackground")).Args);
        Assert.Equal([-0.5f, 1.25f], (await Run("obsbot.setImage",
            new() { ["setting"] = "White balance shift", ["value"] = "-0.5", ["value2"] = "1.25" }, P.General + "SetWhiteBalanceShift")).Args);
        Assert.Equal([5600], (await Run("obsbot.setImage", new() { ["setting"] = "Color temperature", ["value"] = "5600" }, P.General + "SetColorTemperature")).Args);
        Assert.Equal([2, 0], (await Run("obsbot.setImage", new() { ["setting"] = "Tail focus mode", ["value"] = "2" }, P.Tail + "SetFocusMode")).Args);
        Assert.Equal([0], (await Run("obsbot.wakeSleep", new() { ["state"] = "Sleep" }, P.General + "WakeSleep")).Args);
        Assert.Equal(["a b", 4], (await Run("obsbot.sendOsc", new() { ["address"] = "/custom", ["args"] = """["a b", 4]""" }, "/custom")).Args);

        var (_, gimbal) = await runner.RunAsync(("obsbot.moveGimbal", new() { ["direction"] = "Left", ["speed"] = "40", ["duration"] = "50" }));
        Assert.Equal([40], (await _center.NextAsync(P.General + "SetGimbalLeft")).Args);
        Assert.Equal([0], (await _center.NextAsync(P.General + "SetGimbalLeft")).Args);   // stopped after the duration
        _ = gimbal;

        var (stateOk, args) = await runner.RunAsync(("obsbot.getState", null));
        Assert.True(stateOk);
        Assert.Equal(true, args["obsbot.isConnected"]);
        Assert.Equal("Desk", args["obsbot.preset1.name"]);
        Assert.Equal(55, args["obsbot.zoom"]);

        var (bad, _) = await runner.RunAsync(("obsbot.setZoom", new() { ["zoom"] = "150" }));
        Assert.False(bad);
        Assert.Contains("between 0 and 100", runner.LastError);
    }

    [Fact]
    public async Task Commands_fail_cleanly_when_not_started()
    {
        _center = new FakeObsbotCenter(ObsbotTransport.Udp);
        _obsbot = new ObsbotService(_env.Settings, _env.Bus, _env.Log, OscTransports.Create, TestTimings.Fast, Tick);
        await Assert.ThrowsAsync<StreamAppNotConnectedException>(() => _obsbot.SendAsync(new OscMessage("/x")));
        await using var runner = new ActionRunner(ObsbotSubActions.Create(_obsbot));
        var (ok, args) = await runner.RunAsync(("obsbot.getState", null));
        Assert.True(ok);
        Assert.Equal(false, args["obsbot.isConnected"]);
        var (zoomed, _) = await runner.RunAsync(("obsbot.setZoom", new() { ["zoom"] = "10" }));
        Assert.False(zoomed);
        Assert.Contains("OBSBOT is not connected", runner.LastError);
        await _obsbot.StartAsync();   // auto-connect is off by default
        Assert.False(_obsbot.IsRunning);
    }

    [Fact]
    public async Task Tcp_to_a_closed_port_retries_with_a_status()
    {
        _center = new FakeObsbotCenter(ObsbotTransport.Tcp);
        var port = _center.Port;
        await _center.DisposeAsync();
        _center = new FakeObsbotCenter(ObsbotTransport.Udp);   // something to dispose later
        _env.Settings.Value.Obsbot.Transport = ObsbotTransport.Tcp;
        _env.Settings.Value.Obsbot.Port = port;
        _obsbot = new ObsbotService(_env.Settings, _env.Bus, _env.Log, OscTransports.Create, TestTimings.Fast, Tick);
        await _obsbot.ConnectAsync();
        await Wait.UntilAsync(() => _obsbot.StatusMessage.Contains("not reachable"), "retry status");
        Assert.Equal(ConnectionState.Connecting, _obsbot.State);
        Assert.Empty(_env.Events(ObsbotService.ConnectedEvent));
    }
}

public sealed class ObsbotTriggerTests
{
    private static bool Matches(string id, string type, Dictionary<string, object?> args, Dictionary<string, string>? settings = null)
    {
        var trigger = new ObsbotTriggers().GetTriggers().Single(t => t.TypeId == id);
        return trigger.EventTypes.Contains(type) && trigger.Matches(new BotEvent(type, args), new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());
    }

    [Fact]
    public void Triggers_filter_by_state_and_address()
    {
        var triggers = new ObsbotTriggers().GetTriggers().ToArray();
        Assert.All(triggers, t => Assert.StartsWith("obsbot.", t.TypeId));
        Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());

        var locked = new Dictionary<string, object?> { ["obsbot.aiLocked"] = true };
        Assert.True(Matches(ObsbotService.AiTrackingChangedEvent, ObsbotService.AiTrackingChangedEvent, locked, new() { ["stateFilter"] = "Locked" }));
        Assert.False(Matches(ObsbotService.AiTrackingChangedEvent, ObsbotService.AiTrackingChangedEvent, locked, new() { ["stateFilter"] = "Unlocked" }));
        var asleep = new Dictionary<string, object?> { ["obsbot.awake"] = false };
        Assert.True(Matches(ObsbotService.SleepStateChangedEvent, ObsbotService.SleepStateChangedEvent, asleep, new() { ["stateFilter"] = "Asleep" }));
        var message = new Dictionary<string, object?> { ["obsbot.address"] = "/a/b" };
        Assert.True(Matches(ObsbotService.MessageEvent, ObsbotService.MessageEvent, message, new() { ["address"] = "/x, /a/b" }));
        Assert.False(Matches(ObsbotService.MessageEvent, ObsbotService.MessageEvent, message, new() { ["address"] = "/x" }));
    }
}
