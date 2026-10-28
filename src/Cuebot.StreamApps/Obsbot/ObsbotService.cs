using System.Net.Sockets;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.StreamApps.Obsbot;

/// <summary>
/// OBSBOT PTZ webcams through OBSBOT Center's OSC server (UDP or TCP), or straight to a Tail 2 / Tail Air. Sends the
/// "Connected" hello, polls state (device info, zoom, gimbal, AI tracking, presets, Meet modes) and turns changes into
/// events. UDP has no connection, so the service counts as connected once Center answered; commands are sent as soon as
/// the socket is open either way. Reconnects (TCP) or keeps saying hello (UDP) with exponential backoff.
/// </summary>
public sealed class ObsbotService : IConnectionStatus, IAsyncDisposable
{
    public const string ConnectedEvent = "obsbot.connected";
    public const string DisconnectedEvent = "obsbot.disconnected";
    public const string DeviceChangedEvent = "obsbot.deviceChanged";
    public const string SleepStateChangedEvent = "obsbot.sleepStateChanged";
    public const string ZoomChangedEvent = "obsbot.zoomChanged";
    public const string GimbalMovedEvent = "obsbot.gimbalMoved";
    public const string AiTrackingChangedEvent = "obsbot.aiTrackingChanged";
    public const string MessageEvent = "obsbot.message";

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<ObsbotTransport, IOscTransport> _transports;
    private readonly StreamAppsTimings _timings;
    private readonly TimeSpan? _tickOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile IOscTransport? _transport;
    private volatile bool _connected;
    private volatile ObsbotState _state = ObsbotState.Empty;
    private long _lastHeardTicks;
    private long _messageCount;

    public ObsbotService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, OscTransports.Create, StreamAppsTimings.Default)
    {
    }

    internal ObsbotService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<ObsbotTransport, IOscTransport> transports,
        StreamAppsTimings timings, TimeSpan? tick = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transports = transports;
        _timings = timings;
        _tickOverride = tick;
    }

    public string Name => "OBSBOT";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (on a background thread) when the polled state changed.</summary>
    public event Action? DataChanged;

    public ObsbotSettings Settings => _settings.Value.Obsbot;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>OBSBOT Center answered (UDP) or the TCP connection is up.</summary>
    public bool IsConnected => _connected;

    /// <summary>The socket is open: commands can be sent (over UDP even before Center answered).</summary>
    public bool CanSend => _transport is not null;

    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    public ObsbotState Current => _state;

    /// <summary>The last OSC message received (for the UI).</summary>
    public OscMessage? LastMessage { get; private set; }

    /// <summary>The series used by Tiny/Tail/Meet commands: the setting, or a guess from the selected device's name.</summary>
    public ObsbotSeries Series => Settings.Series != ObsbotSeries.Auto ? Settings.Series : ObsbotProtocol.Guess(_state.SelectedName, _state.DeviceType);

    private string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
    private int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 16284;
    private int ListenPort => Settings.ListenPort is >= 0 and < 65536 ? Settings.ListenPort : 57120;

    private TimeSpan Tick => _tickOverride ?? TimeSpan.FromSeconds(Settings.PollSeconds > 0 ? Math.Min(Settings.PollSeconds, 3600) : 5);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Connecting to OBSBOT Center...");
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects when auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(Name, $"Automatic connection failed: {ex.Message}");
        }
    }

    /// <summary>Sends one OSC message to OBSBOT Center.</summary>
    public async Task SendAsync(OscMessage message, CancellationToken ct = default)
    {
        var transport = _transport ?? throw new StreamAppNotConnectedException(Name);
        try
        {
            await transport.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new StreamAppRequestException($"Could not send {message.Address} to OBSBOT Center: {ex.Message}");
        }
        _log.Debug(Name, $"Sent {message}");
    }

    public async Task SendAllAsync(IEnumerable<OscMessage> messages, CancellationToken ct = default)
    {
        foreach (var message in messages) await SendAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>Asks for fresh state and waits briefly for the answers (never throws on silence).</summary>
    public async Task<ObsbotState> RefreshAsync(CancellationToken ct = default)
    {
        var before = Interlocked.Read(ref _messageCount);
        var queries = ObsbotProtocol.Queries(Series).ToArray();
        await SendAllAsync(queries, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Min(1500, _timings.RequestTimeout.TotalMilliseconds));
        // Replies arrive within a few ms; stop once at least the general three came back (or time runs out).
        while (DateTime.UtcNow < deadline && Interlocked.Read(ref _messageCount) - before < 3)
            await Task.Delay(20, ct).ConfigureAwait(false);
        await Task.Delay(30, ct).ConfigureAwait(false);
        return _state;
    }

    /// <summary>The state as trigger/sub-action variables (obsbot.zoom, obsbot.deviceName, ...).</summary>
    public static Dictionary<string, object?> StateArgs(ObsbotState s)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["obsbot.deviceIndex"] = s.SelectedDevice,
            ["obsbot.deviceName"] = s.SelectedName,
            ["obsbot.awake"] = s.Awake,
            ["obsbot.zoom"] = s.Zoom,
            ["obsbot.fov"] = s.Fov,
            ["obsbot.fovDegrees"] = ObsbotState.FovLabel(s.Fov),
            ["obsbot.pitch"] = s.Pitch,
            ["obsbot.yaw"] = s.Yaw,
            ["obsbot.aiLocked"] = s.AiLocked,
            ["obsbot.virtualBackground"] = s.VirtualBackground,
            ["obsbot.autoFraming"] = s.AutoFraming,
            ["obsbot.deviceCount"] = s.Devices.Count(d => d.Connected),
        };
        foreach (var p in s.Presets)
        {
            args[$"obsbot.preset{p.Index + 1}.exists"] = p.Exists;
            args[$"obsbot.preset{p.Index + 1}.name"] = p.Name;
        }
        return args;
    }

    // ---- Loop ----

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var transport = _transports(Settings.Transport);
                var display = $"{(Settings.Transport == ObsbotTransport.Tcp ? "tcp" : "udp")}://{Host}:{Port}";
                string reason = "connection closed";
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(_timings.ConnectTimeout);
                        await transport.OpenAsync(Host, Port, ListenPort, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    reason = $"{display} not reachable ({(ex is OperationCanceledException ? "timed out" : ex.Message)})";
                    if (!Settings.AutoReconnect)
                    {
                        SetStatus(ConnectionState.Disconnected, reason);
                        return;
                    }
                    SetStatus(ConnectionState.Connecting, $"{reason}; retrying in {Seconds(delay)}");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = Next(delay);
                    continue;
                }

                var hadConnected = false;
                try
                {
                    hadConnected = await RunSessionAsync(transport, display, ct).ConfigureAwait(false);
                    reason = transport.CloseReason ?? "connection closed";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    reason = "disconnected";
                }
                catch (Exception ex)
                {
                    reason = transport.CloseReason ?? ex.Message;
                    hadConnected = _connected;
                }
                finally
                {
                    _transport = null;
                    if (ct.IsCancellationRequested)
                    {
                        try
                        {
                            using var bye = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                            await transport.SendAsync(ObsbotProtocol.Goodbye(), bye.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                        {
                            // Best effort.
                        }
                    }
                    await transport.DisposeAsync().ConfigureAwait(false);
                    MarkLost(reason);
                }
                if (ct.IsCancellationRequested) return;
                if (hadConnected) delay = _timings.ReconnectDelay;
                if (!Settings.AutoReconnect)
                {
                    SetStatus(ConnectionState.Disconnected, reason);
                    return;
                }
                SetStatus(ConnectionState.Connecting, $"{reason}; retrying in {Seconds(delay)}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Next(delay);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Connection loop stopped: {ex.Message}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    /// <summary>One open socket: hello, receive loop, polling. Returns whether Center was ever heard from.</summary>
    private async Task<bool> RunSessionAsync(IOscTransport transport, string display, CancellationToken ct)
    {
        _transport = transport;
        var tcp = Settings.Transport == ObsbotTransport.Tcp;
        var everConnected = false;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = Task.Run(async () =>
        {
            while (await transport.ReceiveAsync(session.Token).ConfigureAwait(false) is { } message)
            {
                OnMessage(message, display);
                everConnected |= _connected;
            }
        }, CancellationToken.None);

        if (tcp)
        {
            MarkConnected($"Connected to OBSBOT Center at {display}");
            everConnected = true;
        }
        else
        {
            if (transport.CloseReason is { } warning) _log.Warn(Name, warning);
            SetStatus(ConnectionState.Connecting, $"Sending to {display}; waiting for OBSBOT Center to answer (OSC on? ports match?)");
        }

        try
        {
            if (tcp) await transport.SendAsync(ObsbotProtocol.Hello(), session.Token).ConfigureAwait(false);
            var nextPoll = DateTime.UtcNow;
            while (!receive.IsCompleted)
            {
                if (!_connected && !tcp)
                {
                    await transport.SendAsync(ObsbotProtocol.Hello(), session.Token).ConfigureAwait(false);
                }
                else if (!tcp && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastHeardTicks), DateTimeKind.Utc) > SilenceLimit())
                {
                    MarkLost("OBSBOT Center stopped answering");
                    SetStatus(ConnectionState.Connecting, $"OBSBOT Center stopped answering at {display}; still trying");
                    continue;
                }
                if (_connected && Settings.PollSeconds > 0 && DateTime.UtcNow >= nextPoll)
                {
                    foreach (var query in ObsbotProtocol.Queries(Series)) await transport.SendAsync(query, session.Token).ConfigureAwait(false);
                    nextPoll = DateTime.UtcNow + Tick;
                }
                await Task.WhenAny(receive, Task.Delay(_connected ? Tick : Min(Tick, _timings.ReconnectDelay * 2), session.Token)).ConfigureAwait(false);
            }
            await receive.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
                // Closing.
            }
        }
        return everConnected;
    }

    private TimeSpan SilenceLimit()
    {
        var polled = Settings.PollSeconds > 0 ? Tick * 3 : TimeSpan.MaxValue;
        return polled < TimeSpan.FromSeconds(15) && _tickOverride is null ? TimeSpan.FromSeconds(15) : polled;
    }

    private void OnMessage(OscMessage message, string display)
    {
        Interlocked.Exchange(ref _lastHeardTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _messageCount);
        LastMessage = message;
        if (!_connected) MarkConnected($"OBSBOT Center answered at {display}");

        var before = _state;
        var after = ObsbotProtocol.Apply(before, message);
        if (ReferenceEquals(before, after))
        {
            if (message.Address != ObsbotProtocol.ConnectedResp)
                Publish(MessageEvent, new()
                {
                    ["obsbot.address"] = message.Address,
                    ["obsbot.args"] = JsonArgs(message),
                    ["obsbot.arg0"] = message.Args.Count > 0 ? ArgValue(message.Args[0]) : null,
                    ["obsbot.arg1"] = message.Args.Count > 1 ? ArgValue(message.Args[1]) : null,
                    ["obsbot.arg2"] = message.Args.Count > 2 ? ArgValue(message.Args[2]) : null,
                });
            return;
        }
        _state = after;
        if (before.SelectedDevice is not null && (before.SelectedDevice != after.SelectedDevice || before.SelectedName != after.SelectedName))
            Publish(DeviceChangedEvent, StateArgs(after));
        if (before.Awake is not null && before.Awake != after.Awake) Publish(SleepStateChangedEvent, StateArgs(after));
        if (before.Zoom is not null && (before.Zoom != after.Zoom || before.Fov != after.Fov))
        {
            var args = StateArgs(after);
            args["obsbot.oldZoom"] = before.Zoom;
            Publish(ZoomChangedEvent, args);
        }
        if (before.Pitch is not null && (before.Pitch != after.Pitch || before.Yaw != after.Yaw)) Publish(GimbalMovedEvent, StateArgs(after));
        if (before.AiLocked is not null && before.AiLocked != after.AiLocked) Publish(AiTrackingChangedEvent, StateArgs(after));
        DataChanged?.Invoke();
    }

    private static object? ArgValue(object? arg) => arg switch
    {
        float f => (double)f,
        byte[] b => Convert.ToBase64String(b),
        _ => arg,
    };

    private static string JsonArgs(OscMessage message) => System.Text.Json.JsonSerializer.Serialize(message.Args.Select(ArgValue));

    private void MarkConnected(string message)
    {
        SetStatus(ConnectionState.Connected, message);
        if (_connected) return;
        _connected = true;
        _log.Info(Name, message);
        Publish(ConnectedEvent, []);
    }

    private void MarkLost(string reason)
    {
        if (!_connected) return;
        _connected = false;
        _state = ObsbotState.Empty;
        _log.Warn(Name, $"Connection lost: {reason}");
        Publish(DisconnectedEvent, new() { ["reason"] = reason });
        DataChanged?.Invoke();
    }

    private void Publish(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("obsbot.host", Host);
        args.TryAdd("obsbot.port", Port);
        args.TryAdd("obsbot.transport", Settings.Transport.ToString().ToUpperInvariant());
        args.TryAdd("service", Name);
        _bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private TimeSpan Next(TimeSpan current) => TimeSpan.FromTicks(Math.Min(current.Ticks * 2, _timings.MaxReconnectDelay.Ticks));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Seconds(TimeSpan delay) => delay.TotalSeconds >= 1 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMilliseconds:0} ms";

    private async Task StopAsync()
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Error while disconnecting: {ex.Message}");
        }
        run.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
