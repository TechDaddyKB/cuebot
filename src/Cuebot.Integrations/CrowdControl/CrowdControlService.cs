using System.Collections.Concurrent;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.CrowdControl;

/// <summary>The game session Cuebot last saw start (null once it ended or before one started).</summary>
public sealed record CrowdControlSession(string GameSessionId, string GameName, string GamePackName, DateTimeOffset StartedAt)
{
    public bool Paused { get; init; }
}

/// <summary>A timed effect that is running or paused.</summary>
public sealed record CrowdControlTimedEffect(string RequestId, string EffectName, string User, string Status, long TimeRemainingMs, DateTimeOffset UpdatedAt);

/// <summary>
/// Crowd Control over its PubSub websocket (wss://pubsub.crowdcontrol.live), authenticated with the overlay key from the
/// user's Overlay URL, exactly as Crowd Control's own overlay does: subscribes to the public, private and overlay topics of
/// the key's owner, pings every 8 minutes and reconnects whenever the socket closes (the service ends sockets after 2 h).
/// </summary>
public sealed class CrowdControlService : IntegrationConnection
{
    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly Uri _pubSub;
    private readonly ConcurrentDictionary<string, CrowdControlTimedEffect> _timed = new(StringComparer.Ordinal);
    private readonly RecentKeys _seen = new();

    public CrowdControlService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), IntegrationsTimings.Default, CrowdControlProtocol.DefaultPubSub)
    {
    }

    internal CrowdControlService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        IntegrationsTimings timings, Uri pubSub) : base("Crowd Control", bus, log, transports, timings)
    {
        _settings = settings;
        _pubSub = pubSub;
    }

    public CrowdControlSettings Settings => _settings.Value.CrowdControl;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Kinds accepted by <see cref="SendTestEvent"/>.</summary>
    public IReadOnlyList<string> TestEventKinds => [.. CrowdControlProtocol.Samples.Keys];

    /// <summary>The current game session, from the start/stop events seen since Cuebot connected.</summary>
    public CrowdControlSession? Session { get; private set; }

    /// <summary>Timed effects currently running or paused.</summary>
    public IReadOnlyList<CrowdControlTimedEffect> TimedEffects => [.. _timed.Values.OrderBy(t => t.UpdatedAt)];

    /// <summary>Short description of the newest event, or "".</summary>
    public string LastEvent { get; private set; } = "";

    /// <summary>Raised after an event was published or the session state changed.</summary>
    public event Action? DataChanged;

    protected override string EventPrefix => CrowdControlEvents.Prefix;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override IEnumerable<string?> SecretValues => [Credential?.Value, Settings.OverlayUrl.Trim()];

    /// <summary>The saved overlay key (or token) masked, or what is wrong with the saved URL.</summary>
    public string MaskedSetting => CrowdControlProtocol.ParseCredential(Settings.OverlayUrl) switch
    {
        { Credential: { } c } => $"Saved {(c.IsToken ? "token" : "overlay key")}: {Secrets.Mask(c.Value)}",
        _ when Settings.OverlayUrl.Trim().Length == 0 => "No Overlay URL saved",
        var (_, error) => error ?? "",
    };

    private CrowdControlCredential? Credential =>CrowdControlProtocol.ParseCredential(Settings.OverlayUrl).Credential;

    protected override string? MissingSettings() => CrowdControlProtocol.ParseCredential(Settings.OverlayUrl).Error;

    private protected override IReadOnlyList<IntegrationEndpoint> Endpoints() => [new IntegrationEndpoint(_pubSub, _pubSub.Host)];

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        var credential = Credential ?? throw new IntegrationAuthException(MissingSettings() ?? "No overlay key");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var subscribed = new TaskCompletionSource<(string[] Success, string[] Failure)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastReceived = DateTime.UtcNow;

        var reader = ReadAsync();
        await transport.SendAsync(CrowdControlProtocol.Subscribe(credential), ct).ConfigureAwait(false);
        var first = await Task.WhenAny(subscribed.Task, reader, Task.Delay(Timings.RequestTimeout, ct)).ConfigureAwait(false);
        if (first != subscribed.Task)
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Swallow(reader).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(transport.CloseReason ?? "Crowd Control did not answer the subscription");
        }
        var (success, failure) = subscribed.Task.Result;
        if (!success.Contains(CrowdControlProtocol.PrivateTopic, StringComparer.Ordinal))
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Swallow(reader).ConfigureAwait(false);
            throw new IntegrationAuthException(
                "Crowd Control refused the overlay key. Copy your Overlay URL again from the Crowd Control app (Overlays & Assets) and click Connect.");
        }
        if (failure.Length > 0) Log.Warn(Name, $"Some topics were refused: {string.Join(", ", failure)}");
        MarkConnected("Connected to Crowd Control; waiting for effects");

        var keepAlive = KeepAliveAsync();
        try
        {
            var ended = await Task.WhenAny(reader, keepAlive).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
            await ended.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Swallow(reader).ConfigureAwait(false);
            await Swallow(keepAlive).ConfigureAwait(false);
        }

        async Task ReadAsync()
        {
            while (await transport.ReceiveAsync(session.Token).ConfigureAwait(false) is { } text)
            {
                lastReceived = DateTime.UtcNow;
                if (text == CrowdControlProtocol.Pong) continue;
                if (CrowdControlProtocol.Parse(text) is not { } message) continue;
                if (message is { Domain: "direct", Type: "subscription-result" })
                    subscribed.TrySetResult(CrowdControlProtocol.SubscriptionResult(message.Payload));
                else
                    Handle(message);
            }
        }

        async Task KeepAliveAsync()
        {
            var interval = Timings.PubSubPingInterval;
            while (true)
            {
                await Task.Delay(interval, session.Token).ConfigureAwait(false);
                var sent = DateTime.UtcNow;
                await transport.SendAsync(CrowdControlProtocol.Ping, session.Token).ConfigureAwait(false);
                // The documented rule: reconnect when no pong arrives within 15 s.
                await Task.Delay(Timings.RequestTimeout, session.Token).ConfigureAwait(false);
                if (lastReceived < sent) throw new TimeoutException("Crowd Control stopped answering pings");
            }
        }
    }

    private protected override void OnSessionEnded() => _timed.Clear();

    /// <summary>Maps and publishes one PubSub message; returns whether an event was published.</summary>
    internal bool Handle(CrowdControlMessage message, bool isTest = false)
    {
        if (CrowdControlProtocol.Map(message, isTest) is not { } e) return false;
        // A message delivered twice (the same request, transaction or session event) is published once.
        if (!isTest && DedupeKey(e) is { } key && !_seen.Add(key)) return false;
        Track(e);
        Publish(e.Name, e.Args);
        LastEvent = CrowdControlFeedAdapter.Describe(new BotEvent($"{EventPrefix}.{e.Name}", e.Args))?.Title ?? Describe(e);
        DataChanged?.Invoke();
        return true;
    }

    private static string? DedupeKey(CrowdControlEvent e)
    {
        string Text(string key) => e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
        var id = Text("requestId") is { Length: > 0 } r ? r : Text("transactionId") is { Length: > 0 } t ? t : Text("gameSessionId");
        return id.Length == 0 ? null : $"{e.Name}|{id}|{Text("timedStatus")}|{Text("timeRemaining")}|{Text("paused")}|{Text("pausedUntil")}|{Text("timestamp")}";
    }

    private static string Describe(CrowdControlEvent e) => e.Name switch
    {
        "gameSessionStart" => $"Game session started: {e.Args["gameName"]}",
        "gameSessionEnd" => "Game session ended",
        "effectRequest" => $"{e.Args["user"]} requested {e.Args["effectName"]}",
        _ => $"{e.Name}: {e.Args.GetValueOrDefault("effectName")}",
    };

    private void Track(CrowdControlEvent e)
    {
        var a = e.Args;
        string Text(string key) => a.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        switch (e.Name)
        {
            case "gameSessionStart":
                Session = new CrowdControlSession(Text("gameSessionId"), Text("gameName"), Text("gamePackName"), DateTimeOffset.UtcNow);
                _timed.Clear();
                break;
            case "gameSessionEnd":
                Session = null;
                _timed.Clear();
                break;
            case "gameSessionPaused" when Session is { } s:
                Session = s with { Paused = a["paused"] is true };
                break;
            case "timedEffectStarted" or "timedEffectUpdated":
                _timed[Text("requestId")] = new CrowdControlTimedEffect(Text("requestId"), Text("effectName"), Text("user"), Text("timedStatus"),
                    a["timeRemaining"] is long ms ? ms : 0, DateTimeOffset.UtcNow);
                break;
            case "timedEffectEnded" or "effectRefund" or "effectFailure":
                _timed.TryRemove(Text("requestId"), out _);
                break;
        }
    }

    /// <summary>
    /// Publishes a sample event of <paramref name="kind"/> locally (never sent to Crowd Control), marked <c>isTest</c>.
    /// Returns false for an unknown kind.
    /// </summary>
    public bool SendTestEvent(string kind) =>
        CrowdControlProtocol.Samples.TryGetValue(kind.Trim(), out var sample)
        && CrowdControlProtocol.Parse(sample) is { } message
        && Handle(message, isTest: true);

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already reported or the session is over.
        }
    }

    /// <summary>The last few hundred message keys.</summary>
    private sealed class RecentKeys(int capacity = 500)
    {
        private readonly HashSet<string> _set = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private readonly object _gate = new();

        public bool Add(string key)
        {
            lock (_gate)
            {
                if (!_set.Add(key)) return false;
                _order.Enqueue(key);
                while (_order.Count > capacity) _set.Remove(_order.Dequeue());
                return true;
            }
        }
    }
}
