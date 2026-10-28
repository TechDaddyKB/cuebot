using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;

namespace Cuebot.Midi;

public enum MidiConnectionState { Closed, Open, Waiting, Error }

/// <summary>A configured MIDI In or MIDI Out connection and its live state.</summary>
public sealed class MidiConnection
{
    internal MidiConnection(MidiDirection direction, MidiDeviceConfig config)
    {
        Direction = direction;
        Config = config;
    }

    public MidiDirection Direction { get; }

    /// <summary>The persisted settings. Change them through <see cref="MidiService.UpdateDevice"/>.</summary>
    public MidiDeviceConfig Config { get; }

    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Device => Config.Device;
    public MidiConnectionState State { get; internal set; }
    public string Message { get; internal set; } = "Closed";
    public bool IsOpen => State == MidiConnectionState.Open;

    /// <summary>The port's system name is in the current device list.</summary>
    public bool IsPresent { get; internal set; }

    internal IMidiInputPort? Input;
    internal IMidiOutputPort? Output;
    internal bool WantOpen;
    internal DateTimeOffset NextRetry;
    internal int Generation;

    public override string ToString() => $"{Name} ({Device}): {State}";
}

/// <summary>
/// Streamer.bot's MIDI: MIDI In connections whose messages become "MIDI Message" triggers (matched against each
/// connection's event listeners), MIDI Out connections used by the MIDI sub-actions, automatic open on startup,
/// unplug detection and reopen with a retry interval, and learn mode. Device access goes through an
/// <see cref="IMidiBackend"/> (WinMM on Windows, ALSA sequencer on Linux).
/// </summary>
public sealed class MidiService : IConnectionStatus, IAsyncDisposable
{
    public const string ServiceName = "MIDI";
    private const string LogSource = "MIDI";

    private readonly JsonFile<MidiSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IMidiBackend _backend;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly object _deviceGate = new();   // serializes scans and opens (a stale scan must not close a fresh open)
    private readonly List<MidiConnection> _inputs = [];
    private readonly List<MidiConnection> _outputs = [];
    private readonly List<Learner> _learners = [];
    private readonly SemaphoreSlim _wake = new(0);
    private IReadOnlyList<MidiPortInfo> _inPorts = [];
    private IReadOnlyList<MidiPortInfo> _outPorts = [];
    private CancellationTokenSource? _run;
    private Task _start = Task.CompletedTask;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    public MidiService(JsonFile<MidiSettings> settings, EventBus bus, AppLog log, IMidiBackend? backend = null, TimeProvider? time = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _backend = backend ?? MidiBackends.CreateDefault();
        _time = time ?? TimeProvider.System;
        foreach (var config in settings.Value.Inputs) _inputs.Add(new MidiConnection(MidiDirection.Input, config));
        foreach (var config in settings.Value.Outputs) _outputs.Add(new MidiConnection(MidiDirection.Output, config));
        _backend.PortsChanged += Wake;
        StatusMessage = "Not started";
    }

    public string Name => ServiceName;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; }
    public event Action? StatusChanged;

    /// <summary>A connection opened, closed, was added/removed/edited, or the device list changed.</summary>
    public event Action? ConnectionsChanged;

    /// <summary>Every message received on an open MIDI In connection (after the clock filter), for monitors and learn UIs.</summary>
    public event Action<MidiConnection, MidiMessage>? MessageReceived;

    public MidiSettings Settings => _settings.Value;

    public string BackendName => _backend.Name;

    /// <summary>Why MIDI does not work on this system, or null.</summary>
    public string? UnavailableReason => _backend.UnavailableReason;

    public bool IsAvailable => UnavailableReason is null;

    public IReadOnlyList<MidiConnection> Inputs
    {
        get { lock (_gate) return _inputs.ToArray(); }
    }

    public IReadOnlyList<MidiConnection> Outputs
    {
        get { lock (_gate) return _outputs.ToArray(); }
    }

    /// <summary>System ports seen by the last scan.</summary>
    public IReadOnlyList<MidiPortInfo> Ports(MidiDirection direction)
    {
        lock (_gate) return direction == MidiDirection.Input ? _inPorts : _outPorts;
    }

    /// <summary>Reads the system port list now.</summary>
    public IReadOnlyList<MidiPortInfo> RefreshPorts(MidiDirection direction)
    {
        IReadOnlyList<MidiPortInfo> ports;
        try
        {
            ports = _backend.ListPorts(direction);
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Could not list MIDI {direction} devices: {ex.Message}");
            ports = [];
        }
        lock (_gate)
        {
            if (direction == MidiDirection.Input) _inPorts = ports;
            else _outPorts = ports;
        }
        return ports;
    }

    public void SaveSettings() => _settings.ScheduleSave();

    // ---- Lifecycle ------------------------------------------------------------------------------------------------

    /// <summary>Opens the connections marked Auto Open and starts watching for unplugged/replugged devices. Never throws.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_disposed || _run is not null) return Task.CompletedTask;
        var run = new CancellationTokenSource();
        _run = run;
        // Opening a port can block for seconds (WinMM soft synths, slow drivers), so never on the caller's thread.
        return _start = Task.Run(() =>
        {
            try
            {
                if (UnavailableReason is { } reason) _log.Info(LogSource, $"MIDI is not available: {reason}");
                RefreshPorts(MidiDirection.Input);
                RefreshPorts(MidiDirection.Output);
                foreach (var connection in Inputs.Concat(Outputs).Where(c => c.Config.AutoOpen)) Open(connection);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                _log.Error(LogSource, "Starting MIDI failed", ex);
            }
            _loop = MonitorAsync(run.Token);
        }, CancellationToken.None);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(Settings.MonitorIntervalMs, 250, 60000)), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            try
            {
                CheckDevices();
            }
            catch (Exception ex)
            {
                _log.Error(LogSource, "Device check failed", ex);
            }
        }
    }

    /// <summary>One monitor pass: rescan, close connections whose device vanished, reopen ones that are due.</summary>
    internal void CheckDevices()
    {
        lock (_deviceGate)
        {
            var inPorts = RefreshPorts(MidiDirection.Input);
            var outPorts = RefreshPorts(MidiDirection.Output);
            var now = _time.GetUtcNow();
            foreach (var connection in Inputs.Concat(Outputs))
            {
                var present = Present(connection.Direction == MidiDirection.Input ? inPorts : outPorts, connection.Device);
                connection.IsPresent = present;
                if (connection.IsOpen && !present) Lost(connection, connection.Generation, "The device was unplugged");
                else if (!connection.IsOpen && connection.WantOpen && now >= connection.NextRetry) TryOpen(connection);
            }
        }
        Changed();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _run?.Cancel();
        try { await _start.ConfigureAwait(false); await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        foreach (var connection in Inputs.Concat(Outputs)) CloseCore(connection, "Closed");
        lock (_gate)
        {
            foreach (var learner in _learners) learner.Completion.TrySetCanceled();
            _learners.Clear();
        }
        _backend.Dispose();
        _run?.Dispose();
        _wake.Dispose();
    }

    // ---- Connections ----------------------------------------------------------------------------------------------

    /// <summary>A connection by id, name, or system device name (case-insensitive); null when none matches.</summary>
    public MidiConnection? Find(MidiDirection direction, string text)
    {
        text = text.Trim();
        var list = direction == MidiDirection.Input ? Inputs : Outputs;
        return list.FirstOrDefault(c => c.Id == text)
               ?? list.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(c => string.Equals(c.Device, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Like <see cref="Find"/>; empty text means the only (or first) connection. Throws when nothing matches.</summary>
    public MidiConnection Resolve(MidiDirection direction, string text)
    {
        var list = direction == MidiDirection.Input ? Inputs : Outputs;
        var label = direction == MidiDirection.Input ? "MIDI In" : "MIDI Out";
        if (list.Count == 0) throw new InvalidOperationException($"No {label} device is configured (add one on the MIDI page).");
        if (string.IsNullOrWhiteSpace(text)) return list[0];
        return Find(direction, text)
               ?? throw new ArgumentException($"No {label} device named '{text.Trim()}'. Configured: {string.Join(", ", list.Select(c => c.Name))}.");
    }

    public MidiConnection AddDevice(MidiDirection direction, MidiDeviceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name)) config.Name = config.Device;
        if (string.IsNullOrWhiteSpace(config.Name)) throw new ArgumentException("A MIDI device needs a name.");
        config.RetryIntervalSeconds = Math.Clamp(config.RetryIntervalSeconds, 1, 3600);
        if (direction == MidiDirection.Output) config.Events.Clear();
        var connection = new MidiConnection(direction, config);
        lock (_gate)
        {
            (direction == MidiDirection.Input ? _inputs : _outputs).Add(connection);
            (direction == MidiDirection.Input ? Settings.Inputs : Settings.Outputs).Add(config);
        }
        SaveSettings();
        _log.Info(LogSource, $"Added MIDI {(direction == MidiDirection.Input ? "In" : "Out")} '{config.Name}' ({config.Device})");
        if (config.AutoOpen && _run is not null) Open(connection);
        Changed();
        return connection;
    }

    /// <summary>Edits a connection's settings; reopens it when the device changed while open.</summary>
    public void UpdateDevice(MidiConnection connection, Action<MidiDeviceConfig> edit)
    {
        string device;
        lock (_gate)
        {
            device = connection.Config.Device;
            edit(connection.Config);
            connection.Config.RetryIntervalSeconds = Math.Clamp(connection.Config.RetryIntervalSeconds, 1, 3600);
        }
        SaveSettings();
        if (!string.Equals(device, connection.Config.Device, StringComparison.Ordinal) && (connection.IsOpen || connection.WantOpen))
        {
            CloseCore(connection, "Device changed");
            Open(connection);
        }
        Changed();
    }

    public void RemoveDevice(MidiConnection connection)
    {
        CloseCore(connection, "Removed");
        lock (_gate)
        {
            _inputs.Remove(connection);
            _outputs.Remove(connection);
            Settings.Inputs.Remove(connection.Config);
            Settings.Outputs.Remove(connection.Config);
        }
        SaveSettings();
        Changed();
    }

    /// <summary>Opens (or keeps trying to open, with Reopen on Close) a connection. Returns whether it is open now.</summary>
    public bool Open(MidiConnection connection)
    {
        lock (_gate)
        {
            if (connection.IsOpen) return true;
            connection.WantOpen = true;
        }
        TryOpen(connection);
        Changed();
        return connection.IsOpen;
    }

    public void Close(MidiConnection connection)
    {
        CloseCore(connection, "Closed");
        Changed();
    }

    private void TryOpen(MidiConnection connection)
    {
        lock (_deviceGate) TryOpenCore(connection);
        UpdateStatus();
    }

    private void TryOpenCore(MidiConnection connection)
    {
        if (connection.IsOpen) return;
        if (!IsAvailable)
        {
            Failed(connection, UnavailableReason ?? "MIDI is not available");
            return;
        }
        var ports = connection.Direction == MidiDirection.Input ? RefreshPorts(MidiDirection.Input) : RefreshPorts(MidiDirection.Output);
        connection.IsPresent = Present(ports, connection.Device);
        if (!connection.IsPresent)
        {
            Failed(connection, $"'{connection.Device}' is not connected");
            return;
        }
        int generation;
        lock (_gate) generation = ++connection.Generation;
        try
        {
            if (connection.Direction == MidiDirection.Input)
            {
                var port = _backend.OpenInput(connection.Device, p => OnPacket(connection, generation, p),
                    reason => OnClosedByDevice(connection, generation, reason));
                lock (_gate) connection.Input = port;
            }
            else
            {
                var port = _backend.OpenOutput(connection.Device, reason => OnClosedByDevice(connection, generation, reason));
                lock (_gate) connection.Output = port;
            }
            lock (_gate)
            {
                connection.State = MidiConnectionState.Open;
                connection.Message = "Open";
            }
            _log.Info(LogSource, $"Opened MIDI {Label(connection)} '{connection.Name}' ({connection.Device})");
        }
        catch (Exception ex)
        {
            Failed(connection, ex.Message);
        }
    }

    private void Failed(MidiConnection connection, string reason)
    {
        lock (_gate)
        {
            if (connection.WantOpen && connection.Config.ReopenOnClose)
            {
                var seconds = Math.Clamp(connection.Config.RetryIntervalSeconds, 1, 3600);
                connection.NextRetry = _time.GetUtcNow().AddSeconds(seconds);
                connection.State = MidiConnectionState.Waiting;
                connection.Message = $"{reason.TrimEnd('.')}; retrying every {seconds} s";
            }
            else
            {
                connection.WantOpen = false;
                connection.State = MidiConnectionState.Error;
                connection.Message = reason;
            }
        }
        UpdateStatus();
    }

    private void OnClosedByDevice(MidiConnection connection, int generation, string reason)
    {
        Lost(connection, generation, reason);
        Changed();
        Wake();
    }

    /// <summary>The device went away (or a send failed): drop the handle and, with Reopen on Close, schedule a retry.</summary>
    private void Lost(MidiConnection connection, int generation, string reason)
    {
        IDisposable? handle;
        lock (_gate)
        {
            if (connection.Generation != generation || !connection.IsOpen) return;
            handle = Detach(connection);
            connection.State = MidiConnectionState.Closed;
            connection.Message = reason;
        }
        Dispose(handle);
        _log.Warn(LogSource, $"MIDI {Label(connection)} '{connection.Name}' closed: {reason}");
        if (connection.WantOpen && connection.Config.ReopenOnClose) Failed(connection, reason);
        else
        {
            lock (_gate) connection.WantOpen = false;
            UpdateStatus();
        }
    }

    private void CloseCore(MidiConnection connection, string message)
    {
        IDisposable? handle;
        bool wasOpen;
        lock (_deviceGate)
        lock (_gate)
        {
            wasOpen = connection.IsOpen;
            connection.WantOpen = false;
            handle = Detach(connection);
            connection.State = MidiConnectionState.Closed;
            connection.Message = message;
        }
        Dispose(handle);
        if (wasOpen) _log.Info(LogSource, $"Closed MIDI {Label(connection)} '{connection.Name}'");
        UpdateStatus();
    }

    private static IDisposable? Detach(MidiConnection connection)
    {
        IDisposable? handle = (IDisposable?)connection.Input ?? connection.Output;
        connection.Input = null;
        connection.Output = null;
        connection.Generation++;
        return handle;
    }

    private void Dispose(IDisposable? handle)
    {
        try
        {
            handle?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Closing a MIDI port failed: {ex.Message}");
        }
    }

    // ---- Events (listeners) ---------------------------------------------------------------------------------------

    public MidiInEventConfig AddEvent(MidiConnection input, MidiInEventConfig listener)
    {
        RequireInput(input);
        if (string.IsNullOrWhiteSpace(listener.Name)) throw new ArgumentException("A MIDI event needs a name.");
        Normalize(listener);
        lock (_gate) input.Config.Events.Add(listener);
        SaveSettings();
        Changed();
        return listener;
    }

    public void UpdateEvent(MidiConnection input, MidiInEventConfig listener, Action<MidiInEventConfig> edit)
    {
        RequireInput(input);
        lock (_gate)
        {
            if (!input.Config.Events.Contains(listener)) throw new ArgumentException($"'{listener.Name}' is not an event of {input.Name}.");
            edit(listener);
            Normalize(listener);
        }
        SaveSettings();
        Changed();
    }

    public void RemoveEvent(MidiConnection input, MidiInEventConfig listener)
    {
        lock (_gate) input.Config.Events.Remove(listener);
        SaveSettings();
        Changed();
    }

    /// <summary>Every configured listener as (connection, listener).</summary>
    public IReadOnlyList<(MidiConnection Connection, MidiInEventConfig Event)> AllEvents()
    {
        lock (_gate) return _inputs.SelectMany(c => c.Config.Events.Select(e => (c, e))).ToArray();
    }

    /// <summary>
    /// Waits for the next message on a MIDI In connection (opening it if needed) — Streamer.bot's automatic detection.
    /// NoteOff messages are skipped when <paramref name="ignoreNoteOff"/>.
    /// </summary>
    public async Task<MidiMessage> LearnAsync(MidiConnection input, bool ignoreNoteOff, CancellationToken ct)
    {
        RequireInput(input);
        if (!input.IsOpen && !Open(input)) throw new InvalidOperationException($"MIDI In '{input.Name}' could not be opened: {input.Message}.");
        var learner = new Learner(input, ignoreNoteOff);
        lock (_gate) _learners.Add(learner);
        try
        {
            using var registration = ct.Register(() => learner.Completion.TrySetCanceled(ct));
            return await learner.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _learners.Remove(learner);
        }
    }

    private static void Normalize(MidiInEventConfig listener)
    {
        listener.Name = listener.Name.Trim();
        listener.Channel = Math.Clamp(listener.Channel, 1, 16);
        listener.Data = Math.Clamp(listener.Data, 0, 127);
    }

    private static void RequireInput(MidiConnection connection)
    {
        if (connection.Direction != MidiDirection.Input) throw new ArgumentException($"'{connection.Name}' is a MIDI Out device.");
    }

    // ---- Receiving ------------------------------------------------------------------------------------------------

    private void OnPacket(MidiConnection connection, int generation, MidiPacket packet)
    {
        try
        {
            if (!MidiMessage.TryParse(packet.Data, out var message)) return;
            if (Settings.IgnoreClockAndSensing && message.Command is MidiCommand.TimingClock or MidiCommand.AutoSensing) return;
            MidiInEventConfig[] listeners;
            Learner[] learners;
            lock (_gate)
            {
                if (connection.Generation != generation) return;
                listeners = connection.Config.Events.ToArray();
                learners = _learners.Where(l => l.Connection == connection).ToArray();
            }
            foreach (var learner in learners)
                if (!(learner.IgnoreNoteOff && message.Command == MidiCommand.NoteOff)) learner.Completion.TrySetResult(message);

            var matched = listeners.Where(l => MidiEventMatcher.Matches(l, message)).ToArray();
            _bus.Publish(new BotEvent(MidiEvents.Message, MidiEventMatcher.Args(message, connection.Config, matched, packet.TimestampMs)));
            MessageReceived?.Invoke(connection, message);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "Handling a MIDI message failed", ex);
        }
    }

    // ---- Sending --------------------------------------------------------------------------------------------------

    /// <summary>Sends to a MIDI Out connection (by name; empty = the first), opening it first when it is closed.</summary>
    public Task SendAsync(string device, MidiMessage message, CancellationToken ct = default) => SendAsync(device, [message], ct);

    public async Task SendAsync(string device, IReadOnlyList<MidiMessage> messages, CancellationToken ct = default)
    {
        var connection = Resolve(MidiDirection.Output, device);
        if (!connection.IsOpen && !Open(connection))
            throw new InvalidOperationException($"MIDI Out '{connection.Name}' is not open: {connection.Message}.");
        IMidiOutputPort? port;
        int generation;
        lock (_gate)
        {
            port = connection.Output;
            generation = connection.Generation;
        }
        if (port is null) throw new InvalidOperationException($"MIDI Out '{connection.Name}' is not open.");
        await Task.Run(() =>
        {
            try
            {
                foreach (var message in messages)
                {
                    ct.ThrowIfCancellationRequested();
                    port.Send(message.Bytes);
                }
            }
            catch (IOException ex)
            {
                Lost(connection, generation, ex.Message);
                Changed();
                throw;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a channel message to one channel, or to all 16 when <paramref name="channel"/> is empty (Streamer.bot:
    /// "leave blank to broadcast to all channels").
    /// </summary>
    public Task SendChannelAsync(string device, string channel, Func<int, MidiMessage> build, CancellationToken ct = default) =>
        SendAsync(device, Channels(channel).Select(build).ToArray(), ct);

    /// <summary>Note On, then (when <paramref name="noteOff"/>) Note Off after <paramref name="durationMs"/>.</summary>
    public async Task SendNoteAsync(string device, string channel, int note, int velocity, int durationMs, bool noteOff, CancellationToken ct = default)
    {
        var channels = Channels(channel);
        await SendAsync(device, channels.Select(c => MidiMessage.Channel(MidiCommand.NoteOn, c, note, velocity)).ToArray(), ct).ConfigureAwait(false);
        if (!noteOff) return;
        if (durationMs > 0) await Task.Delay(durationMs, ct).ConfigureAwait(false);
        await SendAsync(device, channels.Select(c => MidiMessage.Channel(MidiCommand.NoteOff, c, note, 0)).ToArray(), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Channel text: empty = 1-16, otherwise one channel 1-16.</summary>
    public static int[] Channels(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(1, 16).ToArray();
        return int.TryParse(channel.Trim(), out var c) && c is >= 1 and <= 16
            ? [c]
            : throw new ArgumentException($"Channel must be 1-16 or empty for all channels (got '{channel}').");
    }

    // ---- Status ---------------------------------------------------------------------------------------------------

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void Changed()
    {
        try { ConnectionsChanged?.Invoke(); }
        catch (Exception ex) { _log.Error(LogSource, "A MIDI listener failed", ex); }
    }

    private void UpdateStatus()
    {
        var all = Inputs.Concat(Outputs).ToArray();
        var open = all.Count(c => c.IsOpen);
        if (UnavailableReason is { } reason) SetStatus(ConnectionState.Disconnected, reason);
        else if (open > 0) SetStatus(ConnectionState.Connected, $"{open} of {all.Length} MIDI device{(all.Length == 1 ? "" : "s")} open");
        else if (all.Any(c => c.State == MidiConnectionState.Waiting)) SetStatus(ConnectionState.Connecting, "Waiting for MIDI devices");
        else if (all.Any(c => c.State == MidiConnectionState.Error))
            SetStatus(ConnectionState.Error, all.First(c => c.State == MidiConnectionState.Error) is var e ? $"{e.Name}: {e.Message}" : "");
        else SetStatus(ConnectionState.Disconnected, all.Length == 0 ? "No MIDI devices configured" : "No MIDI device open");
    }

    private void SetStatus(ConnectionState state, string message)
    {
        if (State == state && StatusMessage == message) return;
        State = state;
        StatusMessage = message;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { _log.Error(LogSource, "A status listener failed", ex); }
    }

    private static bool Present(IReadOnlyList<MidiPortInfo> ports, string device) =>
        ports.Any(p => string.Equals(p.Name, device, StringComparison.OrdinalIgnoreCase));

    private static string Label(MidiConnection connection) => connection.Direction == MidiDirection.Input ? "In" : "Out";

    private sealed class Learner(MidiConnection connection, bool ignoreNoteOff)
    {
        public MidiConnection Connection => connection;
        public bool IgnoreNoteOff => ignoreNoteOff;
        public TaskCompletionSource<MidiMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
