using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server.Api;
using Cuebot.Server.Hosting;

namespace Cuebot.StreamDeck;

/// <summary>One end of a plugin connection (a WebSocket in the app, a fake in tests).</summary>
public interface IStreamDeckPeer
{
    string Id { get; }
    string Remote { get; }
    Task<bool> SendAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// The Stream Deck integration: Cuebot's plugin connects to <c>/api/v1/streamdeck</c> on the REST API listener (same
/// bearer token). Each connection is one Stream Deck app; it reports its devices and visible Cuebot keys, sends key and
/// dial input (→ the Stream Deck Action trigger) and requests (list/run/toggle actions, set globals). Sub-actions push
/// key appearance (title, state, image, value, alert/OK) to every key with a button id.
/// </summary>
public sealed class StreamDeckHub : IApiWebSocketRoute, IConnectionStatus
{
    public const string Source = "Stream Deck";
    public const string ActionEvent = "streamDeck.action";
    public const string ConnectedEvent = "streamDeck.connected";
    public const string DisconnectedEvent = "streamDeck.disconnected";
    public const string InfoEvent = "streamDeck.info";

    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<ApiOperations> _operations;
    private readonly Func<bool> _apiEnabled;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly string _appVersion;

    /// <param name="operations">Action/global operations (shared with the REST API so rules and logging match).</param>
    /// <param name="apiEnabled">Whether the REST API (which serves the plugin endpoint) is switched on; only for the status line.</param>
    public StreamDeckHub(EventBus bus, AppLog log, Func<ApiOperations> operations, Func<bool>? apiEnabled = null,
        ProfileService? profile = null, GlobalVariables? globals = null)
    {
        _bus = bus;
        _log = log;
        _operations = operations;
        _apiEnabled = apiEnabled ?? (() => true);
        _appVersion = typeof(StreamDeckHub).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        if (profile is not null) profile.Changed += () => _ = BroadcastActionsAsync();
        if (globals is not null) globals.VariableChanged += c => _ = BroadcastAsync(StreamDeckProtocol.GlobalChanged(c.Name, globals.Get(c.Name)));
    }

    public string Name => "Stream Deck";

    public ConnectionState State => Instances.Count > 0 ? ConnectionState.Connected : ConnectionState.Disconnected;

    public string StatusMessage
    {
        get
        {
            var instances = Instances;
            if (instances.Count == 0)
            {
                return _apiEnabled()
                    ? "Waiting for the Cuebot Stream Deck plugin"
                    : "The REST API is off: turn it on (Servers page) so the Stream Deck plugin can connect";
            }
            var devices = instances.Sum(i => i.Devices.Count(d => d.Connected));
            var keys = instances.Sum(i => i.Buttons.Count);
            return $"{Plural(instances.Count, "Stream Deck app")}, {Plural(devices, "device")}, {Plural(keys, "visible key")}";
        }
    }

    public event Action? StatusChanged;

    /// <summary>Connected plugins that have said hello, oldest first.</summary>
    public IReadOnlyList<StreamDeckInstanceInfo> Instances =>
        [.. _sessions.Values.Where(s => s.Ready).OrderBy(s => s.ConnectedAt).Select(s => s.Snapshot())];

    /// <summary>Distinct button ids of every visible key (for sub-action suggestions).</summary>
    public IEnumerable<string> KnownButtonIds =>
        Instances.SelectMany(i => i.Buttons).Select(b => b.ButtonId).Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    // ---- IApiWebSocketRoute ----------------------------------------------------------------------------------------

    string IApiWebSocketRoute.Name => StreamDeckProtocol.RouteName;

    Task IWebSocketHandler.OnOpenAsync(WebSocketConnection connection) => OpenAsync(new SocketPeer(connection));

    Task IWebSocketHandler.OnMessageAsync(WebSocketConnection connection, string message) => HandleAsync(connection.Id, message);

    void IWebSocketHandler.OnClose(WebSocketConnection connection) => Close(connection.Id);

    private sealed class SocketPeer(WebSocketConnection connection) : IStreamDeckPeer
    {
        public string Id => connection.Id;
        public string Remote => connection.Remote;
        public Task<bool> SendAsync(string text, CancellationToken ct = default) => connection.SendAsync(text, ct);
    }

    // ---- Connection lifecycle --------------------------------------------------------------------------------------

    public async Task OpenAsync(IStreamDeckPeer peer)
    {
        _sessions[peer.Id] = new Session(peer);
        await peer.SendAsync(StreamDeckProtocol.Welcome(peer.Id, _appVersion)).ConfigureAwait(false);
    }

    public void Close(string peerId)
    {
        if (!_sessions.TryRemove(peerId, out var session) || !session.Ready) return;
        _log.Info(Source, $"Stream Deck plugin disconnected ({session.Peer.Remote})");
        _bus.Publish(new BotEvent(DisconnectedEvent, SessionArgs(session)));
        StatusChanged?.Invoke();
    }

    /// <summary>Handles one message from a plugin. Malformed input is logged and ignored; it never throws.</summary>
    public async Task HandleAsync(string peerId, string text)
    {
        if (!_sessions.TryGetValue(peerId, out var session)) return;
        if (StreamDeckProtocol.TryParse(text) is not { } message)
        {
            _log.Warn(Source, "Ignored a message from the Stream Deck plugin that is not a JSON object");
            return;
        }

        try
        {
            switch (message.Str("type"))
            {
                case "hello": OnHello(session, message); break;
                case "devices": OnDevices(session, message); break;
                case "buttons": session.SetButtons(StreamDeckProtocol.ParseButtons(message)); StatusChanged?.Invoke(); break;
                case "appear":
                    if (message.TryGetProperty("button", out var b) && StreamDeckProtocol.ParseButton(b) is { } button)
                    {
                        session.Upsert(button);
                        StatusChanged?.Invoke();
                    }
                    break;
                case "disappear":
                    session.Remove(message.Str("context"));
                    StatusChanged?.Invoke();
                    break;
                case "input": OnInput(session, message); break;
                case "request": await OnRequestAsync(session, message).ConfigureAwait(false); break;
                case "ping": await session.Peer.SendAsync("{\"type\":\"pong\"}").ConfigureAwait(false); break;
                default: _log.Debug(Source, $"Ignored Stream Deck message type '{message.Str("type")}'"); break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Handling a Stream Deck plugin message failed", ex);
        }
    }

    private void OnHello(Session session, JsonElement message)
    {
        var wasReady = session.Ready;
        session.PluginVersion = message.Obj("plugin")?.Str("version") ?? "";
        var app = message.Obj("app");
        session.AppVersion = app?.Str("version") ?? "";
        session.Platform = app?.Str("platform") ?? "";
        session.SetDevices(StreamDeckProtocol.ParseDevices(message));
        if (message.TryGetProperty("buttons", out _)) session.SetButtons(StreamDeckProtocol.ParseButtons(message));
        session.Ready = true;
        if (!wasReady)
        {
            _log.Info(Source, $"Stream Deck plugin {session.PluginVersion} connected (Stream Deck {session.AppVersion}, {Plural(session.DeviceCount, "device")})");
            _bus.Publish(new BotEvent(ConnectedEvent, SessionArgs(session)));
        }
        _bus.Publish(new BotEvent(InfoEvent, InfoArgs(session)));
        StatusChanged?.Invoke();
    }

    private void OnDevices(Session session, JsonElement message)
    {
        session.SetDevices(StreamDeckProtocol.ParseDevices(message));
        if (session.Ready) _bus.Publish(new BotEvent(InfoEvent, InfoArgs(session)));
        StatusChanged?.Invoke();
    }

    private void OnInput(Session session, JsonElement message)
    {
        var context = message.Str("context");
        var button = session.Find(context);
        if (button is not null && message.Int("state") is { } state) session.Upsert(button with { State = state });
        var deviceId = button?.DeviceId is { Length: > 0 } id ? id : message.Str("device");
        var device = session.Device(deviceId);
        var args = StreamDeckProtocol.InputArgs(message, button, device, session.Peer.Id);
        _bus.Publish(new BotEvent(ActionEvent, args));
    }

    private static Dictionary<string, object?> SessionArgs(Session s) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["instanceId"] = s.Peer.Id,
        ["remote"] = s.Peer.Remote,
        ["pluginVersion"] = s.PluginVersion,
        ["streamDeckVersion"] = s.AppVersion,
        ["platform"] = s.Platform,
        ["deviceCount"] = s.DeviceCount,
    };

    private static Dictionary<string, object?> InfoArgs(Session s)
    {
        var args = SessionArgs(s);
        var devices = s.Snapshot().Devices;
        args["deviceNames"] = string.Join(", ", devices.Select(d => d.Name));
        args["devices"] = JsonSerializer.Serialize(devices.Select(d => new
        {
            id = d.Id, name = d.Name, type = d.Type, typeName = d.TypeName, columns = d.Columns, rows = d.Rows, connected = d.Connected,
        }));
        for (var i = 0; i < devices.Count; i++)
        {
            args[$"device{i}.id"] = devices[i].Id;
            args[$"device{i}.name"] = devices[i].Name;
            args[$"device{i}.type"] = devices[i].TypeName;
        }
        return args;
    }

    // ---- Requests from the plugin ----------------------------------------------------------------------------------

    private async Task OnRequestAsync(Session session, JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var n) ? n : 0;
        var p = message.Obj("params") ?? default;
        string reply;
        try
        {
            var result = await ExecuteAsync(message.Str("method"), p).ConfigureAwait(false);
            reply = StreamDeckProtocol.Response(id, result);
        }
        catch (ApiException ex)
        {
            reply = StreamDeckProtocol.Error(id, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            reply = StreamDeckProtocol.Error(id, ex.Message);
        }
        await session.Peer.SendAsync(reply).ConfigureAwait(false);
    }

    internal async Task<JsonNode?> ExecuteAsync(string method, JsonElement p)
    {
        var ops = _operations();
        switch (method)
        {
            case "getActions":
                return new JsonObject { ["actions"] = ActionList(ops) };
            case "runAction":
            {
                var args = p.Obj("args") is { } a ? JsonNode.Parse(a.GetRawText()) : null;
                return await ops.RunActionAsync(Required(p, "action"), args, wait: false, timeoutSeconds: 30, Source).ConfigureAwait(false);
            }
            case "setActionEnabled":
            {
                var action = ops.FindAction(Required(p, "action"));
                var mode = p.Str("enabled").ToLowerInvariant();
                var enabled = mode switch
                {
                    "true" or "on" or "enable" or "enabled" => true,
                    "false" or "off" or "disable" or "disabled" => false,
                    _ => !action.Enabled,
                };
                return await ops.SetActionEnabledAsync(action.Id.ToString(), enabled).ConfigureAwait(false);
            }
            case "getGlobals":
                return ops.GetGlobals();
            case "setGlobal":
                return SetGlobal(ops, p);
            default:
                throw new ArgumentException($"Unknown request '{method}'");
        }
    }

    private static JsonObject SetGlobal(ApiOperations ops, JsonElement p)
    {
        var name = Required(p, "name");
        var persisted = p.Bool("persisted") ?? true;
        var op = p.Str("op").ToLowerInvariant();
        JsonNode? value;
        switch (op)
        {
            case "add":
            {
                var current = CurrentGlobal(ops, name);
                var by = p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                    : double.TryParse(p.Str("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
                    : throw new ArgumentException("value must be a number to add");
                var baseValue = double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0;
                var sum = baseValue + by;
                value = sum == Math.Floor(sum) && Math.Abs(sum) < long.MaxValue ? JsonValue.Create((long)sum) : JsonValue.Create(sum);
                break;
            }
            case "toggle":
                value = JsonValue.Create(!string.Equals(CurrentGlobal(ops, name), "true", StringComparison.OrdinalIgnoreCase));
                break;
            default:
                value = p.TryGetProperty("value", out var raw) ? JsonNode.Parse(raw.GetRawText()) : null;
                if (value is JsonValue text && text.TryGetValue<string>(out var s)) value = TypedText(s);
                break;
        }
        return ops.SetGlobal(name, value, persisted);
    }

    /// <summary>Text from a key's settings typed like Streamer.bot's Set Global: numbers and booleans keep their type.</summary>
    internal static JsonNode? TypedText(string s)
    {
        var t = s.Trim();
        if (bool.TryParse(t, out var b)) return JsonValue.Create(b);
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (t.Length > 0 && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !t.Contains(',')) return JsonValue.Create(d);
        return JsonValue.Create(s);
    }

    private static string? CurrentGlobal(ApiOperations ops, string name)
    {
        try
        {
            return ops.GetGlobal(name)["value"] is JsonValue v ? v.ToString() : null;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    private static string Required(JsonElement p, string name) =>
        p.Str(name).Trim() is { Length: > 0 } value ? value : throw new ArgumentException($"'{name}' is required");

    private static JsonArray ActionList(ApiOperations ops) =>
        [.. ops.ListActions()["actions"]!.AsArray().Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a!["id"]!.DeepClone(),
            ["name"] = a["name"]!.DeepClone(),
            ["group"] = a["group"]!.DeepClone(),
            ["enabled"] = a["enabled"]!.DeepClone(),
        })];

    private async Task BroadcastActionsAsync()
    {
        if (_sessions.IsEmpty) return;
        try
        {
            await BroadcastAsync(StreamDeckProtocol.ActionsChanged(ActionList(_operations()))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Sending the action list to the Stream Deck plugin failed", ex);
        }
    }

    private async Task<int> BroadcastAsync(string text)
    {
        var ready = _sessions.Values.Where(s => s.Ready).ToArray();
        if (ready.Length == 0) return 0;
        var sent = await Task.WhenAll(ready.Select(s => s.Peer.SendAsync(text))).ConfigureAwait(false);
        return sent.Count(x => x);
    }

    // ---- Commands to keys ------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends an appearance command to every connected plugin (each applies it to all its keys with the button id and
    /// remembers it for keys that appear later). Returns how many visible keys have that id.
    /// </summary>
    public async Task<int> SendCommandAsync(StreamDeckCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.ButtonId)) throw new ArgumentException("A button id is required.");
        if (!StreamDeckProtocol.Commands.Contains(command.Command)) throw new ArgumentException($"Unknown command '{command.Command}'.");
        var normalized = command with { ButtonId = command.ButtonId.Trim() };
        var sessions = _sessions.Values.Where(s => s.Ready).ToArray();
        if (sessions.Length == 0)
        {
            _log.Warn(Source, $"{command.Command} for '{normalized.ButtonId}': no Stream Deck plugin is connected");
            return 0;
        }
        var text = StreamDeckProtocol.Command(normalized);
        await Task.WhenAll(sessions.Select(s => s.Peer.SendAsync(text, ct))).ConfigureAwait(false);
        var matched = 0;
        foreach (var s in sessions) matched += s.CountButtons(normalized.ButtonId);
        if (command.Command is "setState" or "toggleState")
        {
            foreach (var s in sessions) s.ApplyState(normalized.ButtonId, command.Command == "toggleState" ? null : command.State);
        }
        return matched;
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    // ---- Per-connection state --------------------------------------------------------------------------------------

    private sealed class Session(IStreamDeckPeer peer)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, StreamDeckButton> _buttons = new(StringComparer.Ordinal);
        private IReadOnlyList<StreamDeckDevice> _devices = [];

        public IStreamDeckPeer Peer { get; } = peer;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
        public volatile bool Ready;
        public string PluginVersion { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string Platform { get; set; } = "";

        public int DeviceCount
        {
            get { lock (_gate) return _devices.Count; }
        }

        public void SetDevices(IReadOnlyList<StreamDeckDevice> devices)
        {
            lock (_gate) _devices = devices;
        }

        public StreamDeckDevice? Device(string id)
        {
            lock (_gate) return _devices.FirstOrDefault(d => d.Id == id);
        }

        public void SetButtons(IReadOnlyList<StreamDeckButton> buttons)
        {
            lock (_gate)
            {
                _buttons.Clear();
                foreach (var b in buttons) _buttons[b.Context] = b;
            }
        }

        public void Upsert(StreamDeckButton button)
        {
            lock (_gate) _buttons[button.Context] = button;
        }

        public void Remove(string context)
        {
            lock (_gate) _buttons.Remove(context);
        }

        public StreamDeckButton? Find(string context)
        {
            lock (_gate) return _buttons.GetValueOrDefault(context);
        }

        public int CountButtons(string buttonId)
        {
            lock (_gate) return _buttons.Values.Count(b => string.Equals(b.ButtonId, buttonId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Mirrors a state push so the list in the app stays right (null toggles).</summary>
        public void ApplyState(string buttonId, int? state)
        {
            lock (_gate)
            {
                foreach (var b in _buttons.Values.Where(b => string.Equals(b.ButtonId, buttonId, StringComparison.OrdinalIgnoreCase)).ToArray())
                    _buttons[b.Context] = b with { State = state ?? (b.State == 0 ? 1 : 0) };
            }
        }

        public StreamDeckInstanceInfo Snapshot()
        {
            lock (_gate)
            {
                return new StreamDeckInstanceInfo(Peer.Id, Peer.Remote, ConnectedAt, PluginVersion, AppVersion, Platform, _devices,
                    [.. _buttons.Values.OrderBy(b => b.DeviceId).ThenBy(b => b.Row ?? int.MaxValue).ThenBy(b => b.Column ?? int.MaxValue)]);
            }
        }
    }
}
