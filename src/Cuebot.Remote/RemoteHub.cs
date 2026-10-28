using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server.Api;
using Cuebot.Server.Hosting;

namespace Cuebot.Remote;

/// <summary>State of one configured or connected instance, for the UI and scripts.</summary>
/// <param name="DefinitionId">The remote.json entry for outgoing links; null for instances that connected to us.</param>
public sealed record RemoteInstanceStatus(Guid? DefinitionId, string Name, bool Outgoing, ConnectionState State, string Message,
    RemoteInstanceInfo? Peer, string Endpoint, DateTimeOffset? ConnectedAt);

/// <summary>
/// Remote signals between Cuebot instances. Links are WebSockets on the REST API listener (<c>/api/v1/remote</c>, the
/// same bearer token): this instance connects to the instances in remote.json with their API tokens, and other
/// instances connect to it with its token. A link carries signals both ways once both sides said hello, so only one
/// side needs the other's token. Received signals fire the "Remote signal received" trigger, release "Wait for remote
/// signal" runs and (optionally) "Wait for local signal" runs of the same name.
/// </summary>
public sealed class RemoteHub : IApiWebSocketRoute, IConnectionStatus, IAsyncDisposable
{
    public const string Source = "Remote";
    public const string SignalEvent = "remote.signal";
    public const string ConnectedEvent = "remote.connected";
    public const string DisconnectedEvent = "remote.disconnected";

    private readonly RemoteSettings _settings;
    private readonly Action _save;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly SignalService? _signals;
    private readonly Func<bool> _apiEnabled;
    private readonly ConcurrentDictionary<string, Link> _links = new();
    private readonly ConcurrentDictionary<Guid, RemoteClient> _clients = new();
    private readonly string _version;
    private int _disposed;

    /// <param name="save">Persists <paramref name="settings"/> (remote.json); null in tests.</param>
    /// <param name="signals">Local signals to release with received remote signals (see <see cref="RemoteSettings.ReleaseLocalWaiters"/>).</param>
    /// <param name="apiEnabled">Whether this instance's REST API is on (others can only connect to it then); for the status line.</param>
    public RemoteHub(RemoteSettings settings, Action? save, EventBus bus, AppLog log, SignalService? signals = null, Func<bool>? apiEnabled = null)
    {
        _settings = settings;
        _save = save ?? (() => { });
        _bus = bus;
        _log = log;
        _signals = signals;
        _apiEnabled = apiEnabled ?? (() => true);
        _version = typeof(RemoteHub).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        if (_settings.InstanceId == Guid.Empty)
        {
            _settings.InstanceId = Guid.NewGuid();
            _save();
        }
    }

    public RemoteSettings Settings => _settings;

    /// <summary>This instance as announced to others.</summary>
    public RemoteInstanceInfo Self => new(_settings.InstanceId.ToString(), SelfName, OsName(), _version);

    public string SelfName => string.IsNullOrWhiteSpace(_settings.InstanceName) ? Environment.MachineName : _settings.InstanceName.Trim();

    /// <summary>Raised after a signal arrives (after the trigger event is published).</summary>
    public event Action<RemoteSignal>? SignalReceived;

    /// <summary>Raised (from any thread) when a link connects, disconnects or a definition's status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>True while this instance's REST API (which serves <c>/api/v1/remote</c>) is on.</summary>
    public bool AcceptsConnections => _apiEnabled();

    // ---- Status ----------------------------------------------------------------------------------------------------

    string IConnectionStatus.Name => "Remote instances";

    public ConnectionState State
    {
        get
        {
            var statuses = Instances;
            if (statuses.Any(s => s.State == ConnectionState.Connected)) return ConnectionState.Connected;
            if (statuses.Any(s => s.State == ConnectionState.Error)) return ConnectionState.Error;
            return statuses.Any(s => s.State == ConnectionState.Connecting) ? ConnectionState.Connecting : ConnectionState.Disconnected;
        }
    }

    public string StatusMessage
    {
        get
        {
            var connected = Instances.Where(s => s.State == ConnectionState.Connected).Select(s => s.Name).ToArray();
            if (connected.Length > 0) return $"{connected.Length} connected: {string.Join(", ", connected)}";
            if (_settings.Instances.Count > 0) return "No instance connected";
            return AcceptsConnections ? "Waiting for other instances" : "Not set up (Servers page → Remote instances)";
        }
    }

    /// <summary>Configured instances (in remote.json order), then instances that connected to this one.</summary>
    public IReadOnlyList<RemoteInstanceStatus> Instances
    {
        get
        {
            var links = _links.Values.ToArray();
            var list = new List<RemoteInstanceStatus>();
            foreach (var definition in _settings.Instances.ToArray())
            {
                var link = links.FirstOrDefault(l => l.DefinitionId == definition.Id && l.Peer is not null);
                if (link is not null)
                {
                    list.Add(link.Status(ConnectionState.Connected, $"Connected to {link.Peer!.Name} ({link.Peer.Os}, Cuebot {link.Peer.Version})"));
                    continue;
                }
                var client = _clients.GetValueOrDefault(definition.Id);
                list.Add(new RemoteInstanceStatus(definition.Id, definition.Name, true, client?.State ?? ConnectionState.Disconnected,
                    client?.Message ?? "Not connected", null, SafeEndpoint(definition), null));
            }
            foreach (var link in links.Where(l => !l.Outgoing && l.Peer is not null).OrderBy(l => l.ConnectedAt))
                list.Add(link.Status(ConnectionState.Connected, $"Connected from {link.Endpoint} ({link.Peer!.Os}, Cuebot {link.Peer.Version})"));
            return list;
        }
    }

    /// <summary>Names to suggest in sub-actions: configured instances and connected ones.</summary>
    public IEnumerable<string> KnownNames =>
        _settings.Instances.Select(i => i.Name).Concat(Instances.Select(i => i.Name)).Concat(ReadyLinks.Select(l => l.Peer!.Name))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<Link> ReadyLinks => _links.Values.Where(l => l.Peer is not null);

    internal void RaiseStatusChanged() => StatusChanged?.Invoke();

    // ---- Instances (remote.json) -----------------------------------------------------------------------------------

    /// <summary>Connects every enabled instance in remote.json. Never throws.</summary>
    public Task StartAsync(CancellationToken ct = default) =>
        Task.WhenAll(_settings.Instances.Where(d => d.Enabled).ToArray().Select(async definition =>
        {
            try
            {
                await ConnectAsync(definition, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(Source, $"Could not start the link to '{definition.Name}'", ex);
            }
        }));

    /// <summary>Starts (or restarts, picking up edits) the link to <paramref name="definition"/>; returns after the first attempt.</summary>
    public async Task ConnectAsync(RemoteInstanceDefinition definition, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_clients.TryRemove(definition.Id, out var old)) await old.DisposeAsync().ConfigureAwait(false);
        var client = new RemoteClient(this, definition, _log);
        _clients[definition.Id] = client;
        await client.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(Guid definitionId)
    {
        if (_clients.TryRemove(definitionId, out var client)) await client.DisposeAsync().ConfigureAwait(false);
        StatusChanged?.Invoke();
    }

    public bool IsStarted(Guid definitionId) => _clients.TryGetValue(definitionId, out var c) && !c.Stopped;

    /// <summary>Adds an instance to remote.json (not connected until <see cref="ConnectAsync"/>).</summary>
    public RemoteInstanceDefinition AddInstance(string? name = null)
    {
        var definition = new RemoteInstanceDefinition { Name = name ?? UniqueName("Instance") };
        _settings.Instances.Add(definition);
        _save();
        StatusChanged?.Invoke();
        return definition;
    }

    public async Task RemoveInstanceAsync(Guid definitionId)
    {
        await DisconnectAsync(definitionId).ConfigureAwait(false);
        _settings.Instances.RemoveAll(d => d.Id == definitionId);
        _save();
        StatusChanged?.Invoke();
    }

    public void Save() => _save();

    private string UniqueName(string prefix)
    {
        for (var i = 1; ; i++)
        {
            var name = $"{prefix} {i}";
            if (!_settings.Instances.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    internal static string SafeEndpoint(RemoteInstanceDefinition definition)
    {
        try
        {
            return RemoteProtocol.BuildUri(definition.Host, definition.Port).ToString();
        }
        catch (UriFormatException)
        {
            return definition.Host;
        }
    }

    // ---- Sending and waiting ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a signal to every connected instance whose name matches <paramref name="instance"/> (empty or "*" = all),
    /// once per instance even when two links lead to it. Returns how many instances it was sent to.
    /// </summary>
    public async Task<int> SendSignalAsync(string instance, string name, string sender, IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A signal needs a name", nameof(name));
        var text = RemoteProtocol.Signal(name, sender, args ?? new Dictionary<string, object?>());
        var targets = ReadyLinks.Where(l => Matches(instance, l.Label, l.Peer!))
            .GroupBy(l => l.Peer!.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(l => l.Outgoing ? 0 : 1).ToArray())
            .ToArray();
        var sent = 0;
        foreach (var candidates in targets)
        {
            foreach (var link in candidates)
            {
                if (!await link.SendAsync(text, ct).ConfigureAwait(false)) continue;
                sent++;
                break;
            }
        }
        return sent;
    }

    /// <summary>
    /// Waits for the next signal named <paramref name="name"/> from an instance matching <paramref name="instance"/>
    /// (empty or "*" = any). Only signals arriving after the call count. Null on timeout; a null or infinite timeout
    /// waits until cancelled.
    /// </summary>
    public async Task<RemoteSignal?> WaitForSignalAsync(string name, string? instance, TimeSpan? timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RemoteSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(RemoteSignal s)
        {
            if (string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) && Matches(instance, s.Label, s.From)) tcs.TrySetResult(s);
        }
        SignalReceived += OnSignal;
        try
        {
            var wait = timeout is { } t && t != Timeout.InfiniteTimeSpan
                ? tcs.Task.WaitAsync(t < TimeSpan.Zero ? TimeSpan.Zero : t, ct)
                : tcs.Task.WaitAsync(ct);
            return await wait.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            SignalReceived -= OnSignal;
        }
    }

    /// <summary>Empty, "*" or "any" matches every instance; otherwise the local label, announced name or id (case-insensitive).</summary>
    internal static bool Matches(string? filter, string label, RemoteInstanceInfo peer)
    {
        var f = filter?.Trim() ?? "";
        if (f.Length == 0 || f == "*" || f.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
        return f.Equals(label, StringComparison.OrdinalIgnoreCase) || f.Equals(peer.Name, StringComparison.OrdinalIgnoreCase)
               || f.Equals(peer.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Action arguments of a received signal: the sent arguments, then the signal and instance details.</summary>
    public static Dictionary<string, object?> SignalArgs(RemoteSignal signal, bool includeSent = true)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (includeSent)
        {
            foreach (var (key, value) in signal.Args) args[key] = value;
        }
        args["signalName"] = signal.Name;
        args["signalSender"] = signal.Sender;
        foreach (var (key, value) in InstanceArgs(signal.From, signal.Label)) args[key] = value;
        return args;
    }

    private static Dictionary<string, object?> InstanceArgs(RemoteInstanceInfo peer, string label) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["instanceId"] = peer.Id,
        ["instanceName"] = peer.Name,
        ["instanceLabel"] = label,
        ["instanceOs"] = peer.Os,
        ["instanceVersion"] = peer.Version,
    };

    // ---- Links -----------------------------------------------------------------------------------------------------

    /// <summary>One open WebSocket to another instance (either direction).</summary>
    internal sealed class Link(string key, Guid? definitionId, string definitionName, string endpoint,
        Func<string, CancellationToken, Task<bool>> send, Func<Task> close)
    {
        public string Key => key;
        public Guid? DefinitionId => definitionId;
        public bool Outgoing => definitionId is not null;
        public string Endpoint => endpoint;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;

        /// <summary>Set by the peer's hello; signals are only exchanged after it.</summary>
        public RemoteInstanceInfo? Peer { get; set; }

        /// <summary>Last error message the peer sent (e.g. "cannot connect to itself").</summary>
        public string? Error { get; set; }

        public string Label => Outgoing ? definitionName : Peer?.Name ?? endpoint;

        public Task<bool> SendAsync(string text, CancellationToken ct) => send(text, ct);
        public Task CloseAsync() => close();

        public RemoteInstanceStatus Status(ConnectionState state, string message) =>
            new(definitionId, Label, Outgoing, state, message, Peer, endpoint, ConnectedAt);
    }

    internal async Task<Link> OpenOutgoingAsync(RemoteInstanceDefinition definition, string endpoint,
        Func<string, CancellationToken, Task<bool>> send, Func<Task> close)
    {
        var link = new Link("out:" + Guid.NewGuid(), definition.Id, definition.Name, endpoint, send, close);
        _links[link.Key] = link;
        await send(RemoteProtocol.Hello(Self), CancellationToken.None).ConfigureAwait(false);
        return link;
    }

    internal void CloseLink(Link link)
    {
        if (!_links.TryRemove(link.Key, out _)) return;
        if (link.Peer is { } peer)
        {
            _log.Info(Source, $"Remote instance '{link.Label}' disconnected");
            var args = InstanceArgs(peer, link.Label);
            args["direction"] = link.Outgoing ? "outgoing" : "incoming";
            _bus.Publish(new BotEvent(DisconnectedEvent, args));
        }
        StatusChanged?.Invoke();
    }

    /// <summary>Handles one frame from a link. Malformed input is logged and ignored; it never throws.</summary>
    internal async Task HandleAsync(Link link, string text)
    {
        if (RemoteProtocol.TryParse(text) is not { } message)
        {
            _log.Warn(Source, $"Ignored a message from '{link.Label}' that is not a JSON object with a type");
            return;
        }
        switch (message.Type)
        {
            case "hello":
                await OnHelloAsync(link, message).ConfigureAwait(false);
                break;
            case "signal" when link.Peer is { } peer:
                OnSignal(link, peer, message);
                break;
            case "error":
                link.Error = message.Message;
                _log.Warn(Source, $"'{link.Label}' refused the link: {message.Message}");
                break;
        }
    }

    private async Task OnHelloAsync(Link link, RemoteMessage message)
    {
        if (link.Peer is not null) return;
        string? problem = null;
        if (message.Instance is not { } peer) problem = "The hello has no instance id";
        else if (message.Protocol != RemoteProtocol.Version) problem = $"Unsupported protocol version {message.Protocol} (this Cuebot speaks {RemoteProtocol.Version})";
        else if (string.Equals(peer.Id, Self.Id, StringComparison.OrdinalIgnoreCase)) problem = "An instance cannot connect to itself";
        if (problem is not null)
        {
            link.Error = problem;
            _log.Warn(Source, $"Closed the link with {link.Endpoint}: {problem}");
            await link.SendAsync(RemoteProtocol.Error(problem), CancellationToken.None).ConfigureAwait(false);
            await link.CloseAsync().ConfigureAwait(false);
            return;
        }

        link.Peer = message.Instance;
        if (!link.Outgoing) await link.SendAsync(RemoteProtocol.Hello(Self), CancellationToken.None).ConfigureAwait(false);
        _log.Info(Source, link.Outgoing
            ? $"Connected to remote instance '{link.Label}' ({link.Peer!.Name})"
            : $"Remote instance '{link.Peer!.Name}' connected from {link.Endpoint}");
        var args = InstanceArgs(link.Peer, link.Label);
        args["direction"] = link.Outgoing ? "outgoing" : "incoming";
        _bus.Publish(new BotEvent(ConnectedEvent, args));
        StatusChanged?.Invoke();
    }

    private void OnSignal(Link link, RemoteInstanceInfo peer, RemoteMessage message)
    {
        if (message.Name.Trim().Length == 0) return;
        var signal = new RemoteSignal(message.Name.Trim(), message.Sender, message.Args, peer, link.Label);
        var args = SignalArgs(signal);
        _bus.Publish(new BotEvent(SignalEvent, args));
        SignalReceived?.Invoke(signal);
        if (_settings.ReleaseLocalWaiters) _signals?.Send(signal.Name, args);
    }

    // ---- IApiWebSocketRoute (incoming links) -----------------------------------------------------------------------

    string IApiWebSocketRoute.Name => RemoteProtocol.RouteName;

    Task IWebSocketHandler.OnOpenAsync(WebSocketConnection connection)
    {
        var link = new Link("in:" + connection.Id, null, "", connection.Remote,
            (text, ct) => connection.SendAsync(text, ct),
            () => connection.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "refused"));
        _links[link.Key] = link;
        return Task.CompletedTask;
    }

    Task IWebSocketHandler.OnMessageAsync(WebSocketConnection connection, string message) =>
        _links.TryGetValue("in:" + connection.Id, out var link) ? HandleAsync(link, message) : Task.CompletedTask;

    void IWebSocketHandler.OnClose(WebSocketConnection connection)
    {
        if (_links.TryGetValue("in:" + connection.Id, out var link)) CloseLink(link);
    }

    // ---- Misc ------------------------------------------------------------------------------------------------------

    private static string OsName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var id in _clients.Keys.ToArray())
        {
            if (_clients.TryRemove(id, out var client)) await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
