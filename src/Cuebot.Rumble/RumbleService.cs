using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Rumble.Auth;
using Cuebot.Rumble.Chat;
using Cuebot.Rumble.LiveApi;

namespace Cuebot.Rumble;

/// <summary>
/// The Rumble connection: polls the user's Live Stream API for channel activity, reads the live chat while a
/// stream is live, and (after an explicit sign-in) sends chat messages.
/// </summary>
public sealed class RumbleService : IConnectable, IAsyncDisposable
{
    private readonly JsonFile<RumbleSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly RumbleTimings _timings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly LiveStreamApiClient _api;
    private readonly ChatStreamReader _chatReader;
    private readonly ChatPostClient _chatPoster;
    private readonly RumbleLoginClient _login;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private RumbleConnection? _connection;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;

    public RumbleService(JsonFile<RumbleSettings> settings, EventBus bus, AppLog log)
        : this(settings, RumbleHttp.CreateClient(), ownsHttp: true, bus, log, RumbleEndpoints.Production, TimeProvider.System,
            RumbleTimings.Default)
    {
    }

    internal RumbleService(JsonFile<RumbleSettings> settings, HttpClient http, bool ownsHttp, EventBus bus, AppLog log,
        RumbleEndpoints endpoints, TimeProvider time, RumbleTimings timings)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _time = time;
        _timings = timings;
        _http = http;
        _ownsHttp = ownsHttp;
        _api = new LiveStreamApiClient(http);
        _chatReader = new ChatStreamReader(http, endpoints, timings, time, log);
        _chatPoster = new ChatPostClient(http, endpoints);
        _login = new RumbleLoginClient(http, endpoints);
    }

    public string Name => "Rumble";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public RumbleSettings Settings => _settings.Value;

    /// <summary>True while connected and the Live Stream API reports a live stream.</summary>
    public bool IsLive => _connection?.LiveStream is not null;

    /// <summary>Channel (or user) name of the Live Stream API key while connected.</summary>
    public string? ChannelName => _connection?.Snapshot.DisplayName;

    public string? CurrentStreamTitle => _connection?.LiveStream?.Title;

    /// <summary>Follower count from the latest poll while connected.</summary>
    public int? FollowerCount => _connection?.Snapshot.FollowerCount;

    /// <summary>Subscriber count from the latest poll while connected.</summary>
    public int? SubscriberCount => _connection?.Snapshot.SubscriberCount;

    /// <summary>"Watching now" count of the live stream, while live.</summary>
    public int? ViewerCount => _connection?.LiveStream?.WatchingNow;

    /// <summary>True while the live chat stream is attached (otherwise chat arrives, slower, through the API).</summary>
    public bool IsChatAttached => _connection?.IsChatAttached == true;

    /// <summary>True when a chat session is stored (see <see cref="SignInAsync"/>).</summary>
    public bool CanSendChat => Settings.SessionCookie.Length > 0;

    public string? SignedInUser => CanSendChat && Settings.SignedInUser.Length > 0 ? Settings.SignedInUser : null;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>
    /// Checks the Live Stream API URL by fetching it once, then keeps polling it and attaches to the live chat
    /// whenever a stream is live. Throws when the URL is missing or Rumble rejects it.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var apiUrl = LiveStreamApiUrl.Parse(Settings.LiveStreamApiUrl);
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopConnectionAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Contacting the Rumble Live Stream API...");
            try
            {
                _connection = await RumbleConnection.StartAsync(apiUrl, _api, _chatReader, _bus, _log, _timings, _time,
                    () => Settings.PollSeconds, SetStatus, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetStatus(ConnectionState.Error, ex.Message);
                throw;
            }
            _log.Info("Rumble", $"Connected to {ChannelName} via {LiveStreamApiUrl.Redact(apiUrl.ToString())}");
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

    /// <summary>Connects automatically when an API URL is set and auto-connect is enabled. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect || Settings.LiveStreamApiUrl.Trim().Length == 0) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error("Rumble", "Automatic connection failed", ex);
        }
    }

    /// <summary>
    /// Signs in to rumble.com so Cuebot can send chat messages, and stores only the resulting session (never the
    /// password). Accounts with two-factor sign-in are not supported yet.
    /// </summary>
    public async Task SignInAsync(string username, string password, CancellationToken ct = default)
    {
        var session = await _login.SignInAsync(username, password, ct).ConfigureAwait(false);
        Settings.SessionCookie = session.Cookie;
        Settings.SignedInUser = session.UserName;
        SaveSettings();
        _log.Info("Rumble", $"Signed in to Rumble chat as {session.UserName}");
        StatusChanged?.Invoke();
    }

    /// <summary>Forgets the stored chat session. Reading chat and activity is unaffected.</summary>
    public void SignOut()
    {
        Settings.SessionCookie = "";
        Settings.SignedInUser = "";
        SaveSettings();
        _log.Info("Rumble", "Signed out of Rumble chat");
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Posts to the live stream's chat. Blank messages are ignored; messages closer together than Rumble allows
    /// wait their turn. Throws <see cref="InvalidOperationException"/> when not signed in or not live and
    /// <see cref="RumbleApiException"/> when Rumble rejects the message.
    /// </summary>
    public async Task SendChatMessageAsync(string message, CancellationToken ct = default)
    {
        if (!CanSendChat) throw new InvalidOperationException("Sign in to Rumble (Rumble settings) to send chat messages.");
        if (_connection?.LiveChatId is not { } chatId)
            throw new InvalidOperationException("Rumble chat messages can only be sent while you are live on Rumble.");
        message = message.Trim();
        if (message.Length == 0) return;

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastSend + _timings.SendCooldown - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            await _chatPoster.SendAsync(chatId, message, Settings.SessionCookie, ct).ConfigureAwait(false);
            _lastSend = _time.GetUtcNow();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Disconnects. Safe to call more than once (DI disposes each registration of this singleton).</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Returns true when a connection was running.</summary>
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
            _log.Error("Rumble", "Error while disconnecting", ex);
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
