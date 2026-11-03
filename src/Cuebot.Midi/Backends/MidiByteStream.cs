using Cuebot.Midi.Protocol;

namespace Cuebot.Midi.Backends;

/// <summary>
/// Splits a raw MIDI byte stream (as CoreMIDI delivers it: several messages per packet, SysEx possibly spread over
/// packets) into complete messages. Real-time bytes (F8-FF) are messages of their own even inside SysEx; running
/// status is applied to data bytes that follow a channel message. Not thread-safe: one per input port.
/// </summary>
internal sealed class MidiByteStream
{
    private const int MaxSysex = 64 * 1024;
    private readonly List<byte> _sysex = [];
    private readonly List<byte> _current = [];
    private bool _inSysex;
    private byte _running;
    private int _needed;

    public void Feed(ReadOnlySpan<byte> bytes, Action<byte[]> message)
    {
        foreach (var b in bytes)
        {
            if (b >= 0xF8)
            {
                message([b]);
                continue;
            }
            if (_inSysex)
            {
                if (b == 0xF7)
                {
                    _sysex.Add(b);
                    _inSysex = false;
                    message([.. _sysex]);
                    _sysex.Clear();
                    continue;
                }
                if (b < 0x80)
                {
                    if (_sysex.Count < MaxSysex) _sysex.Add(b);
                    continue;
                }
                // A status byte ends an unterminated SysEx: drop it and read the new message.
                _inSysex = false;
                _sysex.Clear();
            }
            if (b == 0xF0)
            {
                _inSysex = true;
                _sysex.Add(b);
                _current.Clear();
                _running = 0;
                continue;
            }
            if (b >= 0x80)
            {
                _current.Clear();
                _current.Add(b);
                _needed = MidiMessage.Length(b);
                _running = b < 0xF0 ? b : (byte)0;   // system common messages cancel running status
                if (_needed == 0) _current.Clear();   // undefined status (F4, F5) or a stray F7
                else Complete(message);
                continue;
            }
            if (_current.Count == 0)
            {
                if (_running == 0) continue;   // data byte with no status to belong to
                _current.Add(_running);
                _needed = MidiMessage.Length(_running);
            }
            _current.Add(b);
            Complete(message);
        }
    }

    private void Complete(Action<byte[]> message)
    {
        if (_current.Count < _needed) return;
        message([.. _current]);
        _current.Clear();
    }
}
