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
public sealed partial class ObsConnection : IConnectable, IAsyncDisposable
{
    private static readonly TimeSpan ReplaySaveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The reconnect delay doubles after each failed attempt up to this many times the first delay (2 s to 30 s).</summary>
    private const int MaxBackoffFactor = 15;

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

    // Auto-reconnect stops after a wrong password until the host, port or password changes (or Connect is clicked).
    private string? _authFailedFor;
    private volatile bool _exiting;
    private int _highVolumeSubscriptions;
    private volatile bool _publishVolumeMeters;

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

    // Output state, seeded when connecting and kept up to date from the *StateChanged events (no round trip needed).
    public bool IsStreaming { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsRecordingPaused { get; private set; }
    public bool IsReplayBufferActive { get; private set; }
    public bool IsVirtualCamActive { get; private set; }

    /// <summary>True after OBS announced it is shutting down (ExitStarted) until the next connection.</summary>
    public bool IsExiting => _exiting;

    /// <summary>
    /// Raised (on the receive thread) when OBS renames a scene, input or filter, so saved steps can follow the new name.
    /// </summary>
    public event Action<ObsRename>? Renamed;

    /// <summary>"OBS 31.0.0 / obs-websocket 5.5.0" once connected; null before.</summary>
    public string? Version { get; private set; }

    private string? _studioVersion;
    private string? _webSocketVersion;

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

    /// <summary>Connects now (no-op when already connected) and re-enables auto-reconnect (also after a wrong password).</summary>
    /// <exception cref="ObsAuthenticationException">The password is wrong or missing.</exception>
    /// <exception cref="ObsConnectionException">OBS could not be reached or refused the session.</exception>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        _manuallyDisconnected = false;
        _authFailedFor = null;
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
        return await client.RequestAsync(requestType, WithCanvas(requestType, requestData), ct).ConfigureAwait(false);
    }

    /// <summary>Sends an obs-websocket request batch; see <see cref="ObsClient.RequestBatchAsync"/>.</summary>
    public async Task<JsonElement> RequestBatchAsync(IReadOnlyList<object> requests, bool haltOnFailure = false,
        CancellationToken ct = default, ObsBatchExecution executionType = ObsBatchExecution.SerialRealtime)
    {
        if (_client is not { IsConnected: true } client) throw NotConnected();
        return await client.RequestBatchAsync(requests.Select(WithCanvas).ToArray(), haltOnFailure, ct, executionType).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends requests as one batch and fails with the first failed request's error. <see cref="ObsBatchExecution.SerialFrame"/>
    /// applies scene changes in the same video frame, so viewers never see them flip one by one.
    /// </summary>
    internal async Task RequestAllAsync(IReadOnlyList<object> requests, ObsBatchExecution executionType, CancellationToken ct)
    {
        if (requests.Count == 0) return;
        var results = await RequestBatchAsync(requests, haltOnFailure: false, ct, executionType).ConfigureAwait(false);
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("requestStatus", out var status) || status.GetBoolOrNull("result") != false) continue;
            throw new ObsRequestException(result.GetStringOrNull("requestType") ?? "", status.GetIntOrNull("code") ?? 0,
                status.GetStringOrNull("comment"));
        }
    }

    /// <summary>
    /// Sets the high-volume event categories this connection needs (ObsClient.*Subscription bits), sending a Reidentify
    /// when connected and the set changed. <paramref name="publishVolumeMeters"/> also republishes the 20-per-second
    /// InputVolumeMeters events as "Any OBS event" (only when a trigger asks for exactly that).
    /// </summary>
    public void SetHighVolumeSubscriptions(int bits, bool publishVolumeMeters = false)
    {
        _publishVolumeMeters = publishVolumeMeters;
        if (Interlocked.Exchange(ref _highVolumeSubscriptions, bits) == bits) return;
        if (_client is not { IsConnected: true } client) return;
        var wanted = ObsClient.AllLowVolumeEventSubscriptions | bits;
        if (client.EventSubscriptions == wanted) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.ReidentifyAsync(wanted, _lifetime.Token).ConfigureAwait(false);
                _log.Info(LogSource, $"Event subscriptions changed ({Describe(bits)})");
            }
            catch (Exception ex) when (ex is ObsConnectionException or OperationCanceledException)
            {
                // The session ended; the next one identifies with the new subscriptions.
            }
        });
    }

    /// <summary>The high-volume subscription bits currently wanted.</summary>
    public int HighVolumeSubscriptions => _highVolumeSubscriptions;

    private static string Describe(int bits)
    {
        var names = new List<string>();
        if ((bits & ObsClient.InputVolumeMetersSubscription) != 0) names.Add("volume meters");
        if ((bits & ObsClient.InputActiveStateChangedSubscription) != 0) names.Add("source active state");
        if ((bits & ObsClient.InputShowStateChangedSubscription) != 0) names.Add("source show state");
        if ((bits & ObsClient.SceneItemTransformChangedSubscription) != 0) names.Add("source transforms");
        return names.Count == 0 ? "no high-volume events" : "with " + string.Join(", ", names);
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

    // Retries with a capped exponential backoff and jitter (2 s, 4 s, 8 s ... 30 s by default), back to the first delay
    // after a successful connection or when OBS announced that it is closing.
    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        var delay = _reconnectDelay;
        var maxDelay = _reconnectDelay * MaxBackoffFactor;
        while (!ct.IsCancellationRequested)
        {
            if (_client is not null) delay = _reconnectDelay;
            else if (Settings.AutoConnect && !_manuallyDisconnected && !AuthBlocked)
            {
                try
                {
                    await ConnectCoreAsync(ct).ConfigureAwait(false);
                    delay = _reconnectDelay;
                }
                catch (Exception)
                {
                    // Already reported through the status and the log; try again after the delay.
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
                }
            }
            try
            {
                await Task.Delay(delay * (0.8 + Random.Shared.NextDouble() * 0.4), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private string SettingsKey => $"{Settings.Host}\n{Settings.Port}\n{Settings.Password}";

    /// <summary>True while the last attempt failed on the password and host, port and password are unchanged.</summary>
    private bool AuthBlocked => _authFailedFor is { } key && key == SettingsKey;

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
                await client.ConnectAsync(new UriBuilder("ws", Settings.Host, Settings.Port).Uri, Settings.Password, ct,
                    ObsClient.AllLowVolumeEventSubscriptions | _highVolumeSubscriptions).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _client = null;
                await client.DisposeAsync().ConfigureAwait(false);
                ReportConnectFailure(ex, endpoint);
                throw;
            }

            _lastFailure = null;
            _authFailedFor = null;
            _exiting = false;
            SetStatus(ConnectionState.Connected, $"Connected to {endpoint}");
            _log.Info(LogSource, $"Connected to {endpoint}");
            await RefreshCachesAsync(client.ServerVersion, ct).ConfigureAwait(false);
            Publish(new BotEvent(ObsEvents.Connected, new Dictionary<string, object?>
            {
                ["obsVersion"] = Version ?? "", ["studioVersion"] = _studioVersion ?? "", ["websocketVersion"] = _webSocketVersion ?? "",
                ["host"] = Settings.Host,
            }));
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

        if (ex is ObsAuthenticationException) _authFailedFor = SettingsKey;
        var message = ex switch
        {
            ObsAuthenticationException when Settings.AutoConnect =>
                $"{ex.Message} Cuebot stops retrying until the password, host or port changes, or you click Connect.",
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
        IsStreaming = IsRecording = IsRecordingPaused = IsReplayBufferActive = IsVirtualCamActive = false;
        _replaySaved?.TrySetException(new ObsConnectionException(reason));
        OnSessionEndedExtras();
        ScenesChanged?.Invoke();
        var exiting = _exiting;
        if (_manuallyDisconnected)
        {
            SetStatus(ConnectionState.Disconnected, "Disconnected");
            _log.Info(LogSource, "Disconnected");
        }
        else if (exiting)
        {
            // OBS said it is closing (ExitStarted): an expected end, not a warning.
            reason = "OBS was closed.";
            SetStatus(ConnectionState.Disconnected, Settings.AutoConnect ? "OBS was closed. Reconnecting when it is back..." : reason);
            _log.Info(LogSource, reason);
        }
        else
        {
            SetStatus(ConnectionState.Error, Settings.AutoConnect ? $"{reason} Reconnecting..." : reason);
            _log.Warn(LogSource, reason);
        }
        Publish(new BotEvent(ObsEvents.Disconnected,
            new Dictionary<string, object?> { ["reason"] = reason, ["host"] = Settings.Host, ["obsClosing"] = exiting }));
    }

    private void OnObsEvent(ObsEvent e)
    {
        try
        {
            HandleObsEvent(e);
            // Volume meters arrive 20 times a second; only republish them when a trigger asks for exactly that.
            if (e.EventType != "InputVolumeMeters" || _publishVolumeMeters) Publish(ObsEventMapper.Generic(e));
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
                // Scene item names are cached per scene name; a renamed or removed scene makes them stale.
                if (e.EventType != "SceneCreated") _sceneItemNames.Clear();
                if (e.EventType == "SceneNameChanged") RaiseRenamed(ObsRenameKind.Scene, d.GetStringOrNull("oldSceneName"), d.GetStringOrNull("sceneName"));
                RefreshInBackground("scenes", RefreshScenesAsync);
                if (e.EventType == "SceneCreated" ? d.GetBoolOrNull("isGroup") == true : true) RefreshInBackground("groups", RefreshGroupsAsync);
                break;
            case "InputCreated" or "InputRemoved" or "InputNameChanged":
                // The cache maps scene items to source names, so a renamed source must be looked up again.
                if (e.EventType == "InputNameChanged")
                {
                    _sceneItemNames.Clear();
                    RaiseRenamed(ObsRenameKind.Input, d.GetStringOrNull("oldInputName"), d.GetStringOrNull("inputName"));
                }
                RefreshInBackground("inputs", RefreshInputsAsync);
                break;
            case "SourceFilterNameChanged":
                RaiseRenamed(ObsRenameKind.Filter, d.GetStringOrNull("oldFilterName"), d.GetStringOrNull("filterName"), d.GetStringOrNull("sourceName"));
                break;
            case "ExitStarted":
                _exiting = true;
                break;
            case "StreamStateChanged":
                IsStreaming = d.GetBoolOrNull("outputActive") ?? IsStreaming;
                ScenesChanged?.Invoke();
                break;
            case "RecordStateChanged":
                IsRecording = d.GetBoolOrNull("outputActive") ?? IsRecording;
                IsRecordingPaused = IsRecording && d.GetStringOrNull("outputState") == "OBS_WEBSOCKET_OUTPUT_PAUSED";
                ScenesChanged?.Invoke();
                break;
            case "ReplayBufferStateChanged":
                IsReplayBufferActive = d.GetBoolOrNull("outputActive") ?? IsReplayBufferActive;
                ScenesChanged?.Invoke();
                break;
            case "VirtualcamStateChanged":
                IsVirtualCamActive = d.GetBoolOrNull("outputActive") ?? IsVirtualCamActive;
                ScenesChanged?.Invoke();
                break;
            case "SceneItemCreated" or "SceneItemRemoved":
                _sceneItemNames.Clear();
                return;
            case "SceneItemEnableStateChanged":
                PublishVisibilityInBackground(d.GetStringOrNull("sceneName") ?? "", d.GetIntOrNull("sceneItemId") ?? 0,
                    d.GetBoolOrNull("sceneItemEnabled") ?? false);
                return;
            case "CurrentSceneTransitionChanged":
                // obs-websocket 5 has no "transition list changed" event; the list is reloaded here, on every
                // connection and when the scene collection changes.
                CurrentTransition = d.GetStringOrNull("transitionName");
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
        HandleObsEventExtras(e);
        if (ObsEventMapper.Map(e) is { } botEvent) Publish(botEvent);
    }

    private void RaiseRenamed(ObsRenameKind kind, string? oldName, string? newName, string? sourceName = null)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
        Renamed?.Invoke(new ObsRename(kind, oldName, newName, sourceName));
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

    /// <summary>
    /// The source name of a scene item. Scene item events name the group (not the scene) for items inside a group, and
    /// GetSceneItemList only accepts scenes, so this asks GetSceneItemSource (OBS 30.1+) first and falls back to the
    /// scene list, then the group list.
    /// </summary>
    internal async Task<string?> GetSceneItemSourceNameAsync(string sceneName, int sceneItemId, CancellationToken ct)
    {
        if (_sceneItemNames.TryGetValue((sceneName, sceneItemId), out var cached)) return cached;
        try
        {
            var source = await RequestAsync("GetSceneItemSource", new { sceneName, sceneItemId }, ct).ConfigureAwait(false);
            if (source.GetStringOrNull("sourceName") is { } name)
            {
                _sceneItemNames[(sceneName, sceneItemId)] = name;
                return name;
            }
        }
        catch (ObsRequestException)
        {
            // Older OBS (no GetSceneItemSource) or the item is gone; try the lists.
        }
        ObsRequestException? failure = null;
        foreach (var group in new[] { false, true })
        {
            try
            {
                await GetSceneItemsAsync(sceneName, ct, group).ConfigureAwait(false);
                if (_sceneItemNames.TryGetValue((sceneName, sceneItemId), out var found)) return found;
            }
            catch (ObsRequestException ex)
            {
                failure ??= ex;
            }
        }
        if (failure is not null) throw failure;
        return null;
    }

    private void Publish(BotEvent e) => _bus.Publish(ObsEventMapper.WithConnection(e,
        new ObsEventMapper.ObsSource(Name, Settings.Host, _studioVersion ?? "", _webSocketVersion ?? "")));

    /// <summary>Requests of the startup snapshot, sent as one batch (one round trip, which matters for a remote OBS).</summary>
    internal static string[] SnapshotRequests =>
    [
        "GetSceneList", "GetInputList", "GetVersion", "GetStudioModeEnabled", "GetCurrentPreviewScene", "GetSceneTransitionList",
        "GetProfileList", "GetSceneCollectionList", "GetStreamStatus", "GetRecordStatus", "GetReplayBufferStatus",
        "GetVirtualCamStatus", .. ExtraSnapshotRequests,
    ];

    private async Task RefreshCachesAsync(string? webSocketVersion, CancellationToken ct)
    {
        JsonElement results;
        try
        {
            results = await RequestBatchAsync(SnapshotRequests.Select(r => (object)new { requestType = r }).ToArray(), ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn(LogSource, $"Could not load scenes and inputs: {ex.Message}");
            return;
        }

        // Each answer stands alone: older or trimmed-down OBS builds lack some requests, and the preview scene fails
        // while studio mode is off.
        var answers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var result in results.GetArrayItems())
        {
            if (result.GetStringOrNull("requestType") is not { } type) continue;
            if (!result.TryGetProperty("requestStatus", out var status) || status.GetBoolOrNull("result") != true)
            {
                if (type is "GetSceneList" or "GetInputList")
                    _log.Warn(LogSource, $"Could not load {(type == "GetSceneList" ? "scenes" : "inputs")}: {status.GetStringOrNull("comment") ?? "request failed"}");
                continue;
            }
            answers[type] = result.TryGetProperty("responseData", out var data) ? data : default;
        }

        if (answers.TryGetValue("GetSceneList", out var scenes))
        {
            _scenes = ObsResponseParser.ParseScenes(scenes);
            _currentScene = scenes.GetStringOrNull("currentProgramSceneName") ?? _currentScene;
        }
        if (answers.TryGetValue("GetInputList", out var inputs)) _inputs = ObsResponseParser.ParseInputs(inputs);
        var obs = answers.GetValueOrDefault("GetVersion").GetStringOrNull("obsVersion");
        var ws = answers.GetValueOrDefault("GetVersion").GetStringOrNull("obsWebSocketVersion") ?? webSocketVersion;
        if (obs is not null || ws is not null)
        {
            (_studioVersion, _webSocketVersion) = (obs ?? _studioVersion, ws);
            Version = obs is null ? $"obs-websocket {ws}" : $"OBS {obs} / obs-websocket {ws}";
        }
        StudioModeEnabled = answers.GetValueOrDefault("GetStudioModeEnabled").GetBoolOrNull("studioModeEnabled") ?? false;
        PreviewScene = StudioModeEnabled ? answers.GetValueOrDefault("GetCurrentPreviewScene").GetStringOrNull("currentPreviewSceneName") : null;
        if (answers.TryGetValue("GetSceneTransitionList", out var transitions)) ApplyTransitions(transitions);
        if (answers.TryGetValue("GetProfileList", out var profiles)) ApplyProfiles(profiles);
        if (answers.TryGetValue("GetSceneCollectionList", out var collections)) ApplySceneCollections(collections);
        IsStreaming = answers.GetValueOrDefault("GetStreamStatus").GetBoolOrNull("outputActive") ?? false;
        IsRecording = answers.GetValueOrDefault("GetRecordStatus").GetBoolOrNull("outputActive") ?? false;
        IsRecordingPaused = answers.GetValueOrDefault("GetRecordStatus").GetBoolOrNull("outputPaused") ?? false;
        IsReplayBufferActive = answers.GetValueOrDefault("GetReplayBufferStatus").GetBoolOrNull("outputActive") ?? false;
        IsVirtualCamActive = answers.GetValueOrDefault("GetVirtualCamStatus").GetBoolOrNull("outputActive") ?? false;
        ApplyExtraSnapshot(answers);
        ScenesChanged?.Invoke();
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
        ApplyTransitions(await RequestAsync("GetSceneTransitionList", ct: ct).ConfigureAwait(false));
        ScenesChanged?.Invoke();
    }

    private void ApplyTransitions(JsonElement data)
    {
        _transitions = ObsResponseParser.ParseNames(data, "transitions", "transitionName");
        CurrentTransition = data.GetStringOrNull("currentSceneTransitionName") ?? CurrentTransition;
    }

    private async Task RefreshProfilesAsync(CancellationToken ct)
    {
        ApplyProfiles(await RequestAsync("GetProfileList", ct: ct).ConfigureAwait(false));
        ScenesChanged?.Invoke();
    }

    private void ApplyProfiles(JsonElement data)
    {
        _profiles = ObsResponseParser.ParseStrings(data, "profiles");
        CurrentProfile = data.GetStringOrNull("currentProfileName") ?? CurrentProfile;
    }

    private async Task RefreshSceneCollectionsAsync(CancellationToken ct)
    {
        ApplySceneCollections(await RequestAsync("GetSceneCollectionList", ct: ct).ConfigureAwait(false));
        ScenesChanged?.Invoke();
    }

    private void ApplySceneCollections(JsonElement data)
    {
        _sceneCollections = ObsResponseParser.ParseStrings(data, "sceneCollections");
        CurrentSceneCollection = data.GetStringOrNull("currentSceneCollectionName") ?? CurrentSceneCollection;
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
