using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Tits;

/// <summary>Bot event types published by <see cref="TitsService"/>.</summary>
public static class TitsEvents
{
    public const string Prefix = "tits";

    /// <summary>Both sockets are up.</summary>
    public const string Connected = "tits.connected";

    /// <summary>Was fully connected and one socket dropped (Cuebot extra).</summary>
    public const string Disconnected = "tits.disconnected";

    public const string WebsocketConnected = "tits.websocketConnected";
    public const string WebsocketDisconnected = "tits.websocketDisconnected";
    public const string EventsConnected = "tits.eventsConnected";
    public const string EventsDisconnected = "tits.eventsDisconnected";
    public const string ItemHit = "tits.itemHit";
    public const string TriggerActivated = "tits.triggerActivated";
    public const string TriggerEnded = "tits.triggerEnded";
}

/// <summary>
/// T.I.T.S. through its public API: the request socket (ws://host:42069/websocket) is the main connection (status,
/// reconnect); while it is up a second socket on /events receives hit/trigger broadcasts and reconnects on its own.
/// Item and trigger lists are cached on connect so actions can use names.
/// </summary>
public sealed class TitsService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly Func<IWebSocketTransport> _transports;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private volatile IReadOnlyList<TitsEntry> _items = [];
    private volatile IReadOnlyList<TitsEntry> _triggers = [];
    private volatile bool _eventsConnected;
    private volatile bool _fullyConnected;
    private long _nextId;

    public TitsService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal TitsService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("T.I.T.S.", bus, log, transports, timings)
    {
        _settings = settings;
        _transports = transports;
    }

    public TitsSettings Settings => _settings.Value.Tits;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 42069;

    /// <summary>Items in the model importer (cached on connect and by <see cref="RefreshAsync"/>).</summary>
    public IReadOnlyList<TitsEntry> Items => _items;

    /// <summary>Triggers set up in T.I.T.S. (cached on connect and by <see cref="RefreshAsync"/>).</summary>
    public IReadOnlyList<TitsEntry> Triggers => _triggers;

    /// <summary>True while the /events socket is connected.</summary>
    public bool EventsConnected => _eventsConnected;

    /// <summary>Raised (on a background thread) when the item/trigger lists or the events socket changed.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private string HostPart => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    private protected override (Uri Url, string Display) Endpoint() =>
        (new Uri($"ws://{HostPart}:{Port}{TitsProtocol.DataPath}"), $"{Host}:{Port}");

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["tits.host"] = Host,
        ["tits.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args)
    {
        // The base class reports the request socket as "connected"/"disconnected".
        var type = name switch
        {
            "connected" => TitsEvents.WebsocketConnected,
            "disconnected" => TitsEvents.WebsocketDisconnected,
            _ => $"{TitsEvents.Prefix}.{name}",
        };
        PublishEvent(type, args);
        if (name == "disconnected") UpdateFullyConnected(false);
    }

    private void PublishEvent(string type, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    private void UpdateFullyConnected(bool requestSocketUp)
    {
        var now = requestSocketUp && _eventsConnected;
        if (now == _fullyConnected) return;
        _fullyConnected = now;
        PublishEvent(now ? TitsEvents.Connected : TitsEvents.Disconnected, []);
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        Task events = Task.CompletedTask;
        try
        {
            // The upgrade succeeding is the whole handshake: the API has no authentication.
            MarkConnected($"Connected to T.I.T.S. at {Host}:{Port}");
            events = EventsLoopAsync(session.Token);
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
            {
                Log.Warn(Name, $"Could not read the item and trigger lists: {ex.Message}");
            }
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { pump, events })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Session over.
                }
            }
        }
    }

    private protected override void OnSessionEnded()
    {
        _items = [];
        _triggers = [];
        DataChanged?.Invoke();
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                if (TitsProtocol.RequestId(message) is { Length: > 0 } id && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(message);
                else if (!_eventsConnected) OnEvent(message);   // older builds may broadcast on the request socket
            }
        }
        finally
        {
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    private async Task EventsLoopAsync(CancellationToken ct)
    {
        var url = new Uri($"ws://{HostPart}:{Port}{TitsProtocol.EventsPath}");
        var delay = Timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            var transport = _transports();
            var opened = false;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(Timings.ConnectTimeout);
                    await transport.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
                }
                opened = true;
                delay = Timings.ReconnectDelay;
                _eventsConnected = true;
                PublishEvent(TitsEvents.EventsConnected, []);
                UpdateFullyConnected(true);
                RefreshStatus();
                while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
                    if (JsonUtil.TryParse(text) is { ValueKind: JsonValueKind.Object } message)
                        OnEvent(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Session over.
            }
            catch (Exception ex)
            {
                if (!opened) Log.Debug(Name, $"Events socket not reachable: {ex.Message}");
                else Log.Warn(Name, $"Events socket error: {ex.Message}");
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            if (opened)
            {
                _eventsConnected = false;
                PublishEvent(TitsEvents.EventsDisconnected, new() { ["reason"] = transport.CloseReason ?? "closed" });
                UpdateFullyConnected(!ct.IsCancellationRequested);
                if (!ct.IsCancellationRequested) RefreshStatus();
            }
            if (ct.IsCancellationRequested) return;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Timings.MaxReconnectDelay.Ticks));
        }
    }

    private void RefreshStatus()
    {
        if (IsConnected)
            SetStatus(ConnectionState.Connected,
                $"Connected to T.I.T.S. at {Host}:{Port} ({(_eventsConnected ? "events connected" : "events socket not connected yet")}; {_items.Count} items, {_triggers.Count} triggers)");
        DataChanged?.Invoke();
    }

    private void OnEvent(JsonElement message)
    {
        var type = TitsProtocol.MessageType(message) switch
        {
            TitsProtocol.HitEvent => TitsEvents.ItemHit,
            TitsProtocol.TriggerActivatedEvent => TitsEvents.TriggerActivated,
            TitsProtocol.TriggerEndedEvent => TitsEvents.TriggerEnded,
            _ => null,
        };
        if (type is null) return;
        try
        {
            PublishEvent(type, TitsProtocol.EventArgs(message));
        }
        catch (Exception ex)
        {
            Log.Error(Name, "Could not process a T.I.T.S. event", ex);
        }
    }

    // ---- Requests ----

    /// <summary>Sends a request on /websocket and returns the whole response; error responses throw.</summary>
    internal async Task<JsonElement> RequestAsync(string messageType, object? data, CancellationToken ct)
    {
        EnsureConnected();
        var id = "cuebot-" + Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        JsonElement response;
        try
        {
            await SendAsync(TitsProtocol.Request(messageType, id, data), ct).ConfigureAwait(false);
            response = await tcs.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("T.I.T.S. did not answer in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
        if (TitsProtocol.IsError(response)) throw new StreamAppRequestException($"T.I.T.S. refused the request: {TitsProtocol.ErrorText(response)}");
        return response;
    }

    /// <summary>Reloads the item and trigger lists.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _items = TitsProtocol.Entries(await RequestAsync(TitsProtocol.ItemList, TitsProtocol.ItemListData(), ct).ConfigureAwait(false), "items");
        _triggers = TitsProtocol.Entries(await RequestAsync(TitsProtocol.TriggerList, null, ct).ConfigureAwait(false), "triggers");
        RefreshStatus();
    }

    public async Task<IReadOnlyList<TitsEntry>> GetItemsAsync(CancellationToken ct = default)
    {
        _items = TitsProtocol.Entries(await RequestAsync(TitsProtocol.ItemList, TitsProtocol.ItemListData(), ct).ConfigureAwait(false), "items");
        DataChanged?.Invoke();
        return _items;
    }

    public async Task<IReadOnlyList<TitsEntry>> GetTriggersAsync(CancellationToken ct = default)
    {
        _triggers = TitsProtocol.Entries(await RequestAsync(TitsProtocol.TriggerList, null, ct).ConfigureAwait(false), "triggers");
        DataChanged?.Invoke();
        return _triggers;
    }

    /// <summary>Throws <paramref name="amount"/> items, each picked at random from <paramref name="itemIds"/>. Returns how many T.I.T.S. threw.</summary>
    public async Task<int> ThrowItemsAsync(IReadOnlyList<string> itemIds, double delaySeconds = 0.05, int amount = 1, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) throw new ArgumentException("At least one item is required.");
        if (amount < 1) throw new ArgumentException("Amount must be at least 1.");
        if (delaySeconds < 0) throw new ArgumentException("Delay cannot be negative.");
        var response = await RequestAsync(TitsProtocol.ThrowItems, TitsProtocol.ThrowData(itemIds, delaySeconds, amount, true), ct).ConfigureAwait(false);
        return (int)(TitsProtocol.Data(response).Num("numberOfThrownItems") ?? amount);
    }

    /// <summary>Throws items given by name (or id); unknown names fail before anything is thrown.</summary>
    public async Task<int> ThrowItemsByNameAsync(IReadOnlyList<string> names, double delaySeconds = 0.05, int amount = 1, CancellationToken ct = default) =>
        await ThrowItemsAsync(await ResolveItemsAsync(names, ct).ConfigureAwait(false), delaySeconds, amount, ct).ConfigureAwait(false);

    /// <summary>Item ids for names or ids (case-insensitive), reloading the list once when one is unknown.</summary>
    public async Task<IReadOnlyList<string>> ResolveItemsAsync(IReadOnlyList<string> namesOrIds, CancellationToken ct = default)
    {
        if (namesOrIds.Count == 0) throw new ArgumentException("At least one item is required.");
        if (namesOrIds.Any(n => Find(_items, n) is null)) await GetItemsAsync(ct).ConfigureAwait(false);
        var items = _items;
        return namesOrIds.Select(n => Find(items, n)?.Id ?? throw new StreamAppRequestException($"T.I.T.S. has no item '{n}'.")).ToArray();
    }

    /// <summary>Activates a trigger by id or name (T.I.T.S. falls back to the name when the id is unknown).</summary>
    public async Task ActivateTriggerAsync(string idOrName, CancellationToken ct = default)
    {
        var wanted = Steps.Require(idOrName, "Trigger");
        var known = Find(_triggers, wanted);
        await RequestAsync(TitsProtocol.TriggerActivate,
            TitsProtocol.ActivateData(known?.Id ?? "", known?.Name ?? wanted), ct).ConfigureAwait(false);
    }

    /// <summary>Activates a trigger by its name only.</summary>
    public Task ActivateTriggerByNameAsync(string name, CancellationToken ct = default) =>
        RequestAsync(TitsProtocol.TriggerActivate, TitsProtocol.ActivateData("", Steps.Require(name, "Trigger name")), ct);

    private static TitsEntry? Find(IReadOnlyList<TitsEntry> entries, string nameOrId) =>
        entries.FirstOrDefault(e => string.Equals(e.Id, nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? entries.FirstOrDefault(e => string.Equals(e.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
}
