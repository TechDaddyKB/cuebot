using Cuebot.Midi.Backends;

namespace Cuebot.Midi.Tests;

/// <summary>An in-memory MIDI backend: tests plug and unplug ports, inject incoming bytes and read what was sent.</summary>
internal sealed class FakeMidiBackend : IMidiBackend
{
    private readonly object _gate = new();
    private readonly List<string> _inPorts = [];
    private readonly List<string> _outPorts = [];
    private readonly List<FakeInput> _openInputs = [];
    private readonly List<FakeOutput> _openOutputs = [];

    public string Name => "Fake";
    public string? Unavailable { get; set; }
    public string? UnavailableReason => Unavailable;
    public event Action? PortsChanged;
    public int OpenCount;
    public volatile bool FailOpens;
    public volatile bool FailSends;
    public List<(string Port, byte[] Bytes)> Sent { get; } = [];

    public FakeMidiBackend Plug(MidiDirection direction, string name)
    {
        lock (_gate) (direction == MidiDirection.Input ? _inPorts : _outPorts).Add(name);
        PortsChanged?.Invoke();
        return this;
    }

    /// <summary>Removes a port; with <paramref name="notify"/> the open handle reports the closure like a real backend would.</summary>
    public void Unplug(MidiDirection direction, string name, bool notify = false)
    {
        FakeInput[] inputs;
        FakeOutput[] outputs;
        lock (_gate)
        {
            (direction == MidiDirection.Input ? _inPorts : _outPorts).Remove(name);
            inputs = _openInputs.Where(i => i.Name == name).ToArray();
            outputs = _openOutputs.Where(o => o.Name == name).ToArray();
        }
        if (!notify) return;
        if (direction == MidiDirection.Input) foreach (var i in inputs) i.Closed("unplugged");
        else foreach (var o in outputs) o.Closed("unplugged");
    }

    public void Emit(string port, params byte[] bytes)
    {
        FakeInput[] inputs;
        lock (_gate) inputs = _openInputs.Where(i => i.Name == port && !i.Disposed).ToArray();
        foreach (var input in inputs) input.Received(new MidiPacket(bytes, 42));
    }

    public int OpenInputs(string port)
    {
        lock (_gate) return _openInputs.Count(i => i.Name == port && !i.Disposed);
    }

    public IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction)
    {
        lock (_gate) return (direction == MidiDirection.Input ? _inPorts : _outPorts).Select(n => new MidiPortInfo(n, direction)).ToArray();
    }

    public IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed)
    {
        lock (_gate)
        {
            if (FailOpens || !_inPorts.Contains(name)) throw new IOException($"cannot open {name}");
            OpenCount++;
            var input = new FakeInput(name, received, closed);
            _openInputs.Add(input);
            return input;
        }
    }

    public IMidiOutputPort OpenOutput(string name, Action<string> closed)
    {
        lock (_gate)
        {
            if (FailOpens || !_outPorts.Contains(name)) throw new IOException($"cannot open {name}");
            OpenCount++;
            var output = new FakeOutput(this, name, closed);
            _openOutputs.Add(output);
            return output;
        }
    }

    public void Dispose()
    {
    }

    internal sealed class FakeInput(string name, Action<MidiPacket> received, Action<string> closed) : IMidiInputPort
    {
        public string Name => name;
        public bool Disposed { get; private set; }
        public Action<MidiPacket> Received => received;
        public Action<string> Closed => closed;
        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeOutput(FakeMidiBackend owner, string name, Action<string> closed) : IMidiOutputPort
    {
        public string Name => name;
        public bool Disposed { get; private set; }
        public Action<string> Closed => closed;

        public void Send(ReadOnlySpan<byte> message)
        {
            if (Disposed) throw new IOException("closed");
            if (owner.FailSends) throw new IOException("device gone");
            lock (owner._gate) owner.Sent.Add((name, message.ToArray()));
        }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
