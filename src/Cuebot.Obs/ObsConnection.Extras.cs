using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>One canvas (OBS 32.1+).</summary>
public sealed record ObsCanvas(string Name, string Uuid);

/// <summary>What OBS renamed (see <see cref="ObsConnection.Renamed"/>).</summary>
public enum ObsRenameKind { Scene, Input, Filter }

/// <param name="SourceName">For filters: the source (after any rename) the filter is on.</param>
public sealed record ObsRename(ObsRenameKind Kind, string OldName, string NewName, string? SourceName = null);

/// <summary>
/// One talking detector, from a "Talking" trigger: <paramref name="Input"/> ("" = every input) is talking while its
/// level is at or above <paramref name="ThresholdDb"/>, and stops after <paramref name="QuietMs"/> below it.
/// </summary>
public sealed record ObsTalkingSettings(string Input, double ThresholdDb, int QuietMs)
{
    public const double DefaultThresholdDb = -30;
    public const int DefaultQuietMs = 300;

    public static ObsTalkingSettings From(IReadOnlyDictionary<string, string> settings) => Create(
        settings.TryGetValue("input", out var input) ? input : "",
        settings.TryGetValue("threshold", out var threshold) ? threshold : "",
        settings.TryGetValue("quietMs", out var quiet) ? quiet : "");

    public static ObsTalkingSettings From(Cuebot.Core.Actions.ParameterValues settings) =>
        Create(settings["input"], settings["threshold"], settings["quietMs"]);

    /// <summary>The detector a talking event came from (its args carry the detector's settings).</summary>
    public static ObsTalkingSettings FromEvent(IReadOnlyDictionary<string, object?> args) => new(
        Convert.ToString(args.GetValueOrDefault("detectorInput"), System.Globalization.CultureInfo.InvariantCulture) ?? "",
        args.GetValueOrDefault("thresholdDb") is double db ? db : DefaultThresholdDb,
        args.GetValueOrDefault("quietMs") is int ms ? ms : DefaultQuietMs);

    private static ObsTalkingSettings Create(string input, string threshold, string quiet) => new(
        input.Trim(),
        double.TryParse(threshold, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db)
            ? Math.Clamp(db, -100, 26) : DefaultThresholdDb,
        int.TryParse(quiet, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? Math.Clamp(ms, 0, 60_000) : DefaultQuietMs);
}

/// <summary>Extra cached lists of a connection (groups), talking detection and the other newer features.</summary>
public sealed partial class ObsConnection
{
    private string[] _groups = [];
    private string[] _hotkeys = [];
    private string[] _inputKinds = [];
    private string[] _filterKinds = [];
    private string[] _outputs = [];
    private IReadOnlyList<ObsCanvas> _canvases = [];

    // Kept after a disconnect so editors still offer suggestions.

    /// <summary>Group names (GetGroupList).</summary>
    public IReadOnlyList<string> Groups => _groups;

    /// <summary>Hotkey names, like OBSBasic.StartStreaming (GetHotkeyList).</summary>
    public IReadOnlyList<string> Hotkeys => _hotkeys;

    /// <summary>Kinds of source that can be created, like browser_source (GetInputKindList).</summary>
    public IReadOnlyList<string> InputKinds => _inputKinds;

    /// <summary>Kinds of filter that can be added, like color_filter_v2 (GetSourceFilterKindList, OBS 30.1+).</summary>
    public IReadOnlyList<string> FilterKinds => _filterKinds;

    /// <summary>Output names, e.g. a plugin's vertical stream (GetOutputList).</summary>
    public IReadOnlyList<string> Outputs => _outputs;

    /// <summary>Canvases (GetCanvasList, OBS 32.1+); the main one is usually "Main".</summary>
    public IReadOnlyList<ObsCanvas> Canvases => _canvases;

    private static string[] ExtraSnapshotRequests =>
        ["GetGroupList", "GetHotkeyList", "GetInputKindList", "GetSourceFilterKindList", "GetOutputList", "GetCanvasList"];

    private void ApplyExtraSnapshot(IReadOnlyDictionary<string, JsonElement> answers)
    {
        if (answers.TryGetValue("GetGroupList", out var groups)) _groups = ObsResponseParser.ParseStrings(groups, "groups");
        if (answers.TryGetValue("GetHotkeyList", out var hotkeys)) _hotkeys = ObsResponseParser.ParseStrings(hotkeys, "hotkeys");
        if (answers.TryGetValue("GetInputKindList", out var kinds)) _inputKinds = ObsResponseParser.ParseStrings(kinds, "inputKinds");
        if (answers.TryGetValue("GetSourceFilterKindList", out var filters)) _filterKinds = ObsResponseParser.ParseStrings(filters, "sourceFilterKinds");
        if (answers.TryGetValue("GetOutputList", out var outputs)) _outputs = ObsResponseParser.ParseNames(outputs, "outputs", "outputName");
        if (answers.TryGetValue("GetCanvasList", out var canvases)) _canvases = ParseCanvases(canvases);
    }

    private static ObsCanvas[] ParseCanvases(JsonElement data) =>
        data.GetArray("canvases")
            .Select(c => (Name: c.GetStringOrNull("canvasName") ?? c.GetStringOrNull("name"), Uuid: c.GetStringOrNull("canvasUuid") ?? c.GetStringOrNull("uuid")))
            .Where(c => c.Name is not null && c.Uuid is not null)
            .Select(c => new ObsCanvas(c.Name!, c.Uuid!))
            .ToArray();

    private async Task RefreshCanvasesAsync(CancellationToken ct)
    {
        _canvases = ParseCanvases(await RequestAsync("GetCanvasList", ct: ct).ConfigureAwait(false));
        ScenesChanged?.Invoke();
    }

    private async Task RefreshGroupsAsync(CancellationToken ct)
    {
        _groups = ObsResponseParser.ParseStrings(await RequestAsync("GetGroupList", ct: ct).ConfigureAwait(false), "groups");
        ScenesChanged?.Invoke();
    }

    // ---- Talking detection (InputVolumeMeters) ----

    private sealed class TalkingState
    {
        public bool Talking;
        public long LastLoudMs;
    }

    private volatile ObsTalkingSettings[] _talking = [];
    private readonly Dictionary<(ObsTalkingSettings Detector, string Input), TalkingState> _talkingStates = new();

    /// <summary>The talking detectors enabled triggers ask for (set by the service from the profile).</summary>
    public void SetTalkingDetectors(IReadOnlyList<ObsTalkingSettings> detectors)
    {
        lock (_talkingStates)
        {
            _talking = detectors.ToArray();
            foreach (var key in _talkingStates.Keys.Where(k => !_talking.Contains(k.Detector)).ToArray()) _talkingStates.Remove(key);
        }
    }

    /// <summary>Clock for talking detection, in milliseconds (replaceable in tests).</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private void OnVolumeMeters(JsonElement data)
    {
        var detectors = _talking;
        if (detectors.Length == 0) return;
        var now = Clock();
        var published = new List<BotEventArgs>();
        lock (_talkingStates)
        {
            foreach (var input in data.GetArray("inputs"))
            {
                if (input.GetStringOrNull("inputName") is not { } name) continue;
                var db = ObsTransforms.PeakDb(input);
                foreach (var detector in detectors)
                {
                    if (detector.Input.Length > 0 && !string.Equals(detector.Input, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_talkingStates.TryGetValue((detector, name), out var state)) _talkingStates[(detector, name)] = state = new TalkingState();
                    if (db >= detector.ThresholdDb)
                    {
                        state.LastLoudMs = now;
                        if (state.Talking) continue;
                        state.Talking = true;
                        published.Add(new BotEventArgs(detector, name, true, db));
                    }
                    else if (state.Talking && now - state.LastLoudMs >= detector.QuietMs)
                    {
                        state.Talking = false;
                        published.Add(new BotEventArgs(detector, name, false, db));
                    }
                }
            }
        }
        foreach (var e in published) Publish(TalkingEvent(e));
    }

    private sealed record BotEventArgs(ObsTalkingSettings Detector, string Input, bool Talking, double LevelDb);

    private static Cuebot.Core.Events.BotEvent TalkingEvent(BotEventArgs e) => new(ObsEvents.Talking, new Dictionary<string, object?>
    {
        ["inputName"] = e.Input,
        ["talking"] = e.Talking,
        ["levelDb"] = double.IsNegativeInfinity(e.LevelDb) ? -100.0 : Math.Round(e.LevelDb, 1),
        ["thresholdDb"] = e.Detector.ThresholdDb,
        ["quietMs"] = e.Detector.QuietMs,
        ["detectorInput"] = e.Detector.Input,
    });

    private void HandleObsEventExtras(Protocol.ObsEvent e)
    {
        var d = e.EventData;
        switch (e.EventType)
        {
            case "InputVolumeMeters":
                OnVolumeMeters(d);
                break;
            case "CanvasCreated" or "CanvasRemoved" or "CanvasNameChanged":
                RefreshInBackground("canvases", RefreshCanvasesAsync);
                break;
            case "SceneItemLockStateChanged":
                PublishItemEventInBackground(d, (scene, source, id) => ObsEventMapper.SourceLockChanged(scene, source, id,
                    d.GetBoolOrNull("sceneItemLocked") ?? false));
                break;
            case "SceneItemTransformChanged":
                PublishItemEventInBackground(d, (scene, source, id) => ObsEventMapper.SourceTransformChanged(scene, source, id,
                    d.TryGetProperty("sceneItemTransform", out var t) ? t.GetRawText() : "{}"));
                break;
        }
    }

    /// <summary>Publishes an event about a scene item once its source name is known (resolved off the receive thread).</summary>
    private void PublishItemEventInBackground(JsonElement d, Func<string, string, int, Cuebot.Core.Events.BotEvent> create)
    {
        var sceneName = d.GetStringOrNull("sceneName") ?? "";
        var sceneItemId = d.GetIntOrNull("sceneItemId") ?? 0;
        _ = Task.Run(async () =>
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
            Publish(create(sceneName, sourceName, sceneItemId));
        });
    }

    private void OnSessionEndedExtras()
    {
        // Whoever was talking is not any more: let "stopped talking" triggers clean up.
        List<BotEventArgs> stopped;
        lock (_talkingStates)
        {
            stopped = _talkingStates.Where(s => s.Value.Talking).Select(s => new BotEventArgs(s.Key.Detector, s.Key.Input, false, double.NegativeInfinity)).ToList();
            _talkingStates.Clear();
        }
        foreach (var e in stopped) Publish(TalkingEvent(e));
    }
}
