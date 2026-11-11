using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Obsbot;

namespace Cuebot.StreamApps.Vnyan;

/// <summary>Bot event types published by <see cref="VnyanService"/>.</summary>
public static class VnyanEvents
{
    /// <summary>VNyan's "Websocket Message" node sent text to Cuebot's VNyan server.</summary>
    public const string Message = "vnyan.message";
}

/// <summary>
/// VNyan, four ways in and one way out:
/// <list type="bullet">
/// <item>HTTP API: <c>POST /</c> with <c>{"action":"&lt;name&gt;","payload":{..}}</c> fires every "API Message" node
/// whose name is that action. VNyan answers <c>{"result":0}</c> once it got the message (observed, not documented);
/// it cannot tell whether any node used it.</item>
/// <item>WebSocket receiver (ws://host:8000/vnyan, "Start Receiver" on): text fires its "WebSocket" nodes, the
/// number after the command being the node's Value. The connection is kept open after the first command.</item>
/// <item>OSC receiver (UDP, port set in VNyan): <c>/VNyan/Param/Float</c> and <c>/VNyan/Param/String</c> (name,
/// value) set VNyan parameters directly; <c>/avatar/parameters/&lt;name&gt;</c> too.</item>
/// <item>Out: a small WebSocket server here receives what VNyan's "Websocket Message" node sends, as the
/// <see cref="VnyanEvents.Message"/> trigger.</item>
/// </list>
/// The status combines how the last message went, the kept WebSocket and the server.
/// </summary>
public sealed class VnyanService : IConnectionStatus, IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly HttpClient _http;
    private readonly StreamAppsTimings _timings;
    private readonly KeptWebSocketSender _webSocket;
    private readonly Func<IPEndPoint, byte[], CancellationToken, Task> _sendUdp;
    private readonly SemaphoreSlim _listenGate = new(1, 1);
    private readonly ConcurrentDictionary<long, string> _peers = new();
    private CancellationTokenSource? _listenRun;
    private Task _listenLoop = Task.CompletedTask;
    private long _nextPeer;
    private string _sendStatus = "Nothing sent yet";
    private bool _sendFailed;
    private bool _sendOk;
    private string _listenStatus = "";
    private bool _listenFailed;

    public VnyanService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, http, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal VnyanService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, HttpClient http,
        Func<IWebSocketTransport> webSocketTransports, StreamAppsTimings timings, Func<IPEndPoint, byte[], CancellationToken, Task>? sendUdp = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _http = http;
        _timings = timings;
        _webSocket = new KeptWebSocketSender(webSocketTransports, timings, (url, reason) =>
            $"VNyan's WebSocket receiver is not reachable at {url.Authority} ({reason}). Is VNyan open, and is Settings > Misc > Websockets > Start Receiver on with port {url.Port}?"
            + RemoteHint);
        _sendUdp = sendUdp ?? SendUdpAsync;
    }

    public string Name => "VNyan";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Nothing sent yet";
    public event Action? StatusChanged;

    public VnyanSettings Settings => _settings.Value.Vnyan;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>The names typed into this profile's "Send to VNyan" steps (set at startup; for suggestions and the card).</summary>
    public Func<IEnumerable<string>> UsedActionNames { get; set; } = () => [];

    private string Host
    {
        get
        {
            var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
            return host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        }
    }

    public Uri Url => new($"http://{Host}:{(Settings.Port is > 0 and < 65536 ? Settings.Port : 8069)}/");

    public Uri WebSocketUrl => new($"ws://{Host}:{WebSocketPort}{VnyanProtocol.WebSocketPath}");

    public int WebSocketPort => Settings.WebSocketPort is > 0 and < 65536 ? Settings.WebSocketPort : 8000;

    public int ListenPort => Settings.ListenPort is > 0 and < 65536 ? Settings.ListenPort : 8070;

    /// <summary>True while Cuebot's server for VNyan's "Websocket Message" node is listening.</summary>
    public bool IsListening { get; private set; }

    /// <summary>VNyan connections to Cuebot's server right now.</summary>
    public int ListenerClients => _peers.Count;

    /// <summary>True while the WebSocket connection to VNyan's receiver is open.</summary>
    public bool IsWebSocketOpen => _webSocket.IsOpen;

    private bool IsRemote =>
        !(Settings.Host.Trim() is "" or "localhost" || (IPAddress.TryParse(Settings.Host.Trim().Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));

    private string RemoteHint => IsRemote
        ? " VNyan's docs only name localhost; if VNyan runs on another PC, it may only accept messages from that PC itself."
        : "";

    // ---- HTTP API (API Message nodes) ----

    /// <summary>Sends text values to the API Message nodes named <paramref name="action"/>.</summary>
    public Task SendAsync(string action, IReadOnlyDictionary<string, string> payload, CancellationToken ct = default) =>
        SendAsync(action, VnyanProtocol.Payload(payload), ct);

    /// <summary>Sends an API message; throws when VNyan is not reachable or refused it.</summary>
    public async Task SendAsync(string action, JsonObject payload, CancellationToken ct = default)
    {
        var name = Steps.Require(action, "Action name");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var content = new StringContent(VnyanProtocol.Body(name, payload), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(Url, content, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StreamAppRequestException($"VNyan answered {(int)response.StatusCode}: {Short(body)}");
            if (JsonUtil.TryParse(body) is { ValueKind: JsonValueKind.Object } json && json.Num("result") is { } result && result != 0)
                throw new StreamAppRequestException($"VNyan refused the message (result {result}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SendFailed("VNyan did not answer in time");
            throw new TimeoutException("VNyan did not answer in time. Is VNyan open?" + RemoteHint);
        }
        catch (HttpRequestException ex)
        {
            SendFailed($"{Url.Authority} not reachable ({ex.Message})");
            throw new StreamAppRequestException($"VNyan is not reachable at {Url.Authority}. Is VNyan open? ({ex.Message})" + RemoteHint);
        }
        catch (StreamAppRequestException ex)
        {
            SendFailed(ex.Message);
            throw;
        }
        SendSucceeded($"VNyan at {Url.Authority} got '{name}'");
    }

    /// <summary>Sends a message no node listens to, to check VNyan answers.</summary>
    public Task TestAsync(CancellationToken ct = default) => SendAsync("CuebotTest", new Dictionary<string, string>(), ct);

    // ---- WebSocket receiver (WebSocket nodes) ----

    /// <summary>Sends text to VNyan's WebSocket nodes (e.g. "Wave" or "Throw 5"), keeping the connection open.</summary>
    public async Task SendWebSocketAsync(string text, CancellationToken ct = default)
    {
        if (text.Trim().Length == 0) throw new ArgumentException("Command is required.");
        var url = WebSocketUrl;
        try
        {
            await _webSocket.SendAsync(url, text, ct).ConfigureAwait(false);
        }
        catch (StreamAppRequestException ex)
        {
            SendFailed(ex.Message);
            throw;
        }
        SendSucceeded($"Sent '{text}' to VNyan's WebSocket receiver at {url.Authority}");
    }

    // ---- OSC receiver (parameters) ----

    /// <summary>Sends OSC messages to VNyan's OSC receiver (several go as one bundle).</summary>
    public async Task SendOscAsync(IReadOnlyList<OscMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) throw new ArgumentException("Nothing to send.");
        if (Settings.OscPort is not (> 0 and < 65536))
            throw new StreamAppRequestException("Set VNyan's OSC port on the VNyan card first: in VNyan it is under Settings > Misc (OSC Receiver Port).");
        var packet = messages.Count == 1 ? Osc.Encode(messages[0]) : Osc.EncodeBundle(messages);
        try
        {
            var address = await OscTransports.ResolveAsync(Host.Trim('[', ']'), ct).ConfigureAwait(false);
            await _sendUdp(new IPEndPoint(address, Settings.OscPort), packet, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            SendFailed($"OSC to {Host}:{Settings.OscPort} failed ({ex.Message})");
            throw new StreamAppRequestException($"Could not send OSC to VNyan at {Host}:{Settings.OscPort} ({ex.Message}).");
        }
        // UDP has no answer: VNyan's Monitor window shows whether the parameters arrived.
        SendSucceeded($"Sent {(messages.Count == 1 ? "a parameter" : $"{messages.Count} parameters")} to VNyan's OSC port {Settings.OscPort}");
    }

    private static async Task SendUdpAsync(IPEndPoint target, byte[] packet, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.SendToAsync(packet, SocketFlags.None, target, ct).ConfigureAwait(false);
    }

    // ---- Cuebot's server for the "Websocket Message" node ----

    /// <summary>Starts the server when it is switched on in the settings.</summary>
    public Task StartAsync() => Settings.Listen ? StartListeningAsync() : Task.CompletedTask;

    /// <summary>Starts (or restarts on a new port) the server VNyan's "Websocket Message" node sends to.</summary>
    public async Task StartListeningAsync()
    {
        await _listenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListeningCoreAsync().ConfigureAwait(false);
            var run = new CancellationTokenSource();
            _listenRun = run;
            var port = ListenPort;
            _listenLoop = Task.Run(() => ListenLoopAsync(port, run.Token), CancellationToken.None);
        }
        finally
        {
            _listenGate.Release();
        }
    }

    public async Task StopListeningAsync()
    {
        await _listenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListeningCoreAsync().ConfigureAwait(false);
            SetListenStatus("", false);
        }
        finally
        {
            _listenGate.Release();
        }
    }

    private async Task StopListeningCoreAsync()
    {
        if (_listenRun is not { } run) return;
        _listenRun = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _listenLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        run.Dispose();
        IsListening = false;
    }

    private async Task ListenLoopAsync(int port, CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                LocalWebSocketServer server;
                try
                {
                    server = LocalWebSocketServer.Start(IPAddress.Loopback, port, LocalWebSocketServer.AnyPath);
                }
                catch (SocketException ex)
                {
                    SetListenStatus($"cannot listen on port {port} ({ex.Message}); retrying", true);
                    _log.Warn(Name, $"Cannot listen for VNyan on port {port}: {ex.Message}");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.MaxReconnectDelay.Ticks));
                    continue;
                }
                await using (server.ConfigureAwait(false))
                {
                    IsListening = true;
                    UpdateListenStatus();
                    _log.Info(Name, $"Listening for VNyan's Websocket Message node on ws://127.0.0.1:{server.Port}/");
                    var sessions = new List<Task>();
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var (socket, remote) = await server.AcceptAsync(ct).ConfigureAwait(false);
                            sessions.Add(RunPeerAsync(new WebSocketTextChannel(socket), remote, ct));
                            sessions.RemoveAll(t => t.IsCompleted);
                        }
                    }
                    finally
                    {
                        IsListening = false;
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
            _log.Error(Name, $"VNyan server stopped: {ex.Message}");
            SetListenStatus($"server stopped ({ex.Message})", true);
        }
    }

    private async Task RunPeerAsync(WebSocketTextChannel channel, string remote, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextPeer);
        _peers[id] = remote;
        UpdateListenStatus();
        try
        {
            while (await channel.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                var args = VnyanProtocol.ParseIncoming(text);
                args["service"] = Name;
                _bus.Publish(new BotEvent(VnyanEvents.Message, args));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or InvalidDataException)
        {
            _log.Warn(Name, $"VNyan connection error: {ex.Message}");
        }
        finally
        {
            _peers.TryRemove(id, out _);
            await channel.DisposeAsync().ConfigureAwait(false);
            if (!ct.IsCancellationRequested) UpdateListenStatus();
        }
    }

    private void UpdateListenStatus()
    {
        var clients = _peers.Count;
        SetListenStatus(clients switch
        {
            0 => $"listening on ws://127.0.0.1:{ListenPort}/",
            1 => $"VNyan connected to ws://127.0.0.1:{ListenPort}/",
            _ => $"{clients} connections on ws://127.0.0.1:{ListenPort}/",
        }, false);
    }

    // ---- Status ----

    private void SendFailed(string message)
    {
        _log.Warn(Name, message);
        _sendStatus = message;
        _sendFailed = true;
        _sendOk = false;
        UpdateStatus();
    }

    private void SendSucceeded(string message)
    {
        _sendStatus = message;
        _sendFailed = false;
        _sendOk = true;
        UpdateStatus();
    }

    private void SetListenStatus(string message, bool failed)
    {
        _listenStatus = message;
        _listenFailed = failed;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        State = _sendFailed || _listenFailed ? ConnectionState.Error
            : _sendOk || _peers.Count > 0 || IsWebSocketOpen ? ConnectionState.Connected
            : ConnectionState.Disconnected;
        var parts = new List<string> { _sendStatus };
        if (IsWebSocketOpen) parts.Add("WebSocket open");
        if (_listenStatus.Length > 0) parts.Add(_listenStatus);
        StatusMessage = string.Join(" · ", parts);
        StatusChanged?.Invoke();
    }

    /// <summary>Names that differ only in capital letters (VNyan treats them as different nodes), as "Wave / wave".</summary>
    public static IReadOnlyList<string> CaseClashes(IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g))
            .ToList();

    private static string Short(string text) => text.Length > 200 ? text[..200] + "…" : text;

    public async ValueTask DisposeAsync()
    {
        await StopListeningCoreAsync().ConfigureAwait(false);
        await _webSocket.DisposeAsync().ConfigureAwait(false);
        _listenGate.Dispose();
    }
}
