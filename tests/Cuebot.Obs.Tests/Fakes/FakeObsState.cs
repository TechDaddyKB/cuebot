using System.Text.Json;

namespace Cuebot.Obs.Tests.Fakes;

/// <summary>
/// A small in-memory OBS behind <see cref="FakeObsServer.Handler"/>: scenes with items, groups, inputs with settings,
/// mute, volume and audio tracks, filters, transitions, studio mode, outputs, profiles and scene collections. It answers
/// the obs-websocket 5 requests Cuebot sends the way OBS does (including failures for unknown names).
/// </summary>
internal sealed class FakeObsState
{
    private readonly FakeObsServer _server;
    private int _nextItemId = 1;

    public FakeObsState(FakeObsServer server, string name = "OBS")
    {
        _server = server;
        Name = name;
        server.Handler = Handle;
        AddScene("Gaming", "Webcam", "Game", "Overlay");
        AddScene("Starting", "Countdown", "Music");
        AddGroup("Alerts", "Follow", "Sub", "Raid");
        Scenes["Gaming"].Add(new Item(_nextItemId++, "Alerts", Enabled: true, IsGroup: true));
        CurrentScene = "Starting";
        foreach (var input in new[] { "Webcam", "Game", "Overlay", "Countdown", "Music", "Follow", "Sub", "Raid", "Mic" })
            Inputs[input] = new Input();
        Filters["Webcam"] = [new Filter("Blur", false), new Filter("Color", true), new Filter("Crop", true)];
    }

    public string Name { get; }
    public Dictionary<string, List<Item>> Scenes { get; } = new();
    public Dictionary<string, List<Item>> Groups { get; } = new();
    public Dictionary<string, Input> Inputs { get; } = new();
    public Dictionary<string, List<Filter>> Filters { get; } = new();
    public string CurrentScene { get; set; }
    public string? PreviewScene { get; set; }
    public bool StudioMode { get; set; }
    public string Transition { get; set; } = "Fade";
    public int TransitionDuration { get; set; } = 300;
    public bool Streaming { get; set; }
    public bool Recording { get; set; }
    public bool RecordPaused { get; set; }
    public bool ReplayBuffer { get; set; }
    public bool VirtualCam { get; set; }
    public string Profile { get; set; } = "Default";
    public string SceneCollection { get; set; } = "Main";
    public List<string> Chapters { get; } = [];
    public List<string> Hotkeys { get; } = [];
    public List<string> Screenshots { get; } = [];

    public sealed record Item(int Id, string Source, bool Enabled = true, bool IsGroup = false)
    {
        public bool Enabled { get; set; } = Enabled;
        public Dictionary<string, double> Transform { get; } = new()
        {
            ["positionX"] = 100, ["positionY"] = 50, ["scaleX"] = 1, ["scaleY"] = 1, ["rotation"] = 0,
            ["sourceWidth"] = 200, ["sourceHeight"] = 100, ["alignment"] = 5, ["cropLeft"] = 0, ["cropTop"] = 0,
            ["cropRight"] = 0, ["cropBottom"] = 0,
        };
    }

    public sealed class Input
    {
        public Dictionary<string, JsonElement> Settings { get; set; } = new();
        public bool Muted { get; set; }
        public double VolumeDb { get; set; }
        public Dictionary<string, bool> Tracks { get; } = new() { ["1"] = true, ["2"] = false, ["3"] = false, ["4"] = false, ["5"] = false, ["6"] = false };
        public string MediaState { get; set; } = "OBS_MEDIA_STATE_STOPPED";
        public double MediaCursor { get; set; }
        public List<string> PressedButtons { get; } = [];
    }

    public sealed class Filter(string name, bool enabled)
    {
        public string Name { get; } = name;
        public bool Enabled { get; set; } = enabled;
        public Dictionary<string, JsonElement> Settings { get; set; } = new();
    }

    public void AddScene(string name, params string[] sources) =>
        Scenes[name] = sources.Select(s => new Item(_nextItemId++, s)).ToList();

    public void AddGroup(string name, params string[] sources) =>
        Groups[name] = sources.Select(s => new Item(_nextItemId++, s)).ToList();

    private FakeReply? Handle(string type, JsonElement d)
    {
        try
        {
            return HandleCore(type, d);
        }
        catch (KeyNotFoundException ex)
        {
            return FakeReply.Fail(600, ex.Message);
        }
    }

    private FakeReply HandleCore(string type, JsonElement d)
    {
        switch (type)
        {
            case "GetVersion": return Ok(new { obsVersion = "31.0.0", obsWebSocketVersion = "5.5.0" });
            case "GetSceneList":
                return Ok(new
                {
                    currentProgramSceneName = CurrentScene,
                    scenes = Scenes.Keys.Select((name, i) => new { sceneName = name, sceneIndex = Scenes.Count - 1 - i }).ToArray(),
                });
            case "GetInputList": return Ok(new { inputs = Inputs.Keys.Select(n => new { inputName = n }).ToArray() });
            case "GetCurrentProgramScene": return Ok(new { currentProgramSceneName = CurrentScene });
            case "SetCurrentProgramScene":
                CurrentScene = Scene(Str(d, "sceneName")).Key;
                return Ok();
            case "GetStudioModeEnabled": return Ok(new { studioModeEnabled = StudioMode });
            case "SetStudioModeEnabled":
                StudioMode = d.GetProperty("studioModeEnabled").GetBoolean();
                return Ok();
            case "GetCurrentPreviewScene":
                return StudioMode ? Ok(new { currentPreviewSceneName = PreviewScene ?? CurrentScene }) : FakeReply.Fail(506, "Studio mode is not active.");
            case "SetCurrentPreviewScene":
                if (!StudioMode) return FakeReply.Fail(506, "Studio mode is not active.");
                PreviewScene = Scene(Str(d, "sceneName")).Key;
                return Ok();
            case "TriggerStudioModeTransition":
                if (!StudioMode) return FakeReply.Fail(506, "Studio mode is not active.");
                (CurrentScene, PreviewScene) = (PreviewScene ?? CurrentScene, CurrentScene);
                return Ok();
            case "GetSceneTransitionList":
                return Ok(new { currentSceneTransitionName = Transition, transitions = new[] { new { transitionName = "Fade" }, new { transitionName = "Cut" } } });
            case "SetCurrentSceneTransition":
                Transition = Str(d, "transitionName") is "Fade" or "Cut" ? Str(d, "transitionName") : throw new KeyNotFoundException("No transition.");
                return Ok();
            case "SetCurrentSceneTransitionDuration":
                TransitionDuration = d.GetProperty("transitionDuration").GetInt32();
                return Ok();
            case "GetProfileList": return Ok(new { currentProfileName = Profile, profiles = new[] { "Default", "Streaming" } });
            case "SetCurrentProfile":
                Profile = Str(d, "profileName");
                return Ok();
            case "GetSceneCollectionList": return Ok(new { currentSceneCollectionName = SceneCollection, sceneCollections = new[] { "Main", "Alt" } });
            case "SetCurrentSceneCollection":
                SceneCollection = Str(d, "sceneCollectionName");
                return Ok();

            case "GetSceneItemList": return Ok(new { sceneItems = ItemList(Scene(Str(d, "sceneName")).Value) });
            case "GetGroupSceneItemList":
                return Groups.TryGetValue(Str(d, "sceneName"), out var group) ? Ok(new { sceneItems = ItemList(group) }) : FakeReply.Fail(600, "No group.");
            case "GetSceneItemId":
                var found = Container(Str(d, "sceneName")).FirstOrDefault(i => i.Source == Str(d, "sourceName"));
                return found is null ? FakeReply.Fail(600, "No scene items were found.") : Ok(new { sceneItemId = found.Id });
            case "GetSceneItemEnabled": return Ok(new { sceneItemEnabled = FindItem(d).Enabled });
            case "SetSceneItemEnabled":
                FindItem(d).Enabled = d.GetProperty("sceneItemEnabled").GetBoolean();
                return Ok();
            case "GetSceneItemLocked": return Ok(new { sceneItemLocked = false });
            case "GetSceneItemTransform": return Ok(new { sceneItemTransform = FindItem(d).Transform });
            case "SetSceneItemTransform":
                var transform = FindItem(d).Transform;
                foreach (var p in d.GetProperty("sceneItemTransform").EnumerateObject()) transform[p.Name] = p.Value.GetDouble();
                return Ok();

            case "GetInputMute": return Ok(new { inputMuted = FindInput(d).Muted });
            case "SetInputMute":
                FindInput(d).Muted = d.GetProperty("inputMuted").GetBoolean();
                return Ok();
            case "ToggleInputMute":
                var input = FindInput(d);
                input.Muted = !input.Muted;
                return Ok(new { inputMuted = input.Muted });
            case "GetInputVolume": return Ok(new { inputVolumeDb = FindInput(d).VolumeDb, inputVolumeMul = Math.Pow(10, FindInput(d).VolumeDb / 20) });
            case "SetInputVolume":
                FindInput(d).VolumeDb = d.TryGetProperty("inputVolumeDb", out var db) ? db.GetDouble() : 20 * Math.Log10(d.GetProperty("inputVolumeMul").GetDouble());
                return Ok();
            case "GetInputAudioTracks": return Ok(new { inputAudioTracks = FindInput(d).Tracks });
            case "SetInputAudioTracks":
                foreach (var p in d.GetProperty("inputAudioTracks").EnumerateObject()) FindInput(d).Tracks[p.Name] = p.Value.GetBoolean();
                return Ok();
            case "GetInputSettings": return Ok(new { inputSettings = FindInput(d).Settings, inputKind = "fake" });
            case "SetInputSettings":
                FindInput(d).Settings = Merge(FindInput(d).Settings, d.GetProperty("inputSettings"), Overlay(d));
                return Ok();
            case "PressInputPropertiesButton":
                FindInput(d).PressedButtons.Add(Str(d, "propertyName"));
                return Ok();

            case "GetMediaInputStatus": return Ok(new { mediaState = FindInput(d).MediaState, mediaCursor = FindInput(d).MediaCursor });
            case "TriggerMediaInputAction":
                FindInput(d).MediaState = Str(d, "mediaAction") switch
                {
                    "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY" or "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART" => "OBS_MEDIA_STATE_PLAYING",
                    "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE" => "OBS_MEDIA_STATE_PAUSED",
                    "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP" => "OBS_MEDIA_STATE_STOPPED",
                    "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_NEXT" or "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PREVIOUS" => FindInput(d).MediaState,
                    var other => throw new KeyNotFoundException($"Bad media action {other}"),
                };
                return Ok();
            case "SetMediaInputCursor":
                FindInput(d).MediaCursor = d.GetProperty("mediaCursor").GetDouble();
                return Ok();
            case "OffsetMediaInputCursor":
                FindInput(d).MediaCursor += d.GetProperty("mediaCursorOffset").GetDouble();
                return Ok();

            case "GetSourceFilterList":
                return Ok(new { filters = FilterList(Str(d, "sourceName")).Select((f, i) => new { filterName = f.Name, filterEnabled = f.Enabled, filterIndex = i, filterKind = "fake" }).ToArray() });
            case "GetSourceFilter": return Ok(new { filterEnabled = FindFilter(d).Enabled });
            case "SetSourceFilterEnabled":
                FindFilter(d).Enabled = d.GetProperty("filterEnabled").GetBoolean();
                return Ok();
            case "SetSourceFilterSettings":
                FindFilter(d).Settings = Merge(FindFilter(d).Settings, d.GetProperty("filterSettings"), Overlay(d));
                return Ok();

            case "StartStream" or "StopStream" or "ToggleStream":
                Streaming = Next(type, Streaming);
                return type == "ToggleStream" ? Ok(new { outputActive = Streaming }) : Ok();
            case "StartRecord" or "StopRecord" or "ToggleRecord":
                Recording = Next(type, Recording);
                return Ok();
            case "PauseRecord": RecordPaused = true; return Ok();
            case "ResumeRecord": RecordPaused = false; return Ok();
            case "ToggleRecordPause": RecordPaused = !RecordPaused; return Ok();
            case "StartReplayBuffer" or "StopReplayBuffer" or "ToggleReplayBuffer":
                ReplayBuffer = Next(type, ReplayBuffer);
                return Ok();
            case "StartVirtualCam" or "StopVirtualCam" or "ToggleVirtualCam":
                VirtualCam = Next(type, VirtualCam);
                return Ok();
            case "SaveReplayBuffer":
                if (!ReplayBuffer) return FakeReply.Fail(501, "Replay buffer is not active.");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    await _server.SendEventAsync("ReplayBufferSaved", new { savedReplayPath = $"/replays/{Name}.mkv" });
                });
                return Ok();
            case "GetLastReplayBufferReplay": return Ok(new { savedReplayPath = $"/replays/{Name}-last.mkv" });
            case "CreateRecordChapter":
                Chapters.Add(d.ValueKind == JsonValueKind.Object && d.TryGetProperty("chapterName", out var chapter) ? chapter.GetString()! : "");
                return Ok();
            case "GetStreamStatus":
                return Ok(new { outputActive = Streaming, outputReconnecting = false, outputTimecode = "00:01:00.000", outputDuration = 60000, outputCongestion = 0.0, outputBytes = 1024, outputSkippedFrames = 2, outputTotalFrames = 3600 });
            case "GetRecordStatus":
                return Ok(new { outputActive = Recording, outputPaused = RecordPaused, outputTimecode = "00:00:10.000", outputDuration = 10000, outputBytes = 512 });
            case "GetReplayBufferStatus": return Ok(new { outputActive = ReplayBuffer });
            case "GetVirtualCamStatus": return Ok(new { outputActive = VirtualCam });
            case "GetOutputStatus":
                return Str(d, "outputName") == "adv_stream" ? Ok(new { outputActive = true }) : FakeReply.Fail(600, "No output.");
            case "SaveSourceScreenshot":
                Screenshots.Add($"{Str(d, "sourceName")}|{Str(d, "imageFormat")}|{Str(d, "imageFilePath")}");
                return Ok();
            case "TriggerHotkeyByName":
                Hotkeys.Add(Str(d, "hotkeyName"));
                return Ok();
            case "GetStats":
                return Ok(new { cpuUsage = 12.5, memoryUsage = 900.0, availableDiskSpace = 100000.0, activeFps = 60.0, averageFrameRenderTime = 1.2, renderSkippedFrames = 1, renderTotalFrames = 1000, outputSkippedFrames = 0, outputTotalFrames = 1000 });
            default:
                return FakeReply.Fail(204, $"Unknown request {type}");
        }
    }

    private static FakeReply Ok(object? data = null) => FakeReply.Ok(data);

    private static string Str(JsonElement d, string property) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(property, out var v) ? v.GetString() ?? "" : "";

    private static bool Overlay(JsonElement d) => !d.TryGetProperty("overlay", out var o) || o.GetBoolean();

    private static bool Next(string request, bool current) =>
        request.StartsWith("Start", StringComparison.Ordinal) || (!request.StartsWith("Stop", StringComparison.Ordinal) && !current);

    private static Dictionary<string, JsonElement> Merge(Dictionary<string, JsonElement> current, JsonElement update, bool overlay)
    {
        var result = overlay ? new Dictionary<string, JsonElement>(current) : new Dictionary<string, JsonElement>();
        foreach (var p in update.EnumerateObject()) result[p.Name] = p.Value.Clone();
        return result;
    }

    private KeyValuePair<string, List<Item>> Scene(string name) =>
        Scenes.FirstOrDefault(s => s.Key == name) is { Key: not null } scene ? scene : throw new KeyNotFoundException($"No source was found by the name of `{name}`.");

    private List<Item> Container(string name) =>
        Scenes.TryGetValue(name, out var items) || Groups.TryGetValue(name, out items) ? items : throw new KeyNotFoundException($"No scene `{name}`.");

    private Item FindItem(JsonElement d) =>
        Container(Str(d, "sceneName")).FirstOrDefault(i => i.Id == d.GetProperty("sceneItemId").GetInt32()) ?? throw new KeyNotFoundException("No scene item.");

    private Input FindInput(JsonElement d) =>
        Inputs.TryGetValue(Str(d, "inputName"), out var input) ? input : throw new KeyNotFoundException($"No input `{Str(d, "inputName")}`.");

    private List<Filter> FilterList(string source) =>
        Filters.TryGetValue(source, out var filters) ? filters : Inputs.ContainsKey(source) || Scenes.ContainsKey(source) ? [] : throw new KeyNotFoundException("No source.");

    private Filter FindFilter(JsonElement d) =>
        FilterList(Str(d, "sourceName")).FirstOrDefault(f => f.Name == Str(d, "filterName")) ?? throw new KeyNotFoundException("No filter.");

    private static object[] ItemList(List<Item> items) =>
        items.Select((item, i) => (object)new
        {
            sceneItemId = item.Id, sourceName = item.Source, sceneItemEnabled = item.Enabled, isGroup = item.IsGroup,
            sceneItemIndex = items.Count - 1 - i, inputKind = item.IsGroup ? null : "fake",
        }).ToArray();
}
