using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Cuebot.Midi.Backends;

/// <summary>
/// macOS MIDI through CoreMIDI: USB and Bluetooth devices, the IAC driver's virtual buses and network sessions all
/// appear as sources (MIDI In) and destinations (MIDI Out). The process has one CoreMIDI client, made on its own thread
/// with a run loop so CoreMIDI can report setup changes (a device plugged in or removed), and kept until the process
/// ends: CoreMIDI can hang when a second client is set up after the first was disposed. Each backend opens one input
/// port per source and shares the client's output port. Ports are opened by display name, made unique as on the other
/// backends.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class CoreMidiBackend : IMidiBackend
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const int SetupChanged = 1;   // kMIDIMsgSetupChanged

    private static readonly Lazy<(uint Client, uint OutPort, string? Error)> Shared = new(StartClient);
    private static event Action? SetupChangedForAll;

    private readonly object _gate = new();
    private readonly List<Input> _inputs = [];
    private uint _client, _outPort;
    private string? _unavailable;
    private bool _subscribed;
    private bool _disposed;

    public string Name => "CoreMIDI";

    public string? UnavailableReason
    {
        get
        {
            lock (_gate) return Ensure() ? null : _unavailable;
        }
    }

    public event Action? PortsChanged;

    public IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction)
    {
        try
        {
            return PortNames.Unique(Endpoints(direction).Select(e => e.Name)).Select(n => new MidiPortInfo(n, direction)).ToArray();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    public IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed)
    {
        lock (_gate)
        {
            if (!Ensure()) throw new IOException(_unavailable);
            var input = new Input(this, name, Find(MidiDirection.Input, name), received, closed);
            _inputs.Add(input);
            return input;
        }
    }

    public IMidiOutputPort OpenOutput(string name, Action<string> closed)
    {
        lock (_gate)
        {
            if (!Ensure()) throw new IOException(_unavailable);
            return new Output(this, name, Find(MidiDirection.Output, name), closed);
        }
    }

    /// <summary>Uses the process's client (started on first use); false (with <see cref="_unavailable"/>) when CoreMIDI refuses.</summary>
    private bool Ensure()
    {
        if (_client != 0) return true;
        if (_disposed) return false;
        var (client, outPort, error) = Shared.Value;
        if (error is not null)
        {
            _unavailable = error;
            return false;
        }
        _client = client;
        _outPort = outPort;
        if (!_subscribed) SetupChangedForAll += OnSetupChanged;
        _subscribed = true;
        return true;
    }

    private static (uint, uint, string?) StartClient()
    {
        using var ready = new ManualResetEventSlim();
        var status = 0;
        uint client = 0, port = 0;
        new Thread(() =>
        {
            var name = CfString("Cuebot");
            uint c = 0, p = 0;
            status = MIDIClientCreate(name, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Notify, 0, &c);
            CFRelease(name);
            if (status == 0)
            {
                var portName = CfString("Cuebot out");
                status = MIDIOutputPortCreate(c, portName, &p);
                CFRelease(portName);
            }
            client = c;
            port = p;
            ready.Set();
            if (status == 0) CFRunLoopRun();   // CoreMIDI delivers setup changes on this run loop
        }) { IsBackground = true, Name = "Cuebot CoreMIDI" }.Start();
        ready.Wait();
        return status == 0 ? (client, port, null) : (0, 0, $"CoreMIDI could not start (error {status}).");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Notify(nint notification, nint refCon)
    {
        if (Marshal.ReadInt32(notification) != SetupChanged) return;
        ThreadPool.QueueUserWorkItem(_ => SetupChangedForAll?.Invoke());
    }

    private void OnSetupChanged()
    {
        Input[] inputs;
        lock (_gate) inputs = [.. _inputs];
        var present = Endpoints(MidiDirection.Input).Select(e => e.Ref).ToHashSet();
        foreach (var input in inputs.Where(i => !present.Contains(i.Source))) input.Gone();
        PortsChanged?.Invoke();
    }

    private static List<(uint Ref, string Name)> Endpoints(MidiDirection direction)
    {
        var count = direction == MidiDirection.Input ? MIDIGetNumberOfSources() : MIDIGetNumberOfDestinations();
        var result = new List<(uint, string)>();
        for (nuint i = 0; i < count; i++)
        {
            var endpoint = direction == MidiDirection.Input ? MIDIGetSource(i) : MIDIGetDestination(i);
            if (endpoint != 0) result.Add((endpoint, DisplayName(endpoint) ?? $"MIDI device {i + 1}"));
        }
        return result;
    }

    private static uint Find(MidiDirection direction, string name)
    {
        var endpoints = Endpoints(direction);
        var index = PortNames.Unique(endpoints.Select(e => e.Name)).FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? endpoints[index].Ref : throw new IOException($"MIDI device '{name}' is not connected.");
    }

    private static string? DisplayName(uint endpoint)
    {
        nint value;
        if (MIDIObjectGetStringProperty(endpoint, DisplayNameKey.Value, &value) != 0 || value == 0) return null;
        try { return FromCfString(value); }
        finally { CFRelease(value); }
    }

    private static readonly Lazy<nint> DisplayNameKey = new(() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreMidi), "kMIDIPropertyDisplayName")));

    private static nint CfString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes) return CFStringCreateWithCString(0, p, Utf8);
    }

    private static string? FromCfString(nint cf)
    {
        var buffer = stackalloc byte[512];
        return CFStringGetCString(cf, buffer, 512, Utf8) ? Marshal.PtrToStringUTF8((nint)buffer) : null;
    }

    public void Dispose()
    {
        Input[] inputs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            inputs = [.. _inputs];
        }
        foreach (var input in inputs) input.Dispose();
        if (_subscribed) SetupChangedForAll -= OnSetupChanged;
    }

    // ---- Input ----------------------------------------------------------------------------------------------------

    private sealed class Input : IMidiInputPort
    {
        private readonly CoreMidiBackend _backend;
        private readonly Action<MidiPacket> _received;
        private readonly Action<string> _closed;
        private readonly MidiByteStream _stream = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private GCHandle _self;
        private uint _port;
        private int _closedRaised;

        public Input(CoreMidiBackend backend, string name, uint source, Action<MidiPacket> received, Action<string> closed)
        {
            _backend = backend;
            Name = name;
            Source = source;
            _received = received;
            _closed = closed;
            _self = GCHandle.Alloc(this);
            var portName = CfString("Cuebot in");
            uint port;
            var status = MIDIInputPortCreate(backend._client, portName, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Read,
                GCHandle.ToIntPtr(_self), &port);
            CFRelease(portName);
            if (status != 0)
            {
                _self.Free();
                throw new IOException($"Could not open MIDI In '{name}' (CoreMIDI error {status}).");
            }
            _port = port;
            status = MIDIPortConnectSource(_port, source, 0);
            if (status != 0)
            {
                Dispose();
                throw new IOException($"Could not connect to MIDI In '{name}' (CoreMIDI error {status}).");
            }
        }

        public string Name { get; }
        public uint Source { get; }

        /// <summary>CoreMIDI's read callback (its own high-priority thread): one packet list, each packet's bytes in turn.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Read(nint packetList, nint refCon, nint connRefCon)
        {
            if (GCHandle.FromIntPtr(refCon).Target is not Input input) return;
            try
            {
                foreach (var bytes in PacketBytes(packetList)) input.Feed(bytes);
            }
            catch
            {
                // never let an exception cross into CoreMIDI
            }
        }

        private void Feed(byte[] bytes)
        {
            lock (_stream) _stream.Feed(bytes, message => _received(new MidiPacket(message, _clock.ElapsedMilliseconds)));
        }

        public void Gone()
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) == 0) _closed("The device closed (unplugged?)");
        }

        public void Dispose()
        {
            lock (_backend._gate) _backend._inputs.Remove(this);
            if (_port == 0) return;
            MIDIPortDisconnectSource(_port, Source);
            MIDIPortDispose(_port);
            _port = 0;
            _self.Free();
        }
    }

    /// <summary>
    /// The data of each MIDIPacket in a MIDIPacketList: a UInt32 count, then packets of a UInt64 timestamp, a UInt16
    /// length and the bytes, packed to 4 bytes (arm64 also starts each packet on a 4-byte boundary, see MIDIPacketNext).
    /// </summary>
    internal static List<byte[]> PacketBytes(nint packetList)
    {
        var result = new List<byte[]>();
        var count = Marshal.ReadInt32(packetList);
        var packet = packetList + 4;
        for (var i = 0; i < count; i++)
        {
            var length = (ushort)Marshal.ReadInt16(packet + 8);
            var data = new byte[length];
            Marshal.Copy(packet + 10, data, 0, length);
            result.Add(data);
            packet += 10 + length;
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) packet = (packet + 3) & ~(nint)3;
        }
        return result;
    }

    // ---- Output ---------------------------------------------------------------------------------------------------

    private sealed class Output(CoreMidiBackend backend, string name, uint destination, Action<string> closed) : IMidiOutputPort
    {
        private int _closedRaised;

        public string Name => name;

        public void Send(ReadOnlySpan<byte> message)
        {
            // A one-packet MIDIPacketList: count, timestamp 0 (now), length, bytes.
            var size = 4 + 10 + message.Length;
            var buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
            try
            {
                *(uint*)buffer = 1;
                *(ushort*)(buffer + 12) = (ushort)message.Length;
                message.CopyTo(new Span<byte>(buffer + 14, message.Length));
                var status = MIDISend(backend._outPort, destination, (nint)buffer);
                if (status == 0) return;
                if (Interlocked.Exchange(ref _closedRaised, 1) == 0) closed("The device closed (unplugged?)");
                throw new IOException($"MIDI Out '{Name}' failed (CoreMIDI error {status}).");
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        public void Dispose()
        {
        }
    }

    // ---- Native ---------------------------------------------------------------------------------------------------

    [DllImport(CoreMidi)] private static extern int MIDIClientCreate(nint name, nint notifyProc, nint refCon, uint* client);
    [DllImport(CoreMidi)] private static extern int MIDIInputPortCreate(uint client, nint name, nint readProc, nint refCon, uint* port);
    [DllImport(CoreMidi)] private static extern int MIDIOutputPortCreate(uint client, nint name, uint* port);
    [DllImport(CoreMidi)] private static extern int MIDIPortConnectSource(uint port, uint source, nint connRefCon);
    [DllImport(CoreMidi)] private static extern int MIDIPortDisconnectSource(uint port, uint source);
    [DllImport(CoreMidi)] private static extern int MIDIPortDispose(uint port);
    [DllImport(CoreMidi)] private static extern nuint MIDIGetNumberOfSources();
    [DllImport(CoreMidi)] private static extern nuint MIDIGetNumberOfDestinations();
    [DllImport(CoreMidi)] private static extern uint MIDIGetSource(nuint index);
    [DllImport(CoreMidi)] private static extern uint MIDIGetDestination(nuint index);
    [DllImport(CoreMidi)] private static extern int MIDIObjectGetStringProperty(uint obj, nint property, nint* value);
    [DllImport(CoreMidi)] private static extern int MIDISend(uint port, uint destination, nint packetList);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint cf, byte* buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
    [DllImport(CoreFoundation)] private static extern void CFRunLoopRun();
}
