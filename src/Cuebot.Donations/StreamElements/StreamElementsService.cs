using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Core.Storage;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.StreamElements;

/// <summary>
/// The StreamElements connection: real-time activities (tips, merch, store redemptions and, optionally, relayed
/// platform events), tip moderation, stream status, session data, overlay, chatbot, contest, giveaway, song request and
/// (optionally) chat topics over the Astro gateway, or the deprecated realtime Socket.IO endpoint; plus the REST API.
/// Authenticated with the channel's JWT (or, for events only, its Overlay Token).
/// </summary>
public sealed partial class StreamElementsService : DonationServiceBase, IStreamElementsOverlaySource
{
    public static readonly Uri AstroEndpoint = new("wss://astro.streamelements.com");
    public static readonly Uri RealtimeEndpoint = new("https://realtime.streamelements.com");
    public const string ActivitiesTopic = StreamElementsTopics.Activities;

    /// <summary>Legacy sessions that end before authenticating, in a row, before Cuebot switches to Astro.</summary>
    internal const int LegacyFailuresBeforeFallback = 3;

    /// <summary>Activity types fetched after a reconnect that was not a resume (the ones Cuebot publishes itself).</summary>
    private static readonly string[] BackfillTypes = ["tip", "merch", "redemption"];

    private readonly Func<IWebSocketTransport> _transports;
    private readonly Uri _astro;
    private readonly Uri _realtime;
    private readonly DonationsTimings _timings;
    private readonly StreamElementsApi _api;
    private readonly GlobalVariables? _globals;
    private readonly StreamElementsTipGate _tips;
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private readonly LinkedList<string> _deletedChat = new();
    private readonly HashSet<string> _deletedChatSet = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastActivityAt;
    private DateTimeOffset? _sessionStartedAt;
    private bool? _isLive;

    public StreamElementsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, HttpClient http, GlobalVariables? globals = null)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), AstroEndpoint, RealtimeEndpoint,
            DonationsTimings.Default, new StreamElementsApi(http), globals)
    {
    }

    internal StreamElementsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Uri astro, Uri realtime, DonationsTimings timings, StreamElementsApi api, GlobalVariables? globals = null) : base(settings, bus, log)
    {
        _transports = transports;
        _astro = astro;
        _realtime = realtime;
        _timings = timings;
        _api = api;
        _globals = globals;
        _tips = new StreamElementsTipGate(e => Publish(e), timings.TipStateTimeout, log);
    }

    public override string Name => StreamElementsEventMapper.Service;
    public StreamElementsSettings Settings => SettingsFile.Value.StreamElements;

    /// <summary>The JWT, or the Overlay Token when no JWT is set (events only).</summary>
    protected override string Token => Settings.JwtToken.Trim().Length > 0 ? Settings.JwtToken : Settings.OverlayToken;

    private bool UsesOverlayToken => Settings.JwtToken.Trim().Length == 0 && Settings.OverlayToken.Trim().Length > 0;

    protected override string MissingTokenMessage => "Enter your StreamElements JWT token (or Overlay Token) first.";
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => Settings.RelayPlatformEvents;
    protected override string ConnectedEvent => StreamElementsEventTypes.Connected;
    protected override string DisconnectedEvent => StreamElementsEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => [.. StreamElementsEventMapper.Samples.Keys];

    private protected override string RedactSecrets(string? text) =>
        Secrets.Redact(Secrets.Redact(text, Settings.JwtToken.Trim()), Settings.OverlayToken.Trim());

    /// <summary>Whether StreamElements says the channel is live (null until the first stream status message).</summary>
    public bool? IsLive => _isLive;

    /// <summary>"twitch" → "Twitch", for showing which platform account a token belongs to.</summary>
    public static string PlatformName(string provider) => StreamElementsEventMapper.Platform(provider);

    /// <summary>The canonical room (channel id) Astro confirmed for the last subscription.</summary>
    public string Room { get; private set; } = "";

    /// <summary>Topics subscribed in the current Astro session.</summary>
    public IReadOnlyCollection<string> SubscribedTopics
    {
        get { lock (_stateGate) return _subscribed.ToArray(); }
    }

    /// <summary>Raised when a StreamElements overlay changed (id, name), e.g. to offer importing it again.</summary>
    public event Action<string, string>? OverlayUpdated;

    public override bool SendTestEvent(string kind)
    {
        if (!StreamElementsEventMapper.Samples.TryGetValue(kind, out var sample)) return false;
        using var doc = JsonDocument.Parse(sample);
        return StreamElementsEventMapper.Map(doc.RootElement, isTest: true) is { } e && Publish(e);
    }

    /// <summary>Handles one activity (also used by tests and the reconnect back-fill).</summary>
    /// <summary>
    /// Handles one activity; <paramref name="backfill"/> marks one fetched after a reconnect (<c>isBackfill</c>), so
    /// triggers can leave it out and overlay alert boxes skip it (event lists, goals and totals still count it).
    /// </summary>
    internal bool HandleActivity(JsonElement activity, bool backfill = false)
    {
        if (StreamElementsEventMapper.Map(activity) is not { } e) return false;
        if (backfill && e.Args.ContainsKey("isBackfill")) e.Args["isBackfill"] = true;
        if (activity.Str("createdAt") is { Length: > 0 } created
            && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            lock (_stateGate)
                if (_lastActivityAt is null || at > _lastActivityAt) _lastActivityAt = at;
        }
        if (e.Type == StreamElementsEventTypes.Tip)
        {
            _tips.WaitForApproval = Settings.WaitForTipApproval;
            _tips.OnActivityTip(e);
            return true;
        }
        return Publish(e);
    }

    /// <summary>Handles one legacy <c>event:test</c> (the dashboard's emulated alerts), published as a test.</summary>
    internal bool HandleWidgetTest(JsonElement test) => StreamElementsEventMapper.MapWidgetTest(test) is { } e && Publish(e);

    /// <summary>Handles one Astro topic message (also used by tests).</summary>
    internal void HandleTopicMessage(string topic, JsonElement data)
    {
        switch (topic)
        {
            case StreamElementsTopics.Activities:
                HandleActivity(data);
                break;
            case StreamElementsTopics.Tips or StreamElementsTopics.TipsModeration:
                _tips.WaitForApproval = Settings.WaitForTipApproval;
                _tips.OnTipDocument(data);
                break;
            case StreamElementsTopics.StreamStatus:
                HandleStreamStatus(data);
                break;
            case StreamElementsTopics.SessionUpdate:
                HandleSessionUpdate(data);
                break;
            case StreamElementsTopics.SessionReset:
                ApplySession(data);
                PublishQuiet(new DonationEvent(StreamElementsEventTypes.SessionReset, [], Args()));
                break;
            case StreamElementsTopics.Chat:
                HandleChat(data);
                break;
            default:
                if (StreamElementsTopicMapper.Map(topic, data) is not { } e) break;
                if (e.Type == StreamElementsEventTypes.OverlayUpdated)
                    OverlayUpdated?.Invoke(StreamElementsTopicMapper.Text(e.Args["overlayId"]), StreamElementsTopicMapper.Text(e.Args["overlayName"]));
                PublishQuiet(e);
                break;
        }
    }

    /// <summary>Publishes without adding a line to the page's "last events" list (chatty, non-money events).</summary>
    private void PublishQuiet(DonationEvent e) => Bus.Publish(new BotEvent(e.Type, e.Args));

    private static Dictionary<string, object?> Args() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["service"] = StreamElementsEventMapper.Service,
        ["isTest"] = false,
    };

    // ---- stream status (channel.stream.status) ----

    private void HandleStreamStatus(JsonElement data)
    {
        if (data.Child("isLive") is not { ValueKind: JsonValueKind.True or JsonValueKind.False } flag) return;
        var live = flag.ValueKind == JsonValueKind.True;
        bool changed;
        lock (_stateGate)
        {
            changed = _isLive != live;
            _isLive = live;
        }
        _globals?.SetValue("seIsLive", live, false);
        if (!changed) return;
        var args = Args();
        args["isLive"] = live;
        PublishQuiet(new DonationEvent(live ? StreamElementsEventTypes.StreamOnline : StreamElementsEventTypes.StreamOffline, [], args));
    }

    // ---- session data (channel.session.update / reset, GET sessions) ----

    private void HandleSessionUpdate(JsonElement data)
    {
        var key = data.First("key", "name");
        if (key.Length == 0) return;
        var value = data.Child("data") ?? default;
        if (value.ValueKind == JsonValueKind.Object) SetSessionGlobals(key, value);
        var args = Args();
        args["key"] = key;
        args["variable"] = SessionVariable(key, "");
        args["provider"] = data.Str("provider");
        if (value.ValueKind == JsonValueKind.Object) StreamElementsTopicMapper.Flatten(value, "", args);
        args["service"] = StreamElementsEventMapper.Service;
        args["isTest"] = false;
        PublishQuiet(new DonationEvent(StreamElementsEventTypes.SessionUpdated, [], args));
    }

    /// <summary>A whole session object (<c>{"tip-session": {...}, ...}</c>, or a GET response with it under <c>data</c>).</summary>
    internal void ApplySession(JsonElement session)
    {
        if (session.Child("data") is { ValueKind: JsonValueKind.Object } inner && !session.Has("tip-session") && !session.Has("follower-latest"))
            session = inner;
        if (session.ValueKind != JsonValueKind.Object) return;
        foreach (var property in session.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && property.Name.Contains('-', StringComparison.Ordinal))
                SetSessionGlobals(property.Name, property.Value);
        }
    }

    private void SetSessionGlobals(string key, JsonElement value)
    {
        if (_globals is null) return;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        StreamElementsTopicMapper.Flatten(value, "", fields);
        foreach (var (field, v) in fields)
        {
            if (field == "activityId") continue;
            _globals.SetValue(SessionVariable(key, field), v is string or bool or long or double ? v : StreamElementsTopicMapper.Text(v), false);
        }
    }

    /// <summary><c>tip-session</c> + <c>amount</c> → <c>se.tipSession.amount</c> (global names allow no dashes).</summary>
    public static string SessionVariable(string key, string field)
    {
        var parts = key.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(parts.Select((p, i) => i == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
        var cleanField = new string(field.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.').ToArray());
        return cleanField.Length > 0 ? $"se.{name}.{cleanField}" : $"se.{name}";
    }

    // ---- unified chat (channel.chat) ----

    private void HandleChat(JsonElement data)
    {
        var args = Args();
        var provider = data.Str("provider");
        args["provider"] = provider;
        args["platform"] = StreamElementsEventMapper.Platform(provider);
        args["sentAt"] = data.Str("sent_at");
        switch (data.Str("event"))
        {
            case "message":
            {
                var id = data.Str("id");
                lock (_stateGate)
                    if (id.Length > 0 && _deletedChatSet.Contains(id)) return;   // deleted before it arrived
                var author = data.Child("author") ?? default;
                var roles = author.Items("roles").Select(r => r.ToString()).ToArray();
                args["messageId"] = id;
                args["message"] = data.Str("text");
                args["user"] = author.First("name", "login");
                args["userName"] = author.First("login", "name");
                args["userId"] = author.Str("id");
                args["color"] = author.Str("color");
                args["avatar"] = author.Str("avatar_url");
                args["roles"] = string.Join(", ", roles);
                args["isModerator"] = roles.Contains("moderator", StringComparer.OrdinalIgnoreCase);
                args["isSubscriber"] = roles.Contains("subscriber", StringComparer.OrdinalIgnoreCase);
                args["isBroadcaster"] = roles.Contains("broadcaster", StringComparer.OrdinalIgnoreCase);
                args["kind"] = data.Str("kind");
                args["bits"] = (int)data.Items("fragments").Where(f => f.Str("type") == "cheer").Sum(f => f.Child("cheer")?.Number("bits") ?? 0);
                args["isReply"] = data.Child("reply") is { ValueKind: JsonValueKind.Object };
                args["isSharedChat"] = data.Child("shared_from") is { ValueKind: JsonValueKind.Object };
                PublishQuiet(new DonationEvent(StreamElementsEventTypes.ChatMessage, [], args));
                break;
            }
            case "message_deleted":
            {
                var id = data.Str("message_id");
                lock (_stateGate)
                {
                    if (id.Length > 0 && _deletedChatSet.Add(id))
                    {
                        _deletedChat.AddLast(id);
                        while (_deletedChat.Count > 300)
                        {
                            _deletedChatSet.Remove(_deletedChat.First!.Value);
                            _deletedChat.RemoveFirst();
                        }
                    }
                }
                args["messageId"] = id;
                PublishQuiet(new DonationEvent(StreamElementsEventTypes.ChatMessageDeleted, [], args));
                break;
            }
            case "user_cleared":
            {
                var user = data.Child("user") ?? default;
                args["user"] = user.First("name", "login");
                args["userName"] = user.First("login", "name");
                args["userId"] = user.Str("id");
                args["duration"] = (int)data.Number("duration_seconds");
                args["isTimeout"] = data.Has("duration_seconds");
                PublishQuiet(new DonationEvent(StreamElementsEventTypes.ChatUserCleared, [], args));
                break;
            }
            case "chat_cleared":
                PublishQuiet(new DonationEvent(StreamElementsEventTypes.ChatCleared, [], args));
                break;
        }
    }

    // ---- connection ----

    /// <summary>Validates the token through the REST API; a network failure is only logged (the socket retries).</summary>
    private protected override async Task PrepareAsync(CancellationToken ct)
    {
        lock (_stateGate)
        {
            _subscribed.Clear();
            _isLive = null;
        }
        Room = "";
        if (UsesOverlayToken)
        {
            Log.Info(Name, "Using the Overlay Token: events only (points, store and bot steps need the JWT)");
            return;
        }
        try
        {
            ResetChannel();
            await GetChannelIdAsync(ct).ConfigureAwait(false);
        }
        catch (StreamElementsApiException ex) when (ex.Status is 401 or 403)
        {
            throw new InvalidOperationException(
                "StreamElements rejected the JWT token. Copy it again: avatar (top right) → your channel name → pick the channel → copy the JWT.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StreamElementsApiException)
        {
            Log.Warn(Name, $"Could not look up the channel: {RedactSecrets(ex.Message)}");
        }
        _ = LoadSessionAsync(ct);
    }

    /// <summary>Fills the <c>se.*</c> session globals once on connect (best effort).</summary>
    private async Task LoadSessionAsync(CancellationToken ct)
    {
        if (_globals is null || Settings.ChannelId.Length == 0) return;
        try
        {
            ApplySession(await _api.GetSessionAsync(Settings.JwtToken, Settings.ChannelId, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StreamElementsApiException or JsonException)
        {
            Log.Warn(Name, $"Could not read the session data: {RedactSecrets(ex.Message)}");
        }
    }

    private protected override async Task RunAsync(CancellationToken ct)
    {
        if (Settings.Transport != StreamElementsTransport.LegacyRealtime || UsesOverlayToken)
        {
            await RunAstroAsync(ct).ConfigureAwait(false);
            return;
        }
        Log.Warn(Name, "The legacy realtime socket is deprecated (StreamElements' docs no longer describe it) and may stop working");
        if (await RunRealtimeAsync(ct).ConfigureAwait(false))
        {
            Log.Warn(Name, $"The legacy realtime socket failed {LegacyFailuresBeforeFallback} times in a row; switching to Astro");
            await RunAstroAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunAstroAsync(CancellationToken ct)
    {
        var token = Token.Trim();
        var topics = StreamElementsTopics.For(Settings.ReceiveChat);
        var client = new AstroClient(_transports, _astro, _timings, Log)
        {
            Room = UsesOverlayToken ? "" : Settings.ChannelId,
            TokenType = UsesOverlayToken ? "apikey" : "jwt",
        };
        _tips.DocumentsExpected = false;
        var firstSession = true;
        client.Connected += () =>
        {
            lock (_stateGate) _subscribed.Clear();
            _tips.DocumentsExpected = false;
            SetStatus(ConnectionState.Connecting, "Subscribing to StreamElements topics...");
        };
        client.Welcomed += resumed =>
        {
            if (!firstSession && !resumed) _ = BackfillAsync(ct);
            firstSession = false;
        };
        client.Subscribed += (topic, room) =>
        {
            lock (_stateGate) _subscribed.Add(topic);
            if (room.Length > 0) Room = room;
            if (topic is StreamElementsTopics.Tips or StreamElementsTopics.TipsModeration) _tips.DocumentsExpected = true;
            if (topic == ActivitiesTopic)
            {
                lock (_stateGate) _sessionStartedAt ??= DateTimeOffset.UtcNow;
                OnConnected($"Connected to StreamElements{ChannelSuffix()} (Astro{(UsesOverlayToken ? ", Overlay Token" : "")})");
            }
        };
        client.SubscriptionFailed += (topic, error, description) =>
        {
            if (topic == ActivitiesTopic) SetStatus(ConnectionState.Error, $"Could not subscribe to {topic}: {error} {description}".TrimEnd());
            else if (topic is StreamElementsTopics.Tips) _tips.ReleaseUnknown();
        };
        client.Disconnected += reason => OnDisconnected(reason, fatal: reason.Contains("rejected", StringComparison.Ordinal));
        client.MessageReceived += HandleTopicMessage;
        await client.RunAsync(token, topics, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After a reconnect that was not a resume: publishes tips, merch and redemptions missed while offline, marked
    /// <c>isBackfill</c> (already-seen activity ids are dropped by the duplicate check).
    /// </summary>
    private async Task BackfillAsync(CancellationToken ct)
    {
        DateTimeOffset since;
        lock (_stateGate)
        {
            if ((_lastActivityAt ?? _sessionStartedAt) is not { } at) return;
            since = at;
        }
        if (UsesOverlayToken || Settings.ChannelId.Length == 0) return;
        try
        {
            var missed = await _api.GetActivitiesAsync(Settings.JwtToken, Settings.ChannelId, since, BackfillTypes, 25, ct).ConfigureAwait(false);
            var count = 0;
            foreach (var activity in missed.OrderBy(a => a.Str("createdAt"), StringComparer.Ordinal))
                if (HandleActivity(activity, backfill: true)) count++;
            if (count > 0) Log.Info(Name, $"Published {count} StreamElements activities missed while reconnecting");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StreamElementsApiException)
        {
            Log.Warn(Name, $"Could not fetch the activities missed while reconnecting: {RedactSecrets(ex.Message)}");
        }
    }

    /// <summary>Runs the legacy socket; returns true when it should give way to Astro (it kept failing).</summary>
    private async Task<bool> RunRealtimeAsync(CancellationToken ct)
    {
        var token = Settings.JwtToken.Trim();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var failures = 0;
        var authenticated = false;
        var client = new SocketIoClient(_transports, _timings, Log, Name)
        {
            EngineIoVersion = 3,
            Redact = text => Secrets.Redact(text, token),
            OnConnectedAsync = (send, _) => send(SocketIoFraming.Event("authenticate", new { method = "jwt", token })),
        };
        client.Connected += _ =>
        {
            authenticated = false;
            SetStatus(ConnectionState.Connecting, "Authenticating with StreamElements (legacy)...");
        };
        client.Disconnected += (reason, fatal) =>
        {
            failures = authenticated ? 0 : failures + 1;
            OnDisconnected(reason, fatal);
            if (failures >= LegacyFailuresBeforeFallback) stop.Cancel();
        };
        client.EventReceived += (name, args) =>
        {
            switch (name)
            {
                case "authenticated":
                    authenticated = true;
                    failures = 0;
                    OnConnected($"Connected to StreamElements{ChannelSuffix()} (legacy realtime, deprecated)");
                    break;
                case "unauthorized":
                    SetStatus(ConnectionState.Error, "StreamElements rejected the JWT token");
                    break;
                case "event" when args.Count > 0:
                    HandleActivity(args[0]);
                    break;
                case "event:test" when args.Count > 0:
                    HandleWidgetTest(args[0]);
                    break;
            }
        };
        try
        {
            await client.RunAsync(eio => SocketIoFraming.Url(_realtime, eio, new Dictionary<string, string>()), stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Gave up on the legacy socket.
        }
        return !ct.IsCancellationRequested && stop.IsCancellationRequested;
    }

    private string ChannelSuffix() => Settings.ChannelName.Length > 0
        ? $" as {Settings.ChannelName}{(Settings.ChannelProvider.Length > 0 ? $" ({StreamElementsEventMapper.Platform(Settings.ChannelProvider)})" : "")}"
        : "";
}
