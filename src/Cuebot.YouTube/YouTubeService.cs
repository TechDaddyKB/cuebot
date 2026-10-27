using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Auth;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Monitoring;

namespace Cuebot.YouTube;

/// <summary>
/// The YouTube connection: Google sign-in (device code, or browser + loopback) for a broadcaster and an optional bot
/// account, broadcast monitoring, live chat polling, sending and moderation, and broadcast/video updates through the
/// YouTube Data API v3. All calls count against the user's own Google Cloud project quota (see YOUTUBE.md).
/// </summary>
public sealed class YouTubeService : IConnectionStatus, IAsyncDisposable
{
    private const int QuotaSaveStep = 25;

    private readonly JsonFile<YouTubeSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly YouTubeTimings _timings;
    private readonly GoogleOAuthClient _oauth;
    private readonly AccountTokens _broadcaster;
    private readonly AccountTokens _bot;
    private readonly YouTubeApi _api;
    private readonly ChatterDirectory _chatters = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Dictionary<string, string> _banIds = new(StringComparer.Ordinal);
    private readonly IDisposable _pollWatch;
    private YouTubeConnection? _connection;
    private int _lastSavedQuota;
    private string? _activePollMessageId;

    public YouTubeService(JsonFile<YouTubeSettings> settings, HttpClient http, EventBus bus, AppLog log)
        : this(settings, http, bus, log, YouTubeEndpoints.Production, TimeProvider.System, YouTubeTimings.Default)
    {
    }

    internal YouTubeService(JsonFile<YouTubeSettings> settings, HttpClient http, EventBus bus, AppLog log,
        YouTubeEndpoints endpoints, TimeProvider time, YouTubeTimings timings)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _time = time;
        _timings = timings;
        _oauth = new GoogleOAuthClient(http, endpoints, time);
        _broadcaster = new AccountTokens(settings, YouTubeAccountKind.Broadcaster, _oauth, time, timings, log);
        _bot = new AccountTokens(settings, YouTubeAccountKind.Bot, _oauth, time, timings, log);
        var quota = new QuotaTracker(time);
        quota.Restore(Settings.QuotaDay, Settings.QuotaUsed);
        quota.Changed += OnQuotaChanged;
        quota.Exhausted += OnQuotaExhausted;
        _api = new YouTubeApi(new YouTubeApiClient(http, endpoints.Api, quota));
        _pollWatch = bus.Subscribe(TrackPolls);
    }

    public string Name => "YouTube";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public YouTubeSettings Settings => _settings.Value;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>True when the Google OAuth client ID and secret are entered.</summary>
    public bool IsConfigured => Settings.ClientId.Trim().Length > 0 && Settings.ClientSecret.Trim().Length > 0;

    public bool IsBroadcasterSignedIn => Settings.Broadcaster.IsSignedIn;
    public bool IsBotSignedIn => Settings.Bot.IsSignedIn;
    public bool IsConnected => _connection is not null;

    /// <summary>Broadcasts whose chat is being read, most recently started first.</summary>
    public IReadOnlyList<YouTubeBroadcast> MonitoredBroadcasts => _connection?.Broadcasts.Monitored ?? [];

    public IReadOnlyList<YouTubeBroadcast> UpcomingBroadcasts => _connection?.Broadcasts.Upcoming ?? [];

    /// <summary>The broadcast actions use by default.</summary>
    public YouTubeBroadcast? LatestBroadcast => _connection?.Broadcasts.Latest;

    public bool IsLive => LatestBroadcast is not null;

    public BroadcastStatistics? StatisticsOf(string broadcastId) => _connection?.StatisticsOf(broadcastId);

    public int QuotaUsedToday => _api.Quota.UsedToday;
    public DateTimeOffset? QuotaExhaustedUntil => _api.Quota.ExhaustedUntil;
    public DateTimeOffset QuotaResetsAt => _api.Quota.NextReset;

    /// <summary>Estimated units per hour of polling while live and while waiting, from the current settings.</summary>
    public (double Live, double Idle) EstimateQuotaPerHour()
    {
        var s = Settings;
        var chat = YouTubeTimings.Clamp(s.MinimumChatPollSeconds, _timings.MinimumChatPoll).TotalSeconds;
        return QuotaCosts.EstimatePerHour(chat,
            YouTubeTimings.Clamp(s.BroadcastPollSeconds, _timings.MinimumBroadcastPoll).TotalSeconds,
            YouTubeTimings.Clamp(s.LivePollSeconds, _timings.MinimumLivePoll).TotalSeconds,
            YouTubeTimings.Clamp(s.SubscriberPollSeconds, _timings.MinimumSubscriberPoll).TotalSeconds,
            s.MonitorUpcoming, s.PollNewSubscribers);
    }

    // ---------------------------------------------------------------- sign-in

    /// <summary>Starts a device-code sign-in (needs a "TVs and Limited Input devices" OAuth client). Show the returned code.</summary>
    public Task<DeviceAuthorization> BeginDeviceSignInAsync(YouTubeAccountKind account, CancellationToken ct = default)
    {
        RequireClient();
        return _oauth.StartDeviceFlowAsync(account, Settings.ClientId.Trim(), YouTubeScopes.Device, ct);
    }

    /// <summary>Waits for the user to approve the code, stores the tokens and channel, and connects after a broadcaster sign-in.</summary>
    public async Task CompleteDeviceSignInAsync(DeviceAuthorization auth, CancellationToken ct = default)
    {
        var tokens = await _oauth.PollDeviceTokenAsync(auth, Settings.ClientId.Trim(), Settings.ClientSecret.Trim(), ct).ConfigureAwait(false);
        await FinishSignInAsync(auth.Account, tokens, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Browser sign-in for "Desktop app" OAuth clients: opens Google's consent page (via <paramref name="openBrowser"/>)
    /// and receives the result on a loopback address, with PKCE.
    /// </summary>
    public async Task SignInWithBrowserAsync(YouTubeAccountKind account, Action<Uri> openBrowser, CancellationToken ct = default)
    {
        RequireClient();
        using var receiver = new LoopbackReceiver();
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        openBrowser(_oauth.BuildAuthorizeUri(Settings.ClientId.Trim(), receiver.RedirectUri, YouTubeScopes.Loopback,
            Pkce.Challenge(verifier), state));
        var result = await receiver.WaitForRedirectAsync(ct).ConfigureAwait(false);
        if (result.GetValueOrDefault("state") != state)
            throw new YouTubeApiException("The sign-in response did not match this request. Try again.", 0, "state_mismatch");
        if (result.TryGetValue("error", out var error))
            throw new YouTubeApiException(error == "access_denied" ? "Sign-in was denied in the browser." : $"Google sign-in failed: {error}", 0, error);
        var code = result.GetValueOrDefault("code") ?? "";
        var tokens = await _oauth.ExchangeCodeAsync(Settings.ClientId.Trim(), Settings.ClientSecret.Trim(), code, verifier,
            receiver.RedirectUri, ct).ConfigureAwait(false);
        await FinishSignInAsync(account, tokens, ct).ConfigureAwait(false);
    }

    /// <summary>Forgets an account's tokens (revoking them at Google in the background). Signing out the broadcaster disconnects.</summary>
    public async Task SignOutAsync(YouTubeAccountKind account)
    {
        var tokens = Tokens(account);
        var refresh = tokens.Account.RefreshToken;
        var access = tokens.Account.AccessToken;
        tokens.Account.Clear();
        SaveSettings();
        _log.Info("YouTube", $"Signed out the {Label(account)} account");
        if (account == YouTubeAccountKind.Broadcaster)
        {
            await DisconnectAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Signed out");
        }
        else
        {
            StatusChanged?.Invoke();
        }
        var token = refresh.Length > 0 ? refresh : access;
        if (token.Length == 0) return;
        try
        {
            await _oauth.RevokeAsync(token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("YouTube", $"Could not revoke the {Label(account)} sign-in: {ex.Message}");
        }
    }

    private async Task FinishSignInAsync(YouTubeAccountKind account, OAuthTokens tokens, CancellationToken ct)
    {
        var target = Tokens(account);
        target.Store(tokens);
        var channel = await _api.GetMyChannelAsync(target, ct).ConfigureAwait(false)
            ?? throw new YouTubeApiException("This Google account has no YouTube channel. Create one, or pick the right (brand) account when signing in.", 0, "noChannel");
        var a = target.Account;
        a.ChannelId = channel.Id;
        a.ChannelTitle = channel.Title;
        a.CustomUrl = channel.CustomUrl;
        a.ProfileImageUrl = channel.ThumbnailUrl;
        SaveSettings();
        _log.Info("YouTube", $"Signed in the {Label(account)} account as {channel.Title}");
        StatusChanged?.Invoke();
        if (account == YouTubeAccountKind.Broadcaster) await ConnectAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- connection

    /// <summary>Checks the broadcaster sign-in (refreshing it if needed), then starts monitoring broadcasts and chat.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        RequireClient();
        if (!IsBroadcasterSignedIn) throw new InvalidOperationException("Sign in the YouTube broadcaster account first.");
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopConnectionAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Checking YouTube sign-in...");
            try
            {
                var channel = await _api.GetMyChannelAsync(_broadcaster, ct).ConfigureAwait(false)
                    ?? throw new YouTubeApiException("The signed-in Google account has no YouTube channel.", 0, "noChannel");
                Settings.Broadcaster.ChannelId = channel.Id;
                Settings.Broadcaster.ChannelTitle = channel.Title;
                Settings.Broadcaster.CustomUrl = channel.CustomUrl;
                Settings.Broadcaster.ProfileImageUrl = channel.ThumbnailUrl;
                SaveSettings();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetStatus(ConnectionState.Error, ex.Message);
                throw;
            }
            var connection = new YouTubeConnection(_api, _broadcaster, () => Settings, _chatters, _bus, _log, _timings, _time, UpdateStatus);
            _connection = connection;
            connection.Start();
            _log.Info("YouTube", $"Connected as {Settings.Broadcaster.ChannelTitle}");
            UpdateStatus();
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
            if (await StopConnectionAsync().ConfigureAwait(false)) SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects automatically when configured, signed in and auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect || !IsConfigured || !IsBroadcasterSignedIn) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error("YouTube", "Automatic connection failed", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _pollWatch.Dispose();
        _settings.Value.QuotaDay = _api.Quota.Day;
        _settings.Value.QuotaUsed = _api.Quota.UsedToday;
        _settings.ScheduleSave();
    }

    // ---------------------------------------------------------------- chat

    /// <summary>
    /// Posts to a monitored broadcast's chat (the latest one unless <paramref name="broadcastId"/> is given). With
    /// <paramref name="useBot"/> the bot account posts; <paramref name="fallback"/> lets the broadcaster post when the
    /// bot is not signed in or its send fails. Messages longer than 200 characters are split.
    /// </summary>
    public async Task SendChatMessageAsync(string message, bool useBot = false, bool fallback = true, string? broadcastId = null,
        CancellationToken ct = default)
    {
        var parts = MessageSplitter.Split(message);
        if (parts.Count == 0) return;
        var chat = RequireBroadcast(broadcastId).LiveChatId;
        var sender = useBot && IsBotSignedIn ? _bot
            : useBot && !fallback ? throw new InvalidOperationException("The YouTube bot account is not signed in.")
            : _broadcaster;
        foreach (var part in parts)
        {
            try
            {
                await _api.InsertChatMessageAsync(sender, chat, part, ct).ConfigureAwait(false);
            }
            catch (YouTubeApiException ex) when (sender == _bot && fallback && !ex.IsQuotaExceeded)
            {
                _log.Warn("YouTube", $"The bot account could not send ({ex.Message}); sending as the broadcaster");
                sender = _broadcaster;
                await _api.InsertChatMessageAsync(sender, chat, part, ct).ConfigureAwait(false);
            }
        }
    }

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default)
    {
        RequireConnected();
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("No message id given.");
        return _api.DeleteChatMessageAsync(_broadcaster, messageId.Trim(), ct);
    }

    /// <summary>Permanently bans a user (channel id, @handle or a recent chatter's display name) from the chat.</summary>
    public Task BanUserAsync(string user, string? broadcastId = null, CancellationToken ct = default) => BanAsync(user, null, broadcastId, ct);

    /// <summary>Times a user out for <paramref name="seconds"/>.</summary>
    public Task TimeoutUserAsync(string user, int seconds, string? broadcastId = null, CancellationToken ct = default) =>
        BanAsync(user, Math.Max(1, seconds), broadcastId, ct);

    /// <summary>Lifts a ban or timeout that Cuebot placed during this session (YouTube needs the ban's id to lift it).</summary>
    public async Task UnbanUserAsync(string user, CancellationToken ct = default)
    {
        RequireConnected();
        var (channelId, _) = await ResolveUserAsync(user, ct).ConfigureAwait(false);
        string? banId;
        lock (_banIds) banId = _banIds.GetValueOrDefault(channelId);
        if (banId is null)
            throw new InvalidOperationException("Cuebot can only lift bans it placed itself during this session; use YouTube Studio for others.");
        await _api.UnbanAsync(_broadcaster, banId, ct).ConfigureAwait(false);
        lock (_banIds) _banIds.Remove(channelId);
    }

    public async Task AddModeratorAsync(string user, string? broadcastId = null, CancellationToken ct = default)
    {
        var chat = RequireBroadcast(broadcastId).LiveChatId;
        var (channelId, _) = await ResolveUserAsync(user, ct).ConfigureAwait(false);
        await _api.AddModeratorAsync(_broadcaster, chat, channelId, ct).ConfigureAwait(false);
    }

    public async Task RemoveModeratorAsync(string user, string? broadcastId = null, CancellationToken ct = default)
    {
        var chat = RequireBroadcast(broadcastId).LiveChatId;
        var (channelId, _) = await ResolveUserAsync(user, ct).ConfigureAwait(false);
        var moderators = await _api.ListModeratorsAsync(_broadcaster, chat, ct).ConfigureAwait(false);
        if (!moderators.TryGetValue(channelId, out var moderatorId))
            throw new InvalidOperationException("That user is not a moderator of this chat.");
        await _api.RemoveModeratorAsync(_broadcaster, moderatorId, ct).ConfigureAwait(false);
    }

    /// <summary>Starts a chat poll (2–4 options) in a monitored broadcast; returns the poll's message id.</summary>
    public async Task<string> CreatePollAsync(string question, IReadOnlyList<string> options, string? broadcastId = null, CancellationToken ct = default)
    {
        var chat = RequireBroadcast(broadcastId).LiveChatId;
        var choices = options.Select(o => o.Trim()).Where(o => o.Length > 0).ToArray();
        if (question.Trim().Length == 0) throw new ArgumentException("The poll needs a question.");
        if (choices.Length is < 2 or > 4) throw new ArgumentException("A YouTube poll needs 2 to 4 options.");
        var id = await _api.CreatePollAsync(_broadcaster, chat, question.Trim(), choices, ct).ConfigureAwait(false);
        _activePollMessageId = id;
        return id;
    }

    /// <summary>Ends a poll: the given poll message id, or the latest active poll.</summary>
    public async Task EndPollAsync(string? pollMessageId = null, CancellationToken ct = default)
    {
        RequireConnected();
        var id = string.IsNullOrWhiteSpace(pollMessageId) ? _activePollMessageId : pollMessageId.Trim();
        if (id is null) throw new InvalidOperationException("There is no active YouTube poll.");
        await _api.EndPollAsync(_broadcaster, id, ct).ConfigureAwait(false);
        if (id == _activePollMessageId) _activePollMessageId = null;
    }

    // ---------------------------------------------------------------- broadcast and channel info

    /// <summary>Changes title/description/category/tags/privacy of a broadcast (the latest monitored one by default).</summary>
    public async Task<YouTubeVideo> UpdateBroadcastAsync(VideoChange change, string? broadcastId = null, CancellationToken ct = default)
    {
        RequireSignedIn();
        var id = string.IsNullOrWhiteSpace(broadcastId) ? DefaultVideoId() : broadcastId.Trim();
        var video = await _api.UpdateVideoAsync(_broadcaster, id, change, ct).ConfigureAwait(false);
        if (_connection?.Broadcasts.Find(id) is { } current)
            _connection.Broadcasts.Replace(current with
            {
                Title = video.Title.Length > 0 ? video.Title : current.Title,
                Description = change.Description ?? current.Description,
                PrivacyStatus = change.PrivacyStatus ?? current.PrivacyStatus,
            });
        return video;
    }

    /// <summary>Changes tags with a function of the current ones (add, remove, clear).</summary>
    public async Task<YouTubeVideo> UpdateTagsAsync(Func<IReadOnlyList<string>, IReadOnlyList<string>> update, string? broadcastId = null,
        CancellationToken ct = default)
    {
        var video = await GetVideoAsync(broadcastId, ct).ConfigureAwait(false);
        return await UpdateBroadcastAsync(new VideoChange(Tags: update(video.Tags)), video.Id, ct).ConfigureAwait(false);
    }

    /// <summary>A video's details and live statistics (the latest monitored broadcast when no id is given).</summary>
    public async Task<YouTubeVideo> GetVideoAsync(string? videoId = null, CancellationToken ct = default)
    {
        RequireSignedIn();
        var id = string.IsNullOrWhiteSpace(videoId) ? DefaultVideoId() : videoId.Trim();
        var videos = await _api.GetVideosAsync(_broadcaster, [id], ct).ConfigureAwait(false);
        return videos.FirstOrDefault() ?? throw new InvalidOperationException($"YouTube video {id} was not found.");
    }

    /// <summary>A channel by channel id, @handle or recent chatter's display name.</summary>
    public async Task<YouTubeChannel> GetChannelAsync(string user, CancellationToken ct = default)
    {
        RequireSignedIn();
        var (channelId, _) = await ResolveUserAsync(user, ct).ConfigureAwait(false);
        return await _api.GetChannelAsync(_broadcaster, channelId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"YouTube channel {channelId} was not found.");
    }

    /// <summary>
    /// Resolves a user reference to a channel id: a channel id ("UC..."), a display name seen in chat recently, or an
    /// @handle (one channels.list call).
    /// </summary>
    public async Task<(string ChannelId, string Name)> ResolveUserAsync(string user, CancellationToken ct = default)
    {
        var text = user.Trim();
        if (text.Length == 0) throw new ArgumentException("No YouTube user given.");
        if (IsChannelId(text)) return (text, _chatters.NameOf(text) ?? text);
        if (_chatters.IdOf(text) is { } known) return (known, text.TrimStart('@'));
        RequireSignedIn();
        var channel = await _api.GetChannelByHandleAsync(_broadcaster, text, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No YouTube channel found for \"{text}\".");
        _chatters.Remember(channel.Id, channel.Title);
        return (channel.Id, channel.Title);
    }

    internal static bool IsChannelId(string text) =>
        text.Length == 24 && text.StartsWith("UC", StringComparison.Ordinal) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // ---------------------------------------------------------------- helpers

    private async Task BanAsync(string user, int? seconds, string? broadcastId, CancellationToken ct)
    {
        var chat = RequireBroadcast(broadcastId).LiveChatId;
        var (channelId, _) = await ResolveUserAsync(user, ct).ConfigureAwait(false);
        var banId = await _api.BanAsync(_broadcaster, chat, channelId, seconds, ct).ConfigureAwait(false);
        if (banId.Length > 0) lock (_banIds) _banIds[channelId] = banId;
    }

    private YouTubeBroadcast RequireBroadcast(string? broadcastId)
    {
        var connection = _connection ?? throw new InvalidOperationException("YouTube is not connected.");
        if (!string.IsNullOrWhiteSpace(broadcastId))
            return connection.Broadcasts.Find(broadcastId.Trim())
                ?? throw new InvalidOperationException($"YouTube broadcast {broadcastId} is not live or not monitored.");
        return connection.Broadcasts.Latest ?? throw new InvalidOperationException("There is no live YouTube broadcast.");
    }

    /// <summary>The latest live broadcast, else the next upcoming one.</summary>
    private string DefaultVideoId() =>
        LatestBroadcast?.Id ?? UpcomingBroadcasts.FirstOrDefault()?.Id
        ?? throw new InvalidOperationException("There is no live or upcoming YouTube broadcast; give a video id.");

    private void RequireConnected()
    {
        if (_connection is null) throw new InvalidOperationException("YouTube is not connected.");
    }

    private void RequireSignedIn()
    {
        if (!IsBroadcasterSignedIn) throw new InvalidOperationException("Sign in the YouTube broadcaster account first.");
    }

    private void RequireClient()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Enter the OAuth client ID and client secret from your Google Cloud project first.");
    }

    private AccountTokens Tokens(YouTubeAccountKind kind) => kind == YouTubeAccountKind.Bot ? _bot : _broadcaster;

    private static string Label(YouTubeAccountKind kind) => kind == YouTubeAccountKind.Bot ? "bot" : "broadcaster";

    private void TrackPolls(BotEvent e)
    {
        if (e.Type == YouTubeEventTypes.PollStarted) _activePollMessageId = e.Args.GetString("pollMessageId");
        else if (e.Type == YouTubeEventTypes.PollClosed && e.Args.GetString("pollMessageId") == _activePollMessageId) _activePollMessageId = null;
    }

    private void OnQuotaChanged()
    {
        var used = _api.Quota.UsedToday;
        if (Math.Abs(used - _lastSavedQuota) >= QuotaSaveStep || used < _lastSavedQuota)
        {
            _lastSavedQuota = used;
            Settings.QuotaDay = _api.Quota.Day;
            Settings.QuotaUsed = used;
            SaveSettings();
        }
    }

    private void OnQuotaExhausted(DateTimeOffset until)
    {
        _log.Warn("YouTube", $"Daily API quota exhausted; polling pauses until {until.ToLocalTime():t}");
        _bus.Publish(YouTubeEvents.QuotaExceeded(until, _api.Quota.UsedToday));
        Settings.QuotaDay = _api.Quota.Day;
        Settings.QuotaUsed = _api.Quota.UsedToday;
        SaveSettings();
        UpdateStatus();
    }

    /// <summary>Recomputes the status line from the connection state.</summary>
    private void UpdateStatus()
    {
        var connection = _connection;
        if (connection is null) return;
        if (_api.Quota.ExhaustedUntil is { } until)
        {
            SetStatus(ConnectionState.Error, $"Daily API quota exhausted; resumes at {until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}");
            return;
        }
        var latest = connection.Broadcasts.Latest;
        var text = latest is null
            ? connection.HasChecked ? "Connected, waiting for a live broadcast" : "Connected, looking for a live broadcast..."
            : connection.StatisticsOf(latest.Id)?.ConcurrentViewers is { } viewers
                ? $"Live: {latest.Title} ({viewers} watching)"
                : $"Live: {latest.Title}";
        if (connection.Problem is { } problem) text += $" (last error: {problem})";
        SetStatus(ConnectionState.Connected, text);
    }

    private async Task<bool> StopConnectionAsync()
    {
        var connection = _connection;
        if (connection is null) return false;
        _connection = null;
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("YouTube", "Error while disconnecting", ex);
        }
        return true;
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
