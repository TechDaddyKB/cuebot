using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Midi;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against the real MIDI stack (WinMM on Windows, ALSA sequencer on Linux). Nothing goes live. See
/// docs/LIVE_TESTING.md: CUEBOT_LIVE_MIDI=1 lists the ports; CUEBOT_LIVE_MIDI_OUT sends a few harmless messages to that
/// output; CUEBOT_LIVE_MIDI_LOOPBACK_IN (with _OUT) checks a full round trip through a loopback cable; CUEBOT_LIVE_MIDI_IN
/// waits for you to press a control on that input.
/// </summary>
public sealed class MidiLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [LiveServiceFact("CUEBOT_LIVE_MIDI", flag: true)]
    public void Ports_are_listed_or_the_backend_explains_why_not()
    {
        using var backend = MidiBackends.CreateDefault();
        output.WriteLine($"Backend: {backend.Name}; unavailable: {backend.UnavailableReason ?? "no"}");
        foreach (var direction in new[] { MidiDirection.Input, MidiDirection.Output })
        {
            var ports = backend.ListPorts(direction);
            output.WriteLine($"{direction}: {(ports.Count == 0 ? "(none)" : string.Join(", ", ports.Select(p => $"{p.Name} {p.Detail}".Trim())))}");
            Assert.Equal(ports.Count, ports.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [LiveServiceFact("CUEBOT_LIVE_MIDI_OUT")]
    public async Task Output_accepts_notes_controllers_and_sysex()
    {
        await using var services = Build("midi-out");
        var midi = services.GetRequiredService<MidiService>();
        var name = ServicesLiveEnvironment.Get("CUEBOT_LIVE_MIDI_OUT")!;
        var connection = midi.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Live out", Device = name, AutoOpen = false });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(midi.Open(connection), connection.Message);
        output.WriteLine($"open {clock.ElapsedMilliseconds} ms");
        await midi.SendNoteAsync("Live out", "1", 60, 1, 50, noteOff: true);
        await midi.SendAsync("Live out", MidiMessage.Channel(MidiCommand.ControlChange, 1, 123, 0));       // All Notes Off
        output.WriteLine($"short messages done {clock.ElapsedMilliseconds} ms");
        await midi.SendAsync("Live out", MidiMessage.SysexFromHex("7E 7F 06 01"));                         // Identity Request
        output.WriteLine($"sysex done {clock.ElapsedMilliseconds} ms");
        output.WriteLine($"{connection}: sent Note On/Off, All Notes Off, Identity Request");
        midi.Close(connection);
        output.WriteLine($"closed {clock.ElapsedMilliseconds} ms");
    }

    [LiveServiceFact("CUEBOT_LIVE_MIDI_LOOPBACK_IN")]
    public async Task Loopback_round_trip_runs_the_midi_message_trigger_variables()
    {
        await using var services = Build("midi-loopback");
        var midi = services.GetRequiredService<MidiService>();
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var inName = ServicesLiveEnvironment.Get("CUEBOT_LIVE_MIDI_LOOPBACK_IN")!;
        var outName = ServicesLiveEnvironment.Get("CUEBOT_LIVE_MIDI_LOOPBACK_OUT") ?? inName;
        var input = midi.AddDevice(MidiDirection.Input, new MidiDeviceConfig { Name = "Loop in", Device = inName, AutoOpen = false });
        midi.AddEvent(input, new MidiInEventConfig { Name = "Probe", Command = MidiCommand.ControlChange, Channel = 16, Data = 85 });
        midi.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Loop out", Device = outName, AutoOpen = false });
        Assert.True(midi.Open(input), input.Message);

        await midi.SendAsync("Loop out", MidiMessage.Channel(MidiCommand.ControlChange, 16, 85, 42));
        await midi.SendAsync("Loop out", MidiMessage.SysexFromHex("7D 01 02 03"));   // 7D = non-commercial manufacturer id
        var start = DateTime.UtcNow;
        while (events.Count < 2 && DateTime.UtcNow - start < Timeout) await Task.Delay(20);

        var cc = Assert.Single(events, e => Equals(e.Args["commandCode"], "ControlChange"));
        Assert.Equal("Probe", cc.Args["eventName"]);
        Assert.Equal(16, cc.Args["channel"]);
        Assert.Equal(42, cc.Args["value"]);
        var sysex = Assert.Single(events, e => Equals(e.Args["commandCode"], "Sysex"));
        Assert.Equal("F0 7D 01 02 03 F7", sysex.Args["rawMessage"]);
    }

    [LiveServiceFact("CUEBOT_LIVE_MIDI_IN")]
    public async Task Learns_a_control_pressed_on_a_real_device()
    {
        await using var services = Build("midi-in");
        var midi = services.GetRequiredService<MidiService>();
        var input = midi.AddDevice(MidiDirection.Input,
            new MidiDeviceConfig { Name = "Live in", Device = ServicesLiveEnvironment.Get("CUEBOT_LIVE_MIDI_IN")!, AutoOpen = false });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        output.WriteLine("Press a key, pad or knob on the device within 30 seconds...");
        var message = await midi.LearnAsync(input, ignoreNoteOff: true, cts.Token);
        var learned = MidiEventMatcher.Learn(message);
        output.WriteLine($"Learned {learned.Name}: raw {learned.RawMessage} ({message.Hex})");
        Assert.True(MidiEventMatcher.Matches(learned, message));
    }

    private static ServiceProvider Build(string name)
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, name);
        Directory.CreateDirectory(dataDirectory);
        var midiJson = Path.Combine(dataDirectory, "midi.json");
        if (File.Exists(midiJson)) File.Delete(midiJson);   // every run starts from a clean device list
        return new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddMidi().BuildServiceProvider();
    }
}
