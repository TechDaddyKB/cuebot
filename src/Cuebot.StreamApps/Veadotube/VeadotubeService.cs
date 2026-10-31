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
    private volatile IReadOnlyList<VeadotubeNode> _nodes = [];
    private volatile VeadotubeInstance? _instance;

    public VeadotubeService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(),
            () => VeadotubeInstances.Find(VeadotubeInstances.DefaultFolder, DateTimeOffset.UtcNow), StreamAppsTimings.Default)
    {
    }

    internal VeadotubeService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Func<IReadOnlyList<VeadotubeInstance>> discover, StreamAppsTimings timings)
        : base("veadotube", bus, log, transports, timings)
    {
        _settings = settings;
        _discover = discover;
    }

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
        DataChanged?.Invoke();
    }

    private async Task OnMessageAsync(string text, CancellationToken ct)
    {
        if (VeadotubeProtocol.Split(text) is not ({ } channel, { } body) || channel != VeadotubeProtocol.NodesChannel) return;
        if (VeadotubeProtocol.ParseNodes(body) is not { } message) return;
        if (message.Event == "list" && message.Node is null)
        {
            var known = _nodes;
            _nodes = message.Entries;
            foreach (var node in message.Entries.Where(n => !known.Contains(n)))
            {
                if (node.Type is VeadotubeProtocol.StateEventsType or VeadotubeProtocol.BooleanType)
                    await SendAsync(VeadotubeProtocol.Listen(node), ct).ConfigureAwait(false);
                if (node.Type == VeadotubeProtocol.StateEventsType)
                {
                    await SendAsync(VeadotubeProtocol.StateList(node), ct).ConfigureAwait(false);
                    await SendAsync(VeadotubeProtocol.StatePeek(node), ct).ConfigureAwait(false);
                }
                else if (node.Type == VeadotubeProtocol.BooleanType)
                {
                    await SendAsync(VeadotubeProtocol.BooleanGet(node), ct).ConfigureAwait(false);
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
                    _states[key] = VeadotubeProtocol.States(message.Payload);
                    UpdateStatus();
                    break;
                case "peek":
                    OnState(from, message.Payload.Str("state"));
                    break;
            }
        }
        else if (from.Type == VeadotubeProtocol.BooleanType
                 && (VeadotubeProtocol.BooleanValue(message.Payload) ?? (message.Payload.ValueKind == JsonValueKind.Object ? false : null)) is { } value)
        {
            // An empty object means "no value set yet", which behaves as off (push-to-talk not held).
            var had = _switches.TryGetValue(key, out var before);
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
        SetStatus(ConnectionState.Connected,
            $"Connected to {(_instance?.Name ?? "veadotube")}: {states} avatar states{(current is { Length: > 0 } ? $", showing {current}" : "")}");
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

    // ---- Commands ----

    /// <summary>
    /// Changes the avatar state: "set" (show only this state), "push" (show it on top), "pop" (take it off), "toggle"
    /// (push or pop) or "clear" (back to the bottom state; no state needed). Returns the state id sent.
    /// </summary>
    public async Task<string?> ChangeStateAsync(string mode, string state, string node = "", CancellationToken ct = default)
    {
        var target = RequireNode(VeadotubeProtocol.StateEventsType, node, "avatar state node");
        var @event = mode.Trim().ToLowerInvariant() switch
        {
            "set" or "" => "set",
            "push" => "push",
            "pop" => "pop",
            "toggle" => "toggle",
            "clear" => "clear",
            _ => throw new ArgumentException($"Unknown state change '{mode}' (use Set, Push, Pop, Toggle or Clear)."),
        };
        string? id = null;
        if (@event != "clear")
        {
            var wanted = Steps.Require(state, "State");
            var states = States(target);
            var known = states.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? states.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (known is null && states.Count > 0)
                throw new StreamAppRequestException($"veadotube has no avatar state '{wanted}' (it has: {string.Join(", ", states.Select(s => s.Name))}).");
            id = known?.Id ?? wanted;
        }
        await SendAsync(VeadotubeProtocol.StateEvent(target, @event, id), ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Turns an on/off node (push-to-talk in veadotube mini) on, off or the other way. Returns the value asked for, when known.</summary>
    public async Task<bool?> SetSwitchAsync(SwitchMode mode, string node = "", CancellationToken ct = default)
    {
        var target = RequireNode(VeadotubeProtocol.BooleanType, node, "on/off node");
        if (mode == SwitchMode.Toggle)
        {
            await SendAsync(VeadotubeProtocol.BooleanToggle(target), ct).ConfigureAwait(false);
            return SwitchValue(target) is { } now ? !now : null;
        }
        await SendAsync(VeadotubeProtocol.BooleanSet(target, mode == SwitchMode.On), ct).ConfigureAwait(false);
        return mode == SwitchMode.On;
    }

    /// <summary>Sets (or adds to) a number node; full veadotube only, where you expose nodes yourself.</summary>
    public async Task SetNumberAsync(string node, double value, bool add, CancellationToken ct = default)
    {
        var target = RequireNode(VeadotubeProtocol.NumberType, Steps.Require(node, "Node"), "number node");
        await SendAsync(VeadotubeProtocol.NumberChange(target, add, value), ct).ConfigureAwait(false);
    }
}
