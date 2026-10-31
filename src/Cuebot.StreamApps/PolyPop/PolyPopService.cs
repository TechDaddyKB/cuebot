using System.Collections.Concurrent;
using System.Net.Sockets;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.PolyPop;

/// <summary>
/// PolyPop through its community WebSocket plugin. The published plugin is a websocket <em>client</em> (it connects to
/// ws://127.0.0.1:&lt;its Port setting&gt;), so by default Cuebot listens on Address:Port/Endpoint and the plugin connects;
/// <see cref="PolyPopMode.Client"/> connects out instead, for plugin builds that listen. Either way alerts are sent as
/// <see cref="PolyPopProtocol.Alert"/> to every connected plugin instance and messages from PolyPop become events.
/// </summary>
public sealed class PolyPopService : IConnectable, IAsyncDisposable
{
    public const string Prefix = "polypop";
    public const string ConnectedEvent = "polypop.connected";
    public const string DisconnectedEvent = "polypop.disconnected";
    public const string MessageEvent = "polypop.message";

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<IWebSocketTransport> _transports;
    private readonly StreamAppsTimings _timings;
    private readonly ConcurrentDictionary<long, Peer> _peers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextPeer;
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;

    public PolyPopService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal PolyPopService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transports = transports;
        _timings = timings;
    }

    public string Name => "PolyPop";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not started";
    public event Action? StatusChanged;

    public PolyPopSettings Settings => _settings.Value.PolyPop;

    public void SaveSettings() => _settings.ScheduleSave();

    public bool IsConnected => !_peers.IsEmpty;

    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    /// <summary>Connected plugin instances.</summary>
    public int ClientCount => _peers.Count;

    /// <summary>The port actually listened on in server mode (differs from the setting when it is 0), else 0.</summary>
    public int ListeningPort { get; private set; }

    /// <summary>The last message received from PolyPop (for the UI).</summary>
    public PolyPopMessage? LastMessage { get; private set; }

    public string Address => Settings.Address.Trim().Length > 0 ? Settings.Address.Trim() : "127.0.0.1";

    public string Endpoint => LocalWebSocketServer.NormalizePath(Settings.Endpoint);

    public int Port => Settings.Port is >= 0 and < 65536 ? Settings.Port : 9652;

    /// <summary>Starts listening (server mode) or connecting (client mode) in the background.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            var run = new CancellationTokenSource();
            _run = run;
            var mode = Settings.Mode;
            SetStatus(ConnectionState.Connecting, mode == PolyPopMode.Server ? "Starting..." : "Connecting to PolyPop...");
            _loop = Task.Run(() => mode == PolyPopMode.Server ? ServerLoopAsync(run.Token) : ClientLoopAsync(run.Token), CancellationToken.None);
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
            SetStatus(ConnectionState.Disconnected, "Stopped");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts when auto-start is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoStart) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(Name, $"Automatic start failed: {ex.Message}");
        }
    }

    /// <summary>"name=value" lines (or a JSON object) as alert variables.</summary>
    public static Dictionary<string, string> ParseVariables(string text) => Steps.ParsePairs(text);

    /// <summary>Raises the PolyPop WebSocket Alert named <paramref name="alertName"/> with text variables. Returns the number of plugins reached.</summary>
    public async Task<int> TriggerAlertAsync(string alertName, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
    {
        var title = Steps.Require(alertName, "Alert name");
        var peers = _peers.Values.ToArray();
        if (peers.Length == 0) throw new StreamAppNotConnectedException(Name);
        var message = PolyPopProtocol.Alert(title, variables);
        var sent = 0;
        foreach (var peer in peers)
        {
            try
            {
                await peer.Send(message, ct).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
                _log.Warn(Name, $"Could not send the alert to {peer.Display}: {ex.Message}");
            }
        }
        if (sent == 0) throw new StreamAppNotConnectedException(Name);
        return sent;
    }

    // ---- Server mode ----

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                LocalWebSocketServer server;
                try
                {
                    server = LocalWebSocketServer.Start(LocalWebSocketServer.ParseAddress(Address), Port, Endpoint);
                }
                catch (Exception ex) when (ex is SocketException or FormatException)
                {
                    var reason = ex is FormatException ? ex.Message : $"cannot listen on {Address}:{Port} ({ex.Message})";
                    SetStatus(ConnectionState.Error, $"{reason}; retrying in {Seconds(delay)}");
                    _log.Warn(Name, reason);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = Next(delay);
                    continue;
                }
                await using (server.ConfigureAwait(false))
                {
                    ListeningPort = server.Port;
                    UpdateServerStatus();
                    _log.Info(Name, $"Listening for the PolyPop plugin on ws://{Address}:{server.Port}{Endpoint}");
                    var sessions = new List<Task>();
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var (socket, remote) = await server.AcceptAsync(ct).ConfigureAwait(false);
                            var channel = new WebSocketTextChannel(socket);
                            sessions.Add(RunPeerAsync(remote, channel.SendAsync, channel.ReceiveAsync, channel, () => channel.CloseReason, ct));
                            sessions.RemoveAll(t => t.IsCompleted);
                        }
                    }
                    finally
                    {
                        ListeningPort = 0;
                        try
                        {
                            await Task.WhenAll(sessions).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Stopping.
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"PolyPop server stopped: {ex.Message}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    private void UpdateServerStatus()
    {
        if (_run is null) return;
        var count = _peers.Count;
        if (count > 0)
            SetStatus(ConnectionState.Connected, count == 1 ? $"PolyPop connected ({_peers.Values.First().Display})" : $"{count} PolyPop plugin instances connected");
        else
            SetStatus(ConnectionState.Connecting, $"Waiting for PolyPop on ws://{Address}:{ListeningPort}{Endpoint} (set the WebSocket plugin's Port to {ListeningPort})");
    }

    // ---- Client mode ----

    private async Task ClientLoopAsync(CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var host = Address.Contains(':') && !Address.StartsWith('[') ? $"[{Address}]" : Address;
                var url = new Uri($"ws://{host}:{Port}{Endpoint}");
                var transport = _transports();
                var connected = false;
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(_timings.ConnectTimeout);
                        await transport.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
                    }
                    connected = true;
                    delay = _timings.ReconnectDelay;
                    await RunPeerAsync(url.ToString(), transport.SendAsync, transport.ReceiveAsync, transport, () => transport.CloseReason, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    SetStatus(ConnectionState.Connecting, $"PolyPop not reachable at {url} ({(ex is OperationCanceledException ? "timed out" : ex.Message)}); retrying in {Seconds(delay)}");
                }
                if (ct.IsCancellationRequested) return;
                if (connected) SetStatus(ConnectionState.Connecting, $"PolyPop disconnected; reconnecting in {Seconds(delay)}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Next(delay);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    // ---- Peers ----

    private sealed record Peer(string Display, Func<string, CancellationToken, Task> Send);

    private async Task RunPeerAsync(string display, Func<string, CancellationToken, Task> send, Func<CancellationToken, Task<string?>> receive,
        IAsyncDisposable connection, Func<string?> closeReason, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextPeer);
        var first = false;
        try
        {
            _peers[id] = new Peer(display, send);
            first = _peers.Count == 1;
            _log.Info(Name, $"PolyPop plugin connected ({display})");
            if (first) Publish(ConnectedEvent, new() { ["polypop.remote"] = display });
            if (Settings.Mode == PolyPopMode.Client) SetStatus(ConnectionState.Connected, $"Connected to PolyPop at {display}");
            else UpdateServerStatus();

            while (await receive(ct).ConfigureAwait(false) is { } text) OnMessage(text, display);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or InvalidDataException)
        {
            _log.Warn(Name, $"PolyPop connection error: {ex.Message}");
        }
        finally
        {
            _peers.TryRemove(id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
            _log.Info(Name, $"PolyPop plugin disconnected ({display}{(closeReason() is { } r ? ": " + r : "")})");
            if (_peers.IsEmpty) Publish(DisconnectedEvent, new() { ["polypop.remote"] = display, ["reason"] = closeReason() ?? "closed" });
            if (Settings.Mode == PolyPopMode.Server && !ct.IsCancellationRequested) UpdateServerStatus();
        }
    }

    private void OnMessage(string text, string display)
    {
        var message = PolyPopProtocol.Parse(text);
        LastMessage = message;
        if (string.Equals(message.Type, "disconnect", StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug(Name, $"PolyPop plugin is unloading ({display})");
            return;
        }
        Publish(MessageEvent, new()
        {
            ["polypop.type"] = message.Type,
            ["polypop.name"] = message.Name,
            ["polypop.data"] = message.Data,
            ["polypop.sourceId"] = message.SourceId,
            ["polypop.raw"] = message.Raw,
            ["polypop.remote"] = display,
        });
        StatusChanged?.Invoke();
    }

    private void Publish(string type, Dictionary<string, object?> args)
    {
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
            _log.Error(Name, $"Error while stopping: {ex.Message}");
        }
        run.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
