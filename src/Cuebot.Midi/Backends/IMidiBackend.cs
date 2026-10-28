namespace Cuebot.Midi.Backends;

public enum MidiDirection { Input, Output }

/// <summary>A MIDI port the system offers. <see cref="Name"/> is the stable key stored in settings.</summary>
public sealed record MidiPortInfo(string Name, MidiDirection Direction, string Detail = "");

/// <summary>Bytes of one complete incoming message, with milliseconds since the port was opened.</summary>
public readonly record struct MidiPacket(byte[] Data, long TimestampMs);

/// <summary>
/// The thin device layer: everything above it (connections, reconnect, matching, triggers, sub-actions) is portable and
/// tested with a fake. Implementations must never throw from enumeration (return an empty list) and may call the
/// callbacks on any thread.
/// </summary>
public interface IMidiBackend : IDisposable
{
    /// <summary>"WinMM", "ALSA sequencer", ...</summary>
    string Name { get; }

    /// <summary>Why MIDI cannot work here (no library, no sequencer device), or null when it can.</summary>
    string? UnavailableReason { get; }

    /// <summary>Raised (any thread) when the system reports ports appearing or going away; a hint to re-enumerate soon.</summary>
    event Action? PortsChanged;

    IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction);

    /// <summary>Opens an input port by name. <paramref name="closed"/> runs once if the port goes away while open.</summary>
    IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed);

    IMidiOutputPort OpenOutput(string name, Action<string> closed);
}

public interface IMidiInputPort : IDisposable
{
    string Name { get; }
}

public interface IMidiOutputPort : IDisposable
{
    string Name { get; }

    /// <summary>Sends one complete message (short message or F0 ... F7 SysEx). Throws <see cref="IOException"/> on failure.</summary>
    void Send(ReadOnlySpan<byte> message);
}

/// <summary>Port names with duplicates made unique ("Name", "Name #2", ...), in the system's order.</summary>
internal static class PortNames
{
    public static List<string> Unique(IEnumerable<string> names)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = string.IsNullOrWhiteSpace(raw) ? "MIDI device" : raw.Trim();
            seen[name] = seen.TryGetValue(name, out var n) ? n + 1 : 1;
            result.Add(seen[name] == 1 ? name : $"{name} #{seen[name]}");
        }
        return result;
    }
}

/// <summary>Picks the backend for this OS.</summary>
public static class MidiBackends
{
    public static IMidiBackend CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WinMmBackend();
        if (OperatingSystem.IsLinux()) return new AlsaSeqBackend();
        return new UnavailableBackend("MIDI is implemented for Windows (WinMM) and Linux (ALSA sequencer) only.");
    }
}

/// <summary>A backend that has no devices and explains why.</summary>
public sealed class UnavailableBackend(string reason) : IMidiBackend
{
    public string Name => "None";
    public string? UnavailableReason => reason;

    public event Action? PortsChanged { add { } remove { } }

    public IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction) => [];

    public IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed) => throw new InvalidOperationException(reason);

    public IMidiOutputPort OpenOutput(string name, Action<string> closed) => throw new InvalidOperationException(reason);

    public void Dispose()
    {
    }
}
