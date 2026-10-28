using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Obs.Streamlabs;

/// <summary>
/// The app's connection to Streamlabs Desktop's Remote Control API: reconnects automatically, caches scenes and
/// sources, republishes scene, streaming and recording changes as bot events and offers the commands sub-actions use.
/// </summary>
public sealed class StreamlabsDesktopService : IConnectionStatus, IAsyncDisposable
{
    private const string LogSource = "Streamlabs Desktop";
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly (string Resource, string Event)[] Subscriptions =
    [
        ("ScenesService", "sceneSwitched"), ("ScenesService", "sceneAdded"), ("ScenesService", "sceneRemoved"),
        ("ScenesService", "itemAdded"), ("ScenesService", "itemRemoved"), ("ScenesService", "itemUpdated"),
        ("SourcesService", "sourceAdded"), ("SourcesService", "sourceRemoved"), ("SourcesService", "sourceUpdated"),
        ("StreamingService", "streamingStatusChange"), ("StreamingService", "recordingStatusChange"),
        ("StreamingService", "replayBufferFileWrite"),
    ];

    private readonly JsonFile<StreamlabsSettings> _settingsFile;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile StreamlabsClient? _client;
    private Task? _keepConnected;
    private volatile bool _manuallyDisconnected;
    private int _disposed;
    private int _fullRefreshPending;
    private static readonly TimeSpan FullRefreshDelay = TimeSpan.FromMilliseconds(500);
    private string? _lastFailure;
    private SlobsScene[] _scenes = [];
    private SlobsSource[] _sources = [];
    private string? _currentScene;

    public StreamlabsDesktopService(JsonFile<StreamlabsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, DefaultReconnectDelay)
    {
    }

    internal StreamlabsDesktopService(JsonFile<StreamlabsSettings> settings, EventBus bus, AppLog log, TimeSpan reconnectDelay)
    {
        _settingsFile = settings;
        _bus = bus;
        _log = log;
        _reconnectDelay = reconnectDelay;
    }

    public string Name => "Streamlabs Desktop";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public StreamlabsSettings Settings => _settingsFile.Value;
    public void SaveSettings() => _settingsFile.ScheduleSave();

    public IReadOnlyList<SlobsScene> Scenes => _scenes;
    public IReadOnlyList<string> SceneNames => _scenes.Select(s => s.Name).ToArray();
    public IReadOnlyList<SlobsSource> Sources => _sources;
    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToArray();
    public string? CurrentScene => _currentScene;
    public bool IsStreaming { get; private set; }
    public bool IsRecording { get; private set; }

    /// <summary>Raised (on a background thread) when scenes, sources or the current scene change.</summary>
    public event Action? ScenesChanged;

    public bool IsConnected => _client?.IsConnected == true;

    public Task StartAsync(CancellationToken ct = default)
    {
        _keepConnected ??= Task.Run(() => KeepConnectedAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _manuallyDisconnected = false;
        return ConnectCoreAsync(ct);
    }

    public async Task DisconnectAsync()
    {
        _manuallyDisconnected = true;
        if (_client is { } client) await client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Calls any Remote Control API method and returns its result.</summary>
    public async Task<JsonElement> CallAsync(string resource, string method, object?[]? args = null, CancellationToken ct = default)
    {
        if (_client is not { IsConnected: true } client) throw new StreamlabsException("Streamlabs Desktop is not connected.");
        return await client.CallAsync(resource, method, args, ct).ConfigureAwait(false);
    }

    // ---- Commands ----

    public async Task SetSceneAsync(string sceneName, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(sceneName, ct).ConfigureAwait(false);
        await CallAsync("ScenesService", "makeSceneActive", [scene.Id], ct).ConfigureAwait(false);
    }

    public async Task<string> GetCurrentSceneAsync(CancellationToken ct = default)
    {
        var active = await CallAsync("ScenesService", "activeScene", null, ct).ConfigureAwait(false);
        return active.GetStringOrNull("name") ?? _currentScene ?? "";
    }

    /// <summary>Shows, hides or toggles a source in a scene. Returns whether it is now visible.</summary>
    public async Task<bool> SetSourceVisibilityAsync(string sceneName, string sourceName, VisibilityMode mode, CancellationToken ct = default)
    {
        var scene = await FreshSceneAsync(sceneName, ct).ConfigureAwait(false);   // fresh visibility for Toggle
        var item = scene.Items.FirstOrDefault(i => string.Equals(i.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new StreamlabsException($"Scene '{scene.Name}' has no source named '{sourceName}'.");
        var visible = mode switch { VisibilityMode.Visible => true, VisibilityMode.Hidden => false, _ => !item.Visible };
        await CallAsync(item.ResourceId, "setVisibility", [visible], ct).ConfigureAwait(false);
        return visible;
    }

    /// <summary>Hides every source of a scene. Returns how many were hidden.</summary>
    public async Task<int> HideAllSourcesAsync(string sceneName, CancellationToken ct = default)
    {
        var scene = await FreshSceneAsync(sceneName, ct).ConfigureAwait(false);
        var visible = scene.Items.Where(i => i.Visible && !i.IsFolder).ToArray();
        foreach (var item in visible) await CallAsync(item.ResourceId, "setVisibility", [false], ct).ConfigureAwait(false);
        return visible.Length;
    }

    public Task ControlStreamingAsync(OutputCommand command, CancellationToken ct = default) =>
        ControlOutputAsync(command, "streamingStatus", "toggleStreaming", s => StreamlabsParser.StreamingState(s) ?? s != "offline", ct);

    public Task ControlRecordingAsync(OutputCommand command, CancellationToken ct = default) =>
        ControlOutputAsync(command, "recordingStatus", "toggleRecording", s => StreamlabsParser.RecordingState(s) ?? s != "offline", ct);

    /// <summary>Starts, stops or saves the replay buffer (Toggle starts it when stopped).</summary>
    public async Task ControlReplayBufferAsync(ReplayBufferCommand command, CancellationToken ct = default)
    {
        var method = command switch
        {
            ReplayBufferCommand.Start => "startReplayBuffer",
            ReplayBufferCommand.Stop => "stopReplayBuffer",
            ReplayBufferCommand.Save => "saveReplay",
            _ => (await CallAsync("StreamingService", "getModel", null, ct).ConfigureAwait(false))
                .GetStringOrNull("replayBufferStatus") is "offline" or null ? "startReplayBuffer" : "stopReplayBuffer",
        };
        await CallAsync("StreamingService", method, null, ct).ConfigureAwait(false);
    }

    /// <summary>Mutes, unmutes or toggles a source's audio. Returns whether it is now muted.</summary>
    public async Task<bool> SetSourceMuteAsync(string sourceName, MuteMode mode, CancellationToken ct = default)
    {
        await RefreshSourcesAsync(ct).ConfigureAwait(false);
        var source = FindSource(sourceName);
        var muted = mode switch { MuteMode.Mute => true, MuteMode.Unmute => false, _ => !source.Muted };
        await CallAsync($"AudioSource[\"{source.Id}\"]", "setMuted", [muted], ct).ConfigureAwait(false);
        return muted;
    }

    /// <summary>Merges settings into a source (e.g. {"text": "..."} or {"url": "..."}).</summary>
    public async Task UpdateSourceSettingsAsync(string sourceName, object settings, CancellationToken ct = default)
    {
        if (_sources.Length == 0) await RefreshSourcesAsync(ct).ConfigureAwait(false);
        await CallAsync(FindSource(sourceName).ResourceId, "updateSettings", [settings], ct).ConfigureAwait(false);
    }

    public async Task SetFilterStateAsync(string sourceName, string filterName, bool enabled, CancellationToken ct = default)
    {
        if (_sources.Length == 0) await RefreshSourcesAsync(ct).ConfigureAwait(false);
        var sourceId = _sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))?.Id
                       ?? _scenes.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))?.Id
                       ?? throw new StreamlabsException($"Streamlabs Desktop has no source named '{sourceName}'.");
        await CallAsync("SourceFiltersService", "setVisibility", [sourceId, filterName, enabled], ct).ConfigureAwait(false);
    }

    /// <summary>The filters on a source or scene, by its source id.</summary>
    public async Task<IReadOnlyList<SlobsFilter>> GetFiltersAsync(string sourceId, CancellationToken ct = default) =>
        StreamlabsParser.ParseFilters(await CallAsync("SourceFiltersService", "getFilters", [sourceId], ct).ConfigureAwait(false));

    /// <summary>Shows, hides or toggles a filter on a scene (Set Scene Filter State). Returns whether it is now visible.</summary>
    public async Task<bool> SetSceneFilterStateAsync(string sceneName, string filterName, VisibilityMode mode, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(sceneName, ct).ConfigureAwait(false);
        return await SetFilterVisibilityAsync(scene.Id, filterName, mode, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows, hides or toggles one randomly chosen filter of a scene's source, or of the scene itself when no source is
    /// given. Returns the chosen filter's name, or null when there are no filters.
    /// </summary>
    public async Task<string?> SetRandomFilterStateAsync(string sceneName, string? sourceName, VisibilityMode mode, CancellationToken ct = default)
    {
        var sourceId = await FilterOwnerIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var filters = await GetFiltersAsync(sourceId, ct).ConfigureAwait(false);
        if (filters.Count == 0) return null;
        var chosen = filters[Random.Shared.Next(filters.Count)];
        var visible = mode switch { VisibilityMode.Visible => true, VisibilityMode.Hidden => false, _ => !chosen.Visible };
        await CallAsync("SourceFiltersService", "setVisibility", [sourceId, chosen.Name, visible], ct).ConfigureAwait(false);
        return chosen.Name;
    }

    /// <summary>Hides every filter of a scene's source (Hide Source's Filters). Returns how many were hidden.</summary>
    public async Task<int> HideSourceFiltersAsync(string sceneName, string sourceName, CancellationToken ct = default)
    {
        var sourceId = await FilterOwnerIdAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var visible = (await GetFiltersAsync(sourceId, ct).ConfigureAwait(false)).Where(f => f.Visible).ToArray();
        foreach (var filter in visible)
            await CallAsync("SourceFiltersService", "setVisibility", [sourceId, filter.Name, false], ct).ConfigureAwait(false);
        return visible.Length;
    }

    /// <summary>Hides every source inside a group (a Streamlabs Desktop folder). Returns how many were hidden.</summary>
    public async Task<int> HideGroupSourcesAsync(string sceneName, string groupName, CancellationToken ct = default)
    {
        var children = await GroupChildrenAsync(sceneName, groupName, ct).ConfigureAwait(false);
        var visible = children.Where(i => i.Visible).ToArray();
        foreach (var item in visible) await CallAsync(item.ResourceId, "setVisibility", [false], ct).ConfigureAwait(false);
        return visible.Length;
    }

    /// <summary>
    /// Shows one randomly chosen source of a group and hides the others. Returns the chosen source's name, or null
    /// when the group is empty.
    /// </summary>
    public async Task<string?> SetRandomGroupSourceVisibleAsync(string sceneName, string groupName, CancellationToken ct = default)
    {
        var children = await GroupChildrenAsync(sceneName, groupName, ct).ConfigureAwait(false);
        if (children.Count == 0) return null;
        var chosen = children[Random.Shared.Next(children.Count)];
        foreach (var item in children)
        {
            var show = item.Id == chosen.Id;
            if (item.Visible != show) await CallAsync(item.ResourceId, "setVisibility", [show], ct).ConfigureAwait(false);
        }
        return chosen.Name;
    }

    private async Task<bool> SetFilterVisibilityAsync(string sourceId, string filterName, VisibilityMode mode, CancellationToken ct)
    {
        bool visible;
        if (mode == VisibilityMode.Toggle)
        {
            var filter = (await GetFiltersAsync(sourceId, ct).ConfigureAwait(false))
                         .FirstOrDefault(f => string.Equals(f.Name, filterName, StringComparison.OrdinalIgnoreCase))
                         ?? throw new StreamlabsException($"Streamlabs Desktop has no filter named '{filterName}'.");
            visible = !filter.Visible;
            filterName = filter.Name;
        }
        else
        {
            visible = mode == VisibilityMode.Visible;
        }
        await CallAsync("SourceFiltersService", "setVisibility", [sourceId, filterName, visible], ct).ConfigureAwait(false);
        return visible;
    }

    /// <summary>The source id owning the filters: the named source in the scene, or the scene itself when no source is named.</summary>
    private async Task<string> FilterOwnerIdAsync(string sceneName, string? sourceName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return (await FindSceneAsync(sceneName, ct).ConfigureAwait(false)).Id;
        var item = await FindItemAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        return item.SourceId;
    }

    private async Task<IReadOnlyList<SlobsSceneItem>> GroupChildrenAsync(string sceneName, string groupName, CancellationToken ct)
    {
        var scene = await FreshSceneAsync(sceneName, ct).ConfigureAwait(false);   // fresh visibility
        var group = scene.Items.FirstOrDefault(i => i.IsFolder && string.Equals(i.Name, groupName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new StreamlabsException($"Scene '{scene.Name}' has no group named '{groupName}'.");
        return scene.Items.Where(i => i.ParentId == group.Id && !i.IsFolder).ToArray();
    }

    /// <summary>Mirrors a source by negating its scale on one axis.</summary>
    public async Task FlipSourceAsync(string sceneName, string sourceName, FlipAxis axis, CancellationToken ct = default)
    {
        var item = await FindItemAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        var model = await CallAsync(item.ResourceId, "getModel", null, ct).ConfigureAwait(false);
        var scale = model.TryGetProperty("transform", out var t) && t.TryGetProperty("scale", out var s) ? s : default;
        var x = scale.GetDoubleOrNull("x") ?? 1;
        var y = scale.GetDoubleOrNull("y") ?? 1;
        var flipped = axis == FlipAxis.Horizontal ? new { x = -x, y } : new { x, y = -y };
        await CallAsync(item.ResourceId, "setTransform", [new { scale = flipped }], ct).ConfigureAwait(false);
    }

    public async Task RotateSourceAsync(string sceneName, string sourceName, double degrees, CancellationToken ct = default)
    {
        var item = await FindItemAsync(sceneName, sourceName, ct).ConfigureAwait(false);
        await CallAsync(item.ResourceId, "setTransform", [new { rotation = ((degrees % 360) + 360) % 360 }], ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _manuallyDisconnected = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_keepConnected is { } loop) await loop.ConfigureAwait(false);
        if (_client is { } client) await client.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // ---- Connection ----

    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Settings.AutoConnect && !_manuallyDisconnected && _client is null)
            {
                try { await ConnectCoreAsync(ct).ConfigureAwait(false); }
                catch (Exception) { /* reported through the status and the log */ }
            }
            try { await Task.Delay(_reconnectDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
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
            var client = new StreamlabsClient();
            client.EventReceived += OnEvent;
            // Off the receive thread: a failing connect may be disposing the client while holding the gate.
            client.Disconnected += reason => _ = Task.Run(() => OnSessionEnded(client, reason));
            _client = client;
            try
            {
                await client.ConnectAsync(new UriBuilder("ws", Settings.Host, Settings.Port, "api").Uri, Settings.Token, ct).ConfigureAwait(false);
                foreach (var (resource, eventName) in Subscriptions)
                    await client.SubscribeAsync(resource, eventName, ct).ConfigureAwait(false);
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
            await RefreshAllAsync(ct).ConfigureAwait(false);
            Publish(StreamlabsEvents.Connected);
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
            StreamlabsException => ex.Message,
            WebSocketException => $"Could not reach Streamlabs Desktop at {endpoint}. Is it running with Remote Control enabled?",
            _ => $"Could not connect to Streamlabs Desktop: {ex.Message}",
        };
        SetStatus(ConnectionState.Error, message);
        if (message != _lastFailure) _log.Warn(LogSource, message);
        _lastFailure = message;
    }

    private void OnSessionEnded(StreamlabsClient client, string reason)
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
        Publish(StreamlabsEvents.Disconnected, new() { ["reason"] = reason });
    }

    private void OnEvent(string resourceId, JsonElement data)
    {
        try
        {
            switch (resourceId)
            {
                case "ScenesService.sceneSwitched":
                    var scene = StreamlabsParser.ParseScene(data);
                    if (scene is null) break;
                    var previous = Interlocked.Exchange(ref _currentScene, scene.Name);
                    ScenesChanged?.Invoke();
                    Publish(StreamlabsEvents.SceneChanged, new()
                    {
                        ["sceneName"] = scene.Name, ["sceneId"] = scene.Id, ["previousScene"] = previous ?? "", ["sd.sceneName"] = scene.Name,
                    });
                    break;
                case "ScenesService.itemAdded" or "ScenesService.itemRemoved" or "ScenesService.itemUpdated"
                    when data.GetStringOrNull("sceneId") is { } changedSceneId:
                    RefreshInBackground(c => RefreshSceneAsync(changedSceneId, c));
                    break;
                case "ScenesService.sceneAdded" or "ScenesService.sceneRemoved" or "ScenesService.itemAdded" or
                    "ScenesService.itemRemoved" or "ScenesService.itemUpdated":
                    RefreshScenesSoon();
                    break;
                case "SourcesService.sourceAdded" or "SourcesService.sourceRemoved" or "SourcesService.sourceUpdated":
                    RefreshInBackground(RefreshSourcesAsync);
                    break;
                case "StreamingService.streamingStatusChange":
                    if (StreamlabsParser.StreamingState(data.ValueKind == JsonValueKind.String ? data.GetString() : null) is { } live)
                    {
                        IsStreaming = live;
                        Publish(live ? StreamlabsEvents.StreamStarted : StreamlabsEvents.StreamStopped);
                    }
                    break;
                case "StreamingService.recordingStatusChange":
                    if (StreamlabsParser.RecordingState(data.ValueKind == JsonValueKind.String ? data.GetString() : null) is { } recording)
                    {
                        IsRecording = recording;
                        Publish(recording ? StreamlabsEvents.RecordStarted : StreamlabsEvents.RecordStopped);
                    }
                    break;
                case "StreamingService.replayBufferFileWrite":
                    Publish(StreamlabsEvents.ReplayBufferSaved, new()
                    {
                        ["savedReplayPath"] = data.ValueKind == JsonValueKind.String ? data.GetString() : "",
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Failed to handle event {resourceId}", ex);
        }
    }

    private void RefreshInBackground(Func<CancellationToken, Task> refresh) => _ = Task.Run(async () =>
    {
        try { await refresh(_lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn(LogSource, $"Could not refresh: {ex.Message}"); }
    });

    private async Task RefreshAllAsync(CancellationToken ct)
    {
        try
        {
            await RefreshScenesAsync(ct).ConfigureAwait(false);
            await RefreshSourcesAsync(ct).ConfigureAwait(false);
            var model = await CallAsync("StreamingService", "getModel", null, ct).ConfigureAwait(false);
            IsStreaming = StreamlabsParser.StreamingState(model.GetStringOrNull("streamingStatus")) ?? false;
            IsRecording = StreamlabsParser.RecordingState(model.GetStringOrNull("recordingStatus")) ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn(LogSource, $"Could not load scenes and sources: {ex.Message}");
        }
    }

    private async Task RefreshScenesAsync(CancellationToken ct)
    {
        _scenes = StreamlabsParser.ParseScenes(await CallAsync("ScenesService", "getScenes", null, ct).ConfigureAwait(false));
        var activeId = await CallAsync("ScenesService", "activeSceneId", null, ct).ConfigureAwait(false);
        if (activeId.ValueKind == JsonValueKind.String)
            _currentScene = _scenes.FirstOrDefault(s => s.Id == activeId.GetString())?.Name ?? _currentScene;
        ScenesChanged?.Invoke();
    }

    /// <summary>
    /// Reloads one scene. Streamlabs Desktop limits how often ScenesService.getScenes may be called ("Reached the
    /// limit of calls"), so commands and item events reload only the scene they touch.
    /// </summary>
    private async Task<SlobsScene> RefreshSceneAsync(string sceneId, CancellationToken ct)
    {
        var fresh = StreamlabsParser.ParseScene(await CallAsync("ScenesService", "getScene", [sceneId], ct).ConfigureAwait(false))
                    ?? throw new StreamlabsException($"Streamlabs Desktop has no scene with id '{sceneId}'.");
        var scenes = _scenes;
        var index = Array.FindIndex(scenes, s => s.Id == sceneId);
        _scenes = index < 0 ? [.. scenes, fresh] : [.. scenes[..index], fresh, .. scenes[(index + 1)..]];
        ScenesChanged?.Invoke();
        return fresh;
    }

    private async Task<SlobsScene> FreshSceneAsync(string sceneName, CancellationToken ct) =>
        await RefreshSceneAsync((await FindSceneAsync(sceneName, ct).ConfigureAwait(false)).Id, ct).ConfigureAwait(false);

    /// <summary>One full scene reload for a burst of scene added/removed events (see <see cref="RefreshSceneAsync"/>).</summary>
    private void RefreshScenesSoon()
    {
        if (Interlocked.Exchange(ref _fullRefreshPending, 1) == 1) return;
        RefreshInBackground(async ct =>
        {
            await Task.Delay(FullRefreshDelay, ct).ConfigureAwait(false);
            Volatile.Write(ref _fullRefreshPending, 0);
            await RefreshScenesAsync(ct).ConfigureAwait(false);
        });
    }

    private async Task RefreshSourcesAsync(CancellationToken ct)
    {
        _sources = StreamlabsParser.ParseSources(await CallAsync("SourcesService", "getSources", null, ct).ConfigureAwait(false));
        ScenesChanged?.Invoke();
    }

    private async Task ControlOutputAsync(OutputCommand command, string statusProperty, string toggleMethod,
        Func<string?, bool> isActive, CancellationToken ct)
    {
        if (command != OutputCommand.Toggle)
        {
            var model = await CallAsync("StreamingService", "getModel", null, ct).ConfigureAwait(false);
            var active = isActive(model.GetStringOrNull(statusProperty) ?? "offline");
            if (active == (command == OutputCommand.Start)) return;   // already in the requested state
        }
        await CallAsync("StreamingService", toggleMethod, null, ct).ConfigureAwait(false);
    }

    private async Task<SlobsScene> FindSceneAsync(string sceneName, CancellationToken ct)
    {
        if (_scenes.Length == 0) await RefreshScenesAsync(ct).ConfigureAwait(false);
        var scene = _scenes.FirstOrDefault(s => string.Equals(s.Name, sceneName, StringComparison.OrdinalIgnoreCase));
        if (scene is null)
        {
            await RefreshScenesAsync(ct).ConfigureAwait(false);   // added since the last reload
            scene = _scenes.FirstOrDefault(s => string.Equals(s.Name, sceneName, StringComparison.OrdinalIgnoreCase));
        }
        return scene ?? throw new StreamlabsException($"Streamlabs Desktop has no scene named '{sceneName}'.");
    }

    private async Task<SlobsSceneItem> FindItemAsync(string sceneName, string sourceName, CancellationToken ct)
    {
        var scene = await FindSceneAsync(sceneName, ct).ConfigureAwait(false);
        return scene.Items.FirstOrDefault(i => string.Equals(i.Name, sourceName, StringComparison.OrdinalIgnoreCase))
               ?? throw new StreamlabsException($"Scene '{scene.Name}' has no source named '{sourceName}'.");
    }

    private SlobsSource FindSource(string sourceName) =>
        _sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))
        ?? throw new StreamlabsException($"Streamlabs Desktop has no source named '{sourceName}'.");

    /// <summary>
    /// Streamer.bot's connection id for the single Streamlabs Desktop connection (sd.id). Streamer.bot's is a Guid;
    /// Cuebot's is a fixed one, derived like OBS connection ids.
    /// </summary>
    public static readonly Guid ConnectionId = ObsEventMapper.ConnectionId("streamlabs-desktop:main");

    /// <summary>Publishes an event with Streamer.bot's connection args (sd.id, sd.name, sd.host) added.</summary>
    private void Publish(string type, Dictionary<string, object?>? args = null)
    {
        args ??= [];
        args["sd.id"] = ConnectionId;
        args["sd.name"] = "Main";
        args["sd.host"] = Settings.Host;
        _bus.Publish(new BotEvent(type, args));
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
