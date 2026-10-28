using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Midi.Tests;

public sealed class MidiModuleTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-midi-di-").FullName;
    private readonly FakeMidiBackend _backend = new();
    private readonly ServiceProvider _services;

    public MidiModuleTests()
    {
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddSingleton<IMidiBackend>(_backend).AddMidi()
            .AddSingleton<ISubActionHandler>(new CaptureStep()).BuildServiceProvider();
    }

    private MidiService Midi => _services.GetRequiredService<MidiService>();

    [Fact]
    public void Registers_status_trigger_sub_actions_and_script_functions()
    {
        Assert.Contains(_services.GetServices<IConnectionStatus>(), s => s.Name == "MIDI");
        var registry = _services.GetRequiredService<Registry>();
        var trigger = Assert.Single(registry.Triggers, t => t.Category == "MIDI");
        Assert.Equal("midi.message", trigger.TypeId);
        Assert.Equal("MIDI Message", trigger.DisplayName);
        Assert.Equal(["device", "event"], trigger.Parameters.Select(p => p.Key));
        Assert.Equal(MidiSubActions.TypeIds.Order(), registry.SubActions.Where(s => s.Category == "MIDI").Select(s => s.TypeId).Order());
        var generic = registry.GetSubAction("midi.genericEvent")!;
        Assert.Equal(MidiSubActions.GenericCommands, generic.Parameters.Single(p => p.Key == "command").Choices);

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "midi.isAvailable", "midi.ports", "midi.devices", "midi.open", "midi.close", "midi.sendNoteOn", "midi.sendNoteOff",
                     "midi.sendControlChange", "midi.sendEvent", "midi.sendSysex", "midi.sendRaw", "midi.status" })
            Assert.Contains(name, functions);
    }

    [Fact]
    public void Trigger_suggestions_list_configured_devices_and_events()
    {
        _backend.Plug(MidiDirection.Input, "nanoPAD2");
        var pads = Midi.AddDevice(MidiDirection.Input, new MidiDeviceConfig { Name = "Pads", Device = "nanoPAD2" });
        Midi.AddEvent(pads, new MidiInEventConfig { Name = "Kick" });
        var trigger = _services.GetRequiredService<Registry>().GetTrigger("midi.message")!;
        Assert.Equal(["Any", "Pads"], trigger.Parameters[0].Suggestions!());
        Assert.Equal(["Any", "Kick"], trigger.Parameters[1].Suggestions!());
    }

    [Fact]
    public async Task Sub_actions_send_through_the_backend()
    {
        _backend.Plug(MidiDirection.Output, "APC");
        Midi.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Lights", Device = "APC" });
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "MIDI out",
            SubActions =
            [
                Step("midi.noteOn", ("device", "Lights"), ("channel", "2"), ("note", "C4"), ("velocity", "%vel%"), ("duration", "0"), ("sendNoteOff", "true")),
                Step("midi.controlChange", ("device", "Lights"), ("channel", "1"), ("controller", "7"), ("value", "100")),
                Step("midi.genericEvent", ("device", "Lights"), ("command", "PatchChange"), ("channel", "1"), ("data1", "5")),
                Step("midi.genericEvent", ("device", "Lights"), ("command", "PitchWheelChange"), ("channel", "1"), ("data1", "16383"), ("data2", "")),
                Step("midi.genericEvent", ("device", "Lights"), ("command", "ChannelAfterTouch"), ("channel", "16"), ("data1", "64")),
                Step("midi.sysexEvent", ("device", "Lights"), ("data", "7E 7F 06 01")),
            ],
        };
        Assert.True(await engine.RunAsync(action, new Dictionary<string, object?> { ["vel"] = 90 }));
        Assert.Equal(["91 3C 5A", "81 3C 00", "B0 07 64", "C0 05", "E0 7F 7F", "DF 40", "F0 7E 7F 06 01 F7"],
            _backend.Sent.Select(s => MidiMessage.ToHex(s.Bytes)));

        var bad = new ActionDefinition { Name = "bad", SubActions = [Step("midi.controlChange", ("device", "Lights"), ("controller", "200"))] };
        await engine.RunAsync(bad);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("0-127") == true);
    }

    [Fact]
    public async Task Incoming_messages_run_actions_with_the_midi_message_trigger()
    {
        _backend.Plug(MidiDirection.Input, "nanoPAD2");
        var pads = Midi.AddDevice(MidiDirection.Input, new MidiDeviceConfig { Name = "Pads", Device = "nanoPAD2" });
        Midi.AddEvent(pads, new MidiInEventConfig { Name = "Kick", Command = MidiCommand.NoteOn, Channel = 1, Data = 36 });
        await Midi.StartAsync();
        var trigger = _services.GetRequiredService<Registry>().GetTrigger("midi.message")!;
        bool Fires(string device, string ev, params byte[] bytes)
        {
            var events = new List<BotEvent>();
            using var sub = _services.GetRequiredService<EventBus>().Subscribe(events.Add);
            _backend.Emit("nanoPAD2", bytes);
            var e = Assert.Single(events);
            var settings = ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["device"] = device, ["event"] = ev });
            return trigger.Matches(e, new TriggerDefinition(), settings, new Dictionary<string, object?>());
        }
        Assert.True(Fires("Pads", "Kick", 0x90, 36, 1));
        Assert.False(Fires("Pads", "Kick", 0x90, 37, 1));
        Assert.True(Fires("Pads", "Any", 0x90, 37, 1));
        Assert.False(Fires("Keys", "Any", 0x90, 36, 1));
    }

    [Fact]
    public async Task Script_functions_send_and_describe()
    {
        _backend.Plug(MidiDirection.Output, "APC").Plug(MidiDirection.Input, "nanoPAD2");
        Midi.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Lights", Device = "APC" });
        var api = _services.GetRequiredService<ScriptApi>();
        var engine = _services.GetRequiredService<ActionEngine>();
        var capture = (CaptureStep)_services.GetServices<ISubActionHandler>().Single(h => h.TypeId == "test.capture");
        await engine.RunAsync(new ActionDefinition { Name = "ctx", SubActions = [new SubActionDefinition { Type = "test.capture" }] });
        var ctx = capture.Context!;

        Assert.Equal(true, await api.CallAsync(ctx, "midi.sendControlChange", new { device = "Lights", channel = 2, controller = 1, value = 64 }, default));
        Assert.Equal(true, await api.CallAsync(ctx, "midi.sendEvent", new { device = "Lights", command = "noteoff", channel = "1", data1 = 60, data2 = 0 }, default));
        Assert.Equal(true, await api.CallAsync(ctx, "midi.sendRaw", new { device = "Lights", data = "FA" }, default));
        Assert.Equal(true, await api.CallAsync(ctx, "midi.sendNoteOn", new { device = "Lights", note = "A4", velocity = 1 }, default));
        Assert.Equal(["B1 01 40", "80 3C 00", "FA", "90 45 01"], _backend.Sent.Select(s => MidiMessage.ToHex(s.Bytes)));

        var ports = (Dictionary<string, object?>)(await api.CallAsync(ctx, "midi.ports", new { }, default))!;
        Assert.Equal(["nanoPAD2"], (string[])ports["in"]!);
        var devices = (Dictionary<string, object?>)(await api.CallAsync(ctx, "midi.devices", new { }, default))!;
        var lights = Assert.Single((Dictionary<string, object?>[])devices["out"]!);
        Assert.Equal(true, lights["open"]);
        Assert.Equal(true, await api.CallAsync(ctx, "midi.close", new { device = "Lights" }, default));
        Assert.False(Midi.Outputs[0].IsOpen);
        Assert.Equal(true, await api.CallAsync(ctx, "midi.open", new { device = "Lights", direction = "out" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => api.CallAsync(ctx, "midi.sendEvent", new { device = "Lights", command = "sysex" }, default));
    }

    private static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters) =>
        new() { Type = type, Parameters = parameters.ToDictionary(p => p.Key, p => p.Value) };

    private sealed class CaptureStep : ISubActionHandler
    {
        public ActionRunContext? Context { get; private set; }
        public string TypeId => "test.capture";
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Context = context;
            return Task.FromResult(SubActionResult.Next);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
