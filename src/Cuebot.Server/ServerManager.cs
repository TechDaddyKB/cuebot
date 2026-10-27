using System.Net;
using System.Threading.Channels;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Storage;
using Cuebot.Scripting.Compat;
using Cuebot.Server.Custom;
using Cuebot.Server.Hosting;
using Cuebot.Server.Http;
using Cuebot.Server.SpeakerBot;
using Cuebot.Server.StreamerBot;
using Cuebot.Server.Udp;

namespace Cuebot.Server;

/// <summary>Which built-in server a status or restart refers to.</summary>
public enum ServerKind { WebSocket, Http, Udp, SpeakerBotWebSocket, SpeakerBotUdp }

/// <summary>State of one built-in server, for the UI.</summary>
public sealed record ServerStatus(ServerKind Kind, string Name, bool Enabled, bool Running, string Address, int Port, int Clients, string Message);

/// <summary>
/// Owns Cuebot's local API servers: starts the enabled ones, restarts them when settings change, forwards bus events
/// to WebSocket clients (in order, off the publishing thread) and reports status. Everything is off until enabled.
/// </summary>
public sealed class ServerManager : IAsyncDisposable
{
    private const string LogSource = "Servers";

    private readonly JsonFile<ServerSettings>? _file;
    private readonly ServerSettings _memorySettings = new();
    private readonly ServerServices _services;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<Func<Task>> _outbox = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<ServerKind, string> _errors = [];
    private readonly Dictionary<ServerKind, IPAddress> _bound = [];
    private Dictionary<Guid, string> _actionSignatures = [];
    private Dictionary<Guid, string> _commandSignatures = [];
    private SpeakerBotApi? _speakerApi;
    private Task? _pump;
    private IDisposable? _subscription;
    private bool _started;

    public ServerManager(JsonFile<ServerSettings>? file, ServerServices services)
    {
        _file = file;
        _services = services;
        Requests = new StreamerBotRequests(services);
        WebSocket = new StreamerBotWebSocketServer(Settings.WebSocket, Requests, Origins);
        Http = new HttpApiServer(() => Settings, Requests);
        CustomServers = new CustomWebSocketServers(() => Settings, services.Bus, services.Log);
        CustomClients = new CustomWebSocketClients(() => Settings, services.Bus, services.Log);
        CustomServers.Changed += RaiseChanged;
        CustomClients.Changed += RaiseChanged;
    }

    /// <summary>Raised (from any thread) when a server starts, stops or its clients change.</summary>
    public event Action? StatusChanged;

    /// <summary>Live settings; call <see cref="SaveSettings"/> after changing them and <see cref="ApplyAsync"/> to restart.</summary>
    public ServerSettings Settings => _file?.Value ?? _memorySettings;

    public StreamerBotRequests Requests { get; }
    public StreamerBotWebSocketServer WebSocket { get; private set; }
    public HttpApiServer Http { get; private set; }
    public UdpJsonListener? Udp { get; private set; }
    public SpeakerBotWebSocketServer? SpeakerBotWebSocket { get; private set; }
    public UdpJsonListener? SpeakerBotUdp { get; private set; }
    public CustomWebSocketServers CustomServers { get; }
    public CustomWebSocketClients CustomClients { get; }

    public void SaveSettings() => _file?.ScheduleSave();

    private IReadOnlyCollection<string> Origins() => Settings.AllowedOrigins;

    public IReadOnlyList<ServerStatus> Statuses
    {
        get
        {
            var s = Settings;
            return
            [
                Status(ServerKind.WebSocket, "WebSocket server (Streamer.bot API)", s.WebSocket.Enabled, WebSocket.IsRunning,
                    WebSocket.IsRunning ? WebSocket.Port : s.WebSocket.Port, WebSocket.Clients.Count),
                Status(ServerKind.Http, "HTTP server", s.Http.Enabled, Http.IsRunning, Http.IsRunning ? Http.Port : s.Http.Port, 0),
                Status(ServerKind.Udp, "UDP server", s.Udp.Enabled, Udp is not null, Udp?.Port ?? s.Udp.Port, 0),
                Status(ServerKind.SpeakerBotWebSocket, "Speaker.bot WebSocket API", s.SpeakerBot.WebSocketEnabled,
                    SpeakerBotWebSocket?.IsRunning == true, SpeakerBotWebSocket?.Port is > 0 ? SpeakerBotWebSocket.Port : s.SpeakerBot.WebSocketPort,
                    SpeakerBotWebSocket?.ClientCount ?? 0),
                Status(ServerKind.SpeakerBotUdp, "Speaker.bot UDP API", s.SpeakerBot.UdpEnabled, SpeakerBotUdp is not null,
                    SpeakerBotUdp?.Port ?? s.SpeakerBot.UdpPort, 0),
            ];
        }
    }

    private ServerStatus Status(ServerKind kind, string name, bool enabled, bool running, int port, int clients)
    {
        string message, address;
        lock (_errors)
        {
            address = _bound.TryGetValue(kind, out var a) ? a.ToString() : "";
            message = _errors.GetValueOrDefault(kind) ?? (running ? "Running" : enabled ? "Not running" : "Off");
        }
        return new ServerStatus(kind, name, enabled, running, address, port, clients, message);
    }

    /// <summary>Starts the enabled servers and event forwarding. Never throws; failures are logged and shown in the status.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_started)
        {
            _started = true;
            _pump = Task.Run(PumpAsync, CancellationToken.None);
            _subscription = _services.Bus.Subscribe(OnEvent);
            _services.Engine.RunCompleted += OnRunCompleted;
            _services.Profile.Changed += OnProfileChanged;
            if (_services.Commands is { } commands) commands.Changed += OnCommandsChanged;
            _actionSignatures = ActionSignatures();
            _commandSignatures = CommandSignatures();
        }
        await ApplyAsync(ct).ConfigureAwait(false);
        await CustomServers.StartAutoAsync(ct).ConfigureAwait(false);
        await CustomClients.ConnectAutoAsync(ct).ConfigureAwait(false);
    }

    /// <summary>(Re)starts every built-in server according to the current settings.</summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        foreach (var kind in Enum.GetValues<ServerKind>()) await RestartAsync(kind, ct).ConfigureAwait(false);
    }

    public async Task RestartAsync(ServerKind kind, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(kind).ConfigureAwait(false);
            if (IsEnabled(kind)) await StartCoreAsync(kind, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged();
    }

    private bool IsEnabled(ServerKind kind) => kind switch
    {
        ServerKind.WebSocket => Settings.WebSocket.Enabled,
        ServerKind.Http => Settings.Http.Enabled,
        ServerKind.Udp => Settings.Udp.Enabled,
        ServerKind.SpeakerBotWebSocket => Settings.SpeakerBot.WebSocketEnabled,
        ServerKind.SpeakerBotUdp => Settings.SpeakerBot.UdpEnabled,
        _ => false,
    };

    private async Task StartCoreAsync(ServerKind kind, CancellationToken ct)
    {
        var s = Settings;
        var configured = kind switch
        {
            ServerKind.WebSocket => s.WebSocket.Address,
            ServerKind.Http => s.Http.Address,
            ServerKind.Udp => s.Udp.Address,
            _ => s.SpeakerBot.Address,
        };
        try
        {
            var address = NetworkPolicy.BindAddress(configured, s.AllowLan, out var downgraded);
            if (downgraded) _services.Log.Warn(LogSource, $"{kind}: LAN access is off, listening on 127.0.0.1 instead of {configured}");
            switch (kind)
            {
                case ServerKind.WebSocket:
                    WebSocket = new StreamerBotWebSocketServer(s.WebSocket, Requests, Origins);
                    WebSocket.ClientsChanged += RaiseChanged;
                    await WebSocket.StartAsync(address, ct).ConfigureAwait(false);
                    break;
                case ServerKind.Http:
                    Http = new HttpApiServer(() => Settings, Requests);
                    await Http.StartAsync(address, ct).ConfigureAwait(false);
                    break;
                case ServerKind.Udp:
                    Udp = UdpJsonListener.Start(address, s.Udp.Port, "UDP server", OnUdpDoAction, _services.Log);
                    break;
                case ServerKind.SpeakerBotWebSocket:
                    SpeakerBotWebSocket = new SpeakerBotWebSocketServer(s.SpeakerBot, SpeakerApi(), Origins);
                    SpeakerBotWebSocket.ClientsChanged += RaiseChanged;
                    await SpeakerBotWebSocket.StartAsync(address, ct).ConfigureAwait(false);
                    break;
                case ServerKind.SpeakerBotUdp:
                    var api = SpeakerApi();
                    SpeakerBotUdp = UdpJsonListener.Start(address, s.SpeakerBot.UdpPort, "Speaker.bot UDP", m => api.HandleCommandAsync(m), _services.Log);
                    break;
            }
            lock (_errors)
            {
                _errors.Remove(kind);
                _bound[kind] = address;
            }
            _services.Log.Info(LogSource, $"{kind} listening on {address}:{PortOf(kind)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopCoreAsync(kind).ConfigureAwait(false);
            var message = ex is IOException or System.Net.Sockets.SocketException
                ? $"Could not listen (is port {PortSetting(kind)} in use?): {ex.Message}"
                : ex.Message;
            lock (_errors) _errors[kind] = message;
            _services.Log.Error(LogSource, $"{kind} failed to start", ex);
        }
    }

    private int PortOf(ServerKind kind) => kind switch
    {
        ServerKind.WebSocket => WebSocket.Port,
        ServerKind.Http => Http.Port,
        ServerKind.Udp => Udp?.Port ?? 0,
        ServerKind.SpeakerBotWebSocket => SpeakerBotWebSocket?.Port ?? 0,
        _ => SpeakerBotUdp?.Port ?? 0,
    };

    private int PortSetting(ServerKind kind) => kind switch
    {
        ServerKind.WebSocket => Settings.WebSocket.Port,
        ServerKind.Http => Settings.Http.Port,
        ServerKind.Udp => Settings.Udp.Port,
        ServerKind.SpeakerBotWebSocket => Settings.SpeakerBot.WebSocketPort,
        _ => Settings.SpeakerBot.UdpPort,
    };

    private SpeakerBotApi SpeakerApi() =>
        _speakerApi ??= new SpeakerBotApi(_services.Speaker ?? throw new InvalidOperationException("Speech is not available"), _services.Log);

    private async Task StopCoreAsync(ServerKind kind)
    {
        switch (kind)
        {
            case ServerKind.WebSocket:
                WebSocket.ClientsChanged -= RaiseChanged;
                await WebSocket.DisposeAsync().ConfigureAwait(false);
                break;
            case ServerKind.Http:
                await Http.DisposeAsync().ConfigureAwait(false);
                break;
            case ServerKind.Udp:
                if (Udp is { } udp) await udp.DisposeAsync().ConfigureAwait(false);
                Udp = null;
                break;
            case ServerKind.SpeakerBotWebSocket:
                if (SpeakerBotWebSocket is { } sws) await sws.DisposeAsync().ConfigureAwait(false);
                SpeakerBotWebSocket = null;
                break;
            case ServerKind.SpeakerBotUdp:
                if (SpeakerBotUdp is { } sudp) await sudp.DisposeAsync().ConfigureAwait(false);
                SpeakerBotUdp = null;
                break;
        }
        lock (_errors) _bound.Remove(kind);
    }

    private Task OnUdpDoAction(System.Text.Json.JsonElement message)
    {
        var request = StreamerBotRequests.Str(message, "request", "DoAction");
        if (!request.Equals("DoAction", StringComparison.OrdinalIgnoreCase))
        {
            _services.Log.Warn("UDP server", $"Ignored UDP request '{request}' (only DoAction is supported)");
            return Task.CompletedTask;
        }
        try
        {
            Requests.DoAction(message, "UDP");
        }
        catch (RequestException ex)
        {
            _services.Log.Warn("UDP server", ex.Message);
        }
        return Task.CompletedTask;
    }

    // ---- Event forwarding -----------------------------------------------------------------------------------------

    private void OnEvent(BotEvent e)
    {
        if (WebSocket.IsRunning) Enqueue(() => WebSocket.PublishAsync(e));
        if (SpeakerBotWebSocket is { IsRunning: true } speaker) Enqueue(() => speaker.PublishAsync(e));
    }

    private void OnRunCompleted(ActionRunRecord run)
    {
        if (!WebSocket.IsRunning || run.ExcludedFromHistory) return;
        var data = new Dictionary<string, object?>
        {
            ["actionName"] = run.ActionName,
            ["runId"] = run.RunId.ToString(),
            ["source"] = run.Source,
            ["success"] = run.Success,
            ["state"] = run.State.ToString(),
            ["duration"] = (long)run.Duration.TotalMilliseconds,
            ["error"] = run.Error,
        };
        SendApplicationEvent("Raw", "ActionCompleted", data);
    }

    private void OnProfileChanged()
    {
        var now = ActionSignatures();
        var before = Interlocked.Exchange(ref _actionSignatures, now);
        if (!WebSocket.IsRunning) return;
        var names = _services.Profile.Snapshot.Actions.ToDictionary(a => a.Id, a => a.Name);
        foreach (var (type, id) in Diff(before, now)) SendApplicationEvent("Application", type.Replace("*", "Action"), Entry(id, names));
    }

    private void OnCommandsChanged()
    {
        var now = CommandSignatures();
        var before = Interlocked.Exchange(ref _commandSignatures, now);
        if (!WebSocket.IsRunning) return;
        var names = (_services.Commands?.All() ?? []).ToDictionary(c => c.Id, c => c.Name);
        foreach (var (type, id) in Diff(before, now)) SendApplicationEvent("Application", type.Replace("*", "Command"), Entry(id, names));
    }

    private static Dictionary<string, object?> Entry(Guid id, Dictionary<Guid, string> names) =>
        new() { ["id"] = id.ToString(), ["name"] = names.GetValueOrDefault(id) ?? "" };

    /// <summary>"*Added" / "*Deleted" / "*Updated" per id between two signature maps.</summary>
    internal static IEnumerable<(string Type, Guid Id)> Diff(Dictionary<Guid, string> before, Dictionary<Guid, string> after)
    {
        foreach (var (id, sig) in after)
        {
            if (!before.TryGetValue(id, out var old)) yield return ("*Added", id);
            else if (old != sig) yield return ("*Updated", id);
        }
        foreach (var id in before.Keys.Where(id => !after.ContainsKey(id))) yield return ("*Deleted", id);
    }

    private Dictionary<Guid, string> ActionSignatures() =>
        _services.Profile.Snapshot.Actions.GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => System.Text.Json.JsonSerializer.Serialize(g.First(), Json.Options));

    private Dictionary<Guid, string> CommandSignatures() =>
        (_services.Commands?.All() ?? []).GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => System.Text.Json.JsonSerializer.Serialize(g.First(), Json.Options));

    private void SendApplicationEvent(string source, string type, Dictionary<string, object?> data)
    {
        var name = new StreamerBotEventName(source, type);
        var message = StreamerBotEnvelope.Create(name, StreamerBotEnvelope.Data(data), DateTimeOffset.Now);
        Enqueue(() => WebSocket.SendEventAsync(name, message));
    }

    private void Enqueue(Func<Task> send) => _outbox.Writer.TryWrite(send);

    private async Task PumpAsync()
    {
        await foreach (var send in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await send().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _services.Log.Warn(LogSource, $"Could not forward an event: {ex.Message}");
            }
        }
    }

    private void RaiseChanged() => StatusChanged?.Invoke();

    /// <summary>The dock URLs served by the HTTP server.</summary>
    public IReadOnlyList<(string Name, string Url)> DockUrls
    {
        get
        {
            var host = _bound.TryGetValue(ServerKind.Http, out var a) && !IPAddress.Any.Equals(a) && !IPAddress.IPv6Any.Equals(a)
                ? (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString())
                : "127.0.0.1";
            var port = Http.IsRunning ? Http.Port : Settings.Http.Port;
            return [("Chat dock", $"http://{host}:{port}/feed/chat"), ("Activity dock", $"http://{host}:{port}/feed/events")];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        if (_started)
        {
            _services.Engine.RunCompleted -= OnRunCompleted;
            _services.Profile.Changed -= OnProfileChanged;
            if (_services.Commands is { } commands) commands.Changed -= OnCommandsChanged;
        }
        foreach (var kind in Enum.GetValues<ServerKind>()) await StopCoreAsync(kind).ConfigureAwait(false);
        await CustomServers.DisposeAsync().ConfigureAwait(false);
        await CustomClients.DisposeAsync().ConfigureAwait(false);
        _outbox.Writer.TryComplete();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _gate.Dispose();
    }
}
