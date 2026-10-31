using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Auth;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch;

/// <summary>
/// The Twitch connection: device-code sign-in of the broadcaster (and an optional bot account), the EventSub
/// WebSocket for incoming events, Helix (<see cref="Api"/>) for everything Cuebot does on Twitch, and the periodic
/// token validation Twitch requires.
/// </summary>
public sealed class TwitchService : IConnectable, IAsyncDisposable
{
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SignOutGrace = TimeSpan.FromSeconds(5);

    private readonly JsonFile<TwitchSettings> _settings;
    private readonly AppCredentials _builtInApp;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TwitchEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly EventSubTimeouts _eventSubTimeouts;
    private readonly TwitchAuthClient _auth;
    private readonly TwitchTokenManager _tokens;
    private readonly TwitchTokenManager _botTokens;
    private readonly HelixClient _helix;
    private readonly HelixClient _botHelix;
    private readonly TwitchEventRouter _router;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private ActiveConnection? _connection;
    private ActiveConnection? _botConnection;
    private Task _pendingSignOut = Task.CompletedTask;
    private bool _announcedConnected;
    private bool _announcedChat;

    public TwitchService(JsonFile<TwitchSettings> settings, HttpClient http, EventBus bus, AppLog log)
        : this(settings, http, bus, log, TwitchEndpoints.Production, TimeProvider.System, EventSubTimeouts.Default)
    {
    }

    internal TwitchService(JsonFile<TwitchSettings> settings, HttpClient http, EventBus bus, AppLog log,
        TwitchEndpoints endpoints, TimeProvider time, EventSubTimeouts eventSubTimeouts, AppCredentials? builtInApp = null)
    {
        _settings = settings;
        _builtInApp = builtInApp ?? BuiltInApps.Twitch;
        _bus = bus;
        _log = log;
        _endpoints = endpoints;
        _time = time;
        _eventSubTimeouts = eventSubTimeouts;
        _auth = new TwitchAuthClient(http, endpoints.Auth, time);
        _tokens = new TwitchTokenManager(settings, _auth, log, () => App.ClientId);
        _botTokens = new TwitchTokenManager(settings, _auth, log, () => App.ClientId, s => new BotAccount(s), "Twitch bot");
        _helix = new HelixClient(http, endpoints.Helix, _tokens);
        _botHelix = new HelixClient(http, endpoints.Helix, _botTokens);
        var sent = new SentMessageTracker();
        var counters = new TwitchCounters(() => Settings, settings.ScheduleSave);
        _router = new TwitchEventRouter(time, () => Settings.UserId, () => Settings.Bot.UserId, sent, counters: counters,
            broadcasterLogin: () => Settings.Login);
        Api = new TwitchApi(_helix, _tokens, _botHelix, _botTokens, () => Settings, () => _connection is not null, Publish, sent, counters);
    }

    public string Name => "Twitch";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";

    /// <summary>Raised when the broadcaster or bot connection state changes (from background threads).</summary>
    public event Action? StatusChanged;

    /// <summary>Every Helix operation: channel, chat, moderation, rewards, polls, predictions...</summary>
    public TwitchApi Api { get; }

    public TwitchSettings Settings => _settings.Value;

    /// <summary>The Twitch application signed in with: the user's own (Advanced) or the one built into Cuebot.</summary>
    public AppCredentials App => AppCredentials.Choose(Settings.ClientId, "", _builtInApp);

    /// <summary>True when this build has a Twitch application of its own.</summary>
    public bool HasBuiltInApp => _builtInApp.IsSet;

    /// <summary>True when a Client ID and an access token are stored.</summary>
    public bool IsAuthorized => App.IsSet && Settings.AccessToken.Length > 0;

    /// <summary>True when the bot account has a stored token.</summary>
    public bool IsBotAuthorized => App.IsSet && Settings.Bot.AccessToken.Length > 0;

    /// <summary>Login of the account while a connection is active; otherwise null.</summary>
    public string? ConnectedLogin { get; private set; }

    public ConnectionState BotState { get; private set; } = ConnectionState.Disconnected;
    public string BotStatusMessage { get; private set; } = "No bot account";

    /// <summary>Event types the broadcaster session could not subscribe to (missing scopes or refused).</summary>
    public IReadOnlyList<string> UnavailableEvents { get; private set; } = [];

    public void SaveSettings() => _settings.ScheduleSave();

    // ---------------------------------------------------------------- broadcaster sign-in

    /// <summary>Starts sign-in with the configured Client ID and requested scopes. Show the returned code to the user.</summary>
    public Task<DeviceAuthorization> BeginAuthorizationAsync(CancellationToken ct = default)
    {
        var clientId = RequireClientId();
        return _auth.StartDeviceFlowAsync(clientId, TwitchScopes.ForBroadcaster(Settings.RequestedScopes), ct);
    }

    /// <summary>Waits for the user to approve the code, stores the tokens and account, then connects.</summary>
    public async Task CompleteAuthorizationAsync(DeviceAuthorization auth, CancellationToken ct = default)
    {
        _tokens.Store(await _auth.PollDeviceTokenAsync(auth, ct).ConfigureAwait(false));
        var account = await _tokens.ValidateAsync(ct).ConfigureAwait(false);
        _log.Info("Twitch", $"Signed in as {account.Login}");
        Publish(new BotEvent(TwitchEventTypes.BroadcasterAuthenticated, AccountArgs(account.Login, account.UserId)));
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- bot sign-in

    /// <summary>Starts the bot account's sign-in. The user must log in to Twitch as the bot in the browser.</summary>
    public Task<DeviceAuthorization> BeginBotAuthorizationAsync(CancellationToken ct = default) =>
        _auth.StartDeviceFlowAsync(RequireClientId(), TwitchScopes.Bot, ct);

    /// <summary>Waits for approval, stores the bot tokens and, when the broadcaster is connected, connects the bot too.</summary>
    public async Task CompleteBotAuthorizationAsync(DeviceAuthorization auth, CancellationToken ct = default)
    {
        _botTokens.Store(await _auth.PollDeviceTokenAsync(auth, ct).ConfigureAwait(false));
        var account = await _botTokens.ValidateAsync(ct).ConfigureAwait(false);
        if (account.UserId == Settings.UserId && Settings.UserId.Length > 0)
        {
            _botTokens.Clear();
            throw new InvalidOperationException(
                "That is the broadcaster account. Log out of Twitch in the browser (or use a private window), log in as the bot and try again.");
        }
        _log.Info("Twitch", $"Bot account signed in as {account.Login}");
        SetBotStatus(ConnectionState.Disconnected, $"Signed in as {account.Login}");
        if (_connection is not null) await ConnectBotAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Forgets the bot account and revokes its token in the background.</summary>
    public Task SignOutBotAsync()
    {
        var clientId = App.ClientId;
        var token = Settings.Bot.AccessToken;
        _botTokens.Clear();
        _log.Info("Twitch", "Bot account signed out");
        return _pendingSignOut = Task.WhenAll(_pendingSignOut, SignOutBotInBackgroundAsync(clientId, token));
    }

    // ---------------------------------------------------------------- connection

    /// <summary>Validates the sign-in and starts the EventSub connection, which then reconnects on its own.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!IsAuthorized) throw new InvalidOperationException("Sign in to Twitch first.");
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopConnectionAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Checking Twitch sign-in...");
            TokenValidation account;
            try
            {
                account = await _tokens.ValidateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetStatus(ConnectionState.Error, ex.Message);
                throw;
            }

            ConnectedLogin = account.Login;
            _connection = StartConnection();
            SetStatus(ConnectionState.Connecting, "Connecting to Twitch EventSub...");
        }
        finally
        {
            _connectGate.Release();
        }
        if (IsBotAuthorized) await ConnectBotSafelyAsync(ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await StopConnectionAsync().ConfigureAwait(false))
                SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects the bot account's EventSub session (for its whispers). Throws when its sign-in is invalid.</summary>
    public async Task ConnectBotAsync(CancellationToken ct = default)
    {
        if (!IsBotAuthorized) throw new InvalidOperationException("Sign in the bot account first.");
        await StopBotConnectionAsync().ConfigureAwait(false);
        SetBotStatus(ConnectionState.Connecting, "Checking bot sign-in...");
        try
        {
            await _botTokens.ValidateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetBotStatus(ConnectionState.Error, ex.Message);
            throw;
        }
        _botConnection = StartBotConnection();
        SetBotStatus(ConnectionState.Connecting, "Connecting the bot to EventSub...");
    }

    private async Task ConnectBotSafelyAsync(CancellationToken ct)
    {
        try
        {
            await ConnectBotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Warn("Twitch", $"Bot account could not connect: {ex.Message}");
        }
    }

    /// <summary>Forgets the stored tokens and account, disconnects, and revokes the token in the background.</summary>
    public void SignOut() => _ = SignOutAsync();

    /// <summary>
    /// Like <see cref="SignOut"/>, but completes once the token has been revoked. Disposal also waits for a
    /// pending revocation, so signing out right before shutdown still revokes the token.
    /// </summary>
    public Task SignOutAsync()
    {
        var clientId = App.ClientId;
        var token = Settings.AccessToken;
        _tokens.Clear();
        _log.Info("Twitch", "Signed out");
        return _pendingSignOut = SignOutInBackgroundAsync(clientId, token);
    }

    /// <summary>
    /// Posts to the broadcaster's chat, from the bot account when it is signed in and "send chat as bot" is on.
    /// Blank messages are ignored. Throws <see cref="InvalidOperationException"/> when not connected and
    /// <see cref="TwitchApiException"/> when Twitch rejects or drops the message.
    /// </summary>
    public Task SendChatMessageAsync(string message, CancellationToken ct = default) =>
        Api.SendChatMessageAsync(message, TwitchSender.Default, null, ct);

    /// <summary>Connects automatically when signed in and auto-connect is enabled. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!IsAuthorized || !Settings.AutoConnect) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error("Twitch", "Automatic connection failed", ex);
        }
    }

    /// <summary>
    /// Finishes a pending sign-out (bounded), then disconnects. Safe to call more than once (DI disposes each
    /// registration of this singleton).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _pendingSignOut.WaitAsync(SignOutGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn("Twitch", "Shutting down before the Twitch token could be revoked");
        }
        await DisconnectAsync().ConfigureAwait(false);
    }

    private ActiveConnection StartConnection()
    {
        var cts = new CancellationTokenSource();
        var account = new EventSubAccount("Twitch", () => Settings.UserId, () => Settings.Login, () => Settings.Scopes,
            EventSubSubscriptions.All);
        TwitchEventSubListener? listener = null;
        listener = new TwitchEventSubListener(_helix, account, _router, _bus, _log, (state, message) =>
        {
            if (cts.IsCancellationRequested) return;
            if (listener is not null) UnavailableEvents = listener.Unavailable;
            SetStatus(state, message);
        }, SeedChatSettingsAsync, id => SeedBoxArtAsync(id, cts.Token));
        var eventSub = new EventSubClient(_endpoints.EventSub, listener, _log, _eventSubTimeouts);
        var poller = new TwitchPoller(Api, Settings, Publish, _log, _time);
        var run = Task.WhenAll(eventSub.RunAsync(cts.Token), ValidatePeriodicallyAsync(_tokens, cts, SetStatus), poller.RunAsync(cts.Token));
        return new ActiveConnection(cts, run);
    }

    private ActiveConnection StartBotConnection()
    {
        var cts = new CancellationTokenSource();
        var account = new EventSubAccount("Twitch bot", () => Settings.Bot.UserId, () => Settings.Bot.Login, () => Settings.Bot.Scopes,
            EventSubSubscriptions.Bot, IsBot: true);
        var listener = new TwitchEventSubListener(_botHelix, account, _router, _bus, _log, (state, message) =>
        {
            if (!cts.IsCancellationRequested) SetBotStatus(state, message);
        });
        var eventSub = new EventSubClient(_endpoints.EventSub, listener, _log, _eventSubTimeouts);
        var run = Task.WhenAll(eventSub.RunAsync(cts.Token), ValidatePeriodicallyAsync(_botTokens, cts, SetBotStatus));
        return new ActiveConnection(cts, run);
    }

    /// <summary>
    /// Gives the router the current chat modes (so the first change can be reported as on/off), plus what
    /// Streamer.bot's argument names need and EventSub does not send: the broadcaster's display name and type,
    /// reward settings and the current title and category.
    /// </summary>
    private async Task SeedChatSettingsAsync(CancellationToken ct)
    {
        _router.SeedChatSettings(await Api.GetChatSettingsAsync(ct).ConfigureAwait(false));
        await SeedAsync("broadcaster info", async () =>
        {
            if (await Api.GetUserByIdAsync(Settings.UserId, ct).ConfigureAwait(false) is { } user) _router.SeedBroadcaster(user);
        }).ConfigureAwait(false);
        await SeedAsync("channel info", async () =>
        {
            if (await Api.GetChannelAsync(null, ct).ConfigureAwait(false) is not { } channel) return;
            _router.SeedChannel(channel);
            await SeedBoxArtAsync(channel.GameId, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (Settings.Scopes.Contains(TwitchScopes.ReadRedemptions) || Settings.Scopes.Contains(TwitchScopes.ManageRedemptions))
            await SeedAsync("rewards", async () => _router.SeedRewards(await Api.GetRewardsAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);
        await SeedAsync("chat badges", async () => ChatBadges = await Api.GetChatBadgesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Chat badge images ("set/version" → 4x image URL and title), loaded when the broadcaster connects.</summary>
    public IReadOnlyDictionary<string, (string Url, string Title)> ChatBadges { get; private set; } =
        new Dictionary<string, (string Url, string Title)>();

    /// <summary>Remembers a category's box art for Stream Update's gameBoxArt / oldGameBoxArt.</summary>
    private async Task SeedBoxArtAsync(string gameId, CancellationToken ct)
    {
        if (_router.HasBoxArt(gameId)) return;
        if (await Api.GetCategoryAsync(gameId, ct).ConfigureAwait(false) is { } category) _router.SeedBoxArt(gameId, category.BoxArtUrl);
    }

    private async Task SeedAsync(string what, Func<Task> seed)
    {
        try
        {
            await seed().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug("Twitch", $"Could not read the {what}: {ex.Message}");
        }
    }

    /// <summary>Publishes an event through the router, which adds Streamer.bot's argument names and memory-based values.</summary>
    private void Publish(BotEvent e)
    {
        foreach (var routed in _router.Route(e)) _bus.Publish(routed);
    }

    /// <summary>Twitch requires hourly validation of tokens used by a long-running connection.</summary>
    private async Task ValidatePeriodicallyAsync(TwitchTokenManager tokens, CancellationTokenSource connection,
        Action<ConnectionState, string> report)
    {
        using var timer = new PeriodicTimer(ValidationInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false))
            {
                try
                {
                    await tokens.ValidateAsync(connection.Token).ConfigureAwait(false);
                }
                catch (TwitchApiException ex) when (ex.StatusCode == 401)
                {
                    report(ConnectionState.Error, ex.Message);
                    if (tokens == _tokens) ConnectedLogin = null;
                    await connection.CancelAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Transient (network) failure: the next check, or a 401 on use, catches a real expiry.
                    _log.Warn("Twitch", $"Token validation failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested)
        {
        }
    }

    /// <summary>Returns true when a connection was running.</summary>
    private async Task<bool> StopConnectionAsync()
    {
        await StopBotConnectionAsync().ConfigureAwait(false);
        var connection = _connection;
        if (connection is null) return false;
        _connection = null;
        ConnectedLogin = null;
        await StopAsync(connection).ConfigureAwait(false);
        return true;
    }

    private async Task StopBotConnectionAsync()
    {
        var connection = _botConnection;
        if (connection is null) return;
        _botConnection = null;
        await StopAsync(connection).ConfigureAwait(false);
        SetBotStatus(ConnectionState.Disconnected, IsBotAuthorized ? $"Signed in as {Settings.Bot.Login}" : "No bot account");
    }

    private async Task StopAsync(ActiveConnection connection)
    {
        await connection.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await connection.Run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Twitch", "Error while disconnecting", ex);
        }
        connection.Cancellation.Dispose();
    }

    private async Task SignOutInBackgroundAsync(string clientId, string token)
    {
        await DisconnectAsync().ConfigureAwait(false);
        SetStatus(ConnectionState.Disconnected, "Signed out");
        await RevokeAsync(clientId, token).ConfigureAwait(false);
    }

    private async Task SignOutBotInBackgroundAsync(string clientId, string token)
    {
        await StopBotConnectionAsync().ConfigureAwait(false);
        SetBotStatus(ConnectionState.Disconnected, "No bot account");
        await RevokeAsync(clientId, token).ConfigureAwait(false);
    }

    private async Task RevokeAsync(string clientId, string token)
    {
        if (clientId.Length == 0 || token.Length == 0) return;
        try
        {
            await _auth.RevokeAsync(clientId, token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Twitch", $"Could not revoke the Twitch token: {ex.Message}");
        }
    }

    private string RequireClientId()
    {
        var clientId = App.ClientId;
        if (clientId.Length == 0)
            throw new InvalidOperationException("This build has no Twitch app of its own; enter your Twitch application's Client ID under Advanced.");
        return clientId;
    }

    /// <summary>
    /// Updates the status and publishes the connection events. Chat arrives over EventSub, so "chat connected" follows
    /// the EventSub connection when the chat message subscription was created.
    /// </summary>
    private void SetStatus(ConnectionState state, string message)
    {
        var wasConnected = _announcedConnected;
        State = state;
        StatusMessage = message;
        _announcedConnected = state == ConnectionState.Connected;
        StatusChanged?.Invoke();
        if (_announcedConnected && !wasConnected)
        {
            Publish(new BotEvent(TwitchEventTypes.BroadcasterConnected, AccountArgs(Settings.Login, Settings.UserId)));
            _announcedChat = !UnavailableEvents.Contains("channel.chat.message");
            if (_announcedChat) Publish(new BotEvent(TwitchEventTypes.BroadcasterChatConnected, AccountArgs(Settings.Login, Settings.UserId)));
        }
        else if (wasConnected && !_announcedConnected)
        {
            Publish(new BotEvent(TwitchEventTypes.BroadcasterDisconnected, AccountArgs(Settings.Login, Settings.UserId, message)));
            if (_announcedChat)
                Publish(new BotEvent(TwitchEventTypes.BroadcasterChatDisconnected, AccountArgs(Settings.Login, Settings.UserId, message)));
            _announcedChat = false;
        }
    }

    private void SetBotStatus(ConnectionState state, string message)
    {
        var wasConnected = BotState == ConnectionState.Connected;
        BotState = state;
        BotStatusMessage = message;
        StatusChanged?.Invoke();
        var connected = state == ConnectionState.Connected;
        if (connected && !wasConnected)
            Publish(new BotEvent(TwitchEventTypes.BotConnected, AccountArgs(Settings.Bot.Login, Settings.Bot.UserId)));
        else if (wasConnected && !connected)
            Publish(new BotEvent(TwitchEventTypes.BotDisconnected, AccountArgs(Settings.Bot.Login, Settings.Bot.UserId, message)));
    }

    private static Dictionary<string, object?> AccountArgs(string login, string userId, string? reason = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = login,
            ["userName"] = login,
            ["userId"] = userId,
        };
        if (reason is not null) args["reason"] = reason;
        return args;
    }

    private sealed record ActiveConnection(CancellationTokenSource Cancellation, Task Run);
}

/// <summary>
/// While connected: publishes the viewer count when live and the list of present viewers, at the intervals in
/// <see cref="TwitchSettings"/>. Failures are logged once per kind and never stop the connection.
/// </summary>
internal sealed class TwitchPoller(TwitchApi api, TwitchSettings settings, Action<BotEvent> publish, AppLog log, TimeProvider time)
{
    private const int AdScheduleIntervalSeconds = 30;
    private readonly UpcomingAdWarnings _ads = new();

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(
        LoopAsync(settings.ViewerCountIntervalSeconds, PollViewersAsync, "viewer count", ct),
        LoopAsync(settings.PresentViewersIntervalSeconds, PollPresentAsync, "present viewers", ct),
        LoopAsync(settings.UpcomingAdWarnings ? AdScheduleIntervalSeconds : 0, PollAdScheduleAsync, "ad schedule", ct));

    private async Task LoopAsync(int seconds, Func<CancellationToken, Task> poll, string what, CancellationToken ct)
    {
        if (seconds <= 0) return;
        var warned = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, seconds)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await poll(ct).ConfigureAwait(false);
                    warned = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!warned) log.Warn("Twitch", $"Could not update the {what}: {ex.Message}");
                    warned = true;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task PollViewersAsync(CancellationToken ct)
    {
        if (await api.GetStreamAsync(null, ct).ConfigureAwait(false) is not { } stream) return;
        publish(new BotEvent(TwitchEventTypes.ViewerCountUpdate, new Dictionary<string, object?>
        {
            ["viewers"] = stream.ViewerCount,
            ["viewerCount"] = stream.ViewerCount,
            ["title"] = stream.Title,
            ["gameName"] = stream.GameName,
            ["startedAt"] = stream.StartedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "",
        }));
    }

    private async Task PollPresentAsync(CancellationToken ct)
    {
        if (!settings.Scopes.Contains(TwitchScopes.ReadChatters)) return;
        var chatters = await api.GetChattersAsync(ct).ConfigureAwait(false);
        TwitchStreamInfo? stream;
        try
        {
            stream = await api.GetStreamAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TwitchApiException or HttpRequestException)
        {
            stream = null;   // Streamer.bot's isLive and stream details; the viewer list matters more
        }
        publish(new BotEvent(TwitchEventTypes.PresentViewers, new Dictionary<string, object?>
        {
            ["count"] = chatters.Count,
            ["isLive"] = stream is not null,
            // Streamer.bot's list (id, userName, display, role, isSubscribed); the chatter list has no roles or subs.
            ["users"] = chatters.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.UserId, ["userName"] = c.UserLogin, ["display"] = c.UserName, ["role"] = 1, ["isSubscribed"] = false,
            }).ToList(),
            ["userLogins"] = string.Join(",", chatters.Select(c => c.UserLogin)),
            ["userIds"] = string.Join(",", chatters.Select(c => c.UserId)),
            ["title"] = stream?.Title ?? "",
            ["viewerCount"] = stream?.ViewerCount ?? 0,
            ["startedAt"] = stream?.StartedAt?.LocalDateTime ?? default(DateTime),
        }));
    }

    private async Task PollAdScheduleAsync(CancellationToken ct)
    {
        if (!settings.Scopes.Contains(TwitchScopes.ReadAds) && !settings.Scopes.Contains(TwitchScopes.ManageAds)) return;
        if (await api.GetStreamAsync(null, ct).ConfigureAwait(false) is null) return;
        var schedule = await api.GetAdScheduleAsync(ct).ConfigureAwait(false);
        if (_ads.Check(schedule, time.GetUtcNow()) is { } warning) publish(warning);
    }
}

/// <summary>
/// Streamer.bot's "Upcoming ad": one event per minute from 5 minutes before the next scheduled ad down to 1 minute.
/// Not thread-safe; the poller calls it from one loop.
/// </summary>
internal sealed class UpcomingAdWarnings
{
    public const int FirstWarningMinutes = 5;

    private DateTimeOffset? _lastAd;
    private int _lastMinutes;

    /// <summary>The warning to publish for this schedule now, or null (no ad soon, or this minute was already announced).</summary>
    public BotEvent? Check(TwitchAdSchedule schedule, DateTimeOffset now)
    {
        if (schedule.NextAdAt is not { } next || next <= now) return null;
        var minutes = (int)Math.Ceiling((next - now).TotalMinutes);
        if (minutes > FirstWarningMinutes) return null;
        if (_lastAd == next && _lastMinutes == minutes) return null;
        _lastAd = next;
        _lastMinutes = minutes;
        return new BotEvent(TwitchEventTypes.UpcomingAd, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["minutes"] = minutes,
            ["adLength"] = schedule.Duration,
            ["nextAdAt"] = next.ToString("o", CultureInfo.InvariantCulture),
            ["snoozesLeft"] = schedule.SnoozeCount,
            ["snoozeCount"] = schedule.SnoozeCount,
        });
    }
}
