using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;

namespace Cuebot.Midi;

/// <summary>MIDI functions for user scripts (Streamer.bot documents no C# MIDI methods, so these are Cuebot's own).</summary>
internal sealed class MidiScriptFunctions(MidiService midi) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("midi.isAvailable", "True when MIDI works on this system; see midi.status for the reason when not", [],
            (_, _, _) => Task.FromResult<object?>(midi.IsAvailable)),
        new("midi.status", "Backend name, availability reason and dashboard status", [], (_, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>
        {
            ["backend"] = midi.BackendName,
            ["available"] = midi.IsAvailable,
            ["reason"] = midi.UnavailableReason,
            ["state"] = midi.State.ToString(),
            ["message"] = midi.StatusMessage,
        })),
        new("midi.ports", "System MIDI ports by direction (\"in\" or \"out\"; empty = both) after a fresh scan", ["direction"], (_, a, _) =>
        {
            var direction = a.GetString("direction").Trim().ToLowerInvariant();
            var result = new Dictionary<string, object?>();
            if (direction is "" or "in" or "input") result["in"] = midi.RefreshPorts(MidiDirection.Input).Select(p => p.Name).ToArray();
            if (direction is "" or "out" or "output") result["out"] = midi.RefreshPorts(MidiDirection.Output).Select(p => p.Name).ToArray();
            return Task.FromResult<object?>(result);
        }),
        new("midi.devices", "Configured MIDI In and Out connections with their state and (In) event listeners", [],
            (_, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["in"] = midi.Inputs.Select(MidiDescribe.Connection).ToArray(),
                ["out"] = midi.Outputs.Select(MidiDescribe.Connection).ToArray(),
            })),
        new("midi.open", "Open a configured connection (direction in/out, default out); returns whether it is open", ["device", "direction"],
            (_, a, _) => Task.FromResult<object?>(midi.Open(midi.Resolve(Direction(a), a.GetString("device"))))),
        new("midi.close", "Close a configured connection (direction in/out, default out)", ["device", "direction"], (_, a, _) =>
        {
            midi.Close(midi.Resolve(Direction(a), a.GetString("device")));
            return Task.FromResult<object?>(true);
        }),
        new("midi.sendNoteOn", "Note On (note 0-127 or a name like C4, velocity 0-127); channel empty = all; with noteOff, Note Off after duration ms",
            ["device", "channel", "note", "velocity", "duration", "noteOff"], async (_, a, ct) =>
            {
                var noteText = a.GetString("note", "60");
                var note = MidiNames.TryParseNote(noteText, out var n) ? n : throw new ArgumentException($"Unknown note '{noteText}'.");
                await midi.SendNoteAsync(a.GetString("device"), Channel(a), note, Int(a, "velocity", 127), Int(a, "duration", 0), a.GetBool("noteOff"), ct)
                    .ConfigureAwait(false);
                return true;
            }),
        new("midi.sendNoteOff", "Note Off", ["device", "channel", "note", "velocity"], async (_, a, ct) =>
        {
            var noteText = a.GetString("note", "60");
            var note = MidiNames.TryParseNote(noteText, out var n) ? n : throw new ArgumentException($"Unknown note '{noteText}'.");
            await midi.SendChannelAsync(a.GetString("device"), Channel(a), c => MidiMessage.Channel(MidiCommand.NoteOff, c, note, Int(a, "velocity", 0)), ct)
                .ConfigureAwait(false);
            return true;
        }),
        new("midi.sendControlChange", "Control Change (controller 0-127, value 0-127)", ["device", "channel", "controller", "value"], async (_, a, ct) =>
        {
            await midi.SendChannelAsync(a.GetString("device"), Channel(a),
                c => MidiMessage.Channel(MidiCommand.ControlChange, c, Int(a, "controller", 0), Int(a, "value", 0)), ct).ConfigureAwait(false);
            return true;
        }),
        new("midi.sendEvent", "Generic event: command NoteOn, NoteOff, ControlChange, PatchChange, ChannelAfterTouch, PitchWheelChange (or KeyAfterTouch)",
            ["device", "command", "channel", "data1", "data2"], async (_, a, ct) =>
            {
                var commandText = a.GetString("command");
                var command = MidiNames.TryParseCommand(commandText, out var c) && (int)c < 0xF0
                    ? c
                    : throw new ArgumentException($"Unknown channel command '{commandText}'.");
                await midi.SendChannelAsync(a.GetString("device"), Channel(a),
                    ch => MidiSubActions.Generic(command, ch, a.GetString("data1", "0"), a.GetString("data2")), ct).ConfigureAwait(false);
                return true;
            }),
        new("midi.sendSysex", "SysEx from hex bytes (F0/F7 added when missing)", ["device", "data"], async (_, a, ct) =>
        {
            await midi.SendAsync(a.GetString("device"), MidiMessage.SysexFromHex(a.GetString("data")), ct).ConfigureAwait(false);
            return true;
        }),
        new("midi.sendRaw", "Any one complete MIDI message as hex bytes, e.g. \"B0 07 64\"", ["device", "data"], async (_, a, ct) =>
        {
            await midi.SendAsync(a.GetString("device"), MidiMessage.FromHex(a.GetString("data")), ct).ConfigureAwait(false);
            return true;
        }),
    ];

    private static MidiDirection Direction(JsonElement a) =>
        a.GetString("direction").Trim().ToLowerInvariant() is "in" or "input" ? MidiDirection.Input : MidiDirection.Output;

    private static string Channel(JsonElement a) => a.GetString("channel", "1");

    private static int Int(JsonElement a, string name, int fallback)
    {
        var text = a.GetString(name, fallback.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"{name} must be a whole number.");
    }
}
