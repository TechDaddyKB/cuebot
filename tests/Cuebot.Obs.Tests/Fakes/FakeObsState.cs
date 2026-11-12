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

    /// <summary>False acts like OBS before 30.1 (obs-websocket 5.4), which has no GetSceneItemSource.</summary>
    public bool SupportsSceneItemSource { get; set; } = true;

    public List<string> CustomEvents { get; } = [];
    public string RecordDirectory { get; set; } = "/videos";
    public int Splits { get; set; }
    public Dictionary<string, JsonElement> PersistentData { get; } = new();
    public Dictionary<string, string?> ProfileParameters { get; } = new() { ["Output|Mode"] = "Simple" };
    public Dictionary<string, int> Video { get; } = new() { ["baseWidth"] = 1920, ["baseHeight"] = 1080, ["outputWidth"] = 1280, ["outputHeight"] = 720, ["fpsNumerator"] = 60, ["fpsDenominator"] = 1 };
    public List<string> Profiles { get; } = ["Default", "Streaming"];
    public Dictionary<string, (string? Transition, int? Duration)> TransitionOverrides { get; } = new();

    public sealed record Item(int Id, string Source, bool Enabled = true, bool IsGroup = false)
    {
        public bool Enabled { get; set; } = Enabled;
        public bool Locked { get; set; }
        public string BlendMode { get; set; } = "OBS_BLEND_NORMAL";
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
        public string MonitorType { get; set; } = "OBS_MONITORING_TYPE_NONE";
        public double Balance { get; set; } = 0.5;
        public double SyncOffset { get; set; }
        public bool Active { get; set; }
        public string Kind { get; set; } = "fake";
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
            case "GetVersion": return Ok(new { obsVersion = "32.2.2", obsWebSocketVersion = "5.7.4" });
            case "GetGroupList": return Ok(new { groups = Groups.Keys.ToArray() });
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

            case "GetSceneItemList":
                // Like obs-websocket: GetSceneItemList is "Scenes only"; a group name is the wrong resource type.
                if (Groups.ContainsKey(Str(d, "sceneName"))) return FakeReply.Fail(602, "The specified source is not a scene. (Is group)");
                return Ok(new { sceneItems = ItemList(Scene(Str(d, "sceneName")).Value) });
            case "GetSceneItemSource":
                if (!SupportsSceneItemSource) return FakeReply.Fail(204, "Your request type is not valid.");
                return Ok(new { sourceName = FindItem(d).Source, sourceUuid = Guid.NewGuid().ToString() });
            case "GetGroupSceneItemList":
                return Groups.TryGetValue(Str(d, "sceneName"), out var group) ? Ok(new { sceneItems = ItemList(group) }) : FakeReply.Fail(600, "No group.");
            case "GetSceneItemId":
                // OBS walks items bottom-up; the list keeps them top-down. searchOffset -1 is the top copy.
                var matches = Container(Str(d, "sceneName")).Where(i => i.Source == Str(d, "sourceName")).Reverse().ToList();
                var offset = d.TryGetProperty("searchOffset", out var so) ? so.GetInt32() : 0;
                var found = offset == -1 ? matches.LastOrDefault() : matches.ElementAtOrDefault(offset);
                return found is null ? FakeReply.Fail(600, "No scene items were found.") : Ok(new { sceneItemId = found.Id });
            case "GetSceneItemEnabled": return Ok(new { sceneItemEnabled = FindItem(d).Enabled });
            case "SetSceneItemEnabled":
                FindItem(d).Enabled = d.GetProperty("sceneItemEnabled").GetBoolean();
                return Ok();
            case "GetSceneItemLocked": return Ok(new { sceneItemLocked = FindItem(d).Locked });
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
                return (type == "ToggleStream" ? Ok(new { outputActive = Streaming }) : Ok()) with { EventsBefore = [State("StreamStateChanged", Streaming)] };
            case "StartRecord" or "StopRecord" or "ToggleRecord":
                Recording = Next(type, Recording);
                RecordPaused &= Recording;
                return Ok() with { EventsBefore = [State("RecordStateChanged", Recording)] };
            case "PauseRecord" or "ResumeRecord" or "ToggleRecordPause":
                RecordPaused = type == "PauseRecord" || (type == "ToggleRecordPause" && !RecordPaused);
                return Ok() with
                {
                    EventsBefore = [("RecordStateChanged", new { outputActive = true, outputState = RecordPaused ? "OBS_WEBSOCKET_OUTPUT_PAUSED" : "OBS_WEBSOCKET_OUTPUT_RESUMED" })],
                };
            case "StartReplayBuffer" or "StopReplayBuffer" or "ToggleReplayBuffer":
                ReplayBuffer = Next(type, ReplayBuffer);
                return Ok() with { EventsBefore = [State("ReplayBufferStateChanged", ReplayBuffer)] };
            case "StartVirtualCam" or "StopVirtualCam" or "ToggleVirtualCam":
                VirtualCam = Next(type, VirtualCam);
                return Ok() with { EventsBefore = [State("VirtualcamStateChanged", VirtualCam)] };
            case "BroadcastCustomEvent":
                // obs-websocket sends the CustomEvent to every client subscribed to General events, the sender included.
                var custom = d.GetProperty("eventData").Clone();
                CustomEvents.Add(custom.GetRawText());
                return Ok() with { EventsBefore = [("CustomEvent", custom)] };
            case "CallVendorRequest":
                if (Str(d, "vendorName") != "fake-vendor") return FakeReply.Fail(604, "No vendor was found by that name.");
                return Ok(new
                {
                    vendorName = "fake-vendor", requestType = Str(d, "requestType"),
                    responseData = d.TryGetProperty("requestData", out var vendorData) ? vendorData.Clone() : JsonDocument.Parse("{}").RootElement.Clone(),
                });
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
                if (!Recording) return FakeReply.Fail(501, "Recording is not active.");
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
            // ---- Newer requests (features) ----
            case "GetHotkeyList": return Ok(new { hotkeys = new[] { "OBSBasic.StartStreaming", "OBSBasic.Screenshot" } });
            case "GetInputKindList": return Ok(new { inputKinds = new[] { "browser_source", "image_source", "color_source_v3" } });
            case "GetSourceFilterKindList": return Ok(new { sourceFilterKinds = new[] { "color_filter_v2", "mask_filter_v2" } });
            case "GetOutputList": return Ok(new { outputs = new[] { new { outputName = "adv_stream", outputKind = "rtmp_output" }, new { outputName = "vertical", outputKind = "rtmp_output" } } });
            case "GetCanvasList":
                return Ok(new { canvases = new[] { new { canvasName = "Main", canvasUuid = "c-main" }, new { canvasName = "Vertical", canvasUuid = "c-vert" } } });
            case "SplitRecordFile":
                if (!Recording) return FakeReply.Fail(501, "Recording is not active.");
                Splits++;
                return Ok() with { EventsBefore = [("RecordFileChanged", new { newOutputPath = $"{RecordDirectory}/part{Splits + 1}.mkv" })] };
            case "GetRecordDirectory": return Ok(new { recordDirectory = RecordDirectory });
            case "SetRecordDirectory":
                RecordDirectory = Str(d, "recordDirectory");
                return Ok();
            case "SetInputAudioMonitorType":
                FindInput(d).MonitorType = Str(d, "monitorType");
                return Ok();
            case "GetInputAudioMonitorType": return Ok(new { monitorType = FindInput(d).MonitorType });
            case "SetInputAudioBalance":
                FindInput(d).Balance = d.GetProperty("inputAudioBalance").GetDouble();
                return Ok();
            case "GetInputAudioBalance": return Ok(new { inputAudioBalance = FindInput(d).Balance });
            case "SetInputAudioSyncOffset":
                FindInput(d).SyncOffset = d.GetProperty("inputAudioSyncOffset").GetDouble();
                return Ok();
            case "GetInputAudioSyncOffset": return Ok(new { inputAudioSyncOffset = FindInput(d).SyncOffset });
            case "GetSceneItemIndex":
                var container = Container(Str(d, "sceneName"));
                return Ok(new { sceneItemIndex = container.Count - 1 - container.IndexOf(FindItem(d)) });
            case "SetSceneItemIndex":
                var list = Container(Str(d, "sceneName"));
                var moving = FindItem(d);
                list.Remove(moving);
                list.Insert(Math.Clamp(list.Count - d.GetProperty("sceneItemIndex").GetInt32(), 0, list.Count), moving);
                return Ok();
            case "SetSceneItemLocked":
                FindItem(d).Locked = d.GetProperty("sceneItemLocked").GetBoolean();
                return Ok();
            case "SetSceneItemBlendMode":
                FindItem(d).BlendMode = Str(d, "sceneItemBlendMode");
                return Ok();
            case "CreateInput":
                if (Inputs.ContainsKey(Str(d, "inputName"))) return FakeReply.Fail(601, "A source already exists by that input name.");
                Inputs[Str(d, "inputName")] = new Input
                {
                    Kind = Str(d, "inputKind"),
                    Settings = d.TryGetProperty("inputSettings", out var initial) ? Merge(new(), initial, true) : new(),
                };
                var created = new Item(_nextItemId++, Str(d, "inputName"), d.TryGetProperty("sceneItemEnabled", out var en) && en.GetBoolean());
                Scene(Str(d, "sceneName")).Value.Insert(0, created);
                return Ok(new { inputUuid = Guid.NewGuid().ToString(), sceneItemId = created.Id });
            case "RemoveInput":
                FindInput(d);
                Inputs.Remove(Str(d, "inputName"));
                foreach (var items in Scenes.Values.Concat(Groups.Values)) items.RemoveAll(i => i.Source == Str(d, "inputName"));
                return Ok();
            case "SetInputName":
                Inputs[Str(d, "newInputName")] = FindInput(d);
                Inputs.Remove(Str(d, "inputName"));
                return Ok();
            case "CreateSceneItem":
                var added = new Item(_nextItemId++, Str(d, "sourceName"), !d.TryGetProperty("sceneItemEnabled", out var vis) || vis.GetBoolean());
                Scene(Str(d, "sceneName")).Value.Insert(0, added);
                return Ok(new { sceneItemId = added.Id });
            case "RemoveSceneItem":
                Container(Str(d, "sceneName")).Remove(FindItem(d));
                return Ok();
            case "DuplicateSceneItem":
                var original = FindItem(d);
                var copy = new Item(_nextItemId++, original.Source, original.Enabled);
                Scene(d.TryGetProperty("destinationSceneName", out var dest) ? dest.GetString()! : Str(d, "sceneName")).Value.Insert(0, copy);
                return Ok(new { sceneItemId = copy.Id });
            case "GetSourceActive":
                return Inputs.TryGetValue(Str(d, "sourceName"), out var activeInput)
                    ? Ok(new { videoActive = activeInput.Active, videoShowing = activeInput.Active })
                    : Scenes.ContainsKey(Str(d, "sourceName")) ? Ok(new { videoActive = CurrentScene == Str(d, "sourceName"), videoShowing = true }) : FakeReply.Fail(600, "No source.");
            case "CreateScene":
                if (Scenes.ContainsKey(Str(d, "sceneName"))) return FakeReply.Fail(601, "A source already exists by that scene name.");
                Scenes[Str(d, "sceneName")] = [];
                return Ok(new { sceneUuid = Guid.NewGuid().ToString() });
            case "RemoveScene":
                Scenes.Remove(Scene(Str(d, "sceneName")).Key);
                return Ok();
            case "SetSceneName":
                var renamed = Scene(Str(d, "sceneName"));
                Scenes.Remove(renamed.Key);
                Scenes[Str(d, "newSceneName")] = renamed.Value;
                return Ok();
            case "SetSceneSceneTransitionOverride":
                TransitionOverrides[Scene(Str(d, "sceneName")).Key] = (
                    d.TryGetProperty("transitionName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : null,
                    d.TryGetProperty("transitionDuration", out var td) && td.ValueKind == JsonValueKind.Number ? td.GetInt32() : null);
                return Ok();
            case "CreateSourceFilter":
                FilterListForEdit(Str(d, "sourceName")).Add(new Filter(Str(d, "filterName"), true));
                return Ok();
            case "RemoveSourceFilter":
                FilterListForEdit(Str(d, "sourceName")).Remove(FindFilter(d));
                return Ok();
            case "SetSourceFilterName":
                var filters = FilterListForEdit(Str(d, "sourceName"));
                var oldFilter = FindFilter(d);
                filters[filters.IndexOf(oldFilter)] = new Filter(Str(d, "newFilterName"), oldFilter.Enabled);
                return Ok();
            case "SetSourceFilterIndex":
                var ordered = FilterListForEdit(Str(d, "sourceName"));
                var shifting = FindFilter(d);
                ordered.Remove(shifting);
                ordered.Insert(Math.Min(d.GetProperty("filterIndex").GetInt32(), ordered.Count), shifting);
                return Ok();
            case "GetCurrentSceneTransition":
                return Ok(new { transitionName = Transition, transitionUuid = "t1", transitionKind = "fade_transition", transitionFixed = false, transitionDuration = TransitionDuration, transitionConfigurable = false, transitionSettings = (object?)null });
            case "GetCurrentSceneTransitionCursor": return Ok(new { transitionCursor = 0.25 });
            case "GetSourceScreenshot": return Ok(new { imageData = "data:image/" + Str(d, "imageFormat") + ";base64,QUJD" });
            case "GetPersistentData":
                return Ok(new { slotValue = PersistentData.TryGetValue(Str(d, "realm") + "|" + Str(d, "slotName"), out var slot) ? (object?)slot : null });
            case "SetPersistentData":
                PersistentData[Str(d, "realm") + "|" + Str(d, "slotName")] = d.GetProperty("slotValue").Clone();
                return Ok();
            case "CreateProfile":
                Profiles.Add(Str(d, "profileName"));
                return Ok();
            case "RemoveProfile":
                return Profiles.Remove(Str(d, "profileName")) ? Ok() : FakeReply.Fail(600, "No profile.");
            case "GetProfileParameter":
                return Ok(new { parameterValue = ProfileParameters.GetValueOrDefault(Str(d, "parameterCategory") + "|" + Str(d, "parameterName")), defaultParameterValue = (string?)null });
            case "SetProfileParameter":
                ProfileParameters[Str(d, "parameterCategory") + "|" + Str(d, "parameterName")] =
                    d.GetProperty("parameterValue").ValueKind == JsonValueKind.Null ? null : d.GetProperty("parameterValue").GetString();
                return Ok();
            case "GetVideoSettings": return Ok(Video);
            case "SetVideoSettings":
                foreach (var p in d.EnumerateObject()) Video[p.Name] = p.Value.GetInt32();
                return Ok();
            case "GetStreamServiceSettings":
                return Ok(new { streamServiceType = "rtmp_custom", streamServiceSettings = new { server = "rtmp://live.example/app", key = "live_secret_key", use_auth = false } });
            case "GetOutputSettings":
                return Str(d, "outputName") == "vertical" ? Ok(new { outputSettings = new { bitrate = 6000, key = "secret" } }) : FakeReply.Fail(600, "No output.");
            case "StartOutput" or "StopOutput" or "ToggleOutput":
                return Str(d, "outputName") is "vertical" or "adv_stream" ? Ok() : FakeReply.Fail(600, "No output.");
            case "GetSpecialInputs": return Ok(new { desktop1 = "Desktop Audio", desktop2 = (string?)null, mic1 = "Mic", mic2 = (string?)null, mic3 = (string?)null, mic4 = (string?)null });
            case "GetInputPropertiesListPropertyItems":
                FindInput(d);
                return Ok(new { propertyItems = new[] { new { itemName = "Display 1", itemValue = "0", itemEnabled = true }, new { itemName = "Display 2", itemValue = "1", itemEnabled = true } } });
            case "SendStreamCaption":
                return Streaming ? Ok() : FakeReply.Fail(501, "Streaming is not active.");
            case "SetCurrentSceneTransitionSettings" or "SetTBarPosition" or "OpenVideoMixProjector" or "OpenSourceProjector"
                or "OpenInputPropertiesDialog" or "OpenInputFiltersDialog" or "OpenInputInteractDialog" or "TriggerHotkeyByKeySequence"
                or "CreateSceneCollection" or "SetStreamServiceSettings" or "SetOutputSettings" or "SetInputDeinterlaceMode"
                or "SetInputDeinterlaceFieldOrder":
                return Ok();
            default:
                return FakeReply.Fail(204, $"Unknown request {type}");
        }
    }

    private static FakeReply Ok(object? data = null) => FakeReply.Ok(data);

    private static (string, object?) State(string eventType, bool active) =>
        (eventType, new { outputActive = active, outputState = active ? "OBS_WEBSOCKET_OUTPUT_STARTED" : "OBS_WEBSOCKET_OUTPUT_STOPPED" });

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

    private List<Filter> FilterListForEdit(string source)
    {
        if (!Filters.TryGetValue(source, out var filters)) Filters[source] = filters = FilterList(source);
        return filters;
    }

    private Filter FindFilter(JsonElement d) =>
        FilterList(Str(d, "sourceName")).FirstOrDefault(f => f.Name == Str(d, "filterName")) ?? throw new KeyNotFoundException("No filter.");

    private static object[] ItemList(List<Item> items) =>
        items.Select((item, i) => (object)new
        {
            sceneItemId = item.Id, sourceName = item.Source, sceneItemEnabled = item.Enabled, isGroup = item.IsGroup,
            sceneItemIndex = items.Count - 1 - i, inputKind = item.IsGroup ? null : "fake",
        }).ToArray();
}
