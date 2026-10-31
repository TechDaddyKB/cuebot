using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;

namespace Cuebot.Midi;

/// <summary>The MIDI Message trigger. Type ids are stored in actions.json and must never change.</summary>
internal sealed class MidiTriggers(MidiService midi) : ITriggerProvider
{
    public const string Category = "MIDI";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(MidiEvents.Message, Category, "MIDI Message", MidiEvents.Message,
        [
            new ParameterDescriptor("device", "Device")
            {
                DefaultValue = "Any",
                Help = "A configured MIDI In device (name), or Any",
                Suggestions = () => ["Any", .. midi.Inputs.Select(c => c.Name).Distinct()],
            },
            new ParameterDescriptor("event", "Event")
            {
                DefaultValue = "Any",
                Help = "A configured MIDI In event (name), or Any for every message from the device",
                Suggestions = () => ["Any", .. midi.AllEvents().Select(e => e.Event.Name).Distinct()],
            },
        ], (e, settings, _) => Matches(e, settings["device"], settings["event"])),
    ];

    internal static bool Matches(BotEvent e, string device, string eventFilter)
    {
        if (!IsAny(device) && !Equal(device, Text(e, "name")) && !Equal(device, Text(e, "deviceId")) && !Equal(device, Text(e, "deviceName")))
            return false;
        if (IsAny(eventFilter)) return true;
        var names = Text(e, "eventNames").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ids = Text(e, "eventIds").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return names.Concat(ids).Any(n => Equal(eventFilter, n));
    }

    private static bool IsAny(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase);

    private static bool Equal(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Text(BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>MIDI Out sub-actions (Streamer.bot: Note On, Control Change, Generic Event, Sysex Event). Type ids must never change.</summary>
internal static class MidiSubActions
{
    public const string Category = "MIDI";
    public const string NoteOn = "midi.noteOn", ControlChange = "midi.controlChange", GenericEvent = "midi.genericEvent", SysexEvent = "midi.sysexEvent";

    public static readonly string[] TypeIds = [NoteOn, ControlChange, GenericEvent, SysexEvent];

    /// <summary>The commands Generic Event offers (Streamer.bot's list).</summary>
    public static readonly string[] GenericCommands = MidiGeneric.Commands;

    public static IReadOnlyList<ISubActionHandler> Create(MidiService midi)
    {
        ParameterDescriptor Name() => new("name", "Name") { Help = "A label for this MIDI event (not sent)" };
        ParameterDescriptor Device() => new("device", "Device")
        {
            Help = "A configured MIDI Out device; empty = the first one. Closed devices are opened.",
            Suggestions = () => midi.Outputs.Select(c => c.Name).Distinct(),
        };
        ParameterDescriptor Channel() => new("channel", "Channel") { DefaultValue = "1", Help = "1-16; leave empty to send on all channels" };
        ParameterDescriptor Byte(string key, string label, string help, string defaultValue = "0") =>
            new(key, label, ParameterKind.Number) { DefaultValue = defaultValue, Help = help };

        return
        [
            new MidiSubAction(NoteOn, "Note On",
            [
                Name(), Device(), Channel(),
                new ParameterDescriptor("note", "Note") { DefaultValue = "60", Help = "Note number 0-127 (MIDI byte 2) or a name such as C4 (= 60)" },
                Byte("velocity", "Velocity", "0-127 (MIDI byte 3)", "127"),
                Byte("duration", "Duration (ms)", "How long the note is held before Note Off", "500"),
                new ParameterDescriptor("sendNoteOff", "Send Note Off", ParameterKind.Boolean) { DefaultValue = "true", Help = "Send Note Off when the duration ends" },
            ], async (_, v, ct) =>
            {
                var note = MidiNames.TryParseNote(v["note"], out var n) ? n : throw new ArgumentException($"Note must be 0-127 or a note name (got '{v["note"]}').");
                await midi.SendNoteAsync(v["device"], v["channel"], note, Data(v["velocity"], "Velocity"), Math.Max(0, v.GetInt("duration")),
                    v.GetBool("sendNoteOff", true), ct).ConfigureAwait(false);
            }),
            new MidiSubAction(ControlChange, "Control Change",
            [
                Name(), Device(), Channel(),
                Byte("controller", "Controller", "Control function 0-127 (MIDI byte 2), e.g. 7 = volume, 10 = pan"),
                Byte("value", "Value", "0-127 (MIDI byte 3)"),
            ], (_, v, ct) => midi.SendChannelAsync(v["device"], v["channel"],
                c => MidiMessage.Channel(MidiCommand.ControlChange, c, Data(v["controller"], "Controller"), Data(v["value"], "Value")), ct)),
            new MidiSubAction(GenericEvent, "Send any MIDI message (advanced)",
            [
                Name(), Device(),
                new ParameterDescriptor("command", "Command", ParameterKind.Choice) { DefaultValue = GenericCommands[0], Choices = GenericCommands },
                Channel(),
                Byte("data1", "Data1", "MIDI byte 2 (note, controller, program, pressure; pitch wheel: LSB, or the whole 0-16383 value when Data2 is empty)"),
                new ParameterDescriptor("data2", "Data2") { DefaultValue = "0", Help = "MIDI byte 3 (velocity, value; pitch wheel: MSB). Unused by PatchChange and ChannelAfterTouch" },
            ], (_, v, ct) =>
            {
                var command = MidiNames.TryParseCommand(v["command"], out var c) && GenericCommands.Contains(c.ToString())
                    ? c
                    : throw new ArgumentException($"Unknown command '{v["command"]}'. Use one of {string.Join(", ", GenericCommands)}.");
                return midi.SendChannelAsync(v["device"], v["channel"], ch => Generic(command, ch, v["data1"], v["data2"]), ct);
            }),
            new MidiSubAction(SysexEvent, "Send SysEx message (advanced)",
            [
                Name(), Device(),
                new ParameterDescriptor("data", "Data", ParameterKind.Multiline)
                {
                    Help = "Hex bytes, e.g. F0 7E 7F 06 01 F7 (F0/F7 are added when missing; %variables% allowed)",
                },
            ], (_, v, ct) => midi.SendAsync(v["device"], MidiMessage.SysexFromHex(v["data"]), ct)),
        ];
    }

    internal static MidiMessage Generic(MidiCommand command, int channel, string data1, string data2) => MidiGeneric.Build(command, channel, data1, data2);

    internal static int Data(string text, string label) => MidiGeneric.Data(text, label);

    private sealed class MidiSubAction(
        string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => MidiSubActions.Category;
        public string DisplayName => displayName;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues values, CancellationToken ct)
        {
            await run(context, values, ct).ConfigureAwait(false);
            return SubActionResult.Next;
        }
    }
}

internal static class MidiDescribe
{
    public static Dictionary<string, object?> Connection(MidiConnection c) => new()
    {
        ["id"] = c.Id,
        ["name"] = c.Name,
        ["device"] = c.Device,
        ["direction"] = c.Direction == MidiDirection.Input ? "in" : "out",
        ["state"] = c.State.ToString(),
        ["open"] = c.IsOpen,
        ["present"] = c.IsPresent,
        ["message"] = c.Message,
        ["autoOpen"] = c.Config.AutoOpen,
        ["reopenOnClose"] = c.Config.ReopenOnClose,
        ["retryInterval"] = c.Config.RetryIntervalSeconds,
        ["events"] = c.Config.Events.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["name"] = e.Name,
            ["enabled"] = e.Enabled,
            ["command"] = e.Command.ToString(),
            ["channel"] = e.Channel,
            ["ignoreChannel"] = e.IgnoreChannel,
            ["data"] = e.Data,
            ["ignoreData"] = e.IgnoreData,
            ["rawMessage"] = e.RawMessage,
            ["ignoreNoteOff"] = e.IgnoreNoteOff,
        }).ToArray(),
    };
}
