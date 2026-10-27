using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Kick.Api;
using Cuebot.Kick.Auth;
using Cuebot.Kick.Events;
using Cuebot.Kick.Pusher;
using Cuebot.Kick.Realtime;
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
    private KickChannel? _channel;
    private bool _connected;

    public KickService(JsonFile<KickSettings> settings, HttpClient http, EventBus bus, AppLog log)
        : this(settings, http, bus, log, KickEndpoints.Production, KickTimings.Default, TimeProvider.System,
            () => new ClientWebSocketTransport(), port => new LoopbackReceiver(port))
    {
    }

    internal KickService(JsonFile<KickSettings> settings, HttpClient http, EventBus bus, AppLog log, KickEndpoints endpoints,
        KickTimings timings, TimeProvider time, Func<IPusherTransport> transports, Func<int, IOAuthReceiver> receivers)
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
        _hub = new KickEventHub(bus, time, timings.DedupeWindow);
        _realtime = new KickRealtimeEventSource(transports, new KickRealtimeAuth(http, endpoints), timings, log);
        _realtime.ConnectionChanged += OnRealtimeChanged;
        _hub.Attach(_realtime);
        Webhooks = new KickWebhookEventSource(ct => _api.GetWebhookPublicKeyAsync(ct));
        _hub.Attach(Webhooks);
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
    public IReadOnlyList<IKickEventSource> EventSources => [_realtime, Webhooks];

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
        if (account == KickAccount.Broadcaster) await LoadOwnChannelAsync(ct).ConfigureAwait(false);
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
                await LoadOwnChannelAsync(ct).ConfigureAwait(false);
                SaveSettings();
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
    public async Task<string?> SendChatMessageAsync(string message, bool useBot = false, bool fallback = true, string? replyTo = null,
        CancellationToken ct = default)
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

    /// <summary>Disconnects. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await _realtime.DisposeAsync().ConfigureAwait(false);
    }
}
