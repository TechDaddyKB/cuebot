namespace Cuebot.Midi.Protocol;

/// <summary>Bot event types published by <see cref="MidiService"/>.</summary>
public static class MidiEvents
{
    /// <summary>Every message received on an open MIDI In connection (Streamer.bot's "MIDI Message" trigger).</summary>
    public const string Message = "midi.message";
}

/// <summary>Pure matching and argument logic for MIDI In event listeners.</summary>
public static class MidiEventMatcher
{
    /// <summary>Whether an incoming message is the listener's event (see PROTOCOL.md for the rules).</summary>
    public static bool Matches(MidiInEventConfig listener, MidiMessage message)
    {
        if (!listener.Enabled) return false;
        if (listener.IgnoreNoteOff && message.Command == MidiCommand.NoteOff) return false;
        if (listener.Command != message.Command) return false;
        if (!listener.IgnoreChannel && message.IsChannelMessage && listener.Channel != message.ChannelNumber) return false;
        if (listener.IgnoreData) return true;
        return message.Command == MidiCommand.Sysex
            ? string.Equals(Normalize(listener.RawMessage), message.Hex, StringComparison.OrdinalIgnoreCase)
            : listener.Data == message.DataKey;
    }

    /// <summary>The listener a learned message produces (Streamer.bot's automatic detection).</summary>
    public static MidiInEventConfig Learn(MidiMessage message, MidiInEventConfig? into = null)
    {
        var listener = into ?? new MidiInEventConfig();
        listener.Command = message.Command;
        listener.Channel = message.IsChannelMessage ? message.ChannelNumber : 1;
        listener.IgnoreChannel = !message.IsChannelMessage;
        listener.Data = message.DataKey;
        listener.RawMessage = message.Raw;
        if (string.IsNullOrWhiteSpace(listener.Name)) listener.Name = Describe(message);
        return listener;
    }

    /// <summary>A short label, e.g. "NoteOn C4 (ch 1)" or "ControlChange Pan (ch 2)".</summary>
    public static string Describe(MidiMessage message)
    {
        var channel = message.IsChannelMessage ? $" (ch {message.ChannelNumber})" : "";
        return message.Command switch
        {
            MidiCommand.NoteOn or MidiCommand.NoteOff or MidiCommand.KeyAfterTouch => $"{message.Command} {MidiNames.Note(message.Data1)}{channel}",
            MidiCommand.ControlChange => $"{message.Command} {MidiNames.Controller(message.Data1)}{channel}",
            MidiCommand.PatchChange => $"{message.Command} {message.Data1}{channel}",
            _ => $"{message.Command}{channel}",
        };
    }

    /// <summary>The MIDI Message trigger's variables (Streamer.bot names) plus Cuebot's extras.</summary>
    public static Dictionary<string, object?> Args(MidiMessage message, MidiDeviceConfig connection, IReadOnlyList<MidiInEventConfig> matched,
        long timestampMs)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = connection.Name,
            ["deviceName"] = connection.Device,
            ["deviceId"] = connection.Id,
            ["channel"] = message.ChannelNumber,
            ["commandCode"] = message.Command.ToString(),
            ["commandCodeNum"] = (int)message.Command,
            ["rawMessage"] = message.Raw,
            ["rawBytes"] = message.Hex,
            ["timestamp"] = timestampMs,
            ["value"] = message.Value,
            ["valueNormal"] = Math.Round(message.ValueNormal, 6),
            ["eventName"] = matched.Count > 0 ? matched[0].Name : "",
            ["eventId"] = matched.Count > 0 ? matched[0].Id : "",
            ["eventNames"] = string.Join('\n', matched.Select(m => m.Name.Replace('\n', ' '))),
            ["eventIds"] = string.Join('\n', matched.Select(m => m.Id)),
        };
        switch (message.Command)
        {
            case MidiCommand.NoteOn or MidiCommand.NoteOff or MidiCommand.KeyAfterTouch:
                args["noteNumber"] = message.Data1;
                args["noteName"] = MidiNames.Note(message.Data1);
                if (message.Command != MidiCommand.KeyAfterTouch)
                {
                    args["velocity"] = message.Data2;
                    args["velocityNormal"] = Math.Round(message.Data2 / 127.0, 6);
                }
                break;
            case MidiCommand.ControlChange:
                args["controllerNumber"] = message.Data1;
                args["controllerName"] = MidiNames.Controller(message.Data1);
                break;
        }
        return args;
    }

    private static string Normalize(string hex)
    {
        try
        {
            return MidiMessage.ToHex(MidiMessage.ParseHex(hex));
        }
        catch (ArgumentException)
        {
            return hex.Trim();
        }
    }
}
