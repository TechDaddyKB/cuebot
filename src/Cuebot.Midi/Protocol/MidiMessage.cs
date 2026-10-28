using System.Globalization;
using System.Text;

namespace Cuebot.Midi.Protocol;

/// <summary>MIDI 1.0 message types (status byte; channel messages without the channel nibble). Names follow Streamer.bot's.</summary>
public enum MidiCommand
{
    NoteOff = 0x80,
    NoteOn = 0x90,
    KeyAfterTouch = 0xA0,
    ControlChange = 0xB0,
    PatchChange = 0xC0,
    ChannelAfterTouch = 0xD0,
    PitchWheelChange = 0xE0,
    Sysex = 0xF0,
    TimeCodeQuarterFrame = 0xF1,
    SongPosition = 0xF2,
    SongSelect = 0xF3,
    TuneRequest = 0xF6,
    TimingClock = 0xF8,
    StartSequence = 0xFA,
    ContinueSequence = 0xFB,
    StopSequence = 0xFC,
    AutoSensing = 0xFE,
    SystemReset = 0xFF,
}

/// <summary>
/// One complete MIDI 1.0 message (status byte plus data, or a whole F0 ... F7 SysEx). Immutable; parse incoming bytes
/// with <see cref="TryParse"/>, build outgoing ones with <see cref="Channel(MidiCommand, int, int, int)"/> or
/// <see cref="SysexFromHex"/>.
/// </summary>
public sealed class MidiMessage
{
    private readonly byte[] _bytes;

    private MidiMessage(byte[] bytes)
    {
        _bytes = bytes;
        var status = bytes[0];
        if (status < 0xF0)
        {
            var command = (MidiCommand)(status & 0xF0);
            ChannelNumber = (status & 0x0F) + 1;
            Data1 = bytes.Length > 1 ? bytes[1] : 0;
            Data2 = bytes.Length > 2 ? bytes[2] : 0;
            // Running practice: NoteOn with velocity 0 is a NoteOff (most keyboards send it that way).
            Command = command == MidiCommand.NoteOn && Data2 == 0 ? MidiCommand.NoteOff : command;
        }
        else
        {
            Command = (MidiCommand)status;
            Data1 = status is 0xF1 or 0xF2 or 0xF3 && bytes.Length > 1 ? bytes[1] : 0;
            Data2 = status == 0xF2 && bytes.Length > 2 ? bytes[2] : 0;
        }
    }

    public MidiCommand Command { get; }

    /// <summary>1-16 for channel messages, 0 for system messages.</summary>
    public int ChannelNumber { get; }

    public int Data1 { get; }
    public int Data2 { get; }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public bool IsChannelMessage => ChannelNumber > 0;

    /// <summary>
    /// The value an event listener's "Data" compares with: note number, controller number, program, pressure, or the
    /// pitch wheel's LSB.
    /// </summary>
    public int DataKey => Data1;

    /// <summary>Streamer.bot's raw message value: status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16 (the WinMM short-message layout).</summary>
    public int Packed => _bytes.Length > 3 ? 0 : _bytes[0] | (Data1 << 8) | (Data2 << 16);

    /// <summary>Bytes as upper-case hex separated by spaces, e.g. "90 3C 7F".</summary>
    public string Hex => ToHex(_bytes);

    /// <summary>The raw message as shown and stored: the packed value for short messages, hex for SysEx.</summary>
    public string Raw => Command == MidiCommand.Sysex ? Hex : Packed.ToString(CultureInfo.InvariantCulture);

    /// <summary>The message's value: velocity, controller value, program, pressure, or the 14-bit pitch wheel (0-16383).</summary>
    public int Value => Command switch
    {
        MidiCommand.PatchChange or MidiCommand.ChannelAfterTouch or MidiCommand.SongSelect => Data1,
        MidiCommand.PitchWheelChange or MidiCommand.SongPosition => Data1 | (Data2 << 7),
        MidiCommand.NoteOn or MidiCommand.NoteOff or MidiCommand.KeyAfterTouch or MidiCommand.ControlChange => Data2,
        _ => 0,
    };

    /// <summary><see cref="Value"/> scaled to 0-1.</summary>
    public double ValueNormal => Command is MidiCommand.PitchWheelChange or MidiCommand.SongPosition ? Value / 16383.0 : Value / 127.0;

    public override string ToString() => $"{Command} {Hex}";

    /// <summary>Parses one complete message. Returns false for empty input, a data byte without status, or a truncated message.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out MidiMessage message)
    {
        message = null!;
        if (bytes.IsEmpty || bytes[0] < 0x80) return false;
        var status = bytes[0];
        if (status == 0xF0)
        {
            if (bytes.Length < 2 || bytes[^1] != 0xF7) return false;
        }
        else
        {
            var length = Length(status);
            if (length <= 0 || bytes.Length < length) return false;
            bytes = bytes[..length];
            for (var i = 1; i < bytes.Length; i++)
                if (bytes[i] > 0x7F) return false;
        }
        message = new MidiMessage(bytes.ToArray());
        return true;
    }

    /// <summary>Total length (status included) of a non-SysEx message; 0 for unknown or undefined status bytes.</summary>
    public static int Length(byte status) => status switch
    {
        < 0x80 => 0,
        < 0xC0 => 3,
        < 0xE0 => 2,
        < 0xF0 => 3,
        0xF1 or 0xF3 => 2,
        0xF2 => 3,
        0xF6 or 0xF8 or 0xFA or 0xFB or 0xFC or 0xFE or 0xFF => 1,
        _ => 0,
    };

    /// <summary>Builds a channel message (channel 1-16). For PitchWheelChange, <paramref name="data1"/> is the LSB and
    /// <paramref name="data2"/> the MSB; use <see cref="PitchWheel"/> for a 14-bit value.</summary>
    public static MidiMessage Channel(MidiCommand command, int channel, int data1, int data2 = 0)
    {
        if ((int)command is < 0x80 or > 0xE0 || ((int)command & 0x0F) != 0)
            throw new ArgumentException($"{command} is not a channel message.");
        if (channel is < 1 or > 16) throw new ArgumentException($"Channel must be 1-16 (got {channel}).");
        CheckData(data1, "Data1");
        var status = (byte)((int)command | (channel - 1));
        if (Length(status) == 2) return new MidiMessage([status, (byte)data1]);
        CheckData(data2, "Data2");
        return new MidiMessage([status, (byte)data1, (byte)data2]);
    }

    public static MidiMessage PitchWheel(int channel, int value)
    {
        if (value is < 0 or > 16383) throw new ArgumentException($"Pitch wheel value must be 0-16383 (got {value}).");
        return Channel(MidiCommand.PitchWheelChange, channel, value & 0x7F, value >> 7);
    }

    /// <summary>
    /// A SysEx message from hex bytes ("F0 7E 7F 06 01 F7", "7E7F0601", "0x7E,0x7F"). The F0 and F7 framing is added
    /// when missing; data bytes must be 0x00-0x7F.
    /// </summary>
    public static MidiMessage SysexFromHex(string hex)
    {
        var bytes = ParseHex(hex);
        var body = bytes.AsSpan();
        if (body.Length > 0 && body[0] == 0xF0) body = body[1..];
        if (body.Length > 0 && body[^1] == 0xF7) body = body[..^1];
        if (body.IsEmpty) throw new ArgumentException("SysEx data is empty.");
        foreach (var b in body)
            if (b > 0x7F) throw new ArgumentException($"SysEx data bytes must be 00-7F (got {b:X2}).");
        var framed = new byte[body.Length + 2];
        framed[0] = 0xF0;
        body.CopyTo(framed.AsSpan(1));
        framed[^1] = 0xF7;
        return new MidiMessage(framed);
    }

    /// <summary>Any complete message from hex bytes (e.g. "B0 07 64").</summary>
    public static MidiMessage FromHex(string hex)
    {
        var bytes = ParseHex(hex);
        if (bytes.Length > 0 && bytes[0] == 0xF0) return SysexFromHex(hex);
        return TryParse(bytes, out var message) && message.Bytes.Length == bytes.Length
            ? message
            : throw new ArgumentException($"'{hex}' is not one complete MIDI message.");
    }

    /// <summary>Hex bytes separated by spaces, commas, dashes or nothing; "0x" prefixes are allowed.</summary>
    public static byte[] ParseHex(string hex)
    {
        var digits = new StringBuilder();
        foreach (var token in hex.Split([' ', ',', ';', '-', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (t.Length % 2 == 1) t = "0" + t;
            digits.Append(t);
        }
        try
        {
            return Convert.FromHexString(digits.ToString());
        }
        catch (FormatException)
        {
            throw new ArgumentException($"'{hex}' is not a list of hex bytes.");
        }
    }

    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void CheckData(int value, string name)
    {
        if (value is < 0 or > 127) throw new ArgumentException($"{name} must be 0-127 (got {value}).");
    }
}

/// <summary>Display names for notes and controllers.</summary>
public static class MidiNames
{
    private static readonly string[] Notes = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Note name with octave, middle C (60) = C4.</summary>
    public static string Note(int note) => note is < 0 or > 127 ? "" : $"{Notes[note % 12]}{note / 12 - 1}";

    /// <summary>Parses "C4", "F#2", "Bb3" or a number (0-127).</summary>
    public static bool TryParseNote(string text, out int note)
    {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out note)) return note is >= 0 and <= 127;
        note = -1;
        if (text.Length < 2) return false;
        var index = Array.IndexOf(["C", "", "D", "", "E", "F", "", "G", "", "A", "", "B"], char.ToUpperInvariant(text[0]).ToString());
        if (index < 0) return false;
        var rest = text[1..];
        if (rest.StartsWith('#')) { index++; rest = rest[1..]; }
        else if (rest.StartsWith('b')) { index--; rest = rest[1..]; }
        if (!int.TryParse(rest, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var octave)) return false;
        note = (octave + 1) * 12 + index;
        return note is >= 0 and <= 127;
    }

    /// <summary>MIDI 1.0 control change function names (midi.org's control change table); the number for undefined ones.</summary>
    public static string Controller(int controller) => controller switch
    {
        0 => "BankSelect",
        1 => "Modulation",
        2 => "BreathController",
        4 => "FootController",
        5 => "PortamentoTime",
        6 => "DataEntry",
        7 => "MainVolume",
        8 => "Balance",
        10 => "Pan",
        11 => "Expression",
        12 => "EffectControl1",
        13 => "EffectControl2",
        >= 16 and <= 19 => $"GeneralPurpose{controller - 15}",
        >= 32 and <= 63 => $"{Controller(controller - 32)}Lsb",
        64 => "Sustain",
        65 => "Portamento",
        66 => "Sostenuto",
        67 => "SoftPedal",
        68 => "LegatoFootswitch",
        69 => "Hold2",
        >= 70 and <= 79 => $"SoundController{controller - 69}",
        >= 80 and <= 83 => $"GeneralPurpose{controller - 75}",
        84 => "PortamentoControl",
        88 => "HighResolutionVelocityPrefix",
        >= 91 and <= 95 => $"Effects{controller - 90}Depth",
        96 => "DataIncrement",
        97 => "DataDecrement",
        98 => "NonRegisteredParameterLsb",
        99 => "NonRegisteredParameterMsb",
        100 => "RegisteredParameterLsb",
        101 => "RegisteredParameterMsb",
        120 => "AllSoundOff",
        121 => "ResetAllControllers",
        122 => "LocalControl",
        123 => "AllNotesOff",
        124 => "OmniOff",
        125 => "OmniOn",
        126 => "MonoOn",
        127 => "PolyOn",
        _ => controller.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Parses a command name leniently ("NoteOn", "note on", "cc", "pitch bend"), or a status number (144 / 0x90).</summary>
    public static bool TryParseCommand(string text, out MidiCommand command)
    {
        var key = text.Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
        command = key.ToLowerInvariant() switch
        {
            "noteon" => MidiCommand.NoteOn,
            "noteoff" => MidiCommand.NoteOff,
            "cc" or "controlchange" or "controller" => MidiCommand.ControlChange,
            "pc" or "patchchange" or "programchange" => MidiCommand.PatchChange,
            "channelaftertouch" or "channelpressure" => MidiCommand.ChannelAfterTouch,
            "keyaftertouch" or "polyaftertouch" or "polypressure" => MidiCommand.KeyAfterTouch,
            "pitchwheelchange" or "pitchwheel" or "pitchbend" => MidiCommand.PitchWheelChange,
            "sysex" or "systemexclusive" => MidiCommand.Sysex,
            _ => 0,
        };
        if (command != 0) return true;
        if (Enum.TryParse(key, ignoreCase: true, out command) && Enum.IsDefined(command) && !int.TryParse(key, out _)) return true;
        var number = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(key[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : -1
            : int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : -1;
        if (number is >= 0x80 and < 0xF0) number &= 0xF0;
        command = (MidiCommand)number;
        return number >= 0x80 && Enum.IsDefined(command);
    }
}

/// <summary>Streamer.bot's "Generic Event": one of six channel commands with Data1/Data2 given as text.</summary>
public static class MidiGeneric
{
    /// <summary>The commands Generic Event offers.</summary>
    public static readonly string[] Commands =
    [
        nameof(MidiCommand.NoteOn), nameof(MidiCommand.NoteOff), nameof(MidiCommand.ControlChange), nameof(MidiCommand.PatchChange),
        nameof(MidiCommand.ChannelAfterTouch), nameof(MidiCommand.PitchWheelChange),
    ];

    /// <summary>
    /// Builds the message. PatchChange and ChannelAfterTouch ignore Data2; PitchWheelChange takes Data1 as LSB and Data2 as
    /// MSB, or Data1 as the whole 14-bit value (0-16383) when Data2 is empty. Any channel command is accepted.
    /// </summary>
    public static MidiMessage Build(MidiCommand command, int channel, string data1, string data2)
    {
        if (command == MidiCommand.PitchWheelChange && string.IsNullOrWhiteSpace(data2))
            return MidiMessage.PitchWheel(channel, Number(data1, "Data1"));
        var d2 = command is MidiCommand.PatchChange or MidiCommand.ChannelAfterTouch || string.IsNullOrWhiteSpace(data2) ? 0 : Data(data2, "Data2");
        return MidiMessage.Channel(command, channel, Data(data1, "Data1"), d2);
    }

    /// <summary>A 0-127 data byte from text.</summary>
    public static int Data(string text, string label)
    {
        var value = Number(text, label);
        return value is >= 0 and <= 127 ? value : throw new ArgumentException($"{label} must be 0-127 (got {value}).");
    }

    private static int Number(string text, string label) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d)
            ? (int)d
            : throw new ArgumentException($"{label} must be a whole number (got '{text}').");
}
