using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuebot.Midi.Backends;

/// <summary>
/// Linux MIDI through the ALSA sequencer (libasound.so.2, /dev/snd/seq): every USB/hardware MIDI port appears there, as
/// do PipeWire's and JACK's bridged ports and software synths. Cuebot creates one sequencer client ("Cuebot") with one
/// input port (subscribed from each opened device, and from System:Announce for hot-plug) and one output port (events
/// are sent directly to the device's address). Raw bytes are converted with ALSA's own MIDI event coder
/// (snd_midi_event_encode/decode), so only the fixed header of snd_seq_event_t (alsa/seq_event.h) is read by offset.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class AlsaSeqBackend : IMidiBackend
{
    private const string Lib = "libasound.so.2";
    private const int SeqOpenOutput = 1, SeqOpenInput = 2, NonBlock = 1;
    private const uint CapRead = 1 << 0, CapWrite = 1 << 1, CapSubsRead = 1 << 5, CapSubsWrite = 1 << 6, CapNoExport = 1 << 7;
    private const uint TypeMidiGeneric = 1 << 1, TypeApplication = 1 << 20;
    private const int DataOffset = 16;   // snd_seq_event_t: type, flags, tag, queue, time[8], source[2], dest[2], data[12]
    private const byte EvClientExit = 61, EvPortExit = 64, EvPortUnsubscribed = 67, EvSysex = 130;
    private const byte QueueDirect = 253;
    private const int EAgain = -11, ENoSpc = -28;

    private readonly object _outGate = new();
    private readonly object _inGate = new();
    private readonly Dictionary<(byte Client, byte Port), Input> _inputs = [];
    private readonly Dictionary<(byte Client, byte Port), List<byte>> _sysex = [];
    private nint _out, _in, _decoder;
    private int _outPort = -1, _inPort = -1, _outClient = -1, _inClient = -1;
    private Thread? _reader;
    private volatile bool _disposed;
    private string? _unavailable;
    private long _nextAttempt;

    public string Name => "ALSA sequencer";

    public string? UnavailableReason
    {
        get
        {
            lock (_outGate) return Ensure() ? null : _unavailable;
        }
    }

    public event Action? PortsChanged;

    public IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction)
    {
        lock (_outGate)
        {
            if (!Ensure()) return [];
            try
            {
                var ports = Enumerate(direction);
                return PortNames.Unique(ports.Select(p => p.Name)).Select((n, i) => new MidiPortInfo(n, direction, $"{ports[i].Client}:{ports[i].Port}")).ToArray();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return [];
            }
        }
    }

    public IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed)
    {
        var address = Find(MidiDirection.Input, name);
        var input = new Input(this, name, address, received, closed);
        lock (_inGate)
        {
            var result = snd_seq_connect_from(_in, _inPort, address.Client, address.Port);
            if (result < 0) throw new IOException($"Could not open MIDI In '{name}': {Error(result)}.");
            _inputs[address] = input;
            _sysex.Remove(address);
        }
        return input;
    }

    public IMidiOutputPort OpenOutput(string name, Action<string> closed) => new Output(this, name, Find(MidiDirection.Output, name));

    private (byte Client, byte Port) Find(MidiDirection direction, string name)
    {
        lock (_outGate)
        {
            if (!Ensure()) throw new IOException(_unavailable);
            var ports = Enumerate(direction);
            var names = PortNames.Unique(ports.Select(p => p.Name));
            var index = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? ((byte)ports[index].Client, (byte)ports[index].Port) : throw new IOException($"MIDI device '{name}' is not connected.");
        }
    }

    /// <summary>Opens the sequencer clients and the reader thread once. Failure is retried at most every 5 s.</summary>
    private bool Ensure()
    {
        if (_out != 0) return true;
        if (_disposed || Environment.TickCount64 < _nextAttempt) return false;
        _nextAttempt = Environment.TickCount64 + 5000;
        try
        {
            nint output, input;
            var result = snd_seq_open(&output, "default", SeqOpenOutput, 0);
            if (result < 0)
            {
                _unavailable = $"The ALSA sequencer could not be opened ({Error(result)}). Load the snd-seq kernel module; in Flatpak the app needs device access (--device=all).";
                return false;
            }
            result = snd_seq_open(&input, "default", SeqOpenInput, NonBlock);
            if (result < 0)
            {
                snd_seq_close(output);
                _unavailable = $"The ALSA sequencer could not be opened for input ({Error(result)}).";
                return false;
            }
            snd_seq_set_client_name(output, "Cuebot");
            snd_seq_set_client_name(input, "Cuebot");
            _outPort = snd_seq_create_simple_port(output, "Cuebot MIDI Out", CapRead | CapSubsRead, TypeMidiGeneric | TypeApplication);
            _inPort = snd_seq_create_simple_port(input, "Cuebot MIDI In", CapWrite | CapSubsWrite, TypeMidiGeneric | TypeApplication);
            nint decoder;
            if (_outPort < 0 || _inPort < 0 || snd_midi_event_new(256, &decoder) < 0)
            {
                snd_seq_close(input);
                snd_seq_close(output);
                _unavailable = "Could not create the ALSA sequencer ports.";
                return false;
            }
            snd_midi_event_no_status(decoder, 1);
            _outClient = snd_seq_client_id(output);
            _inClient = snd_seq_client_id(input);
            snd_seq_connect_from(input, _inPort, 0, 1);   // System:Announce -> port start/exit notifications (hot-plug)
            _decoder = decoder;
            _in = input;
            _out = output;
            _unavailable = null;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Cuebot MIDI (ALSA)" };
            _reader.Start();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = "libasound.so.2 (ALSA) was not found. Install alsa-lib (alsa-lib / libasound2) to use MIDI.";
            _nextAttempt = long.MaxValue;
            return false;
        }
    }

    private List<(int Client, int Port, string Name)> Enumerate(MidiDirection direction)
    {
        var wanted = direction == MidiDirection.Input ? CapRead | CapSubsRead : CapWrite | CapSubsWrite;
        var result = new List<(int, int, string)>();
        nint client, port;
        if (snd_seq_client_info_malloc(&client) < 0) return result;
        if (snd_seq_port_info_malloc(&port) < 0)
        {
            snd_seq_client_info_free(client);
            return result;
        }
        try
        {
            snd_seq_client_info_set_client(client, -1);
            while (snd_seq_query_next_client(_out, client) >= 0)
            {
                var id = snd_seq_client_info_get_client(client);
                if (id == 0 || id == _outClient || id == _inClient) continue;   // System, ourselves
                var clientName = Marshal.PtrToStringUTF8(snd_seq_client_info_get_name(client)) ?? $"Client {id}";
                snd_seq_port_info_set_client(port, id);
                snd_seq_port_info_set_port(port, -1);
                while (snd_seq_query_next_port(_out, port) >= 0)
                {
                    var caps = snd_seq_port_info_get_capability(port);
                    if ((caps & CapNoExport) != 0 || (caps & wanted) != wanted) continue;
                    var portName = Marshal.PtrToStringUTF8(snd_seq_port_info_get_name(port)) ?? "";
                    var name = portName.Length == 0 ? clientName
                        : portName.StartsWith(clientName, StringComparison.OrdinalIgnoreCase) ? portName : $"{clientName}: {portName}";
                    result.Add((id, snd_seq_port_info_get_port(port), name));
                }
            }
        }
        finally
        {
            snd_seq_port_info_free(port);
            snd_seq_client_info_free(client);
        }
        return result;
    }

    private void ReadLoop()
    {
        var count = snd_seq_poll_descriptors_count(_in, 1 /* POLLIN */);
        var fds = new PollFd[Math.Max(count, 1)];
        fixed (PollFd* p = fds) count = snd_seq_poll_descriptors(_in, p, (uint)fds.Length, 1);
        var buffer = new byte[256];
        var deliveries = new List<(Input Input, MidiPacket? Packet, string? Closed)>();
        var portsChanged = false;
        while (!_disposed)
        {
            fixed (PollFd* p = fds) poll(p, (nuint)count, 250);
            if (_disposed) break;
            lock (_inGate)
            {
                while (true)
                {
                    nint ev;
                    var result = snd_seq_event_input(_in, &ev);
                    if (result == EAgain) break;
                    if (result == ENoSpc) continue;   // input overrun: some events were lost, keep reading
                    if (result < 0 || ev == 0) break;
                    Process((byte*)ev, buffer, deliveries, ref portsChanged);
                }
            }
            foreach (var (input, packet, closed) in deliveries)
            {
                try
                {
                    if (packet is { } pk) input.Received(pk);
                    else if (closed is not null) input.Lost(closed);
                }
                catch (Exception)
                {
                    // Subscribers log their own errors; the reader must keep running.
                }
            }
            deliveries.Clear();
            if (portsChanged)
            {
                portsChanged = false;
                try { PortsChanged?.Invoke(); }
                catch (Exception) { /* a subscriber's problem */ }
            }
        }
    }

    private void Process(byte* ev, byte[] buffer, List<(Input, MidiPacket?, string?)> deliveries, ref bool portsChanged)
    {
        var type = ev[0];
        var source = (ev[12], ev[13]);
        if (source == (0, 1))
        {
            portsChanged = true;
            var address = (ev[DataOffset], ev[DataOffset + 1]);
            if (type == EvPortExit && _inputs.TryGetValue(address, out var gone)) deliveries.Add((gone, null, "The device was removed"));
            if (type == EvClientExit)
                foreach (var (_, input) in _inputs.Where(i => i.Key.Client == address.Item1).ToArray())
                    deliveries.Add((input, null, "The device was removed"));
            return;
        }
        if (type == EvPortUnsubscribed)
        {
            var sender = (ev[DataOffset], ev[DataOffset + 1]);
            if (_inputs.TryGetValue(sender, out var unsubscribed)) deliveries.Add((unsubscribed, null, "The connection was removed"));
            return;
        }
        if (!_inputs.TryGetValue(source, out var target)) return;
        ReadOnlySpan<byte> bytes;
        if (type == EvSysex)
        {
            // Variable-length event: data.ext = { uint len; void* ptr } (packed), the bytes stay valid until the next input call.
            var length = *(uint*)(ev + DataOffset);
            var data = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<nint>(ev + DataOffset + 4);   // packed: unaligned
            if (length == 0 || data == 0 || length > 1 << 20) return;
            bytes = new ReadOnlySpan<byte>((void*)data, (int)length);
        }
        else
        {
            long length;
            fixed (byte* b = buffer) length = snd_midi_event_decode(_decoder, b, buffer.Length, ev);
            if (length <= 0) return;
            bytes = buffer.AsSpan(0, (int)length);
        }
        if (bytes[0] == 0xF0 || _sysex.ContainsKey(source))
        {
            // ALSA delivers long SysEx in chunks: collect until F7.
            if (bytes[0] == 0xF0) _sysex[source] = [];
            var collected = _sysex[source];
            collected.AddRange(bytes.ToArray());
            if (collected[^1] != 0xF7)
            {
                if (collected.Count > 1 << 20) _sysex.Remove(source);
                return;
            }
            _sysex.Remove(source);
            deliveries.Add((target, new MidiPacket([.. collected], target.Elapsed), null));
            return;
        }
        deliveries.Add((target, new MidiPacket(bytes.ToArray(), target.Elapsed), null));
    }

    private void Close(Input input)
    {
        lock (_inGate)
        {
            if (_inputs.TryGetValue(input.Address, out var current) && current == input)
            {
                _inputs.Remove(input.Address);
                if (_in != 0) snd_seq_disconnect_from(_in, _inPort, input.Address.Client, input.Address.Port);
            }
        }
    }

    private void Send(Output output, ReadOnlySpan<byte> message)
    {
        lock (_outGate)
        {
            if (_out == 0) throw new IOException("The ALSA sequencer is closed.");
            nint encoder;
            if (snd_midi_event_new((nuint)Math.Max(message.Length, 16), &encoder) < 0) throw new IOException("Could not create an ALSA MIDI encoder.");
            try
            {
                var ev = stackalloc byte[32];
                new Span<byte>(ev, 32).Clear();
                fixed (byte* bytes = message)
                {
                    var used = snd_midi_event_encode(encoder, bytes, message.Length, ev);
                    if (used <= 0 || ev[0] == 0) throw new IOException($"'{MidiMessageHex(message)}' is not a complete MIDI message.");
                }
                ev[3] = QueueDirect;           // queue: deliver now
                ev[13] = (byte)_outPort;       // source port (the kernel fills in our client)
                ev[14] = output.Address.Client;
                ev[15] = output.Address.Port;
                var result = snd_seq_event_output_direct(_out, ev);
                if (result < 0) throw new IOException($"Could not send to '{output.Name}': {Error(result)}.");
            }
            finally
            {
                snd_midi_event_free(encoder);
            }
        }
    }

    private static string MidiMessageHex(ReadOnlySpan<byte> bytes) => Protocol.MidiMessage.ToHex(bytes);

    private static string Error(int code)
    {
        try
        {
            return Marshal.PtrToStringUTF8(snd_strerror(code)) ?? $"error {code}";
        }
        catch (Exception)
        {
            return $"error {code}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader?.Join(TimeSpan.FromSeconds(1));
        lock (_inGate)
        {
            if (_in != 0) snd_seq_close(_in);
            if (_decoder != 0) snd_midi_event_free(_decoder);
            _in = 0;
            _decoder = 0;
            _inputs.Clear();
        }
        lock (_outGate)
        {
            if (_out != 0) snd_seq_close(_out);
            _out = 0;
        }
    }

    private sealed class Input(AlsaSeqBackend owner, string name, (byte Client, byte Port) address, Action<MidiPacket> received, Action<string> closed)
        : IMidiInputPort
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _done;

        public string Name => name;
        public (byte Client, byte Port) Address => address;
        public long Elapsed => _clock.ElapsedMilliseconds;

        public void Received(MidiPacket packet)
        {
            if (Volatile.Read(ref _done) == 0) received(packet);
        }

        public void Lost(string reason)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            owner.Close(this);
            closed(reason);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Close(this);
        }
    }

    private sealed class Output(AlsaSeqBackend owner, string name, (byte Client, byte Port) address) : IMidiOutputPort
    {
        private volatile bool _closed;

        public string Name => name;
        public (byte Client, byte Port) Address => address;

        public void Send(ReadOnlySpan<byte> message)
        {
            if (_closed) throw new IOException($"MIDI Out '{name}' is closed.");
            if (!message.IsEmpty) owner.Send(this, message);
        }

        public void Dispose() => _closed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

#pragma warning disable IDE1006 // names follow the C API
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int snd_seq_open(nint* handle, string name, int streams, int mode);
    [LibraryImport(Lib)] private static partial int snd_seq_close(nint handle);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int snd_seq_set_client_name(nint handle, string name);
    [LibraryImport(Lib)] private static partial int snd_seq_client_id(nint handle);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_create_simple_port(nint handle, string name, uint caps, uint type);
    [LibraryImport(Lib)] private static partial int snd_seq_connect_from(nint handle, int myPort, int srcClient, int srcPort);
    [LibraryImport(Lib)] private static partial int snd_seq_disconnect_from(nint handle, int myPort, int srcClient, int srcPort);
    [LibraryImport(Lib)] private static partial int snd_seq_event_input(nint handle, nint* ev);
    [LibraryImport(Lib)] private static partial int snd_seq_event_output_direct(nint handle, byte* ev);
    [LibraryImport(Lib)] private static partial int snd_seq_poll_descriptors_count(nint handle, short events);
    [LibraryImport(Lib)] private static partial int snd_seq_poll_descriptors(nint handle, PollFd* fds, uint space, short events);
    [LibraryImport(Lib)] private static partial int snd_seq_client_info_malloc(nint* info);
    [LibraryImport(Lib)] private static partial void snd_seq_client_info_free(nint info);
    [LibraryImport(Lib)] private static partial void snd_seq_client_info_set_client(nint info, int client);
    [LibraryImport(Lib)] private static partial int snd_seq_client_info_get_client(nint info);
    [LibraryImport(Lib)] private static partial nint snd_seq_client_info_get_name(nint info);
    [LibraryImport(Lib)] private static partial int snd_seq_query_next_client(nint handle, nint info);
    [LibraryImport(Lib)] private static partial int snd_seq_port_info_malloc(nint* info);
    [LibraryImport(Lib)] private static partial void snd_seq_port_info_free(nint info);
    [LibraryImport(Lib)] private static partial void snd_seq_port_info_set_client(nint info, int client);
    [LibraryImport(Lib)] private static partial void snd_seq_port_info_set_port(nint info, int port);
    [LibraryImport(Lib)] private static partial int snd_seq_port_info_get_port(nint info);
    [LibraryImport(Lib)] private static partial nint snd_seq_port_info_get_name(nint info);
    [LibraryImport(Lib)] private static partial uint snd_seq_port_info_get_capability(nint info);
    [LibraryImport(Lib)] private static partial int snd_seq_query_next_port(nint handle, nint info);
    [LibraryImport(Lib)] private static partial int snd_midi_event_new(nuint bufferSize, nint* coder);
    [LibraryImport(Lib)] private static partial void snd_midi_event_free(nint coder);
    [LibraryImport(Lib)] private static partial void snd_midi_event_no_status(nint coder, int on);
    [LibraryImport(Lib)] private static partial nint snd_midi_event_encode(nint coder, byte* buffer, nint count, byte* ev);
    [LibraryImport(Lib)] private static partial nint snd_midi_event_decode(nint coder, byte* buffer, nint count, byte* ev);
    [LibraryImport(Lib)] private static partial nint snd_strerror(int error);
    [LibraryImport("libc", SetLastError = true)] private static partial int poll(PollFd* fds, nuint count, int timeout);
#pragma warning restore IDE1006
}
