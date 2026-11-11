using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>Bot event types published by <see cref="VeadotubeService"/>.</summary>
public static class VeadotubeEvents
{
    public const string Prefix = "veadotube";
    public const string Connected = "veadotube.connected";
    public const string Disconnected = "veadotube.disconnected";

    /// <summary>The avatar state on top of a stateEvents node's stack changed.</summary>
    public const string StateChanged = "veadotube.stateChanged";

    /// <summary>An on/off (boolean) node such as push-to-talk changed.</summary>
    public const string SwitchChanged = "veadotube.switchChanged";

    /// <summary>A number node (full veadotube) changed.</summary>
    public const string NumberChanged = "veadotube.numberChanged";
}

/// <summary>
/// veadotube mini or veadotube through its WebSocket API. The address is found in the instances folder on every
/// (re)connect, because veadotube picks a new port each launch. On connect Cuebot listens to the node list and to every
/// stateEvents and boolean node, so state and push-to-talk changes made in veadotube become triggers.
/// </summary>
public sealed class VeadotubeService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly Func<IReadOnlyList<VeadotubeInstance>> _discover;
    private readonly ConcurrentDictionary<string, IReadOnlyList<VeadotubeState>> _states = new();
    private readonly ConcurrentDictionary<string, string> _current = new();
    private readonly ConcurrentDictionary<string, bool> _switches = new();

    /// <summary>
    /// Boolean nodes whose first value is still expected (until the time given). veadotube mini 2.2 does not answer a
    /// boolean "get" while it has no value, so a value arriving after this is a change from "off", not the first read.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstRead = new();
    private static readonly TimeSpan FirstReadWindow = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, VeadotubeNumber> _numbers = new();
    private readonly ConcurrentDictionary<string, (string Hash, byte[] Png)> _thumbs = new();
    private readonly VeadotubeTimedUndo _undo;
    private readonly HttpClient _http;
    private volatile IReadOnlyList<VeadotubeNode> _nodes = [];
    private volatile VeadotubeInstance? _instance;
    private (string Name, string Id, string Version)? _info;

    /// <summary>A state picture is only asked for when its hash changed; this many states per node at most.</summary>
    private const int MaxThumbnails = 64;

    public VeadotubeService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(),
            () => VeadotubeInstances.Find(VeadotubeInstances.DefaultFolder, DateTimeOffset.UtcNow), StreamAppsTimings.Default)
    {
    }

    internal VeadotubeService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Func<IReadOnlyList<VeadotubeInstance>> discover, StreamAppsTimings timings, HttpMessageHandler? http = null, TimeProvider? time = null)
        : base("veadotube", bus, log, transports, timings)
    {
        _settings = settings;
        _discover = discover;
        _http = new HttpClient(http ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        _undo = new VeadotubeTimedUndo((target, ex) => Log.Warn(Name, $"Could not undo the timed change ({target}): {ex.Message}"), time ?? TimeProvider.System);
    }

    /// <summary>veadotube's own name and version, from the info message it sends on connect (also when connected by address).</summary>
    public string? ConnectedName => _info?.Name ?? _instance?.Name;

    public VeadotubeSettings Settings => _settings.Value.Veadotube;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>The instance connected to (null when connected by address or not connected).</summary>
    public VeadotubeInstance? Instance => _instance;

    /// <summary>The nodes veadotube exposes (cached while connected).</summary>
    public IReadOnlyList<VeadotubeNode> Nodes => _nodes;

    /// <summary>Raised (on a background thread) when nodes, states or values changed.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    /// <summary>Open veadotube programs right now (for the Apps page).</summary>
    public IReadOnlyList<VeadotubeInstance> FindInstances() => _discover();

    private protected override (Uri Url, string Display) Endpoint()
    {
        var setting = Settings.Instance.Trim();
        string server;
        if (VeadotubeInstances.IsAddress(setting))
        {
            _instance = null;
            server = setting;
        }
        else
        {
            var instances = _discover();
            var picked = VeadotubeInstances.Pick(instances, setting) ?? throw new StreamAppUnavailableException(instances.Count == 0
                ? "veadotube is not open (open veadotube mini or veadotube, then Cuebot finds it by itself)"
                : $"no open veadotube matches '{setting}' (open: {string.Join(", ", instances.Select(i => i.Name))})");
            _instance = picked;
            server = picked.Server;
        }
        // veadotube rejects clients without a name; it lists this one as "Cuebot".
        return (new Uri($"ws://{server}/?n=Cuebot"), _instance is { } i ? $"{i.Name} at {server}" : server);
    }

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["veadotube.instance"] = _instance?.Name ?? Settings.Instance,
        ["veadotube.instanceId"] = _instance?.Id ?? "",
    };

    private protected override void Publish(string name, Dictionary<string, object?> args) => PublishEvent($"{VeadotubeEvents.Prefix}.{name}", args);

    private void PublishEvent(string type, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        _info = null;
        MarkConnected(_instance is { } i ? $"Connected to {i.Name} {i.Version}".TrimEnd() : "Connected to veadotube");
        // "listen" only reports later changes; "list" answers now.
        await SendAsync(VeadotubeProtocol.ListenNodes(), ct).ConfigureAwait(false);
        await SendAsync(VeadotubeProtocol.ListNodes(), ct).ConfigureAwait(false);
        while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
        {
            try
            {
                await OnMessageAsync(text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(Name, "Could not process a veadotube message", ex);
            }
        }
    }

    private protected override void OnSessionEnded()
    {
        _nodes = [];
        _states.Clear();
        _current.Clear();
        _switches.Clear();
        _numbers.Clear();
        _firstRead.Clear();
        _undo.CancelAll();
        DataChanged?.Invoke();
    }

    private async Task OnMessageAsync(string text, CancellationToken ct)
    {
        if (VeadotubeProtocol.Split(text) is not ({ } channel, { } body)) return;
        if (channel == VeadotubeProtocol.InstanceChannel)
        {
            // Sent right after connecting: veadotube's own name and version (the only source when connected by address).
            if (VeadotubeProtocol.Info(body) is { } info)
            {
                _info = info;
                UpdateStatus();
            }
            return;
        }
        if (channel != VeadotubeProtocol.NodesChannel || VeadotubeProtocol.ParseNodes(body) is not { } message) return;
        if (message.Event == "list" && message.Node is null)
        {
            var known = _nodes;
            _nodes = message.Entries;
            foreach (var node in message.Entries)
            {
                var isNew = !known.Contains(node);
                if (isNew && node.Type is VeadotubeProtocol.StateEventsType or VeadotubeProtocol.BooleanType or VeadotubeProtocol.NumberType)
                    await SendAsync(VeadotubeProtocol.Listen(node), ct).ConfigureAwait(false);
                if (node.Type == VeadotubeProtocol.StateEventsType)
                {
                    // Also for known nodes: the node list changes when the scene or avatar changes, and the states with it.
                    await SendAsync(VeadotubeProtocol.StateList(node), ct).ConfigureAwait(false);
                    if (isNew) await SendAsync(VeadotubeProtocol.StatePeek(node), ct).ConfigureAwait(false);
                }
                else if (isNew && node.Type == VeadotubeProtocol.BooleanType)
                {
                    _firstRead[Key(node)] = DateTimeOffset.UtcNow + FirstReadWindow;
                    await SendAsync(VeadotubeProtocol.BooleanGet(node), ct).ConfigureAwait(false);
                }
                else if (isNew && node.Type == VeadotubeProtocol.NumberType)
                {
                    await SendAsync(VeadotubeProtocol.NumberGet(node), ct).ConfigureAwait(false);
                }
            }
            UpdateStatus();
            return;
        }
        if (message.Event != "payload" || message.Node is not { } from) return;
        var key = Key(from);
        if (from.Type == VeadotubeProtocol.StateEventsType && message.Payload.ValueKind == JsonValueKind.Object)
        {
            switch (message.Payload.Str("event"))
            {
                case "list":
                    var states = VeadotubeProtocol.States(message.Payload);
                    _states[key] = states;
                    UpdateStatus();
                    foreach (var state in states.Take(MaxThumbnails).Where(st => st.ThumbHash.Length > 0
                                 && !(_thumbs.TryGetValue(key + "/" + st.Id, out var t) && t.Hash == st.ThumbHash)))
                        await SendAsync(VeadotubeProtocol.Thumb(from, state.Id), ct).ConfigureAwait(false);
                    break;
                case "peek":
                    var id = message.Payload.Str("state");
                    // A state Cuebot does not know: the avatar changed, so read the state list again.
                    if (id.Length > 0 && !States(from).Any(st => st.Id == id))
                        await SendAsync(VeadotubeProtocol.StateList(from), ct).ConfigureAwait(false);
                    OnState(from, id);
                    break;
                case "thumb":
                    if (message.Payload.Str("png") is { Length: > 0 } png)
                    {
                        try
                        {
                            _thumbs[key + "/" + message.Payload.Str("state")] = (message.Payload.Str("hash"), Convert.FromBase64String(png));
                            DataChanged?.Invoke();
                        }
                        catch (FormatException)
                        {
                            // Not a picture.
                        }
                    }
                    break;
            }
        }
        else if (from.Type == VeadotubeProtocol.NumberType)
        {
            if (VeadotubeProtocol.NumberValue(message.Payload) is not { } number) return;
            var had = _numbers.TryGetValue(key, out var before);
            _numbers[key] = number;
            DataChanged?.Invoke();
            if (had && before!.Value != number.Value)
                PublishEvent(VeadotubeEvents.NumberChanged, new()
                {
                    ["nodeName"] = NodeName(from),
                    ["nodeId"] = from.Id,
                    ["value"] = number.Value,
                    ["previousValue"] = before.Value,
                    ["min"] = number.Min,
                    ["max"] = number.Max,
                });
        }
        else if (from.Type == VeadotubeProtocol.BooleanType
                 && (VeadotubeProtocol.BooleanValue(message.Payload) ?? (message.Payload.ValueKind == JsonValueKind.Object ? false : null)) is { } value)
        {
            // An empty object means "no value set yet", which behaves as off (push-to-talk not held). Without any answer
            // to the first read (no value yet), a later value is a change from off.
            var had = _switches.TryGetValue(key, out var before);
            if (!had && !(_firstRead.TryRemove(key, out var until) && DateTimeOffset.UtcNow <= until))
            {
                had = true;
                before = false;
            }
            _firstRead.TryRemove(key, out _);
            _switches[key] = value;
            DataChanged?.Invoke();
            if (had && before != value)
                PublishEvent(VeadotubeEvents.SwitchChanged, new()
                {
                    ["nodeName"] = NodeName(from),
                    ["nodeId"] = from.Id,
                    ["value"] = value,
                    ["state"] = value ? "On" : "Off",
                });
        }
    }

    private void OnState(VeadotubeNode from, string stateId)
    {
        var key = Key(from);
        var had = _current.TryGetValue(key, out var before);
        _current[key] = stateId;
        DataChanged?.Invoke();
        UpdateStatus();
        if (!had || before == stateId) return;
        PublishEvent(VeadotubeEvents.StateChanged, new()
        {
            ["state"] = StateName(key, stateId),
            ["stateId"] = stateId,
            ["previousState"] = StateName(key, before ?? ""),
            ["nodeName"] = NodeName(from),
            ["nodeId"] = from.Id,
        });
    }

    private void UpdateStatus()
    {
        if (!IsConnected) return;
        var avatar = StateNode();
        var states = avatar is null ? 0 : States(avatar).Count;
        var current = avatar is null ? null : CurrentState(avatar);
        var version = _info?.Version ?? _instance?.Version ?? "";
        SetStatus(ConnectionState.Connected,
            $"Connected to {ConnectedName ?? "veadotube"}{(version.Length > 0 ? " " + version : "")}: {states} avatar states{(current is { Length: > 0 } ? $", showing {current}" : "")}");
        DataChanged?.Invoke();
    }

    private static string Key(VeadotubeNode node) => node.Type + "/" + node.Id;

    // A payload carries the name; the node list is the fallback.
    private string NodeName(VeadotubeNode node) =>
        node.Name.Length > 0 ? node.Name : _nodes.FirstOrDefault(n => Key(n) == Key(node))?.Label ?? node.Id;

    private string StateName(string key, string id) =>
        _states.TryGetValue(key, out var states) ? states.FirstOrDefault(s => s.Id == id)?.Name ?? id : id;

    // ---- Queries ----

    /// <summary>The avatar states of a stateEvents node.</summary>
    public IReadOnlyList<VeadotubeState> States(VeadotubeNode node) => _states.TryGetValue(Key(node), out var s) ? s : [];

    /// <summary>The name of the state on top of a stateEvents node's stack, when known.</summary>
    public string? CurrentState(VeadotubeNode node) => _current.TryGetValue(Key(node), out var id) ? StateName(Key(node), id) : null;

    /// <summary>A boolean node's value, when known.</summary>
    public bool? SwitchValue(VeadotubeNode node) => _switches.TryGetValue(Key(node), out var v) ? v : null;

    /// <summary>A number node's value and range, when known.</summary>
    public VeadotubeNumber? NumberValue(VeadotubeNode node) => _numbers.TryGetValue(Key(node), out var v) ? v : null;

    /// <summary>A state's picture (PNG, about 128x128), when veadotube sent one.</summary>
    public byte[]? Thumbnail(VeadotubeNode node, string stateId) => _thumbs.TryGetValue(Key(node) + "/" + stateId, out var t) ? t.Png : null;

    /// <summary>The current state's picture as a data: URL (for overlays), or "".</summary>
    public string CurrentThumbnailUrl(VeadotubeNode node) =>
        _current.TryGetValue(Key(node), out var id) && Thumbnail(node, id) is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : "";

    /// <summary>The number nodes (full veadotube).</summary>
    public IEnumerable<VeadotubeNode> NumberNodes() => _nodes.Where(n => n.Type == VeadotubeProtocol.NumberType);

    /// <summary>Node names of a type, for suggestions.</summary>
    public IEnumerable<string> NodeNames(string type) => _nodes.Where(n => n.Type == type).Select(n => n.Label);

    /// <summary>Names of open veadotube programs, for suggestions.</summary>
    public IEnumerable<string> InstanceNames() => _discover().Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>All state names of every stateEvents node (for suggestions).</summary>
    public IEnumerable<string> AllStateNames() => _states.Values.SelectMany(s => s).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>A stateEvents node by name or id; empty picks the first (veadotube mini has one, "avatar state").</summary>
    public VeadotubeNode? StateNode(string nameOrId = "") => FindNode(VeadotubeProtocol.StateEventsType, nameOrId);

    /// <summary>A boolean node by name or id; empty picks the first (veadotube mini: "push-to-talk").</summary>
    public VeadotubeNode? SwitchNode(string nameOrId = "") => FindNode(VeadotubeProtocol.BooleanType, nameOrId);

    private VeadotubeNode? FindNode(string type, string nameOrId)
    {
        var nodes = _nodes.Where(n => n.Type == type).ToArray();
        var w = nameOrId.Trim();
        if (w.Length == 0) return nodes.FirstOrDefault();
        return nodes.FirstOrDefault(n => string.Equals(n.Name, w, StringComparison.OrdinalIgnoreCase))
               ?? nodes.FirstOrDefault(n => string.Equals(n.Id, w, StringComparison.OrdinalIgnoreCase));
    }

    private VeadotubeNode RequireNode(string type, string nameOrId, string what)
    {
        EnsureConnected();
        return FindNode(type, nameOrId) ?? throw new StreamAppRequestException(nameOrId.Trim().Length == 0
            ? $"This veadotube has no {what}."
            : $"veadotube has no {what} called '{nameOrId.Trim()}'.");
    }

    // ---- Targets ----

    /// <summary>
    /// The veadotube a step talks to: empty (or the connected one's name, id or type) is the connected veadotube;
    /// anything else is another open veadotube (by name, id or type) or an address, reached by one-off GET commands.
    /// </summary>
    internal IVeadotubeTarget Target(string instance)
    {
        var wanted = instance.Trim();
        if (wanted.Length == 0 || IsConnectedOne(wanted)) return new LiveTarget(this);
        if (VeadotubeInstances.IsAddress(wanted)) return new VeadotubeRemoteTarget(_http, wanted, wanted);
        var instances = _discover();
        var picked = VeadotubeInstances.Pick(instances, wanted) ?? throw new StreamAppRequestException(instances.Count == 0
            ? "No veadotube is open."
            : $"No open veadotube matches '{wanted}' (open: {string.Join(", ", instances.Select(i => i.Name))}).");
        return _instance?.Id == picked.Id ? new LiveTarget(this) : new VeadotubeRemoteTarget(_http, picked.Server, picked.Name);
    }

    private bool IsConnectedOne(string wanted)
    {
        if (!IsConnected) return false;
        if (_instance is { } i && (string.Equals(i.Id, wanted, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(i.Type, wanted, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(i.Server, wanted, StringComparison.OrdinalIgnoreCase)))
            return true;
        return _info is { } info && (string.Equals(info.Name, wanted, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(info.Id, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The connected veadotube: its caches answer, commands go over the WebSocket.</summary>
    private sealed class LiveTarget(VeadotubeService service) : IVeadotubeTarget
    {
        public string Label => service.ConnectedName ?? "veadotube";

        public Task<VeadotubeNode> NodeAsync(string type, string nameOrId, string what, CancellationToken ct) =>
            Task.FromResult(service.RequireNode(type, nameOrId, what));

        public Task<IReadOnlyList<VeadotubeState>> StatesAsync(VeadotubeNode node, CancellationToken ct) => Task.FromResult(service.States(node));

        public Task<string?> CurrentStateIdAsync(VeadotubeNode node, CancellationToken ct) =>
            Task.FromResult(service._current.TryGetValue(Key(node), out var id) ? id : null);

        public Task<bool?> SwitchAsync(VeadotubeNode node, CancellationToken ct) => Task.FromResult(service.SwitchValue(node));

        public Task<VeadotubeNumber?> NumberAsync(VeadotubeNode node, CancellationToken ct) => Task.FromResult(service.NumberValue(node));

        public Task SendAsync(VeadotubeNode node, VeadotubeCommand command, CancellationToken ct) =>
            service.SendAsync(VeadotubeProtocol.Command(node, command), ct);
    }

    // ---- Commands ----

    /// <summary>
    /// Changes the avatar state: "set" (show only this state), "push" (show it on top), "pop" (take it off), "toggle"
    /// (push or pop), "clear" (back to the bottom state; no state needed) or "random" (set a random state: from the
    /// comma-separated <paramref name="state"/> list, or any). Returns the state id sent.
    /// </summary>
    public Task<string?> ChangeStateAsync(string mode, string state, string node = "", CancellationToken ct = default) =>
        ChangeStateAsync(mode, state, node, "", null, ct);

    /// <summary>
    /// <see cref="ChangeStateAsync(string, string, string, CancellationToken)"/> on any open veadotube
    /// (<paramref name="instance"/>), optionally only <paramref name="duration"/> long: a pushed state is taken off
    /// again, a set or random state goes back to the one before, a toggled state is toggled back.
    /// </summary>
    public async Task<string?> ChangeStateAsync(string mode, string state, string node, string instance, TimeSpan? duration, CancellationToken ct = default)
    {
        var target = Target(instance);
        var stateNode = await target.NodeAsync(VeadotubeProtocol.StateEventsType, node, "avatar state node", ct).ConfigureAwait(false);
        var @event = mode.Trim().ToLowerInvariant() switch
        {
            "set" or "" => "set",
            "push" => "push",
            "pop" => "pop",
            "toggle" => "toggle",
            "clear" => "clear",
            "random" => "random",
            _ => throw new ArgumentException($"Unknown state change '{mode}' (use Set, Push, Pop, Toggle, Random or Clear)."),
        };
        var states = await target.StatesAsync(stateNode, ct).ConfigureAwait(false);
        var before = await target.CurrentStateIdAsync(stateNode, ct).ConfigureAwait(false);
        string? id = null;
        if (@event == "random")
        {
            var pool = state.Trim().Length == 0 ? states.Select(st => st.Id).ToArray()
                : state.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(w => Resolve(states, w)).ToArray();
            if (pool.Length == 0) throw new StreamAppRequestException($"{target.Label} has no avatar states to pick from.");
            var others = pool.Where(p => p != before).ToArray();
            id = (others.Length > 0 ? others : pool)[Random.Shared.Next(others.Length > 0 ? others.Length : pool.Length)];
            @event = "set";
        }
        else if (@event != "clear")
        {
            id = Resolve(states, Steps.Require(state, "State"));
        }
        await target.SendAsync(stateNode, new VeadotubeCommand(@event, id), ct).ConfigureAwait(false);

        var undoKey = $"{target.Label}/{Key(stateNode)}/{(@event is "push" or "pop" or "toggle" ? id : "")}";
        if (duration is { } after && @event != "clear")
        {
            VeadotubeCommand undo = @event switch
            {
                "push" => new("pop", id),
                "pop" => new("push", id),
                "toggle" => new("toggle", id),
                _ => before is { Length: > 0 } ? new("set", before) : new("clear"),
            };
            _undo.Schedule(undoKey, after, () => target.SendAsync(stateNode, undo, CancellationToken.None));
        }
        else
        {
            _undo.Cancel(undoKey);
        }
        return id;

        string Resolve(IReadOnlyList<VeadotubeState> list, string wanted)
        {
            var known = list.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? list.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (known is null && list.Count > 0)
                throw new StreamAppRequestException($"{target.Label} has no avatar state '{wanted}' (it has: {string.Join(", ", list.Select(s => s.Name))}).");
            return known?.Id ?? wanted;
        }
    }

    /// <summary>Turns an on/off node (push-to-talk in veadotube mini) on, off or the other way. Returns the value asked for, when known.</summary>
    public Task<bool?> SetSwitchAsync(SwitchMode mode, string node = "", CancellationToken ct = default) =>
        SetSwitchAsync(mode, node, "", null, ct);

    /// <summary>
    /// <see cref="SetSwitchAsync(SwitchMode, string, CancellationToken)"/> on any open veadotube, optionally only
    /// <paramref name="duration"/> long (then it is set back, like holding a button).
    /// </summary>
    public async Task<bool?> SetSwitchAsync(SwitchMode mode, string node, string instance, TimeSpan? duration, CancellationToken ct = default)
    {
        var target = Target(instance);
        var switchNode = await target.NodeAsync(VeadotubeProtocol.BooleanType, node, "on/off node", ct).ConfigureAwait(false);
        var before = await target.SwitchAsync(switchNode, ct).ConfigureAwait(false);
        bool? result;
        VeadotubeCommand undo;
        if (mode == SwitchMode.Toggle)
        {
            await target.SendAsync(switchNode, new VeadotubeCommand("toggle"), ct).ConfigureAwait(false);
            result = before is { } now ? !now : null;
            undo = new VeadotubeCommand("toggle");
        }
        else
        {
            var on = mode == SwitchMode.On;
            await target.SendAsync(switchNode, new VeadotubeCommand("set", Bool: on), ct).ConfigureAwait(false);
            result = on;
            undo = new VeadotubeCommand("set", Bool: before ?? !on);
        }
        var undoKey = $"{target.Label}/{Key(switchNode)}";
        if (duration is { } after) _undo.Schedule(undoKey, after, () => target.SendAsync(switchNode, undo, CancellationToken.None));
        else _undo.Cancel(undoKey);
        return result;
    }

    /// <summary>Sets (or adds to) a number node; full veadotube only, where you expose nodes yourself.</summary>
    public Task SetNumberAsync(string node, double value, bool add, CancellationToken ct = default) =>
        SetNumberAsync(node, value, add, null, null, "", ct);

    /// <summary>
    /// Sets (or adds to) a number node. veadotube drops a node's range when a number is set without one, so the
    /// known range is sent along unless <paramref name="min"/> and <paramref name="max"/> give a new one.
    /// </summary>
    public async Task<VeadotubeNumber?> SetNumberAsync(string node, double value, bool add, double? min, double? max, string instance,
        CancellationToken ct = default)
    {
        if ((min is null) != (max is null)) throw new ArgumentException("Give both the lowest and the highest value, or neither.");
        if (min > max) throw new ArgumentException("The lowest value must not be above the highest.");
        var target = Target(instance);
        var numberNode = await target.NodeAsync(VeadotubeProtocol.NumberType, Steps.Require(node, "Node"), "number node", ct).ConfigureAwait(false);
        if (min is null && !add && await target.NumberAsync(numberNode, ct).ConfigureAwait(false) is { Min: { } knownMin, Max: { } knownMax })
            (min, max) = (knownMin, knownMax);
        await target.SendAsync(numberNode, new VeadotubeCommand(add ? "add" : "set", Number: value, Min: min, Max: max), ct).ConfigureAwait(false);
        return await target.NumberAsync(numberNode, ct).ConfigureAwait(false);
    }

    /// <summary>A number node's value (the connected veadotube's cache, or asked fresh from another one).</summary>
    public async Task<VeadotubeNumber?> GetNumberAsync(string node, string instance = "", CancellationToken ct = default)
    {
        var target = Target(instance);
        var numberNode = await target.NodeAsync(VeadotubeProtocol.NumberType, Steps.Require(node, "Node"), "number node", ct).ConfigureAwait(false);
        return await target.NumberAsync(numberNode, ct).ConfigureAwait(false);
    }

    /// <summary>Clears an on/off or number node back to "no value" (veadotube's clear event).</summary>
    public async Task ResetNodeAsync(string type, string node, string instance = "", CancellationToken ct = default)
    {
        var nodeType = type.Trim().ToLowerInvariant() switch
        {
            "number" => VeadotubeProtocol.NumberType,
            "onoff" or "boolean" or "switch" or "" => VeadotubeProtocol.BooleanType,
            _ => throw new ArgumentException($"Unknown node type '{type}' (use OnOff or Number)."),
        };
        var target = Target(instance);
        var target2 = await target.NodeAsync(nodeType, node, nodeType == VeadotubeProtocol.NumberType ? "number node" : "on/off node", ct).ConfigureAwait(false);
        await target.SendAsync(target2, new VeadotubeCommand("clear"), ct).ConfigureAwait(false);
        if (target is LiveTarget)
        {
            // veadotube does not announce a cleared value.
            if (nodeType == VeadotubeProtocol.NumberType) _numbers.TryRemove(Key(target2), out _);
            else _switches[Key(target2)] = false;
            DataChanged?.Invoke();
        }
    }

    /// <summary>The current state of any open veadotube: name, id, all names, and the picture when it is the connected one.</summary>
    public async Task<(string State, string StateId, IReadOnlyList<VeadotubeState> States, string ImageUrl)> GetStateAsync(string node, string instance,
        CancellationToken ct = default)
    {
        var target = Target(instance);
        var stateNode = await target.NodeAsync(VeadotubeProtocol.StateEventsType, node, "avatar state node", ct).ConfigureAwait(false);
        var states = await target.StatesAsync(stateNode, ct).ConfigureAwait(false);
        var id = await target.CurrentStateIdAsync(stateNode, ct).ConfigureAwait(false) ?? "";
        var name = states.FirstOrDefault(s => s.Id == id)?.Name ?? id;
        return (name, id, states, target is LiveTarget ? CurrentThumbnailUrl(stateNode) : "");
    }
}
