using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Cuebot.Midi.Protocol;

namespace Cuebot.Midi.Backends;

/// <summary>
/// Windows MIDI through WinMM (winmm.dll: midiIn*/midiOut*), the API every Windows MIDI driver serves. On Windows 11 with
/// Windows MIDI Services installed, WinMM is served by that service, so ports become multi-client automatically.
/// WinMM has no device-change notification, so <see cref="MidiService"/> polls the port list. Port ids are indexes that
/// shift when devices change; ports are therefore always opened by name.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WinMmBackend : IMidiBackend
{
    private const string Lib = "winmm.dll";
    private const int InCapsSize = 76, OutCapsSize = 84, NameOffset = 8, NameChars = 32;

    public string Name => "WinMM";

    public string? UnavailableReason => null;

    public event Action? PortsChanged { add { } remove { } }

    public IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction)
    {
        try
        {
            return PortNames.Unique(RawNames(direction)).Select(n => new MidiPortInfo(n, direction)).ToArray();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    public IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed) =>
        new Input(name, Index(MidiDirection.Input, name), received, closed);

    public IMidiOutputPort OpenOutput(string name, Action<string> closed) => new Output(name, Index(MidiDirection.Output, name));

    public void Dispose()
    {
    }

    private static uint Index(MidiDirection direction, string name)
    {
        var names = PortNames.Unique(RawNames(direction));
        var index = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? (uint)index : throw new IOException($"MIDI device '{name}' is not connected.");
    }

    private static List<string> RawNames(MidiDirection direction)
    {
        var count = direction == MidiDirection.Input ? midiInGetNumDevs() : midiOutGetNumDevs();
        var names = new List<string>();
        var size = direction == MidiDirection.Input ? InCapsSize : OutCapsSize;
        var caps = stackalloc byte[OutCapsSize];
        for (uint i = 0; i < count; i++)
        {
            new Span<byte>(caps, OutCapsSize).Clear();
            var result = direction == MidiDirection.Input ? midiInGetDevCapsW(i, caps, (uint)size) : midiOutGetDevCapsW(i, caps, (uint)size);
            names.Add(result == 0 ? new string((char*)(caps + NameOffset), 0, NameChars).Split('\0')[0] : $"MIDI device {i + 1}");
        }
        return names;
    }

    internal static string Error(uint code) => code switch
    {
        2 => "the device id is no longer valid (unplugged?)",
        4 => "the device is in use by another program",
        6 => "no driver for the device (unplugged?)",
        7 => "out of memory",
        11 => "invalid parameter",
        65 => "the device is still sending",
        67 => "the device is not ready",
        _ => $"WinMM error {code}",
    };

    private static void Check(uint code, string what)
    {
        if (code != 0) throw new IOException($"{what}: {Error(code)}.");
    }

    // ---- Input ----------------------------------------------------------------------------------------------------

    private sealed class Input : IMidiInputPort
    {
        private const uint MimOpen = 0x3C1, MimClose = 0x3C2, MimData = 0x3C3, MimLongData = 0x3C4, MimError = 0x3C5, MimLongError = 0x3C6;
        private const int SysexBuffers = 4, SysexBufferSize = 4096;
        private readonly Action<MidiPacket> _received;
        private readonly Action<string> _closed;
        private readonly Channel<(uint Msg, nint P1, nint P2)> _queue = Channel.CreateUnbounded<(uint, nint, nint)>(new() { SingleReader = true });
        private readonly List<nint> _headers = [];
        private readonly List<byte> _sysex = [];
        private readonly Task _worker;
        private GCHandle _self;
        private nint _handle;
        private volatile bool _closing;
        private int _closedRaised;

        public Input(string name, uint id, Action<MidiPacket> received, Action<string> closed)
        {
            Name = name;
            _received = received;
            _closed = closed;
            _self = GCHandle.Alloc(this);
            nint handle;
            var callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void>)&Callback;
            var result = midiInOpen(&handle, id, callback, GCHandle.ToIntPtr(_self), 0x30000 /* CALLBACK_FUNCTION */);
            if (result != 0)
            {
                _self.Free();
                Check(result, $"Could not open MIDI In '{name}'");
            }
            _handle = handle;
            _worker = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                for (var i = 0; i < SysexBuffers; i++) AddBuffer(Allocate());
                Check(midiInStart(_handle), $"Could not start MIDI In '{name}'");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string Name { get; }

        private nint Allocate()
        {
            var header = (MidiHdr*)NativeMemory.AllocZeroed((nuint)sizeof(MidiHdr));
            header->lpData = (nint)NativeMemory.Alloc(SysexBufferSize);
            header->dwBufferLength = SysexBufferSize;
            _headers.Add((nint)header);
            Check(midiInPrepareHeader(_handle, header, (uint)sizeof(MidiHdr)), "Could not prepare a SysEx buffer");
            return (nint)header;
        }

        private void AddBuffer(nint header)
        {
            ((MidiHdr*)header)->dwBytesRecorded = 0;
            Check(midiInAddBuffer(_handle, (MidiHdr*)header, (uint)sizeof(MidiHdr)), "Could not queue a SysEx buffer");
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Callback(nint handle, uint msg, nint instance, nint param1, nint param2)
        {
            // WinMM forbids calling winmm functions from here: hand everything to the worker.
            if (instance == 0) return;
            if (GCHandle.FromIntPtr(instance).Target is Input input) input._queue.Writer.TryWrite((msg, param1, param2));
        }

        private void Work()
        {
            var reader = _queue.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        Handle(item.Msg, item.P1, item.P2);
                    }
                    catch (Exception)
                    {
                        // A subscriber's failure must not stop the port; the service logs its own errors.
                    }
                }
            }
        }

        private void Handle(uint msg, nint p1, nint p2)
        {
            switch (msg)
            {
                case MimData:
                    var packed = (uint)p1;
                    var status = (byte)packed;
                    var length = MidiMessage.Length(status);
                    if (length == 0) return;
                    var data = new byte[length];
                    for (var i = 0; i < length; i++) data[i] = (byte)(packed >> (8 * i));
                    _received(new MidiPacket(data, p2));
                    break;
                case MimLongData:
                    var header = (MidiHdr*)p1;
                    var recorded = (int)header->dwBytesRecorded;
                    if (recorded > 0) Sysex(new ReadOnlySpan<byte>((void*)header->lpData, recorded), p2);
                    if (!_closing) AddBuffer(p1);
                    break;
                case MimClose:
                    if (!_closing && Interlocked.Exchange(ref _closedRaised, 1) == 0) _closed("The device closed (unplugged?)");
                    break;
                case MimOpen or MimError or MimLongError:
                    break;
            }
        }

        private void Sysex(ReadOnlySpan<byte> chunk, long timestamp)
        {
            foreach (var b in chunk)
            {
                if (b == 0xF0) _sysex.Clear();
                if (_sysex.Count == 0 && b != 0xF0) continue;
                _sysex.Add(b);
                if (b != 0xF7) continue;
                _received(new MidiPacket([.. _sysex], timestamp));
                _sysex.Clear();
            }
            if (_sysex.Count > 1 << 20) _sysex.Clear();
        }

        public void Dispose()
        {
            if (_closing) return;
            _closing = true;
            if (_handle != 0)
            {
                midiInStop(_handle);
                midiInReset(_handle);   // returns every queued buffer (marked done)
                for (var i = 0; i < 100 && _headers.Any(h => (((MidiHdr*)h)->dwFlags & 1 /* MHDR_DONE */) == 0); i++) Thread.Sleep(5);
            }
            _queue.Writer.TryComplete();
            _worker?.Wait(TimeSpan.FromSeconds(2));
            foreach (var h in _headers)
            {
                if (_handle != 0) midiInUnprepareHeader(_handle, (MidiHdr*)h, (uint)sizeof(MidiHdr));
            }
            if (_handle != 0) midiInClose(_handle);
            _handle = 0;
            foreach (var h in _headers)
            {
                NativeMemory.Free((void*)((MidiHdr*)h)->lpData);
                NativeMemory.Free((void*)h);
            }
            _headers.Clear();
            if (_self.IsAllocated) _self.Free();
        }
    }

    // ---- Output ---------------------------------------------------------------------------------------------------

    private sealed class Output : IMidiOutputPort
    {
        private readonly object _gate = new();
        private nint _handle;

        public Output(string name, uint id)
        {
            Name = name;
            nint handle;
            Check(midiOutOpen(&handle, id, 0, 0, 0 /* CALLBACK_NULL */), $"Could not open MIDI Out '{name}'");
            _handle = handle;
        }

        public string Name { get; }

        public void Send(ReadOnlySpan<byte> message)
        {
            if (message.IsEmpty) return;
            lock (_gate)
            {
                if (_handle == 0) throw new IOException($"MIDI Out '{Name}' is closed.");
                if (message[0] != 0xF0)
                {
                    uint packed = 0;
                    for (var i = 0; i < Math.Min(3, message.Length); i++) packed |= (uint)message[i] << (8 * i);
                    Check(midiOutShortMsg(_handle, packed), $"Could not send to '{Name}'");
                    return;
                }
                SendLong(message);
            }
        }

        private void SendLong(ReadOnlySpan<byte> message)
        {
            var header = (MidiHdr*)NativeMemory.AllocZeroed((nuint)sizeof(MidiHdr));
            var data = NativeMemory.Alloc((nuint)message.Length);
            try
            {
                message.CopyTo(new Span<byte>(data, message.Length));
                header->lpData = (nint)data;
                header->dwBufferLength = (uint)message.Length;
                header->dwBytesRecorded = (uint)message.Length;
                Check(midiOutPrepareHeader(_handle, header, (uint)sizeof(MidiHdr)), $"Could not prepare SysEx for '{Name}'");
                try
                {
                    Check(midiOutLongMsg(_handle, header, (uint)sizeof(MidiHdr)), $"Could not send SysEx to '{Name}'");
                    var deadline = Environment.TickCount64 + 5000;
                    while ((header->dwFlags & 1 /* MHDR_DONE */) == 0 && Environment.TickCount64 < deadline) Thread.Sleep(1);
                }
                finally
                {
                    if ((header->dwFlags & 1) == 0) midiOutReset(_handle);
                    midiOutUnprepareHeader(_handle, header, (uint)sizeof(MidiHdr));
                }
            }
            finally
            {
                NativeMemory.Free(data);
                NativeMemory.Free(header);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_handle == 0) return;
                midiOutReset(_handle);
                midiOutClose(_handle);
                _handle = 0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHdr
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public nint lpNext;
        public nint reserved;
        public uint dwOffset;
        public nint r0, r1, r2, r3, r4, r5, r6, r7;
    }

#pragma warning disable SYSLIB1054, IDE1006 // names follow the Win32 API
    [LibraryImport(Lib)] private static partial uint midiInGetNumDevs();
    [LibraryImport(Lib)] private static partial uint midiOutGetNumDevs();
    [LibraryImport(Lib)] private static partial uint midiInGetDevCapsW(nuint id, byte* caps, uint size);
    [LibraryImport(Lib)] private static partial uint midiOutGetDevCapsW(nuint id, byte* caps, uint size);
    [LibraryImport(Lib)] private static partial uint midiInOpen(nint* handle, uint id, nint callback, nint instance, uint flags);
    [LibraryImport(Lib)] private static partial uint midiInStart(nint handle);
    [LibraryImport(Lib)] private static partial uint midiInStop(nint handle);
    [LibraryImport(Lib)] private static partial uint midiInReset(nint handle);
    [LibraryImport(Lib)] private static partial uint midiInClose(nint handle);
    [LibraryImport(Lib)] private static partial uint midiInPrepareHeader(nint handle, MidiHdr* header, uint size);
    [LibraryImport(Lib)] private static partial uint midiInUnprepareHeader(nint handle, MidiHdr* header, uint size);
    [LibraryImport(Lib)] private static partial uint midiInAddBuffer(nint handle, MidiHdr* header, uint size);
    [LibraryImport(Lib)] private static partial uint midiOutOpen(nint* handle, uint id, nint callback, nint instance, uint flags);
    [LibraryImport(Lib)] private static partial uint midiOutShortMsg(nint handle, uint message);
    [LibraryImport(Lib)] private static partial uint midiOutLongMsg(nint handle, MidiHdr* header, uint size);
    [LibraryImport(Lib)] private static partial uint midiOutPrepareHeader(nint handle, MidiHdr* header, uint size);
    [LibraryImport(Lib)] private static partial uint midiOutUnprepareHeader(nint handle, MidiHdr* header, uint size);
    [LibraryImport(Lib)] private static partial uint midiOutReset(nint handle);
    [LibraryImport(Lib)] private static partial uint midiOutClose(nint handle);
#pragma warning restore SYSLIB1054, IDE1006
}
