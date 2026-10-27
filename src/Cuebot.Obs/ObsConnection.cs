using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs;

/// <summary>
/// One named connection to an OBS instance: owns the obs-websocket session, reconnects automatically, caches scene,
/// input, transition, profile and scene collection names for editors and the UI, and republishes OBS activity as bot
/// events tagged with the connection's name. Commands live in ObsConnection.Commands.cs.
/// </summary>
public sealed partial class ObsConnection : IConnectionStatus, IAsyncDisposable
{
    private static readonly TimeSpan ReplaySaveTimeout = TimeSpan.FromSeconds(10);

    private readonly Action _saveSettings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly bool _isDefault;

    // Serializes connection attempts with the handling of a session's end, so their status updates never interleave.
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<(string Scene, int Id), string> _sceneItemNames = new();
    private volatile ObsClient? _client;
    private Task? _keepConnected;
    private volatile bool _manuallyDisconnected;
    private int _disposed;
    private string? _lastFailure;
    private string[] _scenes = [];
    private string[] _inputs = [];
    private string[] _transitions = [];
    private string[] _profiles = [];
    private string[] _sceneCollections = [];
    private string? _currentScene;
    private TaskCompletionSource<string>? _replaySaved;

    internal ObsConnection(ObsConnectionSettings settings, Action saveSettings, EventBus bus, AppLog log,
        TimeSpan reconnectDelay, bool isDefault)
    {
        Settings = settings;
        _saveSettings = saveSettings;
        _bus = bus;
        _log = log;
        _reconnectDelay = reconnectDelay;
        _isDefault = isDefault;
    }

    /// <summary>The connection's name (from its settings), used by sub-actions and triggers to select it.</summary>
    public string Name => Settings.Name;

    /// <summary>True for the default connection stored at the top level of obs.json.</summary>
    public bool IsDefault => _isDefault;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Live settings; call <see cref="SaveSettings"/> after changing them. Changes apply on the next connect.</summary>
    public ObsConnectionSettings Settings { get; }

    /// <summary>Scene names in OBS order. Kept after a disconnect so editors still offer suggestions.</summary>
    public IReadOnlyList<string> Scenes => _scenes;

    /// <summary>Input (source) names. Kept after a disconnect so editors still offer suggestions.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    public IReadOnlyList<string> Transitions => _transitions;
    public IReadOnlyList<string> Profiles => _profiles;
    public IReadOnlyList<string> SceneCollections => _sceneCollections;

    /// <summary>The current program scene, or null while disconnected.</summary>
    public string? CurrentScene => _currentScene;

    /// <summary>The studio mode preview scene, or null when studio mode is off or disconnected.</summary>
    public string? PreviewScene { get; private set; }

    public bool StudioModeEnabled { get; private set; }
    public string? CurrentTransition { get; private set; }
    public string? CurrentProfile { get; private set; }
    public string? CurrentSceneCollection { get; private set; }

    /// <summary>"OBS 31.0.0 / obs-websocket 5.5.0" once connected; null before.</summary>
    public string? Version { get; private set; }

    /// <summary>Raised (on a background thread) when any cached list or current value changes.</summary>
    public event Action? ScenesChanged;

    /// <summary>Raised on the receive thread for every OBS event (after the module handled it). Must not block.</summary>
    public event Action<ObsEvent>? EventReceived;

    public bool IsConnected => _client?.IsConnected == true;

    public void SaveSettings() => _saveSettings();

    private string LogSource => _isDefault ? "OBS" : $"OBS ({Name})";

    /// <summary>
    /// Starts the background loop that connects while <see cref="ObsConnectionSettings.AutoConnect"/> is on, retrying
    /// every few seconds, until the user disconnects manually or the connection is disposed. Returns immediately.
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
        if (_client is not { IsConnected: true } client) throw NotConnected();
        return await client.RequestAsync(requestType, requestData, ct).ConfigureAwait(false);
    }

    /// <summary>Sends an obs-websocket request batch; see <see cref="ObsClient.RequestBatchAsync"/>.</summary>
    public async Task<JsonElement> RequestBatchAsync(IReadOnlyList<object> requests, bool haltOnFailure = false,
        CancellationToken ct = default)
    {
        if (_client is not { IsConnected: true } client) throw NotConnected();
        return await client.RequestBatchAsync(requests, haltOnFailure, ct).ConfigureAwait(false);
    }

    private ObsConnectionException NotConnected() =>
        new(_isDefault ? "OBS is not connected." : $"OBS connection '{Name}' is not connected.");

    public async ValueTask DisposeAsync()
    {
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
            await RefreshCachesAsync(client.ServerVersion, ct).ConfigureAwait(false);
            Publish(new BotEvent(ObsEvents.Connected, new Dictionary<string, object?> { ["obsVersion"] = Version ?? "" }));
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
        PreviewScene = null;
        _replaySaved?.TrySetException(new ObsConnectionException(reason));
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
        Publish(new BotEvent(ObsEvents.Disconnected, new Dictionary<string, object?> { ["reason"] = reason }));
    }

    private void OnObsEvent(ObsEvent e)
    {
        try
        {
            HandleObsEvent(e);
            Publish(ObsEventMapper.Generic(e));
            EventReceived?.Invoke(e);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Failed to handle OBS event {e.EventType}", ex);
        }
    }

    private void HandleObsEvent(ObsEvent e)
    {
        var d = e.EventData;
        switch (e.EventType)
        {
            case "CurrentProgramSceneChanged":
                OnProgramSceneChanged(d.GetStringOrNull("sceneName"));
                return;
            case "SceneListChanged":
                _scenes = ObsResponseParser.ParseScenes(d);
                ScenesChanged?.Invoke();
                return;
            case "SceneCreated" or "SceneRemoved" or "SceneNameChanged":
                RefreshInBackground("scenes", RefreshScenesAsync);
                return;
            case "InputCreated" or "InputRemoved" or "InputNameChanged":
                RefreshInBackground("inputs", RefreshInputsAsync);
                return;
            case "SceneItemCreated" or "SceneItemRemoved":
                _sceneItemNames.Clear();
                return;
            case "SceneItemEnableStateChanged":
                PublishVisibilityInBackground(d.GetStringOrNull("sceneName") ?? "", d.GetIntOrNull("sceneItemId") ?? 0,
                    d.GetBoolOrNull("sceneItemEnabled") ?? false);
                return;
            case "SceneTransitionListChanged" or "CurrentSceneTransitionChanged":
                if (e.EventType == "CurrentSceneTransitionChanged") CurrentTransition = d.GetStringOrNull("transitionName");
                RefreshInBackground("transitions", RefreshTransitionsAsync);
                break;
            case "CurrentPreviewSceneChanged":
                PreviewScene = d.GetStringOrNull("sceneName");
                ScenesChanged?.Invoke();
                break;
            case "StudioModeStateChanged":
                StudioModeEnabled = d.GetBoolOrNull("studioModeEnabled") ?? false;
                if (!StudioModeEnabled) PreviewScene = null;
                ScenesChanged?.Invoke();
                break;
            case "CurrentProfileChanged" or "ProfileListChanged":
                RefreshInBackground("profiles", RefreshProfilesAsync);
                break;
            case "CurrentSceneCollectionChanged":
                _sceneItemNames.Clear();
                RefreshInBackground("scene collection", ct => RefreshCachesAsync(null, ct));
                break;
            case "SceneCollectionListChanged":
                RefreshInBackground("scene collections", RefreshSceneCollectionsAsync);
                break;
            case "ReplayBufferSaved":
                _replaySaved?.TrySetResult(d.GetStringOrNull("savedReplayPath") ?? "");
                break;
        }
        if (ObsEventMapper.Map(e) is { } botEvent) Publish(botEvent);
    }

    private void OnProgramSceneChanged(string? sceneName)
    {
        if (sceneName is null) return;
        var previous = Interlocked.Exchange(ref _currentScene, sceneName);
        ScenesChanged?.Invoke();
        Publish(ObsEventMapper.SceneChanged(sceneName, previous));
    }

    // SceneItemEnableStateChanged carries only the item id; resolve the source name off the receive thread.
    private void PublishVisibilityInBackground(string sceneName, int sceneItemId, bool visible) => _ = Task.Run(async () =>
    {
        string sourceName;
        try
        {
            sourceName = await GetSceneItemSourceNameAsync(sceneName, sceneItemId, _lifetime.Token).ConfigureAwait(false) ?? "";
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Could not resolve scene item {sceneItemId} in '{sceneName}': {ex.Message}");
            sourceName = "";
        }
        Publish(ObsEventMapper.SourceVisibilityChanged(sceneName, sourceName, sceneItemId, visible));
    });

    private async Task<string?> GetSceneItemSourceNameAsync(string sceneName, int sceneItemId, CancellationToken ct)
    {
        if (_sceneItemNames.TryGetValue((sceneName, sceneItemId), out var cached)) return cached;
        foreach (var item in await GetSceneItemsAsync(sceneName, ct).ConfigureAwait(false))
            _sceneItemNames[(sceneName, item.Id)] = item.SourceName;
        return _sceneItemNames.GetValueOrDefault((sceneName, sceneItemId));
    }

    private void Publish(BotEvent e) => _bus.Publish(ObsEventMapper.WithConnection(e, Name));

    private async Task RefreshCachesAsync(string? webSocketVersion, CancellationToken ct)
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

        // Optional extras: older or trimmed-down OBS builds may lack some of these, so each failure stands alone.
        await TryRefreshAsync(async c =>
        {
            var version = await RequestAsync("GetVersion", ct: c).ConfigureAwait(false);
            var obs = version.GetStringOrNull("obsVersion");
            var ws = version.GetStringOrNull("obsWebSocketVersion") ?? webSocketVersion;
            Version = obs is null ? ws is null ? Version : $"obs-websocket {ws}" : $"OBS {obs} / obs-websocket {ws}";
        }, ct).ConfigureAwait(false);
        await TryRefreshAsync(async c =>
        {
            StudioModeEnabled = (await RequestAsync("GetStudioModeEnabled", ct: c).ConfigureAwait(false))
                .GetBoolOrNull("studioModeEnabled") ?? false;
            PreviewScene = StudioModeEnabled
                ? (await RequestAsync("GetCurrentPreviewScene", ct: c).ConfigureAwait(false)).GetStringOrNull("currentPreviewSceneName")
                : null;
        }, ct).ConfigureAwait(false);
        await TryRefreshAsync(RefreshTransitionsAsync, ct).ConfigureAwait(false);
        await TryRefreshAsync(RefreshProfilesAsync, ct).ConfigureAwait(false);
        await TryRefreshAsync(RefreshSceneCollectionsAsync, ct).ConfigureAwait(false);
        ScenesChanged?.Invoke();
    }

    private static async Task TryRefreshAsync(Func<CancellationToken, Task> refresh, CancellationToken ct)
    {
        try
        {
            await refresh(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObsRequestException or ObsConnectionException or TimeoutException or
                                       KeyNotFoundException or InvalidOperationException)
        {
            // Not supported by this OBS, or the session ended meanwhile; the lists simply stay as they were.
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

    private async Task RefreshTransitionsAsync(CancellationToken ct)
    {
        var data = await RequestAsync("GetSceneTransitionList", ct: ct).ConfigureAwait(false);
        _transitions = ObsResponseParser.ParseNames(data, "transitions", "transitionName");
        CurrentTransition = data.GetStringOrNull("currentSceneTransitionName") ?? CurrentTransition;
        ScenesChanged?.Invoke();
    }

    private async Task RefreshProfilesAsync(CancellationToken ct)
    {
        var data = await RequestAsync("GetProfileList", ct: ct).ConfigureAwait(false);
        _profiles = ObsResponseParser.ParseStrings(data, "profiles");
        CurrentProfile = data.GetStringOrNull("currentProfileName") ?? CurrentProfile;
        ScenesChanged?.Invoke();
    }

    private async Task RefreshSceneCollectionsAsync(CancellationToken ct)
    {
        var data = await RequestAsync("GetSceneCollectionList", ct: ct).ConfigureAwait(false);
        _sceneCollections = ObsResponseParser.ParseStrings(data, "sceneCollections");
        CurrentSceneCollection = data.GetStringOrNull("currentSceneCollectionName") ?? CurrentSceneCollection;
        ScenesChanged?.Invoke();
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
