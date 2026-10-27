using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch;

/// <summary>
/// The Twitch connection: device-code sign-in, EventSub WebSocket for incoming events, Helix for sending chat,
/// and the periodic token validation Twitch requires.
/// </summary>
public sealed class TwitchService : IConnectionStatus, IAsyncDisposable
{
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromHours(1);

    private readonly JsonFile<TwitchSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TwitchEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly EventSubTimeouts _eventSubTimeouts;
    private readonly TwitchAuthClient _auth;
    private readonly TwitchTokenManager _tokens;
    private readonly HelixClient _helix;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private ActiveConnection? _connection;

    public TwitchService(JsonFile<TwitchSettings> settings, HttpClient http, EventBus bus, AppLog log)
        : this(settings, http, bus, log, TwitchEndpoints.Production, TimeProvider.System, EventSubTimeouts.Default)
    {
    }

    internal TwitchService(JsonFile<TwitchSettings> settings, HttpClient http, EventBus bus, AppLog log,
        TwitchEndpoints endpoints, TimeProvider time, EventSubTimeouts eventSubTimeouts)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _endpoints = endpoints;
        _time = time;
        _eventSubTimeouts = eventSubTimeouts;
        _auth = new TwitchAuthClient(http, endpoints.Auth, time);
        _tokens = new TwitchTokenManager(settings, _auth, log);
        _helix = new HelixClient(http, endpoints.Helix, _tokens);
    }

    public string Name => "Twitch";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public TwitchSettings Settings => _settings.Value;

    /// <summary>True when a Client ID and an access token are stored.</summary>
    public bool IsAuthorized => Settings.ClientId.Trim().Length > 0 && Settings.AccessToken.Length > 0;

    /// <summary>Login of the account while a connection is active; otherwise null.</summary>
    public string? ConnectedLogin { get; private set; }

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Starts sign-in with the configured Client ID. Show the returned code to the user.</summary>
    public Task<DeviceAuthorization> BeginAuthorizationAsync(CancellationToken ct = default)
    {
        var clientId = Settings.ClientId.Trim();
        if (clientId.Length == 0) throw new InvalidOperationException("Enter your Twitch application's Client ID first.");
        return _auth.StartDeviceFlowAsync(clientId, TwitchScopes.All, ct);
    }

    /// <summary>Waits for the user to approve the code, stores the tokens and account, then connects.</summary>
    public async Task CompleteAuthorizationAsync(DeviceAuthorization auth, CancellationToken ct = default)
    {
        _tokens.Store(await _auth.PollDeviceTokenAsync(auth, ct).ConfigureAwait(false));
        var account = await _tokens.ValidateAsync(ct).ConfigureAwait(false);
        _log.Info("Twitch", $"Signed in as {account.Login}");
        await ConnectAsync(ct).ConfigureAwait(false);
    }

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

    /// <summary>Forgets the stored tokens and account, disconnects, and revokes the token in the background.</summary>
    public void SignOut()
    {
        var clientId = Settings.ClientId.Trim();
        var token = Settings.AccessToken;
        _tokens.Clear();
        _ = SignOutInBackgroundAsync(clientId, token);
        _log.Info("Twitch", "Signed out");
    }

    /// <summary>
    /// Posts to the signed-in broadcaster's chat. Blank messages are ignored. Throws
    /// <see cref="InvalidOperationException"/> when not connected and <see cref="TwitchApiException"/> when Twitch
    /// rejects or drops the message.
    /// </summary>
    public Task SendChatMessageAsync(string message, CancellationToken ct = default)
    {
        if (_connection is null) throw new InvalidOperationException("Twitch is not connected.");
        if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;
        return _helix.SendChatMessageAsync(Settings.UserId, Settings.UserId, message, ct);
    }

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

    /// <summary>Disconnects. Safe to call more than once (DI disposes each registration of this singleton).</summary>
    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private ActiveConnection StartConnection()
    {
        var cts = new CancellationTokenSource();
        var listener = new TwitchEventSubListener(_helix, Settings, _bus, _log, (state, message) =>
        {
            if (!cts.IsCancellationRequested) SetStatus(state, message);
        });
        var eventSub = new EventSubClient(_endpoints.EventSub, listener, _log, _eventSubTimeouts);
        var run = Task.WhenAll(eventSub.RunAsync(cts.Token), ValidatePeriodicallyAsync(cts));
        return new ActiveConnection(cts, run);
    }

    /// <summary>Twitch requires hourly validation of tokens used by a long-running connection.</summary>
    private async Task ValidatePeriodicallyAsync(CancellationTokenSource connection)
    {
        using var timer = new PeriodicTimer(ValidationInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false))
            {
                try
                {
                    await _tokens.ValidateAsync(connection.Token).ConfigureAwait(false);
                }
                catch (TwitchApiException ex) when (ex.StatusCode == 401)
                {
                    SetStatus(ConnectionState.Error, ex.Message);
                    ConnectedLogin = null;
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
        var connection = _connection;
        if (connection is null) return false;
        _connection = null;
        ConnectedLogin = null;
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
        return true;
    }

    private async Task SignOutInBackgroundAsync(string clientId, string token)
    {
        await DisconnectAsync().ConfigureAwait(false);
        SetStatus(ConnectionState.Disconnected, "Signed out");
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

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private sealed record ActiveConnection(CancellationTokenSource Cancellation, Task Run);
}
