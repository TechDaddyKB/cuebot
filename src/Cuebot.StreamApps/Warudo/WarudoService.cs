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
/// Warudo through the WebSocket service its built-in Stream Deck plugin uses (ws://127.0.0.1:19069/), so no Warudo
/// mod is needed: messages are <c>{"action":..,"data":{..}}</c>. "trigger", "toggle" and "message" fire the
/// "On Stream Deck Trigger / Toggle / Message" blueprint nodes whose Receiver Name matches; "getReceivers" lists those
/// names. Warudo only opens the port once the opened scene has one of those nodes.
/// </summary>
public sealed class WarudoService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly ConcurrentQueue<TaskCompletionSource<JsonElement>> _receiverRequests = new();
    private readonly ConcurrentQueue<TaskCompletionSource<JsonElement>> _toggleRequests = new();
    private readonly ConcurrentDictionary<string, bool> _toggles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile IReadOnlyDictionary<WarudoReceiverKind, IReadOnlyList<string>> _receivers = Empty();

    public WarudoService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal WarudoService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("Warudo", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public WarudoSettings Settings => _settings.Value.Warudo;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "localhost";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 19069;

    /// <summary>Receiver names in the opened scene, per node kind (cached on connect and by <see cref="RefreshAsync"/>).</summary>
    public IReadOnlyList<string> Receivers(WarudoReceiverKind kind) => _receivers.TryGetValue(kind, out var names) ? names : [];

    /// <summary>Toggle states Warudo reported.</summary>
    public IReadOnlyDictionary<string, bool> Toggles => _toggles;

    /// <summary>Raised (on a background thread) when the receiver lists or a toggle changed.</summary>
    public event Action? DataChanged;

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
        DataChanged?.Invoke();
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                var data = message.TryGetProperty("data", out var d) ? d.Clone() : default;
                switch (message.Str("action"))
                {
                    case "getReceivers":
                        // Warudo's answer does not say which kind it lists; answers come back in the order asked.
                        if (_receiverRequests.TryDequeue(out var tcs)) tcs.TrySetResult(data);
                        break;
                    case "getToggles" when data.ValueKind == JsonValueKind.Object:
                        foreach (var p in data.EnumerateObject())
                            if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) _toggles[p.Name] = p.Value.GetBoolean();
                        if (_toggleRequests.TryDequeue(out var toggles)) toggles.TrySetResult(data);
                        DataChanged?.Invoke();
                        break;
                    case "toggle" when data.ValueKind == JsonValueKind.Object:
                        OnToggle(data.Str("receiverName"), data.Bool("state") ?? false);
                        break;
                    default:
                        if (message.Str("error") is { Length: > 0 } error) Log.Warn(Name, $"Warudo reported an error: {error}");
                        break;
                }
            }
        }
        finally
        {
            foreach (var queue in new[] { _receiverRequests, _toggleRequests })
                while (queue.TryDequeue(out var tcs)) tcs.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    private void OnToggle(string receiver, bool state)
    {
        if (receiver.Length == 0) return;
        _toggles[receiver] = state;
        DataChanged?.Invoke();
        PublishEvent(WarudoEvents.ToggleChanged, new() { ["receiver"] = receiver, ["state"] = state ? "On" : "Off", ["value"] = state });
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
            var lists = Empty();
            foreach (var kind in lists.Keys.ToArray())
            {
                var names = await RequestAsync(_receiverRequests, "getReceivers", new { type = kind.ToString().ToLowerInvariant() }, ct).ConfigureAwait(false);
                lists[kind] = names.ValueKind == JsonValueKind.Array
                    ? [.. names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).Order(StringComparer.OrdinalIgnoreCase)]
                    : [];
            }
            _receivers = lists;
            await RequestAsync(_toggleRequests, "getToggles", null, ct).ConfigureAwait(false);
            UpdateStatus();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Sends a request and waits for its answer (Warudo answers in order; answers carry no request id).</summary>
    private async Task<JsonElement> RequestAsync(ConcurrentQueue<TaskCompletionSource<JsonElement>> queue, string action, object? data, CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(tcs);
        await SendAsync(Message(action, data), ct).ConfigureAwait(false);
        try
        {
            return await tcs.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Warudo did not answer in time.");
        }
    }

    private Task SendToReceiverAsync(string action, string receiver, string? message, CancellationToken ct)
    {
        var name = Steps.Require(receiver, "Receiver name");
        return SendAsync(Message(action, message is null ? new { receiverName = name } : new { receiverName = name, message }), ct);
    }

    /// <summary>Fires every "On Stream Deck Trigger" node with this receiver name.</summary>
    public Task TriggerAsync(string receiver, CancellationToken ct = default) => SendToReceiverAsync("trigger", receiver, null, ct);

    /// <summary>Flips every "On Stream Deck Toggle" node with this receiver name (Warudo then reports the new state).</summary>
    public Task ToggleAsync(string receiver, CancellationToken ct = default) => SendToReceiverAsync("toggle", receiver, null, ct);

    /// <summary>
    /// Turns a toggle on or off: Warudo only flips, so this flips only when the state it reported differs (an unknown
    /// state is flipped). Returns the state asked for, when known.
    /// </summary>
    public async Task<bool?> SetToggleAsync(string receiver, SwitchMode mode, CancellationToken ct = default)
    {
        var name = Steps.Require(receiver, "Receiver name");
        bool? known = _toggles.TryGetValue(name, out var current) ? current : null;
        if (mode != SwitchMode.Toggle && known is { } now && Steps.Desired(mode, now) == now) return now;
        await ToggleAsync(name, ct).ConfigureAwait(false);
        return mode switch
        {
            SwitchMode.On => true,
            SwitchMode.Off => false,
            _ => known is { } k ? !k : null,
        };
    }

    /// <summary>Sends text to every "On Stream Deck Message" node with this receiver name.</summary>
    public Task SendMessageAsync(string receiver, string message, CancellationToken ct = default) =>
        SendToReceiverAsync("message", receiver, message, ct);
}
