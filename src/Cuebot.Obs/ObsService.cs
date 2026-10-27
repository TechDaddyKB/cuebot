using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs;

/// <summary>
/// The app's connection to OBS: owns the obs-websocket session, reconnects automatically, caches scene and
/// input names for editors, and republishes OBS activity as bot events. Commands live in ObsService.Commands.cs.
/// </summary>
public sealed partial class ObsService : IConnectionStatus, IAsyncDisposable
{
    private const string LogSource = "OBS";
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly JsonFile<ObsSettings> _settingsFile;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeSpan _reconnectDelay;

    // Serializes connection attempts with the handling of a session's end, so their status updates never interleave.
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile ObsClient? _client;
    private Task? _keepConnected;
    private volatile bool _manuallyDisconnected;
    private int _disposed;
    private string? _lastFailure;
    private string[] _scenes = [];
    private string[] _inputs = [];
    private string? _currentScene;

    public ObsService(JsonFile<ObsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, DefaultReconnectDelay)
    {
    }

    internal ObsService(JsonFile<ObsSettings> settings, EventBus bus, AppLog log, TimeSpan reconnectDelay)
    {
        _settingsFile = settings;
        _bus = bus;
        _log = log;
        _reconnectDelay = reconnectDelay;
    }

    public string Name => "OBS";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Live settings; call <see cref="SaveSettings"/> after changing them. Changes apply on the next connect.</summary>
    public ObsSettings Settings => _settingsFile.Value;

    /// <summary>Scene names in OBS order. Kept after a disconnect so editors still offer suggestions.</summary>
    public IReadOnlyList<string> Scenes => _scenes;

    /// <summary>Input (source) names. Kept after a disconnect so editors still offer suggestions.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    /// <summary>The current program scene, or null while disconnected.</summary>
    public string? CurrentScene => _currentScene;

    /// <summary>Raised (on a background thread) when <see cref="Scenes"/>, <see cref="Inputs"/> or <see cref="CurrentScene"/> change.</summary>
    public event Action? ScenesChanged;

    public bool IsConnected => _client?.IsConnected == true;

    public void SaveSettings() => _settingsFile.ScheduleSave();

    /// <summary>
    /// Starts the background loop that connects while <see cref="ObsSettings.AutoConnect"/> is on, retrying every
    /// few seconds, until the user disconnects manually or the service is disposed. Returns immediately.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _keepConnected ??= Task.Run(() => KeepConnectedAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Connects now (no-op when already connected) and re-enables auto-reconnect.</summary>
    /// <exception cref="ObsAuthenticationException">The password is wrong or missing.</exception>
    /// <exception cref="ObsConnectionException">OBS could not be reached or refused the session.</exception>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        _manuallyDisconnected = false;
        return ConnectCoreAsync(ct);
    }

    /// <summary>Closes the session and pauses auto-reconnect until <see cref="ConnectAsync"/> is called.</summary>
    public async Task DisconnectAsync()
    {
        _manuallyDisconnected = true;
        if (_client is { } client) await client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Sends a raw obs-websocket request and returns its <c>responseData</c>.</summary>
    /// <exception cref="ObsConnectionException">OBS is not connected.</exception>
    /// <exception cref="ObsRequestException">OBS reported a failure.</exception>
    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        if (_client is not { IsConnected: true } client) throw new ObsConnectionException("OBS is not connected.");
        return await client.RequestAsync(requestType, requestData, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // DI disposes this instance twice (as ObsService and as IConnectionStatus).
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _manuallyDisconnected = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_keepConnected is { } loop) await loop.ConfigureAwait(false);
        if (_client is { } client) await client.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Settings.AutoConnect && !_manuallyDisconnected && _client is null)
            {
                try
                {
                    await ConnectCoreAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already reported through the status and the log; try again after the delay.
                }
            }
            try
            {
                await Task.Delay(_reconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return;

            var endpoint = $"{Settings.Host}:{Settings.Port}";
            SetStatus(ConnectionState.Connecting, $"Connecting to {endpoint}...");
            var client = new ObsClient();
            client.EventReceived += OnObsEvent;
            client.Disconnected += reason => OnSessionEnded(client, reason);
            _client = client;
            try
            {
                await client.ConnectAsync(new UriBuilder("ws", Settings.Host, Settings.Port).Uri, Settings.Password, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _client = null;
                await client.DisposeAsync().ConfigureAwait(false);
                ReportConnectFailure(ex, endpoint);
                throw;
            }

            _lastFailure = null;
            SetStatus(ConnectionState.Connected, $"Connected to {endpoint}");
            _log.Info(LogSource, $"Connected to {endpoint}");
            await RefreshCachesAsync(ct).ConfigureAwait(false);
            _bus.Publish(new BotEvent(ObsEvents.Connected));
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void ReportConnectFailure(Exception ex, string endpoint)
    {
        if (ex is OperationCanceledException)
        {
            SetStatus(ConnectionState.Disconnected, "Not connected");
            return;
        }

        var message = ex switch
        {
            ObsConnectionException => ex.Message,
            WebSocketException => $"Could not reach OBS at {endpoint}. Is OBS running with its WebSocket server enabled?",
            _ => $"Could not connect to OBS: {ex.Message}",
        };
        SetStatus(ConnectionState.Error, message);
        // The reconnect loop retries every few seconds; log each distinct failure once.
        if (message != _lastFailure) _log.Warn(LogSource, message);
        _lastFailure = message;
    }

    // Runs on the session's receive thread after the session ended, so no request can be pending on it.
    private void OnSessionEnded(ObsClient client, string reason)
    {
        _connectionGate.Wait();
        try
        {
            if (_client != client) return;
            _client = null;
        }
        finally
        {
            _connectionGate.Release();
        }

        _currentScene = null;
        ScenesChanged?.Invoke();
        if (_manuallyDisconnected)
        {
            SetStatus(ConnectionState.Disconnected, "Disconnected");
            _log.Info(LogSource, "Disconnected");
        }
        else
        {
            SetStatus(ConnectionState.Error, Settings.AutoConnect ? $"{reason} Reconnecting..." : reason);
            _log.Warn(LogSource, reason);
        }
        _bus.Publish(new BotEvent(ObsEvents.Disconnected, new Dictionary<string, object?> { ["reason"] = reason }));
    }

    private void OnObsEvent(ObsEvent e)
    {
        try
        {
            switch (e.EventType)
            {
                case "CurrentProgramSceneChanged":
                    OnProgramSceneChanged(e.EventData.GetStringOrNull("sceneName"));
                    break;
                case "SceneListChanged":
                    _scenes = ObsResponseParser.ParseScenes(e.EventData);
                    ScenesChanged?.Invoke();
                    break;
                case "SceneCreated" or "SceneRemoved" or "SceneNameChanged":
                    RefreshInBackground("scenes", RefreshScenesAsync);
                    break;
                case "InputCreated" or "InputRemoved" or "InputNameChanged":
                    RefreshInBackground("inputs", RefreshInputsAsync);
                    break;
                default:
                    if (ObsEventMapper.MapOutputEvent(e) is { } botEvent) _bus.Publish(botEvent);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Failed to handle OBS event {e.EventType}", ex);
        }
    }

    private void OnProgramSceneChanged(string? sceneName)
    {
        if (sceneName is null) return;
        var previous = Interlocked.Exchange(ref _currentScene, sceneName);
        ScenesChanged?.Invoke();
        _bus.Publish(ObsEventMapper.SceneChanged(sceneName, previous));
    }

    private async Task RefreshCachesAsync(CancellationToken ct)
    {
        try
        {
            await RefreshScenesAsync(ct).ConfigureAwait(false);
            await RefreshInputsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn(LogSource, $"Could not load scenes and inputs: {ex.Message}");
        }
    }

    // Event handlers run on the receive thread, which must keep reading to deliver the response: never await there.
    private void RefreshInBackground(string what, Func<CancellationToken, Task> refresh) => _ = Task.Run(async () =>
    {
        try
        {
            await refresh(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Could not refresh {what}: {ex.Message}");
        }
    });

    private async Task RefreshScenesAsync(CancellationToken ct)
    {
        var data = await RequestAsync("GetSceneList", ct: ct).ConfigureAwait(false);
        _scenes = ObsResponseParser.ParseScenes(data);
        _currentScene = data.GetStringOrNull("currentProgramSceneName") ?? _currentScene;
        ScenesChanged?.Invoke();
    }

    private async Task RefreshInputsAsync(CancellationToken ct)
    {
        _inputs = ObsResponseParser.ParseInputs(await RequestAsync("GetInputList", ct: ct).ConfigureAwait(false));
        ScenesChanged?.Invoke();
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
