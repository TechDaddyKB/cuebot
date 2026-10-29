using System.Net;
using System.Text.Json.Nodes;
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
public enum ServerKind { WebSocket, Http, Udp, SpeakerBotWebSocket, SpeakerBotUdp, Api }

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
        Http = new HttpApiServer(() => Settings, Requests, services.Decks);
        CustomServers = new CustomWebSocketServers(() => Settings, services.Bus, services.Log);
        CustomClients = new CustomWebSocketClients(() => Settings, services.Bus, services.Log);
        CustomServers.Changed += RaiseChanged;
        CustomClients.Changed += RaiseChanged;
        ApiOperations = new Cuebot.Server.Api.ApiOperations(services, () => Statuses);
        Mcp = new Cuebot.Server.Mcp.McpServer(ApiOperations, services, () => Settings.Mcp);
        Api = new Cuebot.Server.Api.ApiServer(() => Settings, services, ApiOperations, Mcp);
        MigrateSettings();
    }

    /// <summary>
    /// servers.json version 2: the MCP server starts with Cuebot (127.0.0.1, bearer token, destructive tools still need
    /// confirmation) so AI tools and the Assistant page can use it. Version 1 files had it off only because that was the
    /// default, so it is switched on once; turning it off on the Servers page sticks. In-memory settings are left alone.
    /// </summary>
    private void MigrateSettings()
    {
        if (_file is null || Settings.Version >= ServerSettings.CurrentVersion) return;
        if (Settings.Version < 2)
        {
            Settings.Mcp.Enabled = true;
            _services.Log.Info(LogSource, "The MCP server now starts with Cuebot (local only, token required); turn it off on the Servers page");
        }
        Settings.Version = ServerSettings.CurrentVersion;
        SaveSettings();
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

    /// <summary>The REST API (<c>/api/v1</c>) and MCP (<c>/mcp</c>) listener.</summary>
    public Cuebot.Server.Api.ApiServer Api { get; private set; }

    /// <summary>The operations behind the REST API and the MCP tools.</summary>
    public Cuebot.Server.Api.ApiOperations ApiOperations { get; }

    /// <summary>The MCP server (protocol handling; served by <see cref="Api"/>).</summary>
    public Cuebot.Server.Mcp.McpServer Mcp { get; }

    public void SaveSettings() => _file?.ScheduleSave();

    /// <summary>The API/MCP bearer token, generated (and saved) when there is none yet.</summary>
    public string EnsureApiToken()
    {
        if (string.IsNullOrWhiteSpace(Settings.Api.Token))
        {
            Settings.Api.Token = Cuebot.Server.Api.ApiToken.Generate();
            SaveSettings();
        }
        return Settings.Api.Token;
    }

    /// <summary>Replaces the API/MCP token; clients using the old one get 401 from the next request on.</summary>
    public string RegenerateApiToken()
    {
        Settings.Api.Token = Cuebot.Server.Api.ApiToken.Generate();
        SaveSettings();
        _services.Log.Info(LogSource, "API token regenerated");
        return Settings.Api.Token;
    }

    /// <summary>Base URL of the API/MCP listener as clients on this computer reach it (http://127.0.0.1:7475).</summary>
    public string ApiBaseUrl
    {
        get
        {
            IPAddress? bound;
            lock (_errors) bound = _bound.GetValueOrDefault(ServerKind.Api);
            var host = bound is not null && !IPAddress.Any.Equals(bound) && !IPAddress.IPv6Any.Equals(bound)
                ? (bound.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{bound}]" : bound.ToString())
                : "127.0.0.1";
            return $"http://{host}:{(Api.IsRunning ? Api.Port : Settings.Api.Port)}";
        }
    }

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
                Status(ServerKind.Api, "REST API and MCP server", s.Api.Enabled || s.Mcp.Enabled, Api.IsRunning,
                    Api.IsRunning ? Api.Port : s.Api.Port, 0),
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
            _services.Engine.RunStarted += OnRunStarted;
            _services.Engine.SubActionStarting += OnSubActionStarting;
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
        ServerKind.Api => Settings.Api.Enabled || Settings.Mcp.Enabled,
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
            ServerKind.Api => s.Api.Address,
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
                    Http = new HttpApiServer(() => Settings, Requests, _services.Decks);
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
                case ServerKind.Api:
                    EnsureApiToken();
                    Api = new Cuebot.Server.Api.ApiServer(() => Settings, _services, ApiOperations, Mcp);
                    await Api.StartAsync(address, ct).ConfigureAwait(false);
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
        ServerKind.Api => Api.Port,
        _ => SpeakerBotUdp?.Port ?? 0,
    };

    private int PortSetting(ServerKind kind) => kind switch
    {
        ServerKind.WebSocket => Settings.WebSocket.Port,
        ServerKind.Http => Settings.Http.Port,
        ServerKind.Udp => Settings.Udp.Port,
        ServerKind.SpeakerBotWebSocket => Settings.SpeakerBot.WebSocketPort,
        ServerKind.Api => Settings.Api.Port,
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
            case ServerKind.Api:
                await Api.DisposeAsync().ConfigureAwait(false);
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

    private static readonly StreamerBotEventName RawAction = new("Raw", "Action");
    private static readonly StreamerBotEventName RawSubAction = new("Raw", "SubAction");
    private static readonly StreamerBotEventName RawActionCompleted = new("Raw", "ActionCompleted");

    /// <summary>Raw.Action: <c>{id (run), actionId, name, arguments}</c>, as documented.</summary>
    private void OnRunStarted(ActionRunStart run)
    {
        if (!WebSocket.IsRunning || run.Action.ExcludeFromHistory || !WebSocket.HasSubscribers(RawAction)) return;
        var data = new JsonObject
        {
            ["id"] = run.RunId.ToString(),
            ["actionId"] = run.Action.Id.ToString(),
            ["name"] = run.Action.Name,
            ["source"] = run.Source,
            ["queuedAt"] = run.QueuedAt.ToString("o"),
            ["startedAt"] = run.StartedAt.ToString("o"),
            ["arguments"] = Arguments(run.Args),   // copied now: the run keeps changing its args
        };
        SendEvent(RawAction, data);
    }

    /// <summary>Raw.SubAction: <c>{parentId, parentName, type, id (run), actionId (sub-action), name, arguments}</c>.</summary>
    private void OnSubActionStarting(SubActionStart step)
    {
        if (!WebSocket.IsRunning || step.Action.ExcludeFromHistory || !WebSocket.HasSubscribers(RawSubAction)) return;
        var data = new JsonObject
        {
            ["parentId"] = step.Action.Id.ToString(),
            ["parentName"] = step.Action.Name,
            ["type"] = SubActionCode(step.Step.Type),
            ["cuebotType"] = step.Step.Type,
            ["id"] = step.Args.GetValueOrDefault("runId")?.ToString() ?? "",
            ["actionId"] = step.Step.Id.ToString(),
            ["name"] = step.Handler.DisplayName,
            ["enabled"] = step.Step.Enabled,
            ["arguments"] = Arguments(step.Args),
        };
        SendEvent(RawSubAction, data);
    }

    /// <summary>
    /// Raw.ActionCompleted: Streamer.bot's fields (<c>id</c>, <c>actionId</c>, <c>name</c>, <c>duration</c> in ms,
    /// <c>queuedAt</c>/<c>startedAt</c>/<c>completedAt</c>, <c>success</c>, <c>excludeFromHistory</c>, <c>arguments</c>)
    /// plus Cuebot's (<c>actionName</c>, <c>runId</c>, <c>source</c>, <c>state</c>, <c>error</c>).
    /// </summary>
    private void OnRunCompleted(ActionRunRecord run)
    {
        if (!WebSocket.IsRunning || run.ExcludedFromHistory || !WebSocket.HasSubscribers(RawActionCompleted)) return;
        var data = new JsonObject
        {
            ["id"] = run.RunId.ToString(),
            ["actionId"] = run.ActionId.ToString(),
            ["name"] = run.ActionName,
            ["duration"] = run.Duration.TotalMilliseconds,
            ["queuedAt"] = run.QueuedAt.ToString("o"),
            ["startedAt"] = (run.StartedAt ?? run.QueuedAt).ToString("o"),
            ["completedAt"] = run.CompletedAt.ToString("o"),
            ["success"] = run.Success,
            ["excludeFromHistory"] = run.ExcludedFromHistory,
            ["arguments"] = run.Arguments is null ? new JsonObject() : Arguments(run.Arguments),
            ["actionName"] = run.ActionName,
            ["runId"] = run.RunId.ToString(),
            ["source"] = run.Source,
            ["state"] = run.State.ToString(),
            ["error"] = run.Error,
        };
        SendEvent(RawActionCompleted, data);
    }

    /// <summary>A snapshot of a run's arguments (the engine's <c>runId</c> also as Streamer.bot's <c>runningActionId</c>).</summary>
    private static JsonObject Arguments(IReadOnlyDictionary<string, object?> args)
    {
        var data = StreamerBotEnvelope.Data(args);
        if (data["runningActionId"] is null && data["runId"] is { } runId) data["runningActionId"] = runId.DeepClone();
        return data;
    }

    private static Dictionary<string, string>? _subActionCodes;

    /// <summary>Streamer.bot's numeric sub-action type for a Cuebot type (from the import table), or 0 when none is known.</summary>
    private static int SubActionCode(string cuebotType)
    {
        _subActionCodes ??= Cuebot.Core.Transfer.StreamerBot.StreamerBotMappingTable.Default.SubActions
            .GroupBy(r => r.TargetType, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, Code: g.SelectMany(r => r.TypeKeys).FirstOrDefault(k => k.Length > 0 && k.All(char.IsAsciiDigit))))
            .Where(x => x.Code is not null)
            .ToDictionary(x => x.Key, x => x.Code!, StringComparer.OrdinalIgnoreCase);
        return _subActionCodes.TryGetValue(cuebotType, out var code) && int.TryParse(code, out var n) ? n : 0;
    }

    private void SendEvent(StreamerBotEventName name, JsonObject data)
    {
        var message = StreamerBotEnvelope.Create(name, data, DateTimeOffset.Now);
        Enqueue(() => WebSocket.SendEventAsync(name, message));
    }

    private void OnProfileChanged()
    {
        var now = ActionSignatures();
        var before = Interlocked.Exchange(ref _actionSignatures, now);
        if (!WebSocket.IsRunning) return;
        var names = _services.Profile.Snapshot.Actions.ToDictionary(a => a.Id, a => a.Name);
        foreach (var (type, id) in Diff(before, now)) SendApplicationEvent("Application", type.Replace("*", "Action"), ActionEntry(id, names));
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

    /// <summary>Application.Action*: <c>{id, name, group, enabled}</c> (group and enabled while the action exists).</summary>
    private Dictionary<string, object?> ActionEntry(Guid id, Dictionary<Guid, string> names)
    {
        var entry = Entry(id, names);
        if (_services.Profile.Snapshot.FindAction(id) is { } action)
        {
            entry["group"] = action.Group;
            entry["enabled"] = action.Enabled;
        }
        return entry;
    }

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
            _services.Engine.RunStarted -= OnRunStarted;
            _services.Engine.SubActionStarting -= OnSubActionStarting;
            _services.Profile.Changed -= OnProfileChanged;
            if (_services.Commands is { } commands) commands.Changed -= OnCommandsChanged;
        }
        if (WebSocket.IsRunning)
        {
            // Misc.StreamerbotExiting, sent directly: the outbox pump is about to stop.
            var exiting = new StreamerBotEventName("Misc", "StreamerbotExiting");
            try
            {
                await WebSocket.SendEventAsync(exiting, StreamerBotEnvelope.Create(exiting, new JsonObject(), DateTimeOffset.Now))
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
            }
        }
        foreach (var kind in Enum.GetValues<ServerKind>()) await StopCoreAsync(kind).ConfigureAwait(false);
        await CustomServers.DisposeAsync().ConfigureAwait(false);
        await CustomClients.DisposeAsync().ConfigureAwait(false);
        _outbox.Writer.TryComplete();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _gate.Dispose();
    }
}
