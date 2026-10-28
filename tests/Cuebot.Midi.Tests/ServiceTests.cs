using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;

namespace Cuebot.Midi.Tests;

public sealed class MidiServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-midi-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly FakeMidiBackend _backend = new();
    private readonly ManualTime _time = new();
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private readonly JsonFile<MidiSettings> _settings;
    private MidiService? _service;

    public MidiServiceTests()
    {
        _bus = new EventBus(_log);
        _bus.Subscribe(_events.Enqueue);
        _settings = new JsonFile<MidiSettings>(Path.Combine(_dir, "midi.json"), _log);
        _settings.Value.MonitorIntervalMs = 60000;   // the tests drive CheckDevices themselves
    }

    private MidiService Service => _service ??= new MidiService(_settings, _bus, _log, _backend, _time);

    private MidiConnection AddInput(string name = "Pads", string device = "nanoPAD2", bool reopen = true, int retry = 5)
    {
        _backend.Plug(MidiDirection.Input, device);
        return Service.AddDevice(MidiDirection.Input, new MidiDeviceConfig { Name = name, Device = device, ReopenOnClose = reopen, RetryIntervalSeconds = retry });
    }

    [Fact]
    public async Task Auto_open_on_startup_and_messages_become_events()
    {
        var pads = AddInput();
        Service.AddEvent(pads, new MidiInEventConfig { Name = "Kick", Command = MidiCommand.NoteOn, Channel = 10, Data = 36 });
        Service.AddEvent(pads, new MidiInEventConfig { Name = "Any pad", Command = MidiCommand.NoteOn, IgnoreChannel = true, IgnoreData = true });
        Assert.False(pads.IsOpen);

        await Service.StartAsync();
        Assert.True(pads.IsOpen);
        Assert.Equal(ConnectionState.Connected, Service.State);
        Assert.Equal("1 of 1 MIDI device open", Service.StatusMessage);

        _backend.Emit("nanoPAD2", 0x99, 36, 90);
        var e = Assert.Single(_events);
        Assert.Equal(MidiEvents.Message, e.Type);
        Assert.Equal("Pads", e.Args["name"]);
        Assert.Equal("Kick", e.Args["eventName"]);
        Assert.Equal("Kick\nAny pad", e.Args["eventNames"]);
        Assert.Equal(10, e.Args["channel"]);
        Assert.Equal(90, e.Args["velocity"]);
        Assert.Equal(42L, e.Args["timestamp"]);

        _backend.Emit("nanoPAD2", 0x90, 1, 1);
        Assert.Equal("Any pad", _events.Last().Args["eventName"]);
    }

    [Fact]
    public async Task Clock_and_active_sensing_are_dropped_unless_asked_for()
    {
        AddInput();
        await Service.StartAsync();
        _backend.Emit("nanoPAD2", 0xF8);
        _backend.Emit("nanoPAD2", 0xFE);
        _backend.Emit("nanoPAD2", 0x40);   // stray data byte
        Assert.Empty(_events);
        Service.Settings.IgnoreClockAndSensing = false;
        _backend.Emit("nanoPAD2", 0xF8);
        Assert.Equal("TimingClock", Assert.Single(_events).Args["commandCode"]);
    }

    [Fact]
    public async Task Unplugged_device_waits_and_reopens_after_the_retry_interval()
    {
        var pads = AddInput(retry: 10);
        await Service.StartAsync();
        Assert.Equal(1, _backend.OpenCount);

        _backend.Unplug(MidiDirection.Input, "nanoPAD2");
        Service.CheckDevices();
        Assert.Equal(MidiConnectionState.Waiting, pads.State);
        Assert.Contains("unplugged", pads.Message);
        Assert.Equal(0, _backend.OpenInputs("nanoPAD2"));
        Assert.Equal(ConnectionState.Connecting, Service.State);

        _backend.Plug(MidiDirection.Input, "nanoPAD2");
        _time.Advance(TimeSpan.FromSeconds(5));
        Service.CheckDevices();
        Assert.False(pads.IsOpen);   // not yet due

        _time.Advance(TimeSpan.FromSeconds(6));
        Service.CheckDevices();
        Assert.True(pads.IsOpen);
        Assert.Equal(2, _backend.OpenCount);
        _backend.Emit("nanoPAD2", 0x90, 60, 1);
        Assert.Single(_events);
    }

    [Fact]
    public async Task Without_reopen_an_unplugged_device_stays_closed()
    {
        var pads = AddInput(reopen: false);
        await Service.StartAsync();
        _backend.Unplug(MidiDirection.Input, "nanoPAD2", notify: true);
        Assert.Equal(MidiConnectionState.Closed, pads.State);
        _backend.Plug(MidiDirection.Input, "nanoPAD2");
        _time.Advance(TimeSpan.FromMinutes(5));
        Service.CheckDevices();
        Assert.False(pads.IsOpen);
        Assert.True(Service.Open(pads));
    }

    [Fact]
    public async Task Backend_close_notification_triggers_reopen()
    {
        var pads = AddInput(retry: 1);
        await Service.StartAsync();
        _backend.Unplug(MidiDirection.Input, "nanoPAD2", notify: true);
        Assert.Equal(MidiConnectionState.Waiting, pads.State);
        _backend.Plug(MidiDirection.Input, "nanoPAD2");
        _time.Advance(TimeSpan.FromSeconds(2));
        Service.CheckDevices();
        Assert.True(pads.IsOpen);
    }

    [Fact]
    public async Task Missing_device_at_startup_is_retried_or_reported()
    {
        _settings.Value.Inputs.Add(new MidiDeviceConfig { Name = "Keys", Device = "Keystation", ReopenOnClose = true });
        _settings.Value.Outputs.Add(new MidiDeviceConfig { Name = "Lights", Device = "APC", ReopenOnClose = false });
        await Service.StartAsync();
        var keys = Service.Inputs.Single();
        var lights = Service.Outputs.Single();
        Assert.Equal(MidiConnectionState.Waiting, keys.State);
        Assert.Equal(MidiConnectionState.Error, lights.State);
        Assert.Contains("not connected", lights.Message);

        _backend.Plug(MidiDirection.Input, "Keystation").Plug(MidiDirection.Output, "APC");
        _time.Advance(TimeSpan.FromSeconds(6));
        Service.CheckDevices();
        Assert.True(keys.IsOpen);
        Assert.False(lights.IsOpen);
        Assert.True(lights.IsPresent);
    }

    [Fact]
    public void A_driver_error_on_open_is_reported_or_retried()
    {
        var pads = AddInput(reopen: false);
        _backend.FailOpens = true;
        Assert.False(Service.Open(pads));
        Assert.Equal(MidiConnectionState.Error, pads.State);
        Assert.Contains("cannot open", pads.Message);
        Assert.Equal(ConnectionState.Error, Service.State);

        Service.UpdateDevice(pads, d => d.ReopenOnClose = true);
        Assert.False(Service.Open(pads));
        Assert.Equal(MidiConnectionState.Waiting, pads.State);
        _backend.FailOpens = false;
        _time.Advance(TimeSpan.FromSeconds(6));
        Service.CheckDevices();
        Assert.True(pads.IsOpen);
    }

    [Fact]
    public async Task Closed_connections_do_not_deliver_and_closing_stops_reopen()
    {
        var pads = AddInput();
        await Service.StartAsync();
        Service.Close(pads);
        _backend.Emit("nanoPAD2", 0x90, 60, 1);
        Assert.Empty(_events);
        _time.Advance(TimeSpan.FromMinutes(1));
        Service.CheckDevices();
        Assert.False(pads.IsOpen);
        Assert.Equal(ConnectionState.Disconnected, Service.State);
    }

    [Fact]
    public async Task Learn_returns_the_next_message_and_can_skip_note_off()
    {
        var pads = AddInput();
        await Service.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var learning = Service.LearnAsync(pads, ignoreNoteOff: true, cts.Token);
        _backend.Emit("nanoPAD2", 0x80, 60, 0);
        Assert.False(learning.IsCompleted);
        _backend.Emit("nanoPAD2", 0xB1, 7, 99);
        var message = await learning;
        Assert.Equal(MidiCommand.ControlChange, message.Command);

        var cancelled = new CancellationTokenSource();
        var pending = Service.LearnAsync(pads, false, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Learn_opens_a_closed_input()
    {
        var pads = AddInput();
        Assert.False(pads.IsOpen);
        var learning = Service.LearnAsync(pads, false, CancellationToken.None);
        Assert.True(pads.IsOpen);
        _backend.Emit("nanoPAD2", 0x90, 1, 1);
        Assert.Equal(MidiCommand.NoteOn, (await learning).Command);
    }

    [Fact]
    public async Task Sends_open_the_output_on_demand_and_broadcast_to_all_channels()
    {
        _backend.Plug(MidiDirection.Output, "APC");
        Service.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Lights", Device = "APC", AutoOpen = false });
        await Service.SendChannelAsync("lights", "", c => MidiMessage.Channel(MidiCommand.ControlChange, c, 1, 2));
        Assert.Equal(16, _backend.Sent.Count);
        Assert.Equal(0xBF, _backend.Sent[^1].Bytes[0]);

        _backend.Sent.Clear();
        await Service.SendNoteAsync("", "3", 60, 100, 0, noteOff: true);
        Assert.Equal(["92 3C 64", "82 3C 00"], _backend.Sent.Select(s => MidiMessage.ToHex(s.Bytes)));

        _backend.Sent.Clear();
        await Service.SendAsync("APC", MidiMessage.SysexFromHex("01 02"));
        Assert.Equal("F0 01 02 F7", MidiMessage.ToHex(Assert.Single(_backend.Sent).Bytes));
    }

    [Fact]
    public async Task A_failed_send_marks_the_output_lost()
    {
        _backend.Plug(MidiDirection.Output, "APC");
        var lights = Service.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Lights", Device = "APC", ReopenOnClose = true });
        Assert.True(Service.Open(lights));
        _backend.FailSends = true;
        await Assert.ThrowsAsync<IOException>(() => Service.SendAsync("Lights", MidiMessage.Channel(MidiCommand.NoteOn, 1, 1, 1)));
        Assert.Equal(MidiConnectionState.Waiting, lights.State);
    }

    [Fact]
    public async Task Unknown_devices_and_channels_are_reported()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.SendAsync("x", MidiMessage.Channel(MidiCommand.NoteOn, 1, 1, 1)));
        _backend.Plug(MidiDirection.Output, "APC");
        Service.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Lights", Device = "APC" });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.SendAsync("Nope", MidiMessage.Channel(MidiCommand.NoteOn, 1, 1, 1)));
        Assert.Contains("Lights", error.Message);
        Assert.Throws<ArgumentException>(() => MidiService.Channels("17"));
        Assert.Equal(16, MidiService.Channels("all").Length);
    }

    [Fact]
    public async Task Unavailable_backend_degrades_gracefully()
    {
        _backend.Unavailable = "no sequencer";
        var pads = AddInput();
        await Service.StartAsync();
        Assert.False(Service.IsAvailable);
        Assert.False(pads.IsOpen);
        Assert.Equal(ConnectionState.Disconnected, Service.State);
        Assert.Equal("no sequencer", Service.StatusMessage);
        Service.CheckDevices();
    }

    [Fact]
    public async Task Settings_persist_devices_and_events()
    {
        var pads = AddInput();
        var kick = Service.AddEvent(pads, new MidiInEventConfig { Name = " Kick ", Channel = 99, Data = -3 });
        Assert.Equal("Kick", kick.Name);
        Assert.Equal(16, kick.Channel);
        Assert.Equal(0, kick.Data);
        Service.UpdateEvent(pads, kick, e => e.Data = 36);
        Service.UpdateDevice(pads, d => d.RetryIntervalSeconds = 0);
        Assert.Equal(1, pads.Config.RetryIntervalSeconds);
        _settings.SaveNow();

        var reloaded = new JsonFile<MidiSettings>(Path.Combine(_dir, "midi.json"), _log);
        var device = Assert.Single(reloaded.Value.Inputs);
        Assert.Equal("Pads", device.Name);
        Assert.Equal(36, Assert.Single(device.Events).Data);

        Service.RemoveEvent(pads, kick);
        Service.RemoveDevice(pads);
        Assert.Empty(Service.Inputs);
        Assert.Empty(_settings.Value.Inputs);
        Assert.Throws<ArgumentException>(() => Service.AddEvent(pads, new MidiInEventConfig()));
    }

    [Fact]
    public async Task Changing_the_device_reopens_the_connection()
    {
        var pads = AddInput();
        _backend.Plug(MidiDirection.Input, "Other");
        await Service.StartAsync();
        Service.UpdateDevice(pads, d => d.Device = "Other");
        Assert.True(pads.IsOpen);
        Assert.Equal(0, _backend.OpenInputs("nanoPAD2"));
        Assert.Equal(1, _backend.OpenInputs("Other"));
    }

    [Fact]
    public async Task Ports_changed_from_the_backend_wakes_the_monitor()
    {
        _settings.Value.MonitorIntervalMs = 60000;
        var pads = AddInput(retry: 1);
        await Service.StartAsync();
        _backend.Unplug(MidiDirection.Input, "nanoPAD2", notify: true);
        _time.Advance(TimeSpan.FromSeconds(2));
        _backend.Plug(MidiDirection.Input, "nanoPAD2");   // raises PortsChanged
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!pads.IsOpen && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(pads.IsOpen);
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null) await _service.DisposeAsync();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
