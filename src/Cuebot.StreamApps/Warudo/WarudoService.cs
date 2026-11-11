using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Warudo;

/// <summary>Bot event types published by <see cref="WarudoService"/>.</summary>
public static class WarudoEvents
{
    public const string Prefix = "warudo";
    public const string Connected = "warudo.connected";
    public const string Disconnected = "warudo.disconnected";

    /// <summary>An "On Stream Deck Toggle" node changed state (Warudo reports every change, from any sender).</summary>
    public const string ToggleChanged = "warudo.toggleChanged";
}

/// <summary>The three kinds of Warudo receiver node.</summary>
public enum WarudoReceiverKind { Trigger, Toggle, Message }

/// <summary>
/// Warudo through the WebSocket service its built-in Stream Deck plugin uses (ws://localhost:19069/), so no Warudo
/// mod is needed: messages are <c>{"action":..,"data":{..}}</c>, errors <c>{"action":..,"error":".."}</c>. "trigger",
/// "toggle" and "message" fire the "On Stream Deck Trigger / Toggle / Message" blueprint nodes whose Receiver Name
/// matches; "getReceivers" lists those names. Warudo only opens the port while the opened scene has one of those
/// nodes. Text for the "On WebSocket Message Received" node goes to Warudo's own WebSocket port (19190) instead.
/// </summary>
public sealed class WarudoService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly Lock _gate = new();
    private readonly List<PendingRequest> _pending = [];
    private readonly List<(string Receiver, TaskCompletionSource<bool> Done)> _toggleWaits = [];
    private readonly ConcurrentDictionary<string, bool> _toggles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly WarudoWebSocketSender _webSocket;
    private readonly Func<string, int, CancellationToken, Task<bool>> _probe;
    private volatile IReadOnlyDictionary<WarudoReceiverKind, IReadOnlyList<string>> _receivers = Empty();
    private long _lastRefreshTicks;

    public WarudoService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal WarudoService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings,
        Func<IWebSocketTransport>? webSocketTransports = null, Func<string, int, CancellationToken, Task<bool>>? probe = null)
        : base("Warudo", bus, log, transports, timings)
    {
        _settings = settings;
        _webSocket = new WarudoWebSocketSender(webSocketTransports ?? transports, timings);
        _probe = probe ?? ProbeAsync;
    }

    public WarudoSettings Settings => _settings.Value.Warudo;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "localhost";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 19069;

    /// <summary>Warudo's own WebSocket port (On WebSocket Message Received).</summary>
    public int WebSocketPort => Settings.WebSocketPort is > 0 and < 65536 ? Settings.WebSocketPort : 19190;

    /// <summary>Receiver names in the opened scene, per node kind (cached on connect and by <see cref="RefreshAsync"/>).</summary>
    public IReadOnlyList<string> Receivers(WarudoReceiverKind kind) => _receivers.TryGetValue(kind, out var names) ? names : [];

    /// <summary>Toggle states Warudo reported.</summary>
    public IReadOnlyDictionary<string, bool> Toggles => _toggles;

    /// <summary>Raised (on a background thread) when the receiver lists or a toggle changed.</summary>
    public event Action? DataChanged;

    /// <summary>Receiver suggestions older than this are re-read (in the background) when an editor asks for them.</summary>
    internal TimeSpan SuggestionsMaxAge { get; set; } = TimeSpan.FromSeconds(10);

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private string HostPart => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    private protected override (Uri Url, string Display) Endpoint() => (new Uri($"ws://{HostPart}:{Port}/"), $"Warudo at {Host}:{Port}");

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["warudo.host"] = Host,
        ["warudo.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args) => PublishEvent($"{WarudoEvents.Prefix}.{name}", args);

    private void PublishEvent(string type, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    private static Dictionary<WarudoReceiverKind, IReadOnlyList<string>> Empty() => new()
    {
        [WarudoReceiverKind.Trigger] = [],
        [WarudoReceiverKind.Toggle] = [],
        [WarudoReceiverKind.Message] = [],
    };

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        try
        {
            MarkConnected($"Connected to Warudo at {Host}:{Port}");
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
            {
                Log.Warn(Name, $"Could not read the receiver names: {ex.Message}");
            }
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session over.
            }
        }
    }

    private protected override void OnSessionEnded()
    {
        _receivers = Empty();
        _toggles.Clear();
        _warned.Clear();
        Interlocked.Exchange(ref _lastRefreshTicks, 0);
        DataChanged?.Invoke();
    }

    /// <summary>
    /// Warudo's Stream Deck port is closed while the open scene has no On Stream Deck node, which looks exactly like
    /// Warudo not running. Its own WebSocket port is always open, so an answer there tells the two apart.
    /// </summary>
    private protected override async Task<string> ExplainUnreachableAsync(string reason, CancellationToken ct)
    {
        try
        {
            if (await _probe(Host, WebSocketPort, ct).ConfigureAwait(false))
                return "Warudo is running, but the open scene has no On Stream Deck node. Add an On Stream Deck Trigger, " +
                       "Toggle or Message node to a blueprint, then press Connect";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No answer: Warudo is not running (or not on this computer).
        }
        return reason;
    }

    private async Task<bool> ProbeAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(1000, Timings.ConnectTimeout.TotalMilliseconds)));
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            await client.ConnectAsync(host.Trim('[', ']'), port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                var data = message.TryGetProperty("data", out var d) ? d.Clone() : default;
                var action = message.Str("action");
                var error = message.Str("error");
                switch (action)
                {
                    case "getReceivers" or "getToggles":
                        if (action == "getToggles" && error.Length == 0 && data.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in data.EnumerateObject())
                                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) _toggles[p.Name] = p.Value.GetBoolean();
                            DataChanged?.Invoke();
                        }
                        // Answers carry no request id: each one belongs to the oldest request of the same action, error or not.
                        if (!Answer(action, data, error) && error.Length > 0) Log.Warn(Name, $"Warudo reported an error ({action}): {error}");
                        break;
                    case "toggle" when error.Length == 0 && data.ValueKind == JsonValueKind.Object:
                        OnToggle(data);
                        break;
                    default:
                        if (error.Length > 0) Log.Warn(Name, $"Warudo reported an error{(action.Length > 0 ? $" ({action})" : "")}: {error}");
                        break;
                }
            }
        }
        finally
        {
            List<PendingRequest> pending;
            List<(string Receiver, TaskCompletionSource<bool> Done)> waits;
            lock (_gate)
            {
                pending = [.. _pending];
                _pending.Clear();
                waits = [.. _toggleWaits];
                _toggleWaits.Clear();
            }
            foreach (var p in pending) p.Done.TrySetException(new StreamAppNotConnectedException(Name));
            foreach (var w in waits) w.Done.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    /// <summary>Completes the oldest request for <paramref name="action"/>; false when none was waiting.</summary>
    private bool Answer(string action, JsonElement data, string error)
    {
        PendingRequest? request;
        lock (_gate)
        {
            var index = _pending.FindIndex(p => p.Action == action);
            if (index < 0) return false;
            request = _pending[index];
            _pending.RemoveAt(index);
        }
        if (request.AbandonedAt is not null)
        {
            // The late answer to a request that already timed out: it is used up here, so it cannot answer a later one.
            if (error.Length > 0) Log.Warn(Name, $"Warudo reported an error ({action}): {error}");
            return true;
        }
        if (error.Length > 0) request.Done.TrySetException(new StreamAppRequestException($"Warudo refused {action}: {error}"));
        else request.Done.TrySetResult(data);
        return true;
    }

    private void OnToggle(JsonElement data)
    {
        var receiver = data.Str("receiverName");
        if (receiver.Length == 0) return;
        if (!data.TryGetProperty("state", out var s) || s.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Log.Warn(Name, $"Ignored a toggle report for '{receiver}' without an on/off state.");
            return;
        }
        var state = s.GetBoolean();
        _toggles[receiver] = state;
        List<TaskCompletionSource<bool>> done;
        lock (_gate)
        {
            done = [.. _toggleWaits.Where(w => w.Receiver == receiver).Select(w => w.Done)];
            _toggleWaits.RemoveAll(w => w.Receiver == receiver);
        }
        foreach (var d in done) d.TrySetResult(state);
        DataChanged?.Invoke();
        PublishEvent(WarudoEvents.ToggleChanged, new() { ["receiver"] = receiver, ["state"] = state ? "On" : "Off", ["value"] = state });
        // A toggle Cuebot has not listed: a node was added (or the scene changed) since the last refresh.
        if (!Receivers(WarudoReceiverKind.Toggle).Contains(receiver, StringComparer.Ordinal)) RefreshInBackground();
    }

    private void UpdateStatus()
    {
        if (!IsConnected) return;
        SetStatus(ConnectionState.Connected,
            $"Connected to Warudo at {Host}:{Port}: {Receivers(WarudoReceiverKind.Trigger).Count} trigger, " +
            $"{Receivers(WarudoReceiverKind.Toggle).Count} toggle and {Receivers(WarudoReceiverKind.Message).Count} message receivers");
        DataChanged?.Invoke();
    }

    // ---- Requests ----

    private static string Message(string action, object? data) => JsonUtil.Serialize(new { action, data });

    /// <summary>Reloads the receiver names of the opened scene, and the toggle states.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _lastRefreshTicks, Environment.TickCount64);
            var lists = Empty();
            foreach (var kind in lists.Keys.ToArray())
            {
                var names = await RequestAsync("getReceivers", new { type = kind.ToString().ToLowerInvariant() }, ct).ConfigureAwait(false);
                lists[kind] = names.ValueKind == JsonValueKind.Array
                    ? [.. names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).Order(StringComparer.OrdinalIgnoreCase)]
                    : [];
            }
            _receivers = lists;
            await RequestAsync("getToggles", null, ct).ConfigureAwait(false);
            UpdateStatus();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Starts a refresh unless one is running; failures are only logged.</summary>
    private void RefreshInBackground()
    {
        if (!IsConnected || _refreshGate.CurrentCount == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(Name, $"Could not re-read the receiver names: {ex.Message}");
            }
        });
    }

    /// <summary>Receiver names for an editor's suggestions; re-read in the background when older than <see cref="SuggestionsMaxAge"/>.</summary>
    public IReadOnlyList<string> ReceiverSuggestions(WarudoReceiverKind kind)
    {
        var last = Interlocked.Read(ref _lastRefreshTicks);
        if (IsConnected && Environment.TickCount64 - last >= SuggestionsMaxAge.TotalMilliseconds) RefreshInBackground();
        return Receivers(kind);
    }

    /// <summary>
    /// Sends a request and waits for its answer. Warudo answers in order and its answers carry no request id, so an
    /// answer belongs to the oldest open request of its action. A request that times out keeps its place, so its late
    /// answer is used up there instead of answering the next request; one that never gets an answer is dropped after
    /// a while (logged).
    /// </summary>
    private async Task<JsonElement> RequestAsync(string action, object? data, CancellationToken ct)
    {
        EnsureConnected();
        var request = new PendingRequest(action);
        lock (_gate)
        {
            DropForgotten();
            _pending.Add(request);
        }
        try
        {
            await SendAsync(Message(action, data), ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _pending.Remove(request);
            throw;
        }
        try
        {
            return await request.Done.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            lock (_gate) request.AbandonedAt ??= Environment.TickCount64;
            if (ex is TimeoutException) throw new TimeoutException($"Warudo did not answer {action} in time.");
            throw;
        }
    }

    /// <summary>Drops timed-out requests whose answer never came (under <see cref="_gate"/>).</summary>
    private void DropForgotten()
    {
        var limit = Environment.TickCount64 - (long)(Timings.RequestTimeout.TotalMilliseconds * 4);
        var removed = _pending.RemoveAll(p => p.AbandonedAt is { } at && at < limit);
        if (removed > 0) Log.Warn(Name, $"Warudo never answered {removed} earlier request(s); no longer waiting for them.");
    }

    /// <summary>Warns (once per name and session) when the opened scene has no such receiver; it is still sent.</summary>
    private void CheckReceiver(WarudoReceiverKind kind, string name)
    {
        if (!IsConnected || Interlocked.Read(ref _lastRefreshTicks) == 0) return;
        var known = Receivers(kind);
        if (known.Contains(name, StringComparer.Ordinal) || !_warned.TryAdd($"{kind}\n{name}", 0)) return;
        var close = known.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        Log.Warn(Name, $"No On Stream Deck {kind} node named '{name}' in the open Warudo scene" +
                       (close is null ? "" : $" (did you mean '{close}'? Capital letters matter)") +
                       ". Sent anyway; press Reload receivers after adding the node.");
    }

    private async Task SendToReceiverAsync(WarudoReceiverKind kind, string receiver, string? message, CancellationToken ct)
    {
        var name = Steps.Require(receiver, "Receiver name");
        var action = kind.ToString().ToLowerInvariant();
        await SendAsync(Message(action, message is null ? new { receiverName = name } : new { receiverName = name, message }), ct).ConfigureAwait(false);
        CheckReceiver(kind, name);
    }

    /// <summary>Fires every "On Stream Deck Trigger" node with this receiver name.</summary>
    public Task TriggerAsync(string receiver, CancellationToken ct = default) => SendToReceiverAsync(WarudoReceiverKind.Trigger, receiver, null, ct);

    /// <summary>Flips every "On Stream Deck Toggle" node with this receiver name (Warudo then reports the new state).</summary>
    public Task ToggleAsync(string receiver, CancellationToken ct = default) => SendToReceiverAsync(WarudoReceiverKind.Toggle, receiver, null, ct);

    /// <summary>
    /// Turns a toggle on or off. Warudo can only flip a toggle, so for On/Off the state is read first when Cuebot does
    /// not know it, and the toggle is flipped only when it differs (or is still unknown). Returns the state Warudo
    /// reported after the flip, or null when it did not report one in time.
    /// </summary>
    public async Task<bool?> SetToggleAsync(string receiver, SwitchMode mode, CancellationToken ct = default)
    {
        var name = Steps.Require(receiver, "Receiver name");
        bool? known = _toggles.TryGetValue(name, out var current) ? current : null;
        if (mode != SwitchMode.Toggle && known is null)
        {
            known = await ReadToggleAsync(name, ct).ConfigureAwait(false);
        }
        if (mode != SwitchMode.Toggle && known is { } now && Steps.Desired(mode, now) == now) return now;

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _toggleWaits.Add((name, done));
        try
        {
            await ToggleAsync(name, ct).ConfigureAwait(false);
            return await done.Task.WaitAsync(ToggleReportTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn(Name, $"Warudo did not report the new state of toggle '{name}'. Is there an On Stream Deck Toggle node with that Receiver Name?");
            return null;
        }
        finally
        {
            lock (_gate) _toggleWaits.Remove((name, done));
        }
    }

    private TimeSpan ToggleReportTimeout => Timings.RequestTimeout < TimeSpan.FromSeconds(2) ? Timings.RequestTimeout : TimeSpan.FromSeconds(2);

    /// <summary>A toggle's state as Warudo reports it now (asks Warudo; falls back to the last known state).</summary>
    public async Task<bool?> GetToggleAsync(string receiver, CancellationToken ct = default)
    {
        var name = Steps.Require(receiver, "Receiver name");
        return await ReadToggleAsync(name, ct).ConfigureAwait(false) ?? (_toggles.TryGetValue(name, out var s) ? s : null);
    }

    private async Task<bool?> ReadToggleAsync(string name, CancellationToken ct)
    {
        try
        {
            var states = await RequestAsync("getToggles", null, ct).ConfigureAwait(false);
            return states.ValueKind == JsonValueKind.Object && states.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;
        }
        catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
        {
            Log.Warn(Name, $"Could not read the state of toggle '{name}': {ex.Message}");
            return _toggles.TryGetValue(name, out var s) ? s : null;
        }
    }

    /// <summary>Sends text to every "On Stream Deck Message" node with this receiver name.</summary>
    public Task SendMessageAsync(string receiver, string message, CancellationToken ct = default) =>
        SendToReceiverAsync(WarudoReceiverKind.Message, receiver, message, ct);

    // ---- Warudo's own WebSocket port ----

    /// <summary>The address of Warudo's own WebSocket endpoint.</summary>
    public Uri WebSocketUrl => new($"ws://{HostPart}:{WebSocketPort}/");

    /// <summary>
    /// Sends text to Warudo's own WebSocket port: every "On WebSocket Message Received" node starts with it as its
    /// message. Works without a Stream Deck node and without the Stream Deck connection.
    /// </summary>
    public async Task SendWebSocketMessageAsync(string text, CancellationToken ct = default)
    {
        if (text.Length == 0) throw new ArgumentException("Message is required.");
        await _webSocket.SendAsync(WebSocketUrl, text, ct).ConfigureAwait(false);
    }

    /// <summary>True while the connection to Warudo's WebSocket port is open (it opens on the first message).</summary>
    internal bool WebSocketOpen => _webSocket.IsOpen;

    private protected override ValueTask DisposeMoreAsync() => _webSocket.DisposeAsync();

    private sealed class PendingRequest(string action)
    {
        public string Action { get; } = action;
        public TaskCompletionSource<JsonElement> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When the request timed out (Environment.TickCount64); its answer is then thrown away.</summary>
        public long? AbandonedAt { get; set; }
    }
}
