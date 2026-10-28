using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Meld;

/// <summary>
/// Meld Studio's official WebChannel API: a Qt WebChannel server on ws://host:13376 publishing one object, <c>meld</c>,
/// with the <c>session</c> (scenes, layers, effects, tracks), <c>isStreaming</c>/<c>isRecording</c> properties, their
/// change notifications, the <c>gainUpdated</c> signal and slots such as showScene/toggleLayer/sendCommand. Session
/// updates are diffed into Cuebot events; commands resolve names to Meld's item ids.
/// </summary>
public sealed class MeldStudioService : StreamAppConnection
{
    public const string ObjectName = "meld";
    private const string ObserverContext = "cuebot";

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, (double Gain, bool Muted)> _gains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedTracks = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;
    private volatile QWebChannelObject? _meld;
    private volatile MeldSession _session = MeldSession.Empty;

    public MeldStudioService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal MeldStudioService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log,
        Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("Meld Studio", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public MeldStudioSettings Settings => _settings.Value.Meld;

    public void SaveSettings() => _settings.ScheduleSave();

    public MeldSession Session => _session;

    public bool IsRecording { get; private set; }

    public bool IsStreaming { get; private set; }

    /// <summary>Meld's WebChannel API version (1 when Meld does not publish <c>version</c>); 0 while disconnected.</summary>
    public int ApiVersion { get; private set; }

    /// <summary>The methods Meld published (for diagnostics and the raw invoke sub-action).</summary>
    public IReadOnlyCollection<string> Methods => _meld?.MethodNames ?? [];

    /// <summary>Last gain (0-1) Meld reported for a track through <c>gainUpdated</c>, if any.</summary>
    public double? TrackGain(string trackId) => _gains.TryGetValue(trackId, out var g) ? g.Gain : null;

    /// <summary>Raised (on a background thread) when the session, recording or streaming state changed.</summary>
    public event Action? DataChanged;

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 13376;

    public string ConnectionId
    {
        get
        {
            if (Settings.ConnectionId.Length == 0)
            {
                Settings.ConnectionId = Guid.NewGuid().ToString();
                SaveSettings();
            }
            return Settings.ConnectionId;
        }
    }

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private protected override (Uri Url, string Display) Endpoint()
    {
        var host = Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;
        return (new Uri($"ws://{host}:{Port}"), $"{Host}:{Port}");
    }

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["meldStudio.host"] = Host,
        ["meldStudio.id"] = ConnectionId,
        ["meldStudio.name"] = Settings.Name.Trim().Length > 0 ? Settings.Name.Trim() : "Meld Studio",
        ["meldStudio.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent($"{MeldEvents.Prefix}.{name}", args));
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task Send(string text, CancellationToken token) => transport.SendAsync(text, token);
        var pump = PumpAsync(transport, session.Token);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var objects = await RequestAsync(Send, id, QWebChannel.InitMessage(id), ct).ConfigureAwait(false);
            if (objects.ValueKind != JsonValueKind.Object || !objects.TryGetProperty(ObjectName, out var description))
                throw new StreamAppRequestException("The WebChannel server at this address does not publish Meld Studio's \"meld\" object.");
            var meld = new QWebChannelObject(ObjectName, description);
            var versionValue = meld.Property("version");
            ApiVersion = versionValue is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var version) ? version : 1;
            IsRecording = meld.Property("isRecording") is { ValueKind: JsonValueKind.True };
            IsStreaming = meld.Property("isStreaming") is { ValueKind: JsonValueKind.True };
            _session = MeldSession.Parse(meld.Property("session"));
            _meld = meld;
            // Tell the server we processed the init data so it starts sending property updates.
            await transport.SendAsync(QWebChannel.IdleMessage(), ct).ConfigureAwait(false);
            MarkConnected($"Connected to Meld Studio at {Host}:{Port} (WebChannel API v{ApiVersion}, {_session.Scenes.Count} scenes)");
            DataChanged?.Invoke();
            if (Settings.ObserveTrackGain) await ObserveGainsAsync(meld, ct).ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session over.
            }
        }
    }

    private protected override void OnSessionEnded()
    {
        _meld = null;
        ApiVersion = 0;
        lock (_observedTracks) _observedTracks.Clear();
        _gains.Clear();
        DataChanged?.Invoke();
    }

    private async Task ObserveGainsAsync(QWebChannelObject meld, CancellationToken ct)
    {
        if (meld.SignalIndex("gainUpdated") is not { } signal || !meld.HasMethod("registerTrackObserver")) return;
        try
        {
            await SendAsync(QWebChannel.Connect(ObjectName, signal), ct).ConfigureAwait(false);
            await RegisterNewTracksAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException or StreamAppNotConnectedException)
        {
            Log.Debug(Name, $"Track gain observation unavailable: {ex.Message}");
        }
    }

    private async Task RegisterNewTracksAsync(CancellationToken ct)
    {
        string[] fresh;
        lock (_observedTracks) fresh = _session.Tracks.Select(t => t.Id).Where(_observedTracks.Add).ToArray();
        foreach (var trackId in fresh) await InvokeAsync("registerTrackObserver", [ObserverContext, trackId], ct).ConfigureAwait(false);
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
                foreach (var message in QWebChannel.Messages(text))
                    await HandleAsync(transport, message, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    private async Task HandleAsync(IWebSocketTransport transport, JsonElement message, CancellationToken ct)
    {
        switch (QWebChannel.Type(message))
        {
            case QWebChannel.Response:
                if (QWebChannel.Id(message) is { } id && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(message.TryGetProperty("data", out var data) ? data.Clone() : default);
                break;
            case QWebChannel.PropertyUpdate:
                if (message.TryGetProperty("data", out var updates) && updates.ValueKind == JsonValueKind.Array)
                    foreach (var update in updates.EnumerateArray())
                        if (update.Str("object") == ObjectName && _meld is { } meld && update.TryGetProperty("properties", out var properties))
                            ApplyChanges(meld, meld.ApplyUpdate(properties));
                // qwebchannel.js answers every property update with "idle"; the server waits for it before sending more.
                await transport.SendAsync(QWebChannel.IdleMessage(), ct).ConfigureAwait(false);
                break;
            case QWebChannel.Signal:
                if (message.Str("object") == ObjectName && _meld is { } m && message.TryGetProperty("signal", out var s) && s.TryGetInt32(out var index)
                    && m.SignalName(index) == "gainUpdated" && message.TryGetProperty("args", out var args))
                    OnGainUpdated(args);
                break;
        }
    }

    private void ApplyChanges(QWebChannelObject meld, IReadOnlyList<string> changed)
    {
        if (changed.Count == 0) return;
        try
        {
            if (changed.Contains("isRecording"))
            {
                var recording = meld.Property("isRecording") is { ValueKind: JsonValueKind.True };
                if (recording != IsRecording)
                {
                    IsRecording = recording;
                    Publish(recording ? "recordingStarted" : "recordingStopped", []);
                }
            }
            if (changed.Contains("isStreaming"))
            {
                var streaming = meld.Property("isStreaming") is { ValueKind: JsonValueKind.True };
                if (streaming != IsStreaming)
                {
                    IsStreaming = streaming;
                    Publish(streaming ? "streamingStarted" : "streamingStopped", []);
                }
            }
            if (changed.Contains("session")) ApplySession(MeldSession.Parse(meld.Property("session")));
        }
        catch (Exception ex)
        {
            Log.Error(Name, "Could not process a Meld Studio update", ex);
        }
        DataChanged?.Invoke();
    }

    private void ApplySession(MeldSession next)
    {
        var previous = _session;
        _session = next;
        var oldScene = previous.CurrentScene;
        var newScene = next.CurrentScene;
        if (newScene is not null && !string.Equals(oldScene?.Id, newScene.Id, StringComparison.OrdinalIgnoreCase))
        {
            Publish("sceneChanged", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["meldStudio.sceneName"] = newScene.Name,
                ["meldStudio.sceneId"] = newScene.Id,
                ["meldStudio.oldSceneName"] = oldScene?.Name ?? "",
                ["meldStudio.oldSceneId"] = oldScene?.Id ?? "",
            });
        }
        foreach (var change in MeldSessionDiff.Compare(previous, next)) PublishEvent(change.Event, change.Args);
        // Meld drops every track observer when it rebuilds its audio tracks (restoring or reopening a session does that
        // without closing the socket, and the track ids come back unchanged), so Meld's own Stream Deck plugin
        // registers again on every session change; so does Cuebot. Known gains keep the repeat reports quiet.
        lock (_observedTracks) _observedTracks.Clear();
        if (Settings.ObserveTrackGain && _meld is { } meld && meld.HasMethod("registerTrackObserver") && IsConnected)
            _ = Task.Run(async () =>
            {
                try
                {
                    await RegisterNewTracksAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug(Name, $"Could not observe new tracks: {ex.Message}");
                }
            });
    }

    private void OnGainUpdated(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2 || args[0].ValueKind != JsonValueKind.String) return;
        var trackId = args[0].GetString()!;
        var gain = args[1].ValueKind == JsonValueKind.Number ? args[1].GetDouble() : 0;
        var muted = args.GetArrayLength() > 2 && args[2].ValueKind == JsonValueKind.True;
        // The signal fires "when the gain or mute status of a track changes", and Meld also sends the current value as
        // soon as an observer registers: that first report is the baseline, not a change.
        var known = _gains.TryGetValue(trackId, out var old);
        _gains[trackId] = (gain, muted);
        if (!known || (Math.Abs(old.Gain - gain) < 1e-9 && old.Muted == muted)) return;
        var track = _session.Get(trackId);
        PublishEvent(MeldEvents.TrackGainChanged, new Dictionary<string, object?>
        {
            ["trackId"] = trackId, ["trackName"] = track is null ? "" : _session.DisplayName(track), ["gain"] = gain, ["muted"] = muted,
        });
    }

    /// <summary>Publishes "meldstudio.event" with Streamer.bot's argument names (meldStudioEvent.*).</summary>
    private void PublishEvent(string eventName, Dictionary<string, object?> payload)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["meldStudioEvent.event"] = eventName,
            ["meldStudioEvent._json"] = JsonUtil.Serialize(payload),
        };
        Flatten("meldStudioEvent", payload, args);
        Publish("event", args);
    }

    private static void Flatten(string prefix, Dictionary<string, object?> values, Dictionary<string, object?> into)
    {
        foreach (var (key, value) in values)
        {
            if (value is Dictionary<string, object?> nested) Flatten($"{prefix}.{key}", nested, into);
            else into[$"{prefix}.{key}"] = value;
        }
    }

    // ---- Requests ----

    private async Task<JsonElement> RequestAsync(Func<string, CancellationToken, Task> send, long id, string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await send(message, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Meld Studio did not answer in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Calls a method of Meld's <c>meld</c> object and returns its result (often null).</summary>
    public async Task<JsonElement> InvokeAsync(string method, IReadOnlyList<object?> args, CancellationToken ct = default)
    {
        EnsureConnected();
        var meld = _meld ?? throw new StreamAppNotConnectedException(Name);
        if (!meld.HasMethod(method))
            throw new StreamAppRequestException($"This Meld Studio version does not offer '{method}' (WebChannel API v{ApiVersion}). Update Meld Studio.");
        var id = Interlocked.Increment(ref _nextId);
        return await RequestAsync(SendAsync, id, QWebChannel.Invoke(id, ObjectName, method, args), ct).ConfigureAwait(false);
    }

    private MeldSession LiveSession()
    {
        EnsureConnected();
        return _session;
    }

    private static bool Want(SwitchMode mode, bool current) => Steps.Desired(mode, current);

    // ---- Commands ----

    public async Task<MeldItem> ShowSceneAsync(string scene, CancellationToken ct = default)
    {
        var item = LiveSession().FindScene(scene);
        await InvokeAsync("showScene", [item.Id], ct).ConfigureAwait(false);
        return item;
    }

    public async Task<MeldItem> StageSceneAsync(string scene, CancellationToken ct = default)
    {
        var item = LiveSession().FindScene(scene);
        // Meld resolves setStagedScene through its horizontal scenes only: staging a vertical twin does nothing.
        if (item.Vertical) throw new StreamAppRequestException($"Meld Studio cannot stage the vertical scene '{item.Name}'.");
        await InvokeAsync("setStagedScene", [item.Id], ct).ConfigureAwait(false);
        return item;
    }

    public Task ShowStagedSceneAsync(CancellationToken ct = default) => InvokeAsync("showStagedScene", [], ct);

    /// <summary>Shows/hides a layer (toggled only when its state differs). Returns the new visibility.</summary>
    public async Task<bool> SetLayerVisibilityAsync(string scene, string layer, SwitchMode mode, CancellationToken ct = default)
    {
        var item = LiveSession().FindLayer(scene, layer);
        var desired = Want(mode, item.Visible);
        if (desired != item.Visible) await InvokeAsync("toggleLayer", [item.Parent, item.Id], ct).ConfigureAwait(false);
        return desired;
    }

    public async Task<bool> SetEffectEnabledAsync(string scene, string layer, string effect, SwitchMode mode, CancellationToken ct = default)
    {
        var session = LiveSession();
        var layerItem = session.FindLayer(scene, layer);
        var effectItem = session.FindEffect(layerItem, effect);
        var desired = Want(mode, effectItem.Enabled);
        if (desired != effectItem.Enabled)
            await InvokeAsync("toggleEffect", [layerItem.Parent, layerItem.Id, effectItem.Id], ct).ConfigureAwait(false);
        return desired;
    }

    public Task<bool> SetTrackMutedAsync(string track, SwitchMode mode, CancellationToken ct = default) =>
        SetTrackMutedCoreAsync(LiveSession().FindTrack(track), mode, ct);

    public Task<bool> SetLayerTrackMutedAsync(string scene, string layer, SwitchMode mode, CancellationToken ct = default)
    {
        var session = LiveSession();
        return SetTrackMutedCoreAsync(session.TrackOfLayer(session.FindLayer(scene, layer)), mode, ct);
    }

    private async Task<bool> SetTrackMutedCoreAsync(MeldItem track, SwitchMode mode, CancellationToken ct)
    {
        var desired = Want(mode, track.Muted);
        if (desired != track.Muted) await InvokeAsync("toggleMute", [track.Id], ct).ConfigureAwait(false);
        return desired;
    }

    public async Task<bool> SetTrackMonitoringAsync(string track, SwitchMode mode, CancellationToken ct = default)
    {
        var item = LiveSession().FindTrack(track);
        var desired = Want(mode, item.Monitoring);
        if (desired != item.Monitoring) await InvokeAsync("toggleMonitor", [item.Id], ct).ConfigureAwait(false);
        return desired;
    }

    /// <summary>Sets a track's gain (0.0-1.0, Meld's fader scale).</summary>
    public async Task SetTrackGainAsync(string track, double gain, CancellationToken ct = default)
    {
        var item = LiveSession().FindTrack(track);
        await InvokeAsync("setGain", [item.Id, Math.Clamp(gain, 0, 1)], ct).ConfigureAwait(false);
    }

    /// <summary>Starts/stops/toggles recording. Uses the documented toggleRecord slot only when the state must change.</summary>
    public async Task<bool> SetRecordingAsync(SwitchMode mode, CancellationToken ct = default)
    {
        EnsureConnected();
        var desired = Want(mode, IsRecording);
        if (desired != IsRecording) await InvokeAsync("toggleRecord", [], ct).ConfigureAwait(false);
        return desired;
    }

    public async Task<bool> SetStreamingAsync(SwitchMode mode, CancellationToken ct = default)
    {
        EnsureConnected();
        var desired = Want(mode, IsStreaming);
        if (desired != IsStreaming) await InvokeAsync("toggleStream", [], ct).ConfigureAwait(false);
        return desired;
    }

    public Task TakeScreenshotAsync(bool vertical = false, CancellationToken ct = default) =>
        SendCommandAsync(vertical ? "meld.screenshot.vertical" : "meld.screenshot", ct);

    /// <summary>Runs one of Meld's commands (meld.screenshot, meld.recordClip, meld.replay.show, ...).</summary>
    public Task SendCommandAsync(string command, CancellationToken ct = default) =>
        InvokeAsync("sendCommand", [Steps.Require(command, "Command")], ct);

    /// <summary>Records a clip; <paramref name="seconds"/> 0-90 (null or 0 = the maximum available).</summary>
    public Task RecordClipAsync(double? seconds, CancellationToken ct = default)
    {
        EnsureConnected();
        if (seconds is > 0 && _meld?.HasMethod("sendCommandWithArgs") == true)
            return InvokeAsync("sendCommandWithArgs", ["meld.recordClip", new object[] { Math.Clamp(seconds.Value, 0, 90) }], ct);
        return SendCommandAsync("meld.recordClip", ct);
    }

    /// <summary>Changes a property of a session item (API v2): e.g. a browser layer's url, a layer's name or position.</summary>
    public async Task SetPropertyAsync(string objectId, string property, object? value, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_meld?.HasMethod("setProperty") != true)
            throw new StreamAppRequestException("Changing properties needs Meld Studio's WebChannel API v2; update Meld Studio.");
        await InvokeAsync("setProperty", [Steps.Require(objectId, "Object id"), Steps.Require(property, "Property"), value], ct).ConfigureAwait(false);
    }

    public async Task<MeldItem> SetBrowserLayerUrlAsync(string scene, string layer, string url, CancellationToken ct = default)
    {
        var item = LiveSession().FindLayer(scene, layer);
        await SetPropertyAsync(item.Id, "url", Steps.Require(url, "URL"), ct).ConfigureAwait(false);
        return item;
    }

    /// <summary>
    /// Changes the file an image layer (Meld's <c>source</c> property) or media layer (<c>mediaSource</c>) shows (API v2).
    /// A local path is sent as a file:// URL, as Meld reports them; a URL is sent as it is. Other layers are refused.
    /// </summary>
    public async Task<MeldItem> SetLayerSourceAsync(string scene, string layer, string source, CancellationToken ct = default)
    {
        var item = LiveSession().FindLayer(scene, layer);
        var property = item.Json.TryGetProperty("mediaSource", out _) ? "mediaSource"
            : item.Json.TryGetProperty("source", out _) ? "source"
            : throw new StreamAppRequestException($"Layer '{item.Name}' is not an image or media layer.");
        await SetPropertyAsync(item.Id, property, SourceUrl(Steps.Require(source, "Source")), ct).ConfigureAwait(false);
        return item;
    }

    /// <summary>A file path as a file:// URL; anything that already has a scheme (file://, https://...) unchanged.</summary>
    internal static string SourceUrl(string source)
    {
        source = source.Trim().Trim('"');
        return source.Contains("://", StringComparison.Ordinal) ? source : new Uri(Path.GetFullPath(source)).AbsoluteUri;
    }

    /// <summary>Media layer control: play, pause or seek (seconds).</summary>
    public async Task MediaAsync(string scene, string layer, string action, double seconds, CancellationToken ct = default)
    {
        var item = LiveSession().FindLayer(scene, layer);
        switch (action.Trim().ToLowerInvariant())
        {
            case "play":
            case "pause":
                await InvokeAsync("callFunction", [item.Id, action.Trim().ToLowerInvariant()], ct).ConfigureAwait(false);
                break;
            case "seek":
            case "seekto":
                await InvokeAsync("callFunctionWithArgs", [item.Id, "seekTo", new object[] { Math.Max(0, seconds) }], ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Media action must be Play, Pause or Seek (got '{action}').");
        }
    }

    /// <summary>
    /// Sends a widget event (STOPWATCH_RESET, CONFETTIPOP_TRIGGER, SUBATHONTIMER_ADDTIME, ...). Always two arguments:
    /// events without data send <c>false</c>, as Meld's own Stream Deck plugin does (a one-argument call by name would
    /// match no overload if the slot only has the two-parameter form).
    /// </summary>
    public Task SendStreamEventAsync(string type, double? amount, CancellationToken ct = default) =>
        InvokeAsync("sendStreamEvent",
            [Steps.Require(type, "Event type"), amount is { } a ? new Dictionary<string, object> { ["amount"] = a } : false], ct);

}
