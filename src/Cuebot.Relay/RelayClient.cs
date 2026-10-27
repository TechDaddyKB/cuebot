using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Relay;

/// <summary>Timeouts and delays of <see cref="RelayClient"/> (tests use short ones).</summary>
public sealed record RelayTimings(
    TimeSpan ConnectTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan PingInterval,
    TimeSpan SilenceTimeout,
    TimeSpan HandlerTimeout)
{
    public static RelayTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(15),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(60),
        PingInterval: TimeSpan.FromSeconds(30),
        SilenceTimeout: TimeSpan.FromSeconds(95),
        HandlerTimeout: TimeSpan.FromSeconds(30));
}

/// <summary>One delivery as shown on the Webhooks page (no headers or body: those may hold personal data).</summary>
public sealed record RelayDeliveryRecord(
    string Id,
    string Service,
    string Method,
    string Path,
    DateTimeOffset ReceivedAt,
    DateTimeOffset HandledAt,
    WebhookOutcome Outcome,
    string Detail);

/// <summary>Bot events published by the relay client.</summary>
public static class RelayEvents
{
    /// <summary>After each delivery: service, path, method, outcome (<see cref="WebhookOutcome"/> name), deliveryId, receivedAt.</summary>
    public const string Delivery = "relay.delivery";

    /// <summary>The relay connection is up (args: service = "Webhook relay").</summary>
    public const string Connected = "relay.connected";

    /// <summary>The relay connection went down (args: reason).</summary>
    public const string Disconnected = "relay.disconnected";
}

/// <summary>
/// Cuebot's side of the self-hosted Cloudflare relay (relay/README.md): keeps an outbound websocket to
/// <c>wss://&lt;relay&gt;/connect</c> (bearer connect token), reconnecting with backoff; dispatches each forwarded
/// webhook to the <see cref="IWebhookHandler"/> whose <see cref="IWebhookHandler.Service"/> matches, one at a time in
/// arrival order; acknowledges it afterwards whatever the outcome (so the relay drops it from its buffer); drops
/// replays of ids already handled; publishes <see cref="RelayEvents.Delivery"/> and keeps a short history for the UI.
/// </summary>
public sealed class RelayClient : IConnectionStatus, IAsyncDisposable
{
    /// <summary>Close code the relay uses when a newer connection replaced this one.</summary>
    public const int ReplacedCloseCode = 4000;

    /// <summary>How many deliveries <see cref="RecentDeliveries"/> keeps.</summary>
    public const int RecentCapacity = 100;

    private const int SeenCapacity = 5000;
    private const string LogSource = "Relay";

    private readonly JsonFile<RelaySettings> _settings;
    private readonly Dictionary<string, IWebhookHandler> _byService = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<IRelayTransport> _transportFactory;
    private readonly RelayTimings _timings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _gate = new();
    private readonly LinkedList<RelayDeliveryRecord> _recent = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private bool _sessionConnected;

    public RelayClient(JsonFile<RelaySettings> settings, IEnumerable<IWebhookHandler> handlers, EventBus bus, AppLog log)
        : this(settings, handlers, bus, log, () => new ClientWebSocketRelayTransport(), RelayTimings.Default)
    {
    }

    internal RelayClient(JsonFile<RelaySettings> settings, IEnumerable<IWebhookHandler> handlers, EventBus bus, AppLog log,
        Func<IRelayTransport> transportFactory, RelayTimings timings)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transportFactory = transportFactory;
        _timings = timings;
        foreach (var handler in handlers)
        {
            if (!_byService.TryAdd(handler.Service, handler))
                log.Warn(LogSource, $"Two webhook handlers use the route \"{handler.Service}\"; using {_byService[handler.Service].DisplayName}");
        }
        Handlers = _byService.Values.OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string Name => "Webhook relay";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (on a background thread) when <see cref="RecentDeliveries"/> changed.</summary>
    public event Action? DeliveriesChanged;

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>True while connecting, connected or waiting to reconnect.</summary>
    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    public RelaySettings Settings => _settings.Value;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Every registered handler (one per service route), by display name.</summary>
    public IReadOnlyList<IWebhookHandler> Handlers { get; }

    /// <summary>The latest deliveries, newest first.</summary>
    public IReadOnlyList<RelayDeliveryRecord> RecentDeliveries
    {
        get
        {
            lock (_gate) return _recent.ToArray();
        }
    }

    /// <summary>
    /// The URL to paste into a service's webhook settings: <c>&lt;Url&gt;/hook/&lt;RouteSecret&gt;/&lt;service&gt;</c>,
    /// or null while the relay URL or route secret is missing.
    /// </summary>
    public string? WebhookUrl(string service)
    {
        var s = Settings;
        if (BaseUri(s.Url) is not { } baseUri || string.IsNullOrWhiteSpace(s.RouteSecret)) return null;
        return $"{baseUri.GetLeftPart(UriPartial.Authority)}{baseUri.AbsolutePath.TrimEnd('/')}/hook/{Uri.EscapeDataString(s.RouteSecret.Trim())}/{service}";
    }

    /// <summary>The websocket address for a relay URL (https → wss, http → ws, path + /connect), or null when invalid.</summary>
    public static Uri? ConnectUri(string? relayUrl)
    {
        if (BaseUri(relayUrl) is not { } baseUri) return null;
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/connect",
            Query = "",
            Fragment = "",
        };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    private static Uri? BaseUri(string? relayUrl)
    {
        var text = relayUrl?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme switch
        {
            "https" or "wss" => new UriBuilder(uri) { Scheme = "https", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri,
            "http" or "ws" => new UriBuilder(uri) { Scheme = "http", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri,
            _ => null,
        };
    }

    /// <summary>What is missing to connect, or null when the settings are complete.</summary>
    public string? MissingSettings()
    {
        var s = Settings;
        if (string.IsNullOrWhiteSpace(s.Url)) return "Enter the relay URL (printed by relay/setup)";
        if (ConnectUri(s.Url) is null) return "The relay URL is not a valid https:// address";
        if (string.IsNullOrWhiteSpace(s.ConnectToken)) return "Enter the relay connect token (printed by relay/setup)";
        return null;
    }

    /// <summary>Starts connecting in the background (replacing a running connection). Throws when settings are missing.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (MissingSettings() is { } missing)
        {
            SetStatus(ConnectionState.Error, missing);
            throw new InvalidOperationException(missing);
        }
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Connecting to the relay...");
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects when the relay is enabled, auto-connect is on and the settings are complete. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var s = Settings;
        if (!s.Enabled || !s.AutoConnect || MissingSettings() is not null) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(LogSource, $"Automatic connection failed: {Redact(ex.Message)}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StopAsync()
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Error while disconnecting: {Redact(ex.Message)}");
        }
        run.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (reason, fatal, hadConnected) = await RunOnceAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (fatal)
                {
                    _log.Warn(LogSource, reason);
                    SetStatus(ConnectionState.Error, reason);
                    return;
                }
                if (hadConnected)
                {
                    delay = _timings.ReconnectDelay;
                    _log.Warn(LogSource, $"Connection lost: {reason}");
                }
                SetStatus(ConnectionState.Connecting, $"Reconnecting in {Seconds(delay)} ({reason})");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Connection loop stopped: {Redact(ex.Message)}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    /// <summary>One connection attempt and, when it opens, one session until it ends.</summary>
    private async Task<(string Reason, bool Fatal, bool HadConnected)> RunOnceAsync(CancellationToken ct)
    {
        var settings = Settings;
        var uri = ConnectUri(settings.Url);
        if (uri is null) return ("The relay URL is not a valid https:// address", true, false);
        var transport = _transportFactory();
        var reason = "";
        var fatal = false;
        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_timings.ConnectTimeout);
                await transport.ConnectAsync(uri, settings.ConnectToken.Trim(), timeout.Token).ConfigureAwait(false);
            }
            catch (RelayAuthException ex)
            {
                return (ex.Message, true, false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return ($"relay not reachable: {(ex is OperationCanceledException ? "timed out" : Describe(ex))}", false, false);
            }

            MarkConnected("Connected to the relay");
            try
            {
                reason = await RunSessionAsync(transport, ct).ConfigureAwait(false);
                if (transport.CloseCode == ReplacedCloseCode)
                {
                    reason = "Another Cuebot connected to this relay and took over; click Connect to take it back";
                    fatal = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                reason = "disconnected";
            }
            catch (Exception ex)
            {
                reason = Describe(ex);
            }
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        reason = Redact(reason);
        var hadConnected = _sessionConnected;
        if (hadConnected)
        {
            _sessionConnected = false;
            _bus.Publish(new BotEvent(RelayEvents.Disconnected, new Dictionary<string, object?> { ["service"] = Name, ["reason"] = reason }));
        }
        return (reason, fatal, hadConnected);
    }

    private void MarkConnected(string message)
    {
        SetStatus(ConnectionState.Connected, message);
        if (_sessionConnected) return;
        _sessionConnected = true;
        _log.Info(LogSource, $"Connected to the webhook relay ({BaseUri(Settings.Url)?.Host})");
        _bus.Publish(new BotEvent(RelayEvents.Connected, new Dictionary<string, object?> { ["service"] = Name }));
    }

    /// <summary>Reads until the relay closes; deliveries go to a single dispatcher so they are handled in order.</summary>
    private async Task<string> RunSessionAsync(IRelayTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = Channel.CreateUnbounded<WebhookDelivery>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var lastHeard = Environment.TickCount64;
        var silent = false;
        var dispatcher = Task.Run(() => DispatchLoopAsync(transport, queue.Reader, session.Token), CancellationToken.None);
        var heartbeat = Task.Run(async () =>
        {
            while (!session.Token.IsCancellationRequested)
            {
                await Task.Delay(_timings.PingInterval, session.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref lastHeard) > (long)_timings.SilenceTimeout.TotalMilliseconds)
                {
                    silent = true;
                    await session.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await transport.SendAsync("""{"type":"ping"}""", session.Token).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        try
        {
            while (true)
            {
                string? text;
                try
                {
                    text = await transport.ReceiveAsync(session.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (silent && !ct.IsCancellationRequested)
                {
                    return "the relay stopped answering";
                }
                if (text is null)
                {
                    var code = transport.CloseCode is { } c ? $" ({c}{(string.IsNullOrEmpty(transport.CloseReason) ? "" : " " + transport.CloseReason)})" : "";
                    return $"the relay closed the connection{code}";
                }
                Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
                HandleMessage(text, queue.Writer);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            await session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quiet(dispatcher), Quiet(heartbeat)).ConfigureAwait(false);
        }

        static async Task Quiet(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* the session is over */ }
        }
    }

    private void HandleMessage(string text, ChannelWriter<WebhookDelivery> deliveries)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            _log.Warn(LogSource, "Ignored a relay message that is not JSON");
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "hello":
                    var buffered = root.TryGetProperty("buffered", out var b) && b.TryGetInt32(out var n) ? n : 0;
                    SetStatus(ConnectionState.Connected, buffered > 0
                        ? $"Connected to the relay ({buffered} delivery(s) waiting were replayed)"
                        : "Connected to the relay");
                    break;
                case "delivery":
                    if (TryParseDelivery(root, out var delivery, out var error)) deliveries.TryWrite(delivery);
                    else _log.Warn(LogSource, $"Ignored a malformed delivery from the relay: {error}");
                    break;
                case "pong":
                    break;
                default:
                    _log.Debug(LogSource, $"Ignored relay message type \"{type}\"");
                    break;
            }
        }
    }

    internal static bool TryParseDelivery(JsonElement root, out WebhookDelivery delivery, out string error)
    {
        delivery = null!;
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var id = Str("id");
        if (id.Length == 0)
        {
            error = "no id";
            return false;
        }
        byte[] body;
        try
        {
            body = Convert.FromBase64String(Str("bodyBase64"));
        }
        catch (FormatException)
        {
            error = "body is not base64";
            return false;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in h.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) headers[p.Name.ToLowerInvariant()] = p.Value.GetString() ?? "";
        }
        var receivedAt = DateTimeOffset.TryParse(Str("receivedAt"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow;
        var method = Str("method");
        delivery = new WebhookDelivery(id, Str("service").ToLowerInvariant(), Str("path"), method.Length == 0 ? "POST" : method.ToUpperInvariant(),
            Str("query"), headers, body, receivedAt);
        error = "";
        return true;
    }

    private async Task DispatchLoopAsync(IRelayTransport transport, ChannelReader<WebhookDelivery> reader, CancellationToken ct)
    {
        await foreach (var delivery in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (IsSeen(delivery.Id))
            {
                await AckAsync(transport, delivery.Id, ct).ConfigureAwait(false);
                continue;
            }
            var (outcome, detail) = await DispatchAsync(delivery, ct).ConfigureAwait(false);
            MarkSeen(delivery.Id);
            Record(delivery, outcome, detail);
            await AckAsync(transport, delivery.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the matching handler; throws only when the session is being cancelled (the delivery is then replayed).</summary>
    internal async Task<(WebhookOutcome Outcome, string Detail)> DispatchAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        if (!_byService.TryGetValue(delivery.Service, out var handler))
        {
            _log.Warn(LogSource, $"Webhook for unknown service \"{delivery.Service}\" dropped (no handler for that route)");
            return (WebhookOutcome.NotConfigured, $"No handler for \"{delivery.Service}\"");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timings.HandlerTimeout);
        try
        {
            var outcome = await handler.HandleAsync(delivery, timeout.Token).ConfigureAwait(false);
            if (outcome == WebhookOutcome.Rejected)
                _log.Warn(LogSource, $"{handler.DisplayName} webhook rejected: signature or token did not match");
            else if (outcome == WebhookOutcome.NotConfigured)
                _log.Warn(LogSource, $"{handler.DisplayName} webhook received but {handler.DisplayName} is not set up in Cuebot");
            return (outcome, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _log.Warn(LogSource, $"{handler.DisplayName} webhook handler timed out");
            return (WebhookOutcome.Failed, "Timed out");
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"{handler.DisplayName} webhook handler failed", ex);
            return (WebhookOutcome.Failed, Redact(ex.Message));
        }
    }

    private async Task AckAsync(IRelayTransport transport, string id, CancellationToken ct)
    {
        try
        {
            await transport.SendAsync("{\"type\":\"ack\",\"id\":\"" + JsonEncodedText.Encode(id) + "\"}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The connection dropped: the relay replays the delivery on reconnect and the id check skips it.
            _log.Debug(LogSource, $"Could not acknowledge delivery {id}: {Redact(ex.Message)}");
        }
    }

    private bool IsSeen(string id)
    {
        lock (_gate) return _seen.Contains(id);
    }

    private void MarkSeen(string id)
    {
        lock (_gate)
        {
            if (!_seen.Add(id)) return;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > SeenCapacity) _seen.Remove(_seenOrder.Dequeue());
        }
    }

    private void Record(WebhookDelivery delivery, WebhookOutcome outcome, string detail)
    {
        var record = new RelayDeliveryRecord(delivery.Id, delivery.Service, delivery.Method, delivery.Path, delivery.ReceivedAt,
            DateTimeOffset.Now, outcome, detail);
        lock (_gate)
        {
            _recent.AddFirst(record);
            while (_recent.Count > RecentCapacity) _recent.RemoveLast();
        }
        DeliveriesChanged?.Invoke();
        _bus.Publish(new BotEvent(RelayEvents.Delivery, new Dictionary<string, object?>
        {
            ["service"] = delivery.Service,
            ["path"] = delivery.Path,
            ["method"] = delivery.Method,
            ["outcome"] = outcome.ToString(),
            ["deliveryId"] = delivery.Id,
            ["receivedAt"] = delivery.ReceivedAt,
        }));
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = Redact(message);
        StatusChanged?.Invoke();
    }

    /// <summary>Removes the connect token and route secret from text that may be logged or shown.</summary>
    internal string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var secret in new[] { Settings.ConnectToken, Settings.RouteSecret })
        {
            var s = secret?.Trim();
            if (string.IsNullOrEmpty(s) || s.Length < 4) continue;
            text = text.Replace(s, "***", StringComparison.Ordinal).Replace(Uri.EscapeDataString(s), "***", StringComparison.Ordinal);
        }
        return text;
    }

    private string Describe(Exception ex) => Redact(ex is System.Net.WebSockets.WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message);

    private static string Seconds(TimeSpan delay) => delay.TotalSeconds >= 1 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMilliseconds:0} ms";
}
