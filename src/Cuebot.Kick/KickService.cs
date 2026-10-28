using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Kick.Api;
using Cuebot.Kick.Auth;
using Cuebot.Kick.Compat;
using Cuebot.Kick.Events;
using Cuebot.Kick.Pusher;
using Cuebot.Kick.Realtime;
using Cuebot.Kick.SevenTv;
using Cuebot.Kick.Webhooks;

namespace Cuebot.Kick;

/// <summary>
/// The Kick connection. Official API (OAuth 2.1 + PKCE sign-in, channel info and polling, title/category, chat,
/// moderation) plus event sources: the unofficial real-time websocket now, and the webhook relay later.
/// </summary>
public sealed class KickService : IConnectionStatus, IAsyncDisposable
{
    private readonly JsonFile<KickSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly KickTimings _timings;
    private readonly KickAuthClient _auth;
    private readonly KickTokenManager _tokens;
    private readonly KickApiClient _api;
    private readonly KickWebClient _web;
    private readonly KickEventHub _hub;
    private readonly KickRealtimeEventSource _realtime;
    private readonly Func<int, IOAuthReceiver> _receivers;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _pollStop;
    private Task _poll = Task.CompletedTask;
    private readonly SevenTvEventSource? _sevenTv;
    private readonly IDisposable _emoteBridge;
    private KickChannel? _channel;

    /// <summary>The broadcaster's profile picture, read with the account at connect (for Streamer.bot's broadcastUserProfileImage).</summary>
    private string _broadcasterPicture = "";
    private IReadOnlyList<KickReward> _rewards = [];
    private bool _connected;
    private int _emoteRefreshQueued;

    public KickService(JsonFile<KickSettings> settings, HttpClient http, EventBus bus, AppLog log)
        : this(settings, http, bus, log, KickEndpoints.Production, KickTimings.Default, TimeProvider.System,
            () => new ClientWebSocketTransport(), port => new LoopbackReceiver(port), () => new ClientWebSocketTransport(), SevenTvEndpoints.Production)
    {
    }

    /// <param name="sevenTvTransports">Websockets for 7TV's EventAPI; null leaves real-time 7TV events off (tests).</param>
    internal KickService(JsonFile<KickSettings> settings, HttpClient http, EventBus bus, AppLog log, KickEndpoints endpoints,
        KickTimings timings, TimeProvider time, Func<IPusherTransport> transports, Func<int, IOAuthReceiver> receivers,
        Func<IPusherTransport>? sevenTvTransports = null, SevenTvEndpoints? sevenTvEndpoints = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _time = time;
        _timings = timings;
        _receivers = receivers;
        _auth = new KickAuthClient(http, endpoints, time);
        _tokens = new KickTokenManager(settings, _auth, time, log);
        _api = new KickApiClient(http, endpoints, _tokens);
        _web = new KickWebClient(http, endpoints);
        _hub = new KickEventHub(bus, time, timings.DedupeWindow)
        {
            Broadcaster = () => Settings.Broadcaster.UserId > 0
                ? new KickBroadcasterArgs(Settings.Broadcaster.UserName, KickArgs.Id(Settings.Broadcaster.UserId), _broadcasterPicture)
                : null,
            FindReward = id => _rewards.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)),
        };
        _realtime = new KickRealtimeEventSource(transports, new KickRealtimeAuth(http, endpoints), timings, log);
        _realtime.ConnectionChanged += OnRealtimeChanged;
        _hub.Attach(_realtime);
        Webhooks = new KickWebhookEventSource(ct => _api.GetWebhookPublicKeyAsync(ct));
        _hub.Attach(Webhooks);
        if (sevenTvTransports is not null)
        {
            _sevenTv = new SevenTvEventSource(sevenTvTransports, http, sevenTvEndpoints ?? SevenTvEndpoints.Production, timings, log);
            _sevenTv.EmoteSetChanged += QueueEmoteRefresh;
            _sevenTv.ConnectionChanged += (_, _) => StatusChanged?.Invoke();
            _hub.Attach(_sevenTv);
        }
        _emoteBridge = bus.Subscribe(OnEmoteServiceEvent);
    }

    /// <summary>
    /// Reloads the shared emote cache (set by the module registration to the core emote service), so emotes 7TV
    /// reported in real time also show in chat.
    /// </summary>
    public Func<CancellationToken, Task>? EmoteRefresh { get; set; }

    /// <summary>The 7TV EventAPI source, when real-time 7TV events are available.</summary>
    internal SevenTvEventSource? SevenTv => _sevenTv;

    /// <summary>7TV emote watcher status for the Kick page.</summary>
    public string SevenTvStatus => !Settings.UseSevenTvEvents || _sevenTv is null ? "Off"
        : _sevenTv.IsConnected ? "Watching 7TV emote set " + _sevenTv.EmoteSetId
        : _sevenTv.Problem ?? (_connected ? "Connecting..." : "Not connected");

    /// <summary>
    /// The core emote service reports 7TV set changes it notices when it refreshes (stream start, cache expiry,
    /// "Refresh emotes"). While the EventAPI is not connected, those become the Kick 7TV triggers too; the hub drops
    /// any duplicate of an EventAPI report.
    /// </summary>
    private void OnEmoteServiceEvent(BotEvent e)
    {
        if (e.Type is not (EmoteService.EmoteAddedEvent or EmoteService.EmoteRemovedEvent)) return;
        if (!_connected || _sevenTv?.IsConnected == true) return;
        if (!string.Equals(e.Args.GetString("provider"), EmoteProviders.SevenTv, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(e.Args.GetString("platform"), KickArgs.Platform, StringComparison.OrdinalIgnoreCase)) return;
        var args = KickArgs.New("emotes");
        var id = e.Args.GetString("emoteId");
        var name = e.Args.GetString("emote");
        args["emoteId"] = id;
        args["emoteName"] = name;
        args["emote"] = name;
        args["provider"] = EmoteProviders.SevenTv;
        args["imageUrl"] = e.Args.GetString("imageUrl");
        _hub.Handle(new KickEvent(e.Type == EmoteService.EmoteAddedEvent ? KickEventTypes.SevenTvEmoteAdded : KickEventTypes.SevenTvEmoteRemoved,
            args, SevenTvEventSource.DedupeKey(id, name)));
    }

    private void QueueEmoteRefresh()
    {
        if (EmoteRefresh is not { } refresh || Interlocked.Exchange(ref _emoteRefreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);   // coalesce a burst of changes
                Interlocked.Exchange(ref _emoteRefreshQueued, 0);
                await refresh(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _emoteRefreshQueued, 0);
                _log.Warn("Kick", $"Emote refresh after a 7TV change failed: {ex.Message}");
            }
        });
    }

    public string Name => "Kick";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public KickSettings Settings => _settings.Value;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>The webhook event source, fed by the user-hosted relay (see PROTOCOL.md).</summary>
    public KickWebhookEventSource Webhooks { get; }

    /// <summary>Every event source, for status display.</summary>
    public IReadOnlyList<IKickEventSource> EventSources => _sevenTv is null ? [_realtime, Webhooks] : [_realtime, Webhooks, _sevenTv];

    public bool IsBroadcasterSignedIn => Settings.Broadcaster.IsSignedIn;
    public bool IsBotSignedIn => Settings.Bot.IsSignedIn;
    public string? BroadcasterName => IsBroadcasterSignedIn && Settings.Broadcaster.UserName.Length > 0 ? Settings.Broadcaster.UserName : null;
    public string? BotName => IsBotSignedIn && Settings.Bot.UserName.Length > 0 ? Settings.Bot.UserName : null;

    /// <summary>The broadcaster's channel from the latest API poll while connected.</summary>
    public KickChannel? Channel => _channel;

    public bool IsConnected => _connected;
    public bool IsLive => _hub.IsLive ?? _channel?.IsLive ?? false;
    public bool IsRealtimeConnected => _realtime.IsConnected;

    public string RedirectUri => KickEndpoints.RedirectUri(Settings.RedirectPort);

    // ---- sign-in ----

    /// <summary>
    /// Signs an account in with OAuth 2.1 authorization code + PKCE: opens the browser on Kick's consent page and
    /// waits (up to five minutes) for the redirect to http://localhost:{port}/callback.
    /// </summary>
    public async Task SignInAsync(KickAccount account, Action<Uri> openBrowser, CancellationToken ct = default)
    {
        var clientId = Settings.ClientId.Trim();
        var secret = Settings.ClientSecret.Trim();
        if (clientId.Length == 0 || secret.Length == 0)
            throw new InvalidOperationException("Enter the Client ID and Client Secret of your Kick app first.");
        var redirect = RedirectUri;
        var pkce = PkceRequest.Create();
        OAuthCallbackResult callback;
        using (var receiver = _receivers(Settings.RedirectPort))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_timings.SignInTimeout);
            openBrowser(_auth.BuildAuthorizeUrl(clientId, redirect, KickScopes.For(account), pkce));
            try
            {
                callback = await receiver.WaitAsync(pkce.State, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Kick sign-in timed out. Please try again.");
            }
        }
        if (!callback.Succeeded) throw new KickApiException(callback.Error ?? "Kick sign-in failed");

        var tokens = await _auth.ExchangeCodeAsync(clientId, secret, redirect, callback.Code!, pkce.Verifier, ct).ConfigureAwait(false);
        _tokens.Store(account, tokens);
        var user = await _api.GetCurrentUserAsync(account, ct).ConfigureAwait(false);
        var stored = _tokens.Tokens(account);
        stored.UserId = user.UserId;
        stored.UserName = user.Name;
        if (account == KickAccount.Broadcaster)
        {
            _broadcasterPicture = user.ProfilePicture;
            await LoadOwnChannelAsync(ct).ConfigureAwait(false);
        }
        SaveSettings();
        _log.Info("Kick", $"Signed in to Kick as {user.Name} ({account})");
        if (account == KickAccount.Broadcaster) PublishAuthenticated();
        StatusChanged?.Invoke();
    }

    /// <summary>Forgets an account (revoking its tokens at Kick, best effort). Signing out the broadcaster disconnects.</summary>
    public async Task SignOutAsync(KickAccount account)
    {
        var t = _tokens.Tokens(account);
        if (t.RefreshToken.Length > 0) await _auth.RevokeAsync(t.RefreshToken, "refresh_token", CancellationToken.None).ConfigureAwait(false);
        else if (t.AccessToken.Length > 0) await _auth.RevokeAsync(t.AccessToken, "access_token", CancellationToken.None).ConfigureAwait(false);
        _tokens.Clear(account);
        if (account == KickAccount.Broadcaster) await DisconnectAsync().ConfigureAwait(false);
        _log.Info("Kick", $"Signed out of Kick ({account})");
        StatusChanged?.Invoke();
    }

    // ---- connection ----

    /// <summary>Checks the broadcaster sign-in, loads the channel, then starts real-time events and polling.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!IsBroadcasterSignedIn) throw new InvalidOperationException("Sign in to Kick first.");
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Contacting Kick...");
            try
            {
                var user = await _api.GetCurrentUserAsync(KickAccount.Broadcaster, ct).ConfigureAwait(false);
                Settings.Broadcaster.UserId = user.UserId;
                Settings.Broadcaster.UserName = user.Name;
                _broadcasterPicture = user.ProfilePicture;
                await LoadOwnChannelAsync(ct).ConfigureAwait(false);
                SaveSettings();
                await LoadRewardsForEventsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetStatus(ConnectionState.Error, ex.Message);
                throw;
            }

            _connected = true;
            _hub.ResetLive();
            if (_channel is not null) _hub.SetInitialLive(_channel.IsLive);
            PublishAuthenticated();
            await StartRealtimeAsync(ct).ConfigureAwait(false);
            if (_sevenTv is not null && Settings.UseSevenTvEvents) await _sevenTv.StartAsync(Settings.Broadcaster.UserId).ConfigureAwait(false);
            if (Settings.UseWebhookRelay) await RegisterWebhooksAsync(ct).ConfigureAwait(false);
            _pollStop = new CancellationTokenSource();
            _poll = PollLoopAsync(_pollStop.Token);
            UpdateConnectedStatus();
            _log.Info("Kick", $"Connected to Kick channel {Settings.ChannelSlug}");
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
            if (await StopAsync().ConfigureAwait(false)) SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects automatically when signed in and auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect || !IsBroadcasterSignedIn) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error("Kick", "Automatic connection failed", ex);
        }
    }

    /// <summary>Re-reads the channel from the official API now (also done periodically while connected).</summary>
    public async Task<KickChannel?> RefreshChannelAsync(CancellationToken ct = default)
    {
        var current = await _api.GetChannelAsync(null, ct).ConfigureAwait(false);
        if (current is null) return null;
        var previous = _channel;
        _channel = current;
        foreach (var e in KickChannelPoll.Diff(previous, current)) _hub.Handle(e);
        StatusChanged?.Invoke();
        return current;
    }

    private async Task LoadOwnChannelAsync(CancellationToken ct)
    {
        var channel = await _api.GetChannelAsync(null, ct).ConfigureAwait(false);
        if (channel is null) return;
        if (!string.Equals(Settings.ChannelSlug, channel.Slug, StringComparison.OrdinalIgnoreCase))
        {
            // A different channel: the cached internal ids belong to the old one.
            Settings.ChannelId = 0;
            Settings.ChatroomId = 0;
        }
        Settings.ChannelSlug = channel.Slug;
        _channel = channel;
        _hub.SeedChannel(channel);
    }

    private async Task StartRealtimeAsync(CancellationToken ct)
    {
        if (!Settings.UseRealtimeChat) return;
        try
        {
            if (Settings.ChatroomId <= 0 && Settings.ChannelSlug.Length > 0)
            {
                var ids = await _web.GetChannelIdsAsync(Settings.ChannelSlug, ct).ConfigureAwait(false);
                Settings.ChannelId = ids.ChannelId;
                Settings.ChatroomId = ids.ChatroomId;
                SaveSettings();
            }
            await _realtime.StartAsync(Settings.ChannelId, Settings.ChatroomId, Settings.Broadcaster.UserId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KickApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.Warn("Kick", $"Real-time chat is unavailable: {ex.Message}");
            RealtimeProblem = ex.Message;
        }
    }

    /// <summary>Why real-time chat could not start, when it could not.</summary>
    public string? RealtimeProblem { get; private set; }

    private async Task RegisterWebhooksAsync(CancellationToken ct)
    {
        try
        {
            var existing = await _api.ListEventSubscriptionsAsync(ct).ConfigureAwait(false);
            var missing = KickWebhookParser.SupportedEvents
                .Where(e => !existing.Any(s => s.Event == e.Name && s.Version == e.Version && s.Method == "webhook"))
                .ToArray();
            if (missing.Length == 0) return;
            var errors = await _api.SubscribeWebhookEventsAsync(missing, ct).ConfigureAwait(false);
            foreach (var error in errors) _log.Warn("Kick", $"Webhook subscription failed: {error}");
        }
        catch (KickApiException ex)
        {
            _log.Warn("Kick", $"Could not register webhook subscriptions: {ex.Message}");
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(Settings.PollSeconds, 1));
            if (interval < _timings.MinimumPoll) interval = _timings.MinimumPoll;
            try
            {
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
                await RefreshChannelAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("Kick", $"Channel poll failed: {ex.Message}");
            }
        }
    }

    /// <summary>Returns true when something was running.</summary>
    private async Task<bool> StopAsync()
    {
        var wasConnected = _connected;
        _connected = false;
        RealtimeProblem = null;
        var stop = _pollStop;
        _pollStop = null;
        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            stop.Dispose();
        }
        if (_sevenTv is not null) await _sevenTv.StopAsync().ConfigureAwait(false);
        await _realtime.StopAsync().ConfigureAwait(false);
        return wasConnected;
    }

    private void OnRealtimeChanged(bool connected, string reason)
    {
        if (connected) RealtimeProblem = null;
        var args = KickArgs.New(KickPusherDecoder.Source);
        args["reason"] = reason;
        _bus.Publish(new BotEvent(connected ? KickEventTypes.ChatConnected : KickEventTypes.ChatDisconnected, args));
        if (_connected) UpdateConnectedStatus();
    }

    private void UpdateConnectedStatus()
    {
        var realtime = !Settings.UseRealtimeChat ? "real-time chat off"
            : _realtime.IsConnected ? "real-time chat connected"
            : RealtimeProblem is { } problem ? "real-time chat unavailable: " + problem
            : "real-time chat connecting...";
        SetStatus(ConnectionState.Connected, $"Connected as {Settings.Broadcaster.UserName} ({realtime})");
    }

    private void PublishAuthenticated()
    {
        var args = KickArgs.New(KickChannelPoll.Source);
        args["user"] = Settings.Broadcaster.UserName;
        args["userName"] = Settings.Broadcaster.UserName;
        args["userId"] = KickArgs.Id(Settings.Broadcaster.UserId);
        args["channelSlug"] = Settings.ChannelSlug;
        _bus.Publish(new BotEvent(KickEventTypes.BroadcasterAuthenticated, args));
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    // ---- chat ----

    /// <summary>
    /// Sends to the broadcaster's chat, split into Kick-sized parts. With <paramref name="useBot"/> the bot account
    /// sends (or, when it is not signed in and <paramref name="fallback"/> is set, the broadcaster).
    /// </summary>
    public Task<string?> SendChatMessageAsync(string message, bool useBot = false, bool fallback = true, string? replyTo = null,
        CancellationToken ct = default) =>
        SendChatMessageAsync(message, useBot, fallback, replyTo, markInternal: true, ct);

    /// <summary>
    /// As above; with <paramref name="markInternal"/> false the echo is not flagged <c>isInternal</c> (Streamer.bot's
    /// SendMessage <c>internal: false</c>), so commands that ignore internal messages still react to it.
    /// </summary>
    public async Task<string?> SendChatMessageAsync(string message, bool useBot, bool fallback, string? replyTo, bool markInternal,
        CancellationToken ct)
    {
        var parts = ChatText.Split(message);
        if (parts.Count == 0) return null;
        var account = ChooseSender(useBot, fallback);
        var broadcaster = RequireBroadcasterId();
        string? lastId = null;
        foreach (var part in parts)
        {
            var result = await _api.SendChatAsync(account, part, broadcaster, false, replyTo, ct).ConfigureAwait(false);
            if (!result.IsSent) throw new KickApiException("Kick did not accept the chat message");
            lastId = result.MessageId;
            if (markInternal) _hub.MarkSent(result.MessageId);
            replyTo = null;
        }
        return lastId;
    }

    public Task DeleteChatMessageAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("No message id to delete");
        return _api.DeleteChatMessageAsync(KickAccount.Broadcaster, messageId, ct);
    }

    private KickAccount ChooseSender(bool useBot, bool fallback)
    {
        if (useBot && IsBotSignedIn) return KickAccount.Bot;
        if (useBot && !fallback) throw new InvalidOperationException("The Kick bot account is not signed in.");
        if (!IsBroadcasterSignedIn) throw new InvalidOperationException("Sign in to Kick (Kick settings) to send chat messages.");
        return KickAccount.Broadcaster;
    }

    private long RequireBroadcasterId() =>
        Settings.Broadcaster.UserId > 0 ? Settings.Broadcaster.UserId : throw new InvalidOperationException("Sign in to Kick first.");

    // ---- channel ----

    public async Task SetTitleAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("The title is empty");
        await _api.UpdateChannelAsync(title, null, ct).ConfigureAwait(false);
        _log.Info("Kick", "Stream title updated");
    }

    /// <summary>Sets the category by id or by name (exact match preferred, else the first search result).</summary>
    public async Task<KickCategory> SetCategoryAsync(string category, CancellationToken ct = default)
    {
        var found = await FindCategoryAsync(category, ct).ConfigureAwait(false)
                    ?? throw new KickApiException($"No Kick category matches '{category}'");
        await _api.UpdateChannelAsync(null, found.Id, ct).ConfigureAwait(false);
        _log.Info("Kick", $"Category set to {found.Name}");
        return found;
    }

    public async Task<KickCategory?> FindCategoryAsync(string category, CancellationToken ct = default)
    {
        category = category.Trim();
        if (category.Length == 0) return null;
        if (long.TryParse(category, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return await _api.GetCategoryAsync(id, ct).ConfigureAwait(false);
        var results = await _api.SearchCategoriesAsync(category, ct).ConfigureAwait(false);
        return results.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase)) ?? results.FirstOrDefault();
    }

    // ---- moderation ----

    /// <summary>Bans <paramref name="user"/> (name or numeric id) permanently.</summary>
    public async Task BanAsync(string user, string? reason, CancellationToken ct = default)
    {
        var id = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        await _api.BanAsync(KickAccount.Broadcaster, RequireBroadcasterId(), id, null, reason, ct).ConfigureAwait(false);
    }

    /// <summary>Times out <paramref name="user"/>. Kick counts timeouts in whole minutes (1 to 10080); seconds round up.</summary>
    public async Task TimeoutAsync(string user, int seconds, string? reason, CancellationToken ct = default)
    {
        var id = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        await _api.BanAsync(KickAccount.Broadcaster, RequireBroadcasterId(), id, TimeoutMinutes(seconds), reason, ct).ConfigureAwait(false);
    }

    /// <summary>Lifts a ban or timeout.</summary>
    public async Task UnbanAsync(string user, CancellationToken ct = default)
    {
        var id = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        await _api.UnbanAsync(KickAccount.Broadcaster, RequireBroadcasterId(), id, ct).ConfigureAwait(false);
    }

    internal static int TimeoutMinutes(int seconds) => Math.Clamp((int)Math.Ceiling(Math.Max(seconds, 1) / 60.0), 1, 10080);

    // ---- users ----

    /// <summary>
    /// A Kick user id from a numeric id, a name seen in chat or events, or (unofficially) a kick.com channel lookup.
    /// </summary>
    public async Task<long> ResolveUserIdAsync(string user, CancellationToken ct = default)
    {
        user = user.Trim().TrimStart('@');
        if (user.Length == 0) throw new ArgumentException("No user given");
        if (long.TryParse(user, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric > 0) return numeric;
        if (_hub.FindUserId(user) is { } seen && long.TryParse(seen, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return id;
        var ids = await _web.GetChannelIdsAsync(user, ct).ConfigureAwait(false);
        return ids.UserId > 0 ? ids.UserId : throw new KickApiException($"Could not find the Kick user '{user}'");
    }

    /// <summary>Profile and channel of a user (name or id) as action arguments prefixed with <paramref name="prefix"/>.</summary>
    public async Task<Dictionary<string, object?>> GetUserInfoAsync(string user, string prefix = "target", CancellationToken ct = default)
    {
        var id = await ResolveUserIdAsync(user, ct).ConfigureAwait(false);
        var profile = (await _api.GetUsersAsync([id], ct).ConfigureAwait(false)).FirstOrDefault()
                      ?? throw new KickApiException($"Kick has no user with id {id}");
        KickChannel? channel = null;
        try
        {
            channel = await _api.GetChannelByIdAsync(id, ct).ConfigureAwait(false);
        }
        catch (KickApiException)
        {
            // Profile without channel details is still useful.
        }
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [prefix + "User"] = profile.Name,
            [prefix + "UserName"] = profile.Name,
            [prefix + "UserId"] = KickArgs.Id(profile.UserId),
            [prefix + "ProfilePicture"] = profile.ProfilePicture,
            [prefix + "ChannelSlug"] = channel?.Slug ?? "",
            [prefix + "IsLive"] = channel?.IsLive ?? false,
            [prefix + "StreamTitle"] = channel?.Title ?? "",
            [prefix + "Category"] = channel?.Category?.Name ?? "",
            [prefix + "Description"] = channel?.Description ?? "",
        };
    }

    /// <summary>Broadcaster (and bot) details as action arguments.</summary>
    public Dictionary<string, object?> BroadcasterInfo() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["broadcastUser"] = Settings.Broadcaster.UserName,
        ["broadcastUserName"] = Settings.Broadcaster.UserName,
        ["broadcastUserId"] = Settings.Broadcaster.UserId > 0 ? KickArgs.Id(Settings.Broadcaster.UserId) : "",
        ["broadcasterChannelSlug"] = Settings.ChannelSlug,
        ["broadcasterIsLive"] = IsLive,
        ["broadcasterTitle"] = _channel?.Title ?? "",
        ["broadcasterCategory"] = _channel?.Category?.Name ?? "",
        ["broadcasterViewerCount"] = _channel?.ViewerCount ?? 0,
        ["botUser"] = Settings.Bot.UserName,
        ["botUserId"] = Settings.Bot.UserId > 0 ? KickArgs.Id(Settings.Bot.UserId) : "",
    };

    /// <summary>A Kick user id already seen in chat or events for <paramref name="userName"/> (no network lookup).</summary>
    public string? FindKnownUserId(string userName) => _hub.FindUserId(userName);

    /// <summary>The event hub (tests feed events through it).</summary>
    internal KickEventHub Hub => _hub;

    // ---- channel rewards ----

    /// <summary>The rewards from the last <see cref="GetRewardsAsync"/>.</summary>
    public IReadOnlyList<KickReward> Rewards => _rewards;

    /// <summary>
    /// Best effort: the reward settings Streamer.bot reports on redemptions (reward.description, .backgroundColor,
    /// .skipsQueue, .requiresUserInput) are not in Kick's redemption events, so they come from the reward list.
    /// </summary>
    private async Task LoadRewardsForEventsAsync(CancellationToken ct)
    {
        if (!Settings.Broadcaster.Scopes.Contains(KickScopes.RewardsRead)) return;
        try
        {
            await GetRewardsAsync(ct).ConfigureAwait(false);
        }
        catch (KickApiException ex)
        {
            _log.Debug("Kick", $"Could not read the channel rewards: {ex.Message}");
        }
    }

    /// <summary>Reads every reward of the channel (and remembers them for <see cref="Rewards"/>).</summary>
    public async Task<IReadOnlyList<KickReward>> GetRewardsAsync(CancellationToken ct = default)
    {
        _rewards = await _api.GetRewardsAsync(ct).ConfigureAwait(false);
        StatusChanged?.Invoke();
        return _rewards;
    }

    /// <summary>A reward by id or (case-insensitive) title, read fresh from Kick.</summary>
    public async Task<KickReward> FindRewardAsync(string reward, CancellationToken ct = default)
    {
        reward = reward.Trim();
        if (reward.Length == 0) throw new ArgumentException("No reward given");
        var all = await GetRewardsAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(r => string.Equals(r.Id, reward, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(r => string.Equals(r.Title, reward, StringComparison.OrdinalIgnoreCase))
               ?? throw new KickApiException($"No Kick reward matches '{reward}'");
    }

    public async Task<KickReward> CreateRewardAsync(KickRewardUpdate reward, CancellationToken ct = default)
    {
        var created = await _api.CreateRewardAsync(reward, ct).ConfigureAwait(false);
        _rewards = [.. _rewards.Where(r => r.Id != created.Id), created];
        _log.Info("Kick", $"Reward created: {created.Title}");
        StatusChanged?.Invoke();
        return created;
    }

    /// <summary>Changes a reward (id or title). Kick only allows this for rewards created by the same Kick app.</summary>
    public async Task<KickReward> UpdateRewardAsync(string reward, KickRewardUpdate update, CancellationToken ct = default)
    {
        var target = await FindRewardAsync(reward, ct).ConfigureAwait(false);
        var updated = await _api.UpdateRewardAsync(target.Id, update, ct).ConfigureAwait(false) ?? target;
        _rewards = _rewards.Select(r => r.Id == updated.Id ? updated : r).ToArray();
        StatusChanged?.Invoke();
        return updated;
    }

    public async Task<KickReward> DeleteRewardAsync(string reward, CancellationToken ct = default)
    {
        var target = await FindRewardAsync(reward, ct).ConfigureAwait(false);
        await _api.DeleteRewardAsync(target.Id, ct).ConfigureAwait(false);
        _rewards = _rewards.Where(r => r.Id != target.Id).ToArray();
        _log.Info("Kick", $"Reward deleted: {target.Title}");
        StatusChanged?.Invoke();
        return target;
    }

    /// <summary>Pending redemptions (all rewards, or one reward by id or title), every page.</summary>
    public async Task<IReadOnlyList<KickRedemption>> GetPendingRedemptionsAsync(string? reward = null, CancellationToken ct = default)
    {
        var rewardId = string.IsNullOrWhiteSpace(reward) ? null : (await FindRewardAsync(reward, ct).ConfigureAwait(false)).Id;
        var all = new List<KickRedemption>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var (items, next) = await _api.GetRedemptionsAsync(rewardId, "pending", cursor, ct).ConfigureAwait(false);
            all.AddRange(items);
            if (next.Length == 0 || next == cursor) break;
            cursor = next;
        }
        return all;
    }

    /// <summary>Accepts (fulfils) or rejects (refunds) redemptions. Returns the ones Kick refused, with the reason.</summary>
    public Task<IReadOnlyList<KickRedemptionFailure>> UpdateRedemptionsAsync(IEnumerable<string> redemptionIds, bool accept,
        CancellationToken ct = default) => _api.UpdateRedemptionsAsync(redemptionIds, accept, ct);

    /// <summary>Disconnects. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await _realtime.DisposeAsync().ConfigureAwait(false);
        if (_sevenTv is not null) await _sevenTv.DisposeAsync().ConfigureAwait(false);
        _emoteBridge.Dispose();
    }
}
