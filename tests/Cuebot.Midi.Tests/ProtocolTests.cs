using Cuebot.Midi.Backends;
using Cuebot.Core.Events;
using Cuebot.Midi.Protocol;

namespace Cuebot.Midi.Tests;

public sealed class MidiMessageTests
{
    private static MidiMessage Parse(params byte[] bytes)
    {
        Assert.True(MidiMessage.TryParse(bytes, out var message));
        return message;
    }

    [Fact]
    public void Parses_channel_messages()
    {
        var note = Parse(0x91, 60, 100);
        Assert.Equal(MidiCommand.NoteOn, note.Command);
        Assert.Equal(2, note.ChannelNumber);
        Assert.Equal(60, note.Data1);
        Assert.Equal(100, note.Value);
        Assert.Equal(0x91 | 60 << 8 | 100 << 16, note.Packed);
        Assert.Equal("6569105", note.Raw);
        Assert.Equal("91 3C 64", note.Hex);

        var cc = Parse(0xB0, 7, 127);
        Assert.Equal(MidiCommand.ControlChange, cc.Command);
        Assert.Equal(1.0, cc.ValueNormal);

        var program = Parse(0xC5, 12);
        Assert.Equal(MidiCommand.PatchChange, program.Command);
        Assert.Equal(6, program.ChannelNumber);
        Assert.Equal(12, program.Value);

        var pitch = Parse(0xE0, 0x00, 0x40);
        Assert.Equal(MidiCommand.PitchWheelChange, pitch.Command);
        Assert.Equal(8192, pitch.Value);
    }

    [Fact]
    public void Note_on_with_velocity_zero_is_note_off()
    {
        var message = Parse(0x90, 64, 0);
        Assert.Equal(MidiCommand.NoteOff, message.Command);
        Assert.Equal("90 40 00", message.Hex);
    }

    [Fact]
    public void Extra_bytes_after_a_short_message_are_ignored_and_bad_input_is_rejected()
    {
        Assert.Equal(3, Parse(0x80, 1, 2, 99).Bytes.Length);
        Assert.False(MidiMessage.TryParse([], out _));
        Assert.False(MidiMessage.TryParse([0x40, 1], out _));
        Assert.False(MidiMessage.TryParse([0x90, 1], out _));
        Assert.False(MidiMessage.TryParse([0x90, 0x80, 1], out _));
        Assert.False(MidiMessage.TryParse([0xF0, 1, 2], out _));
        Assert.False(MidiMessage.TryParse([0xF4], out _));
    }

    [Fact]
    public void System_messages()
    {
        Assert.Equal(MidiCommand.TimingClock, Parse(0xF8).Command);
        Assert.Equal(0, Parse(0xF8).ChannelNumber);
        var sysex = Parse(0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7);
        Assert.Equal(MidiCommand.Sysex, sysex.Command);
        Assert.Equal("F0 7E 7F 06 01 F7", sysex.Raw);
        Assert.Equal(0, sysex.Packed);
    }

    [Fact]
    public void Builds_messages_and_validates_ranges()
    {
        Assert.Equal("9F 3C 7F", MidiMessage.Channel(MidiCommand.NoteOn, 16, 60, 127).Hex);
        Assert.Equal("C0 05", MidiMessage.Channel(MidiCommand.PatchChange, 1, 5, 99).Hex);
        Assert.Equal("E1 7F 7F", MidiMessage.PitchWheel(2, 16383).Hex);
        Assert.Throws<ArgumentException>(() => MidiMessage.Channel(MidiCommand.NoteOn, 0, 60, 1));
        Assert.Throws<ArgumentException>(() => MidiMessage.Channel(MidiCommand.NoteOn, 17, 60, 1));
        Assert.Throws<ArgumentException>(() => MidiMessage.Channel(MidiCommand.ControlChange, 1, 128, 1));
        Assert.Throws<ArgumentException>(() => MidiMessage.Channel(MidiCommand.Sysex, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => MidiMessage.PitchWheel(1, 16384));
    }

    [Theory]
    [InlineData("F0 7E 7F 06 01 F7")]
    [InlineData("7E 7F 06 01")]
    [InlineData("0x7E,0x7F,0x06,0x01")]
    [InlineData("7e7f0601")]
    public void Sysex_from_hex_adds_framing(string hex) =>
        Assert.Equal("F0 7E 7F 06 01 F7", MidiMessage.SysexFromHex(hex).Hex);

    [Fact]
    public void Sysex_rejects_bad_data()
    {
        Assert.Throws<ArgumentException>(() => MidiMessage.SysexFromHex(""));
        Assert.Throws<ArgumentException>(() => MidiMessage.SysexFromHex("F0 F7"));
        Assert.Throws<ArgumentException>(() => MidiMessage.SysexFromHex("F0 80 F7"));
        Assert.Throws<ArgumentException>(() => MidiMessage.SysexFromHex("zz"));
    }

    [Fact]
    public void Raw_hex_messages()
    {
        Assert.Equal(MidiCommand.ControlChange, MidiMessage.FromHex("B0 07 64").Command);
        Assert.Equal(MidiCommand.Sysex, MidiMessage.FromHex("F0 01 F7").Command);
        Assert.Throws<ArgumentException>(() => MidiMessage.FromHex("B0 07"));
        Assert.Throws<ArgumentException>(() => MidiMessage.FromHex("B0 07 64 01"));
    }

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(61, "C#4")]
    [InlineData(0, "C-1")]
    [InlineData(127, "G9")]
    [InlineData(69, "A4")]
    public void Note_names(int note, string name)
    {
        Assert.Equal(name, MidiNames.Note(note));
        Assert.True(MidiNames.TryParseNote(name, out var parsed));
        Assert.Equal(note, parsed);
    }

    [Fact]
    public void Note_parsing_accepts_numbers_and_flats()
    {
        Assert.True(MidiNames.TryParseNote("Bb3", out var n));
        Assert.Equal(58, n);
        Assert.True(MidiNames.TryParseNote(" 64 ", out n));
        Assert.Equal(64, n);
        Assert.False(MidiNames.TryParseNote("H2", out _));
        Assert.False(MidiNames.TryParseNote("200", out _));
    }

    [Fact]
    public void Controller_names()
    {
        Assert.Equal("Pan", MidiNames.Controller(10));
        Assert.Equal("MainVolume", MidiNames.Controller(7));
        Assert.Equal("Sustain", MidiNames.Controller(64));
        Assert.Equal("PanLsb", MidiNames.Controller(42));
        Assert.Equal("AllNotesOff", MidiNames.Controller(123));
        Assert.Equal("3", MidiNames.Controller(3));
    }

    [Theory]
    [InlineData("NoteOn", MidiCommand.NoteOn)]
    [InlineData("note off", MidiCommand.NoteOff)]
    [InlineData("cc", MidiCommand.ControlChange)]
    [InlineData("Program Change", MidiCommand.PatchChange)]
    [InlineData("pitch bend", MidiCommand.PitchWheelChange)]
    [InlineData("ChannelAfterTouch", MidiCommand.ChannelAfterTouch)]
    [InlineData("144", MidiCommand.NoteOn)]
    [InlineData("0xB3", MidiCommand.ControlChange)]
    public void Command_parsing(string text, MidiCommand expected)
    {
        Assert.True(MidiNames.TryParseCommand(text, out var command));
        Assert.Equal(expected, command);
    }

    [Fact]
    public void Command_parsing_rejects_unknown_values()
    {
        Assert.False(MidiNames.TryParseCommand("bogus", out _));
        Assert.False(MidiNames.TryParseCommand("12", out _));
    }
}

public sealed class MidiEventMatcherTests
{
    private static MidiMessage M(params byte[] bytes) => MidiMessage.TryParse(bytes, out var m) ? m : throw new InvalidOperationException();

    [Fact]
    public void Matches_command_channel_and_data()
    {
        var listener = new MidiInEventConfig { Name = "Pad", Command = MidiCommand.NoteOn, Channel = 10, Data = 36 };
        Assert.True(MidiEventMatcher.Matches(listener, M(0x99, 36, 100)));
        Assert.False(MidiEventMatcher.Matches(listener, M(0x98, 36, 100)));
        Assert.False(MidiEventMatcher.Matches(listener, M(0x99, 37, 100)));
        Assert.False(MidiEventMatcher.Matches(listener, M(0x89, 36, 0)));
        listener.IgnoreChannel = true;
        Assert.True(MidiEventMatcher.Matches(listener, M(0x90, 36, 1)));
        listener.IgnoreData = true;
        Assert.True(MidiEventMatcher.Matches(listener, M(0x90, 99, 1)));
        listener.Enabled = false;
        Assert.False(MidiEventMatcher.Matches(listener, M(0x90, 99, 1)));
    }

    [Fact]
    public void Ignore_note_off_skips_note_off_and_zero_velocity_note_on()
    {
        var listener = new MidiInEventConfig { Command = MidiCommand.NoteOff, Channel = 1, IgnoreData = true };
        Assert.True(MidiEventMatcher.Matches(listener, M(0x90, 60, 0)));
        listener.IgnoreNoteOff = true;
        Assert.False(MidiEventMatcher.Matches(listener, M(0x90, 60, 0)));
        Assert.False(MidiEventMatcher.Matches(listener, M(0x80, 60, 0)));
    }

    [Fact]
    public void Sysex_listeners_compare_the_raw_bytes()
    {
        var listener = new MidiInEventConfig { Command = MidiCommand.Sysex, RawMessage = "f0 01 02 f7" };
        Assert.True(MidiEventMatcher.Matches(listener, M(0xF0, 1, 2, 0xF7)));
        Assert.False(MidiEventMatcher.Matches(listener, M(0xF0, 1, 3, 0xF7)));
        listener.IgnoreData = true;
        Assert.True(MidiEventMatcher.Matches(listener, M(0xF0, 1, 3, 0xF7)));
    }

    [Fact]
    public void System_messages_ignore_the_channel()
    {
        var listener = new MidiInEventConfig { Command = MidiCommand.StartSequence, Channel = 5, IgnoreData = true };
        Assert.True(MidiEventMatcher.Matches(listener, M(0xFA)));
    }

    [Fact]
    public void Learning_fills_the_listener()
    {
        var learned = MidiEventMatcher.Learn(M(0xB2, 10, 64));
        Assert.Equal(MidiCommand.ControlChange, learned.Command);
        Assert.Equal(3, learned.Channel);
        Assert.Equal(10, learned.Data);
        Assert.Equal("ControlChange Pan (ch 3)", learned.Name);
        Assert.Equal(M(0xB2, 10, 64).Raw, learned.RawMessage);
        Assert.True(MidiEventMatcher.Matches(learned, M(0xB2, 10, 0)));

        var named = MidiEventMatcher.Learn(M(0x90, 60, 1), new MidiInEventConfig { Name = "Keep" });
        Assert.Equal("Keep", named.Name);
        Assert.Equal("NoteOn C4 (ch 1)", MidiEventMatcher.Describe(M(0x90, 60, 1)));
    }

    [Fact]
    public void Args_use_streamer_bot_variable_names()
    {
        var connection = new MidiDeviceConfig { Name = "Pads", Device = "nanoPAD2" };
        var listener = new MidiInEventConfig { Name = "Kick" };
        var note = MidiEventMatcher.Args(M(0x90, 60, 127), connection, [listener], 1234);
        Assert.Equal("Pads", note["name"]);
        Assert.Equal("nanoPAD2", note["deviceName"]);
        Assert.Equal(1, note["channel"]);
        Assert.Equal("NoteOn", note["commandCode"]);
        Assert.Equal(144, note["commandCodeNum"]);
        Assert.Equal("C4", note["noteName"]);
        Assert.Equal(60, note["noteNumber"]);
        Assert.Equal(127, note["velocity"]);
        Assert.Equal(1.0, note["velocityNormal"]);
        Assert.Equal(127, note["value"]);
        Assert.Equal(1234L, note["timestamp"]);
        Assert.Equal("Kick", note["eventName"]);
        Assert.False(note.ContainsKey("controllerName"));

        var cc = MidiEventMatcher.Args(M(0xB0, 10, 64), connection, [], 0);
        Assert.Equal("Pan", cc["controllerName"]);
        Assert.Equal(10, cc["controllerNumber"]);
        Assert.Equal(64, cc["value"]);
        Assert.Equal(Math.Round(64 / 127.0, 6), cc["valueNormal"]);
        Assert.Equal("", cc["eventName"]);
        Assert.False(cc.ContainsKey("noteName"));
    }

    [Fact]
    public void Trigger_filters_device_and_event()
    {
        var connection = new MidiDeviceConfig { Id = "abc", Name = "Pads", Device = "nanoPAD2" };
        var e = new BotEvent(MidiEvents.Message, MidiEventMatcher.Args(M(0x90, 60, 127), connection,
            [new MidiInEventConfig { Id = "e1", Name = "Kick" }, new MidiInEventConfig { Id = "e2", Name = "Snare" }], 0));
        Assert.True(MidiTriggers.Matches(e, "Any", "Any"));
        Assert.True(MidiTriggers.Matches(e, "", ""));
        Assert.True(MidiTriggers.Matches(e, "pads", "snare"));
        Assert.True(MidiTriggers.Matches(e, "nanoPAD2", "e1"));
        Assert.True(MidiTriggers.Matches(e, "abc", "Any"));
        Assert.False(MidiTriggers.Matches(e, "Keys", "Any"));
        Assert.False(MidiTriggers.Matches(e, "Any", "Hat"));
    }
}

public sealed class PlatformBackendTests
{
    /// <summary>
    /// The real backend for this OS only lists ports here (nothing is opened): on CI machines without MIDI hardware, a
    /// sequencer device or libasound it must report "no devices" or a reason instead of throwing.
    /// </summary>
    [Fact]
    public void Listing_ports_degrades_gracefully()
    {
        using var backend = MidiBackends.CreateDefault();
        var reason = backend.UnavailableReason;
        var inputs = backend.ListPorts(MidiDirection.Input);
        var outputs = backend.ListPorts(MidiDirection.Output);
        Assert.NotNull(inputs);
        Assert.NotNull(outputs);
        if (reason is not null) Assert.Empty(inputs);
        Assert.False(string.IsNullOrEmpty(backend.Name));
    }

    [Fact]
    public void Duplicate_port_names_are_made_unique()
    {
        Assert.Equal(["Pad", "Pad #2", "Keys", "Pad #3", "MIDI device"], PortNames.Unique(["Pad", "Pad", "Keys", " Pad ", ""]));
    }
}
