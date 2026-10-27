using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs.Tests;

/// <summary>
/// End to end through the real DI registration and action engine: two fake OBS instances ("OBS" = default and
/// "Studio B"), every sub-action, the event triggers and connection selection.
/// </summary>
public sealed class ObsFeatureTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-obs-feature-" + Guid.NewGuid().ToString("N"));
    private readonly FakeObsServer _serverA = FakeObsServer.Start(password: "pw");
    private readonly FakeObsServer _serverB = FakeObsServer.Start();
    private readonly FakeObsState _a;
    private readonly FakeObsState _b;
    private readonly CaptureSubAction _capture = new();
    private ServiceProvider _services = null!;
    private BusRecorder _recorder = null!;
    private ObsService _obs = null!;

    public ObsFeatureTests()
    {
        _a = new FakeObsState(_serverA, "A");
        _b = new FakeObsState(_serverB, "B");
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_directory)).AddObs();
        services.AddSingleton<ISubActionHandler>(_capture);
        _services = services.BuildServiceProvider();
        _recorder = new BusRecorder(_services.GetRequiredService<EventBus>());
        var settings = _services.GetRequiredService<JsonFile<ObsSettings>>().Value;
        settings.Host = "localhost";
        settings.Port = _serverA.Port;
        settings.Password = "pw";
        _obs = _services.GetRequiredService<ObsService>();
        var second = _obs.AddConnection("Studio B", "localhost", _serverB.Port);
        _services.GetRequiredService<ActionEngine>().Start();
        await _obs.ConnectAsync();
        await second.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        _recorder.Dispose();
        await _services.DisposeAsync();
        await _serverA.DisposeAsync();
        await _serverB.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private ObsConnection B => _obs.Get("studio b");

    // ---- Registration ----

    [Fact]
    public void Registers_every_sub_action_trigger_and_script_function()
    {
        var registry = _services.GetRequiredService<Registry>();
        string[] subActions =
        [
            "obs.setScene", "obs.getCurrentScene", "obs.setPreviewScene", "obs.setSourceVisibility", "obs.hideSceneSources",
            "obs.hideGroupSources", "obs.randomSceneSource", "obs.randomGroupSource", "obs.getSceneItemProperties",
            "obs.setSourceTransform", "obs.flipSource", "obs.rotateSource", "obs.setInputMute", "obs.setInputVolume",
            "obs.setAudioTrack", "obs.setText", "obs.setBrowserUrl", "obs.refreshBrowser", "obs.setImageFile", "obs.setMediaFile",
            "obs.setColor", "obs.setInputSettings", "obs.getInputSettings", "obs.mediaControl", "obs.mediaSeek", "obs.setFilterState",
            "obs.setRandomFilterState", "obs.setAllFiltersState", "obs.setFilterSettings", "obs.setTransition",
            "obs.setTransitionDuration", "obs.studioMode", "obs.studioTransition", "obs.streaming", "obs.recording",
            "obs.replayBuffer", "obs.virtualCam", "obs.createRecordChapter", "obs.getOutputState", "obs.screenshot",
            "obs.setProfile", "obs.setSceneCollection", "obs.triggerHotkey", "obs.getStats", "obs.getStatus", "obs.rawRequest",
            "obs.rawBatch",
        ];
        foreach (var id in subActions)
        {
            var handler = registry.GetSubAction(id);
            Assert.NotNull(handler);
            Assert.Equal("connection", handler.Parameters[0].Key);
        }
        Assert.Equal(subActions.Length, registry.SubActions.Count(s => s.Category == "OBS"));

        foreach (var type in typeof(ObsEvents).GetFields().Select(f => (string)f.GetValue(null)!))
            Assert.NotNull(registry.GetTrigger(type));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        Assert.Contains("obs.request", functions);
        Assert.Contains("obs.requestBatch", functions);
        Assert.Contains("slobs.call", functions);
        Assert.Contains(_services.GetServices<IConnectionStatus>(), s => s.Name == "Streamlabs Desktop");
    }

    // ---- Multiple connections ----

    [Fact]
    public async Task Sub_actions_target_the_named_connection_and_default_to_the_first()
    {
        await Run("obs.setScene", new() { ["scene"] = "Gaming" });
        await Run("obs.setScene", new() { ["connection"] = "Studio B", ["scene"] = "Gaming" });
        Assert.Equal("Gaming", _a.CurrentScene);
        Assert.Equal("Gaming", _b.CurrentScene);

        _a.CurrentScene = "Starting";
        await Run("obs.setScene", new() { ["connection"] = "STUDIO B", ["scene"] = "Starting" });
        Assert.Equal("Starting", _b.CurrentScene);
        Assert.DoesNotContain(_serverA.Requests, r => r.Type == "SetCurrentProgramScene" && r.Data.GetProperty("sceneName").GetString() == "Starting");

        Assert.Null(await TryRun("obs.setScene", new() { ["connection"] = "Nope", ["scene"] = "Gaming" }));
        Assert.Contains("no OBS connection named 'Nope'", LastError());
    }

    [Fact]
    public async Task Events_carry_the_connection_name_and_triggers_filter_by_it()
    {
        var profile = _services.GetRequiredService<ProfileService>();
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "only B",
            Triggers = [new TriggerDefinition { Type = ObsEvents.SceneChanged, Settings = { ["connection"] = "Studio B", ["scene"] = "gaming" } }],
            SubActions = [new SubActionDefinition { Type = CaptureSubAction.Id }],
        });
        profile.NotifyChanged();

        await _serverA.SendEventAsync("CurrentProgramSceneChanged", new { sceneName = "Gaming" });
        var fromA = await _recorder.WaitForAsync(ObsEvents.SceneChanged);
        Assert.Equal("OBS", fromA.Args["obsConnection"]);
        await Task.Delay(200);
        Assert.Null(_capture.Args);

        await _serverB.SendEventAsync("CurrentProgramSceneChanged", new { sceneName = "Gaming" });
        var fromB = await _recorder.WaitForAsync(ObsEvents.SceneChanged, 2);
        Assert.Equal("Studio B", fromB.Args["obsConnection"]);
        await WaitUntil(() => _capture.Args is not null);
        Assert.Equal("Studio B", _capture.Args!["obsConnection"]);
        Assert.Equal("Starting", _capture.Args["previousScene"]);
    }

    [Fact]
    public async Task Connections_can_be_added_renamed_and_removed()
    {
        var extra = _obs.AddConnection("Studio B");
        Assert.Equal("Studio B (2)", extra.Name);
        Assert.Throws<ArgumentException>(() => _obs.RenameConnection(extra, "obs"));
        _obs.RenameConnection(extra, "Backup");
        Assert.Same(extra, _obs.Get("backup"));
        Assert.Contains(_obs.Settings.Connections, c => c.Name == "Backup");

        await _obs.RemoveConnectionAsync(extra);
        Assert.DoesNotContain(_obs.Connections, c => c == extra);
        Assert.DoesNotContain(_obs.Settings.Connections, c => c.Name == "Backup");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _obs.RemoveConnectionAsync(_obs.Default));
        Assert.Equal(["OBS", "Studio B"], _obs.ConnectionNames);
    }

    [Fact]
    public void Connection_caches_transitions_profiles_collections_and_version()
    {
        Assert.Equal(["Fade", "Cut"], B.Transitions);
        Assert.Equal("Fade", B.CurrentTransition);
        Assert.Equal(["Default", "Streaming"], B.Profiles);
        Assert.Equal(["Main", "Alt"], B.SceneCollections);
        Assert.Equal("OBS 31.0.0 / obs-websocket 5.5.0", B.Version);
        Assert.Contains("+1/1 other connections", _obs.StatusMessage);
    }

    // ---- Scenes and scene items ----

    [Fact]
    public async Task Get_current_scene_and_preview_scene_and_studio_mode()
    {
        var args = await Run("obs.getCurrentScene", new());
        Assert.Equal("Starting", args["obsScene"]);

        await Run("obs.studioMode", new() { ["state"] = "Enable" });
        Assert.True(_a.StudioMode);
        await Run("obs.setPreviewScene", new() { ["scene"] = "Gaming" });
        Assert.Equal("Gaming", _a.PreviewScene);
        await Run("obs.studioTransition", new());
        Assert.Equal("Gaming", _a.CurrentScene);
        await Run("obs.studioMode", new() { ["state"] = "Toggle" });
        Assert.False(_a.StudioMode);
    }

    [Fact]
    public async Task Hide_and_random_sources_for_scenes_and_groups()
    {
        await Run("obs.hideSceneSources", new() { ["scene"] = "Gaming" });
        Assert.All(_a.Scenes["Gaming"], i => Assert.False(i.Enabled));

        var args = await Run("obs.randomSceneSource", new() { ["scene"] = "Starting" });
        Assert.Single(_a.Scenes["Starting"], i => i.Enabled);
        Assert.Equal(_a.Scenes["Starting"].Single(i => i.Enabled).Source, args["obsSourceName"]);

        await Run("obs.hideGroupSources", new() { ["group"] = "Alerts" });
        Assert.All(_a.Groups["Alerts"], i => Assert.False(i.Enabled));
        args = await Run("obs.randomGroupSource", new() { ["group"] = "Alerts", ["resultArgument"] = "picked" });
        Assert.Equal(_a.Groups["Alerts"].Single(i => i.Enabled).Source, args["picked"]);
    }

    [Fact]
    public async Task Scene_item_properties_transform_flip_and_rotate()
    {
        var args = await Run("obs.getSceneItemProperties", new() { ["scene"] = "Gaming", ["source"] = "Webcam" });
        Assert.Equal(true, args["obsItemVisible"]);
        Assert.Equal(100.0, args["obsItemPositionX"]);
        Assert.Equal(200.0, args["obsItemSourceWidth"]);

        await Run("obs.setSourceTransform", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["positionX"] = "640", ["scaleY"] = "0.5", ["cropLeft"] = "" });
        var t = _a.Scenes["Gaming"].Single(i => i.Source == "Webcam").Transform;
        Assert.Equal(640, t["positionX"]);
        Assert.Equal(0.5, t["scaleY"]);
        Assert.Equal(50, t["positionY"]);

        await Run("obs.flipSource", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["axis"] = "Horizontal" });
        Assert.Equal(-1, t["scaleX"]);
        Assert.Equal(840, t["positionX"]);   // left-aligned: moved by its width so it stays in place

        await Run("obs.rotateSource", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["degrees"] = "90" });
        await Run("obs.rotateSource", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["degrees"] = "300" });
        Assert.Equal(30, t["rotation"]);
        await Run("obs.rotateSource", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["degrees"] = "45", ["relative"] = "false" });
        Assert.Equal(45, t["rotation"]);
    }

    [Fact]
    public async Task Source_visibility_changed_event_resolves_the_source_name()
    {
        var item = _a.Scenes["Gaming"].Single(i => i.Source == "Overlay");
        await _serverA.SendEventAsync("SceneItemEnableStateChanged", new { sceneName = "Gaming", sceneItemId = item.Id, sceneItemEnabled = false });

        var e = await _recorder.WaitForAsync(ObsEvents.SourceVisibilityChanged);
        Assert.Equal("Overlay", e.Args["sourceName"]);
        Assert.Equal(false, e.Args["visible"]);
        Assert.Equal("OBS", e.Args["obsConnection"]);
    }

    // ---- Inputs ----

    [Fact]
    public async Task Input_mute_volume_and_audio_tracks()
    {
        await Run("obs.setInputMute", new() { ["input"] = "Mic", ["state"] = "Mute" });
        Assert.True(_a.Inputs["Mic"].Muted);
        await Run("obs.setInputMute", new() { ["input"] = "Mic" });   // default Toggle
        Assert.False(_a.Inputs["Mic"].Muted);

        await Run("obs.setInputVolume", new() { ["input"] = "Mic", ["mode"] = "SetDb", ["value"] = "-12" });
        Assert.Equal(-12, _a.Inputs["Mic"].VolumeDb);
        await Run("obs.setInputVolume", new() { ["input"] = "Mic", ["mode"] = "AdjustDb", ["value"] = "3" });
        Assert.Equal(-9, _a.Inputs["Mic"].VolumeDb);
        await Run("obs.setInputVolume", new() { ["input"] = "Mic", ["mode"] = "SetMultiplier", ["value"] = "1" });
        Assert.Equal(0, _a.Inputs["Mic"].VolumeDb, 6);
        await Run("obs.setInputVolume", new() { ["input"] = "Mic", ["mode"] = "SetDb", ["value"] = "99" });
        Assert.Equal(26, _a.Inputs["Mic"].VolumeDb);

        await Run("obs.setAudioTrack", new() { ["input"] = "Mic", ["track"] = "2", ["state"] = "Toggle" });
        Assert.True(_a.Inputs["Mic"].Tracks["2"]);
        await Run("obs.setAudioTrack", new() { ["input"] = "Mic", ["track"] = "1", ["state"] = "Disable" });
        Assert.False(_a.Inputs["Mic"].Tracks["1"]);
        Assert.Null(await TryRun("obs.setAudioTrack", new() { ["input"] = "Mic", ["track"] = "7" }));
    }

    [Fact]
    public async Task Text_browser_image_media_and_color_sources()
    {
        await Run("obs.setText", new() { ["input"] = "Overlay", ["text"] = "Hello" });
        await Run("obs.setBrowserUrl", new() { ["input"] = "Overlay", ["url"] = "https://example.com" });
        Assert.Equal("Hello", _a.Inputs["Overlay"].Settings["text"].GetString());   // overlay keeps other settings
        Assert.Equal("https://example.com", _a.Inputs["Overlay"].Settings["url"].GetString());

        await Run("obs.refreshBrowser", new() { ["input"] = "Overlay" });
        Assert.Equal(["refreshnocache"], _a.Inputs["Overlay"].PressedButtons);

        await Run("obs.setImageFile", new() { ["input"] = "Webcam", ["file"] = "C:/img.png" });
        Assert.Equal("C:/img.png", _a.Inputs["Webcam"].Settings["file"].GetString());

        await Run("obs.setMediaFile", new() { ["input"] = "Music", ["file"] = "/music/a.mp3" });
        Assert.Equal("/music/a.mp3", _a.Inputs["Music"].Settings["local_file"].GetString());
        Assert.True(_a.Inputs["Music"].Settings["is_local_file"].GetBoolean());

        await Run("obs.setColor", new() { ["input"] = "Game", ["color"] = "#112233" });
        Assert.Equal(0xFF332211L, _a.Inputs["Game"].Settings["color"].GetInt64());

        await Run("obs.setInputSettings", new() { ["input"] = "Game", ["settings"] = """{"width":1280}""", ["overlay"] = "false" });
        Assert.Equal(["width"], _a.Inputs["Game"].Settings.Keys);
        var args = await Run("obs.getInputSettings", new() { ["input"] = "Game" });
        Assert.Equal("""{"width":1280}""", args["obsInputSettings"]);
    }

    [Fact]
    public async Task Media_control_and_seek()
    {
        await Run("obs.mediaControl", new() { ["input"] = "Music", ["command"] = "Play" });
        Assert.Equal("OBS_MEDIA_STATE_PLAYING", _a.Inputs["Music"].MediaState);
        await Run("obs.mediaControl", new() { ["input"] = "Music", ["command"] = "TogglePlayPause" });
        Assert.Equal("OBS_MEDIA_STATE_PAUSED", _a.Inputs["Music"].MediaState);
        foreach (var command in new[] { "Restart", "Next", "Previous", "Stop" })
            await Run("obs.mediaControl", new() { ["input"] = "Music", ["command"] = command });
        Assert.Equal("OBS_MEDIA_STATE_STOPPED", _a.Inputs["Music"].MediaState);

        await Run("obs.mediaSeek", new() { ["input"] = "Music", ["mode"] = "Absolute", ["milliseconds"] = "5000" });
        await Run("obs.mediaSeek", new() { ["input"] = "Music", ["mode"] = "Offset", ["milliseconds"] = "-1500" });
        Assert.Equal(3500, _a.Inputs["Music"].MediaCursor);
    }

    // ---- Filters ----

    [Fact]
    public async Task Filter_state_random_all_and_settings()
    {
        var filters = _a.Filters["Webcam"];
        await Run("obs.setFilterState", new() { ["source"] = "Webcam", ["filter"] = "Blur", ["state"] = "Toggle" });
        Assert.True(filters[0].Enabled);
        await Run("obs.setFilterState", new() { ["source"] = "Webcam", ["filter"] = "Blur", ["state"] = "Disable" });
        Assert.False(filters[0].Enabled);

        await Run("obs.setAllFiltersState", new() { ["source"] = "Webcam", ["state"] = "Disable" });
        Assert.All(filters, f => Assert.False(f.Enabled));
        var args = await Run("obs.setRandomFilterState", new() { ["source"] = "Webcam", ["state"] = "Enable" });
        Assert.Equal(filters.Single(f => f.Enabled).Name, args["obsFilterName"]);

        await Run("obs.setFilterSettings", new() { ["source"] = "Webcam", ["filter"] = "Color", ["settings"] = """{"opacity":0.5}""" });
        Assert.Equal(0.5, filters[1].Settings["opacity"].GetDouble());
        Assert.Null(await TryRun("obs.setFilterSettings", new() { ["source"] = "Webcam", ["filter"] = "Color", ["settings"] = "[1]" }));
    }

    // ---- Transitions, outputs, misc ----

    [Fact]
    public async Task Transitions_profiles_collections_and_hotkeys()
    {
        await Run("obs.setTransition", new() { ["connection"] = "Studio B", ["transition"] = "Cut" });
        await Run("obs.setTransitionDuration", new() { ["connection"] = "Studio B", ["milliseconds"] = "750" });
        Assert.Equal("Cut", _b.Transition);
        Assert.Equal(750, _b.TransitionDuration);

        await Run("obs.setProfile", new() { ["profile"] = "Streaming" });
        await Run("obs.setSceneCollection", new() { ["sceneCollection"] = "Alt" });
        await Run("obs.triggerHotkey", new() { ["hotkey"] = "OBSBasic.Screenshot" });
        Assert.Equal("Streaming", _a.Profile);
        Assert.Equal("Alt", _a.SceneCollection);
        Assert.Equal(["OBSBasic.Screenshot"], _a.Hotkeys);
    }

    [Fact]
    public async Task Streaming_recording_virtual_cam_and_chapters()
    {
        await Run("obs.streaming", new() { ["mode"] = "Start" });
        Assert.True(_a.Streaming);
        await Run("obs.recording", new() { ["mode"] = "Start" });
        await Run("obs.recording", new() { ["mode"] = "Pause" });
        Assert.True(_a.Recording && _a.RecordPaused);
        await Run("obs.recording", new() { ["mode"] = "TogglePause" });
        Assert.False(_a.RecordPaused);
        await Run("obs.recording", new() { ["mode"] = "Resume" });
        await Run("obs.virtualCam", new() { ["mode"] = "Toggle" });
        Assert.True(_a.VirtualCam);
        await Run("obs.createRecordChapter", new() { ["chapterName"] = "Boss fight" });
        await Run("obs.createRecordChapter", new());
        Assert.Equal(["Boss fight", ""], _a.Chapters);

        var args = await Run("obs.getStatus", new());
        Assert.Equal(true, args["obsConnected"]);
        Assert.Equal(true, args["obsStreaming"]);
        Assert.Equal(true, args["obsRecording"]);
        Assert.Equal(false, args["obsRecordingPaused"]);
        Assert.Equal(true, args["obsVirtualCam"]);
        Assert.Equal(false, args["obsReplayBuffer"]);
        Assert.Equal("Starting", args["obsScene"]);
    }

    [Fact]
    public async Task Replay_buffer_save_returns_the_saved_file()
    {
        await Run("obs.replayBuffer", new() { ["connection"] = "Studio B", ["mode"] = "Start" });
        var args = await Run("obs.replayBuffer", new() { ["connection"] = "Studio B", ["mode"] = "Save" });

        Assert.Equal("/replays/B.mkv", args["obsReplayPath"]);
        var saved = await _recorder.WaitForAsync(ObsEvents.ReplayBufferSaved);
        Assert.Equal("Studio B", saved.Args["obsConnection"]);
        await Run("obs.replayBuffer", new() { ["connection"] = "Studio B", ["mode"] = "Stop" });
        Assert.False(_b.ReplayBuffer);
    }

    [Fact]
    public async Task Output_state_stats_screenshot_and_raw_requests()
    {
        var args = await Run("obs.getOutputState", new() { ["output"] = "Stream" });
        Assert.Equal(false, args["obsOutputActive"]);
        Assert.Equal("00:01:00.000", args["obsOutputTimecode"]);
        args = await Run("obs.getOutputState", new() { ["output"] = "Other", ["outputName"] = "adv_stream" });
        Assert.Equal(true, args["obsOutputActive"]);

        args = await Run("obs.getStats", new());
        Assert.Equal(12.5, args["obsCpuUsage"]);
        Assert.Equal(60.0, args["obsActiveFps"]);
        Assert.Equal(2.0, args["obsStreamOutputSkippedFrames"]);

        args = await Run("obs.screenshot", new() { ["file"] = "C:/shots/a.jpg", ["width"] = "640" });
        Assert.Equal("C:/shots/a.jpg", args["obsScreenshotPath"]);
        Assert.Equal(["Starting|jpg|C:/shots/a.jpg"], _a.Screenshots);
        var request = _serverA.Requests.Last(r => r.Type == "SaveSourceScreenshot").Data;
        Assert.Equal(640, request.GetProperty("imageWidth").GetInt32());
        Assert.False(request.TryGetProperty("imageHeight", out _));

        args = await Run("obs.rawRequest", new() { ["requestType"] = "GetVersion" });
        Assert.Contains("31.0.0", (string)args["obsResult"]!);

        args = await Run("obs.rawBatch", new()
        {
            ["connection"] = "Studio B",
            ["requests"] = """[{"requestType":"SetCurrentProgramScene","requestData":{"sceneName":"Gaming"}},{"requestType":"Bogus"},{"requestType":"GetVersion"}]""",
            ["haltOnFailure"] = "true",
        });
        Assert.Equal("Gaming", _b.CurrentScene);
        var results = System.Text.Json.JsonDocument.Parse((string)args["obsResult"]!).RootElement;
        Assert.Equal(2, results.GetArrayLength());   // halted at the failure
        Assert.False(results[1].GetProperty("requestStatus").GetProperty("result").GetBoolean());
    }

    [Fact]
    public async Task Status_reports_disconnected_without_failing()
    {
        await B.DisconnectAsync();
        var args = await Run("obs.getStatus", new() { ["connection"] = "Studio B" });
        Assert.Equal(false, args["obsConnected"]);
        Assert.Null(await TryRun("obs.setScene", new() { ["connection"] = "Studio B", ["scene"] = "Gaming" }));
        Assert.Contains("'Studio B' is not connected", LastError());
    }

    // ---- Triggers for mapped events ----

    [Theory]
    [InlineData("RecordStateChanged", """{"outputState":"OBS_WEBSOCKET_OUTPUT_PAUSED"}""", ObsEvents.RecordPaused)]
    [InlineData("RecordStateChanged", """{"outputState":"OBS_WEBSOCKET_OUTPUT_RESUMED"}""", ObsEvents.RecordResumed)]
    [InlineData("VirtualcamStateChanged", """{"outputState":"OBS_WEBSOCKET_OUTPUT_STARTED"}""", ObsEvents.VirtualCamStarted)]
    [InlineData("StudioModeStateChanged", """{"studioModeEnabled":true}""", ObsEvents.StudioModeChanged)]
    [InlineData("SceneTransitionStarted", """{"transitionName":"Fade"}""", ObsEvents.TransitionStarted)]
    [InlineData("SceneTransitionEnded", """{"transitionName":"Fade"}""", ObsEvents.TransitionEnded)]
    [InlineData("MediaInputPlaybackStarted", """{"inputName":"Music"}""", ObsEvents.MediaStarted)]
    [InlineData("MediaInputPlaybackEnded", """{"inputName":"Music"}""", ObsEvents.MediaEnded)]
    [InlineData("InputMuteStateChanged", """{"inputName":"Mic","inputMuted":true}""", ObsEvents.InputMuteChanged)]
    [InlineData("InputVolumeChanged", """{"inputName":"Mic","inputVolumeMul":0.5,"inputVolumeDb":-6.02}""", ObsEvents.InputVolumeChanged)]
    [InlineData("VendorEvent", """{"vendorName":"move","eventType":"done","eventData":{"a":1}}""", ObsEvents.VendorEvent)]
    public async Task Obs_events_fire_their_triggers(string obsEvent, string data, string botEvent)
    {
        using var document = System.Text.Json.JsonDocument.Parse(data);
        await _serverB.SendEventAsync(obsEvent, document.RootElement);

        var e = await _recorder.WaitForAsync(botEvent);
        Assert.Equal("Studio B", e.Args["obsConnection"]);
        var generic = await _recorder.WaitForAsync(ObsEvents.Event);
        Assert.Equal(obsEvent, generic.Args["eventType"]);
    }

    private async Task<Dictionary<string, object?>> Run(string type, Dictionary<string, string> parameters) =>
        await TryRun(type, parameters) ?? throw new Xunit.Sdk.XunitException($"{type} failed: {LastError()}");

    /// <summary>Runs one sub-action in the real engine; returns the final args, or null when the step failed.</summary>
    private async Task<Dictionary<string, object?>?> TryRun(string type, Dictionary<string, string> parameters)
    {
        _capture.Reset();
        var action = new ActionDefinition
        {
            Name = "test " + type,
            SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = CaptureSubAction.Id }],
        };
        await _services.GetRequiredService<ActionEngine>().RunAsync(action);
        return _capture.Args;
    }

    private string LastError() => _services.GetRequiredService<ActionEngine>().History.Entries
        .FirstOrDefault(e => e.Error is not null)?.Error ?? "";

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 10 s");
            await Task.Delay(20);
        }
    }

    internal sealed class CaptureSubAction : ISubActionHandler
    {
        public const string Id = "test.capture";
        private volatile Dictionary<string, object?>? _args;
        public Dictionary<string, object?>? Args => _args;
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
        public void Reset() => _args = null;

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            _args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
