using Cuebot.Midi.Protocol;

namespace Cuebot.Midi;

/// <summary>Persisted MIDI configuration (midi.json): MIDI In and MIDI Out device connections, stored separately.</summary>
public sealed class MidiSettings
{
    /// <summary>MIDI In connections; each has its own event listeners.</summary>
    public List<MidiDeviceConfig> Inputs { get; set; } = [];

    /// <summary>MIDI Out connections, used by the MIDI sub-actions.</summary>
    public List<MidiDeviceConfig> Outputs { get; set; } = [];

    /// <summary>Drop MIDI clock (0xF8) and active sensing (0xFE): devices send them many times a second.</summary>
    public bool IgnoreClockAndSensing { get; set; } = true;

    /// <summary>How often the device list is read to notice unplugged / plugged devices (Windows has no notification).</summary>
    public int MonitorIntervalMs { get; set; } = 2000;
}

/// <summary>One MIDI device connection (Streamer.bot: Name, Device, Auto Open on Startup, Reopen on Close, Retry Interval).</summary>
public sealed class MidiDeviceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The user's label for the connection (<c>%name%</c> in the MIDI Message trigger).</summary>
    public string Name { get; set; } = "";

    /// <summary>The system port name, as listed by the backend (see PROTOCOL.md).</summary>
    public string Device { get; set; } = "";

    public bool AutoOpen { get; set; } = true;

    /// <summary>Reconnect when the connection closes (device unplugged, driver error), every <see cref="RetryIntervalSeconds"/>.</summary>
    public bool ReopenOnClose { get; set; } = true;

    public int RetryIntervalSeconds { get; set; } = 5;

    /// <summary>Event listeners (MIDI In only).</summary>
    public List<MidiInEventConfig> Events { get; set; } = [];
}

/// <summary>A MIDI In event listener: which incoming messages count as this named event.</summary>
public sealed class MidiInEventConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public MidiCommand Command { get; set; } = MidiCommand.NoteOn;

    /// <summary>MIDI channel 1-16.</summary>
    public int Channel { get; set; } = 1;

    public bool IgnoreChannel { get; set; }

    /// <summary>The first data byte: note number, controller number, program, pressure (see PROTOCOL.md).</summary>
    public int Data { get; set; }

    public bool IgnoreData { get; set; }

    /// <summary>The learned message (Streamer.bot packed value; hex bytes for SysEx). Informational, except for SysEx.</summary>
    public string RawMessage { get; set; } = "";

    /// <summary>Do not match NoteOff (and learning skips it), for keyboards that send NoteOn then NoteOff.</summary>
    public bool IgnoreNoteOff { get; set; }
}
