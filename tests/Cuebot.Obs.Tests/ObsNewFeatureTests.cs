using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Storage;
using Cuebot.Obs.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs.Tests;

/// <summary>
/// The sub-actions and triggers for the obs-websocket requests and events Cuebot did not use before, end to end
/// through DI and the action engine against a fake OBS 32.2 (obs-websocket 5.7.4).
/// </summary>
public sealed class ObsNewFeatureTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-obs-new-" + Guid.NewGuid().ToString("N"));
    private readonly FakeObsServer _server = FakeObsServer.Start();
    private readonly FakeObsState _obsState;
    private readonly ObsFeatureTests.CaptureSubAction _capture = new();
    private ServiceProvider _services = null!;
    private BusRecorder _recorder = null!;
    private ObsService _obs = null!;

    public ObsNewFeatureTests() => _obsState = new FakeObsState(_server, "A");

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_directory)).AddObs();
        services.AddSingleton<ISubActionHandler>(_capture);
        _services = services.BuildServiceProvider();
        _recorder = new BusRecorder(_services.GetRequiredService<EventBus>());
        var settings = _services.GetRequiredService<JsonFile<ObsSettings>>().Value;
        settings.Host = "localhost";
        settings.Port = _server.Port;
        _obs = _services.GetRequiredService<ObsService>();
        _services.GetRequiredService<ActionEngine>().Start();
        await _obs.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        _recorder.Dispose();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private JsonElement LastRequest(string type) => _server.Requests.Last(r => r.Type == type).Data;

    [Fact]
    public void Caches_hotkeys_kinds_outputs_and_canvases_for_suggestions()
    {
        var c = _obs.Default;
        Assert.Contains("OBSBasic.StartStreaming", c.Hotkeys);
        Assert.Contains("browser_source", c.InputKinds);
        Assert.Contains("color_filter_v2", c.FilterKinds);
        Assert.Equal(["adv_stream", "vertical"], c.Outputs);
        Assert.Equal(["Main", "Vertical"], c.Canvases.Select(x => x.Name));
        var registry = _services.GetRequiredService<Registry>();
        Assert.Contains("OBSBasic.Screenshot", registry.GetSubAction("obs.triggerHotkey")!.Parameters.Single(p => p.Key == "hotkey").Suggestions!());
    }

    [Fact]
    public async Task Recording_split_and_folder()
    {
        Assert.Null(await TryRun("obs.splitRecording", new()));
        await Run("obs.recording", new() { ["mode"] = "Start" });
        await Run("obs.splitRecording", new());
        Assert.Equal(1, _obsState.Splits);
        Assert.Equal("/videos/part2.mkv", (await _recorder.WaitForAsync(ObsEvents.RecordFileChanged)).Args["newOutputPath"]);

        await Run("obs.setRecordDirectory", new() { ["directory"] = @"E:\Episodes" });
        Assert.Equal(@"E:\Episodes", (await Run("obs.getRecordDirectory", new()))["obsRecordDirectory"]);
        Assert.Null(await TryRun("obs.setRecordDirectory", new() { ["directory"] = "relative" }));
    }

    [Fact]
    public async Task Audio_monitoring_balance_sync_and_settings()
    {
        await Run("obs.setAudioMonitor", new() { ["input"] = "Music", ["monitorType"] = "MonitorOnly" });
        await Run("obs.setAudioBalance", new() { ["input"] = "Music", ["balance"] = "2" });
        await Run("obs.setAudioSyncOffset", new() { ["input"] = "Music", ["milliseconds"] = "-2000" });
        Assert.Equal("OBS_MONITORING_TYPE_MONITOR_ONLY", _obsState.Inputs["Music"].MonitorType);
        Assert.Equal(1, _obsState.Inputs["Music"].Balance);
        Assert.Equal(-950, _obsState.Inputs["Music"].SyncOffset);

        var args = await Run("obs.getAudioSettings", new() { ["input"] = "Music" });
        Assert.Equal("MonitorOnly", args["obsAudioMonitorType"]);
        Assert.Equal(1.0, args["obsAudioBalance"]);
        Assert.Equal(-950.0, args["obsAudioSyncOffset"]);
        Assert.Contains("\"1\":true", (string)args["obsAudioTracks"]!);
        Assert.Null(await TryRun("obs.getAudioSettings", new() { ["input"] = "Nope" }));
    }

    [Fact]
    public async Task Source_order_lock_and_blend_mode()
    {
        // Gaming, top first: Webcam, Game, Overlay, Alerts.
        var args = await Run("obs.setSourceOrder", new() { ["scene"] = "Gaming", ["source"] = "Overlay", ["order"] = "Top" });
        Assert.Equal("Overlay", _obsState.Scenes["Gaming"][0].Source);
        Assert.Equal(3.0, Convert.ToDouble(args["obsSourceIndex"]));
        await Run("obs.setSourceOrder", new() { ["scene"] = "Gaming", ["source"] = "Overlay", ["order"] = "Down" });
        Assert.Equal("Overlay", _obsState.Scenes["Gaming"][1].Source);
        await Run("obs.setSourceOrder", new() { ["scene"] = "Gaming", ["source"] = "Overlay", ["order"] = "Bottom" });
        Assert.Equal("Overlay", _obsState.Scenes["Gaming"][^1].Source);

        await Run("obs.setSourceLocked", new() { ["scene"] = "Gaming", ["source"] = "Webcam" });
        Assert.True(_obsState.Scenes["Gaming"].Single(i => i.Source == "Webcam").Locked);
        await Run("obs.setSourceLocked", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["state"] = "Toggle" });
        Assert.False(_obsState.Scenes["Gaming"].Single(i => i.Source == "Webcam").Locked);

        await Run("obs.setBlendMode", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["blendMode"] = "Screen" });
        Assert.Equal("OBS_BLEND_SCREEN", _obsState.Scenes["Gaming"].Single(i => i.Source == "Webcam").BlendMode);
    }

    [Fact]
    public async Task Create_rename_duplicate_and_remove_sources_and_scenes()
    {
        var args = await Run("obs.createInput", new()
        {
            ["scene"] = "Starting", ["name"] = "Viewer card", ["kind"] = "browser_source", ["settings"] = """{"url":"https://example.com"}""", ["visible"] = "false",
        });
        Assert.Equal("browser_source", _obsState.Inputs["Viewer card"].Kind);
        Assert.Equal("https://example.com", _obsState.Inputs["Viewer card"].Settings["url"].GetString());
        Assert.False(_obsState.Scenes["Starting"][0].Enabled);
        Assert.Equal(_obsState.Scenes["Starting"][0].Id, Convert.ToInt32(args["obsSceneItemId"]));

        await Run("obs.addSceneItem", new() { ["scene"] = "Gaming", ["source"] = "Music" });
        Assert.Equal("Music", _obsState.Scenes["Gaming"][0].Source);
        await Run("obs.duplicateSceneItem", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["destinationScene"] = "Starting" });
        Assert.Equal("Webcam", _obsState.Scenes["Starting"][0].Source);
        await Run("obs.removeSceneItem", new() { ["scene"] = "Gaming", ["source"] = "Music" });
        Assert.DoesNotContain(_obsState.Scenes["Gaming"], i => i.Source == "Music");

        await Run("obs.renameInput", new() { ["input"] = "Viewer card", ["newName"] = "Card" });
        await Run("obs.removeInput", new() { ["input"] = "Card" });
        Assert.False(_obsState.Inputs.ContainsKey("Card"));

        await Run("obs.createScene", new() { ["name"] = "BRB" });
        await Run("obs.renameScene", new() { ["scene"] = "BRB", ["newName"] = "Be right back" });
        await Run("obs.setSceneTransitionOverride", new() { ["scene"] = "Be right back", ["transition"] = "Fade", ["milliseconds"] = "2000" });
        Assert.Equal(("Fade", 2000), _obsState.TransitionOverrides["Be right back"]);
        await Run("obs.setSceneTransitionOverride", new() { ["scene"] = "Be right back" });
        Assert.Equal((null, null), _obsState.TransitionOverrides["Be right back"]);
        await Run("obs.removeScene", new() { ["scene"] = "Be right back" });
        Assert.False(_obsState.Scenes.ContainsKey("Be right back"));

        _obsState.Inputs["Webcam"].Active = true;
        args = await Run("obs.getSourceActive", new() { ["source"] = "Webcam" });
        Assert.Equal(true, args["obsSourceActive"]);
    }

    [Fact]
    public async Task Filters_can_be_added_renamed_reordered_and_removed()
    {
        await Run("obs.createFilter", new() { ["source"] = "Game", ["filter"] = "Tint", ["kind"] = "color_filter_v2", ["settings"] = """{"opacity":0.5}""" });
        Assert.Equal("color_filter_v2", LastRequest("CreateSourceFilter").GetProperty("filterKind").GetString());
        await Run("obs.renameFilter", new() { ["source"] = "Game", ["filter"] = "Tint", ["newName"] = "Red tint" });
        await Run("obs.createFilter", new() { ["source"] = "Game", ["filter"] = "Mask", ["kind"] = "mask_filter_v2" });
        await Run("obs.setFilterOrder", new() { ["source"] = "Game", ["filter"] = "Mask", ["position"] = "0" });
        Assert.Equal(["Mask", "Red tint"], _obsState.Filters["Game"].Select(f => f.Name));
        await Run("obs.removeFilter", new() { ["source"] = "Game", ["filter"] = "Mask" });
        Assert.Equal(["Red tint"], _obsState.Filters["Game"].Select(f => f.Name));
    }

    [Fact]
    public async Task Transition_settings_current_transition_and_t_bar()
    {
        await Run("obs.setTransitionSettings", new() { ["settings"] = """{"path":"/stinger.webm"}""", ["overlay"] = "false" });
        Assert.False(LastRequest("SetCurrentSceneTransitionSettings").GetProperty("overlay").GetBoolean());
        var args = await Run("obs.getCurrentTransition", new());
        Assert.Equal("Fade", args["obsTransitionName"]);
        Assert.Equal("fade_transition", args["obsTransitionKind"]);
        Assert.Equal(0.25, args["obsTransitionCursor"]);
        await Run("obs.setTBar", new() { ["position"] = "1.5", ["release"] = "false" });
        Assert.Equal(1, LastRequest("SetTBarPosition").GetProperty("position").GetDouble());
    }

    [Fact]
    public async Task Screenshot_to_variable_caption_projector_dialogs_and_hotkey_keys()
    {
        var args = await Run("obs.screenshotToVariable", new() { ["source"] = "Webcam", ["format"] = "jpg", ["width"] = "320" });
        Assert.Equal("data:image/jpg;base64,QUJD", args["obsScreenshot"]);
        Assert.Equal(320, LastRequest("GetSourceScreenshot").GetProperty("imageWidth").GetInt32());
        Assert.Empty(_obsState.Screenshots);   // nothing written to disk

        Assert.Null(await TryRun("obs.sendCaption", new() { ["text"] = "Hello" }));   // only while streaming
        await Run("obs.streaming", new() { ["mode"] = "Start" });
        await Run("obs.sendCaption", new() { ["text"] = "Hello" });
        Assert.Equal("Hello", LastRequest("SendStreamCaption").GetProperty("captionText").GetString());

        await Run("obs.openProjector", new() { ["projector"] = "Multiview", ["monitor"] = "1" });
        Assert.Equal("OBS_WEBSOCKET_VIDEO_MIX_TYPE_MULTIVIEW", LastRequest("OpenVideoMixProjector").GetProperty("videoMixType").GetString());
        await Run("obs.openProjector", new() { ["projector"] = "Source", ["source"] = "Webcam" });
        Assert.False(LastRequest("OpenSourceProjector").TryGetProperty("monitorIndex", out _));
        await Run("obs.openSourceDialog", new() { ["input"] = "Overlay", ["dialog"] = "Interact" });
        Assert.Contains(_server.Requests, r => r.Type == "OpenInputInteractDialog");

        await Run("obs.triggerHotkeyKeys", new() { ["key"] = "OBS_KEY_F13", ["control"] = "true" });
        var keys = LastRequest("TriggerHotkeyByKeySequence");
        Assert.Equal("OBS_KEY_F13", keys.GetProperty("keyId").GetString());
        Assert.True(keys.GetProperty("keyModifiers").GetProperty("control").GetBoolean());
        Assert.False(keys.GetProperty("keyModifiers").GetProperty("shift").GetBoolean());
    }

    [Fact]
    public async Task Persistent_data_profiles_and_video_settings()
    {
        await Run("obs.setPersistentData", new() { ["slot"] = "deaths", ["value"] = "12" });
        await Run("obs.setPersistentData", new() { ["realm"] = "Profile", ["slot"] = "title", ["value"] = "Speedrun" });
        Assert.Equal(12.0, (await Run("obs.getPersistentData", new() { ["slot"] = "deaths" }))["obsValue"]);
        Assert.Equal("Speedrun", (await Run("obs.getPersistentData", new() { ["realm"] = "Profile", ["slot"] = "title" }))["obsValue"]);
        Assert.Null((await Run("obs.getPersistentData", new() { ["slot"] = "missing" }))["obsValue"]);

        await Run("obs.createProfile", new() { ["profile"] = "Vertical" });
        await Run("obs.removeProfile", new() { ["profile"] = "Vertical" });
        Assert.Equal(["Default", "Streaming"], _obsState.Profiles);
        await Run("obs.createSceneCollection", new() { ["sceneCollection"] = "Test" });

        Assert.Equal("Simple", (await Run("obs.getProfileParameter", new() { ["category"] = "Output", ["name"] = "Mode" }))["obsProfileValue"]);
        await Run("obs.setProfileParameter", new() { ["category"] = "Output", ["name"] = "Mode", ["value"] = "" });
        Assert.Null(_obsState.ProfileParameters["Output|Mode"]);

        await Run("obs.setVideoSettings", new() { ["outputWidth"] = "1920", ["outputHeight"] = "1080", ["fpsNumerator"] = "30" });
        Assert.Equal((1920, 1080, 30, 1), (_obsState.Video["outputWidth"], _obsState.Video["outputHeight"], _obsState.Video["fpsNumerator"], _obsState.Video["fpsDenominator"]));
        Assert.Equal(1920.0, (await Run("obs.getVideoSettings", new()))["obsVideoOutputWidth"]);
        Assert.Null(await TryRun("obs.setVideoSettings", new()));
    }

    [Fact]
    public async Task Stream_destination_and_output_settings_hide_the_stream_key()
    {
        var args = await Run("obs.getStreamService", new());
        Assert.Equal("rtmp_custom", args["obsStreamServiceType"]);
        Assert.Equal("rtmp://live.example/app", args["obsStreamServer"]);
        Assert.DoesNotContain("live_secret_key", (string)args["obsStreamService"]!);
        Assert.Contains("(hidden)", (string)args["obsStreamService"]!);

        args = await Run("obs.getOutputSettings", new() { ["output"] = "vertical" });
        Assert.DoesNotContain("secret", (string)args["obsOutputSettings"]!);
        Assert.Contains("6000", (string)args["obsOutputSettings"]!);

        await Run("obs.setStreamService", new() { ["settings"] = """{"server":"rtmp://a","key":"k"}""" });
        Assert.Equal("rtmp_custom", LastRequest("SetStreamServiceSettings").GetProperty("streamServiceType").GetString());
        await Run("obs.output", new() { ["output"] = "vertical", ["mode"] = "Toggle" });
        Assert.Contains(_server.Requests, r => r.Type == "ToggleOutput");
        await Run("obs.setOutputSettings", new() { ["output"] = "vertical", ["settings"] = """{"bitrate":8000}""" });
        Assert.Null(await TryRun("obs.output", new() { ["output"] = "nope" }));
    }

    [Fact]
    public async Task Special_inputs_list_items_and_deinterlacing()
    {
        var args = await Run("obs.getSpecialInputs", new());
        Assert.Equal("Desktop Audio", args["obsDesktop1"]);
        Assert.Equal("Mic", args["obsMic1"]);
        Assert.Equal("", args["obsMic2"]);

        args = await Run("obs.getInputListItems", new() { ["input"] = "Game", ["property"] = "monitor_id" });
        Assert.Equal(2, args["obsListItemCount"]);
        Assert.Contains("Display 2", (string)args["obsListItems"]!);

        await Run("obs.setDeinterlace", new() { ["input"] = "Game", ["mode"] = "Yadif2x" });
        Assert.Equal("OBS_DEINTERLACE_MODE_YADIF_2X", LastRequest("SetInputDeinterlaceMode").GetProperty("inputDeinterlaceMode").GetString());
        Assert.DoesNotContain(_server.Requests, r => r.Type == "SetInputDeinterlaceFieldOrder");
        await Run("obs.setDeinterlace", new() { ["input"] = "Game", ["fieldOrder"] = "Bottom" });
        Assert.Equal("OBS_DEINTERLACE_FIELD_ORDER_BOTTOM", LastRequest("SetInputDeinterlaceFieldOrder").GetProperty("inputDeinterlaceFieldOrder").GetString());
    }

    [Fact]
    public async Task Canvas_parameter_adds_the_canvas_uuid_to_scene_and_source_requests()
    {
        await Run("obs.setSourceVisibility", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["state"] = "Hidden", ["canvas"] = "vertical" });
        Assert.Equal("c-vert", LastRequest("GetSceneItemId").GetProperty("canvasUuid").GetString());
        Assert.Equal("c-vert", LastRequest("SetSceneItemEnabled").GetProperty("canvasUuid").GetString());

        await Run("obs.hideSceneSources", new() { ["scene"] = "Gaming", ["canvas"] = "Vertical" });
        Assert.Equal("c-vert", LastRequest("SetSceneItemEnabled").GetProperty("canvasUuid").GetString());

        await Run("obs.setSourceVisibility", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["state"] = "Visible" });
        Assert.False(LastRequest("SetSceneItemEnabled").TryGetProperty("canvasUuid", out _));

        Assert.Null(await TryRun("obs.setSourceVisibility", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["canvas"] = "Nope" }));
        var registry = _services.GetRequiredService<Registry>();
        Assert.Contains(registry.GetSubAction("obs.setSourceTransform")!.Parameters, p => p.Key == "canvas");
        Assert.DoesNotContain(registry.GetSubAction("obs.setScene")!.Parameters, p => p.Key == "canvas");
    }

    [Theory]
    [InlineData("SceneTransitionVideoEnded", """{"transitionName":"Stinger"}""", ObsEvents.TransitionVideoEnded, "transitionName", "Stinger")]
    [InlineData("InputAudioMonitorTypeChanged", """{"inputName":"Music","monitorType":"OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT"}""", ObsEvents.AudioMonitorChanged, "monitorType", "MonitorAndOutput")]
    [InlineData("InputAudioBalanceChanged", """{"inputName":"Music","inputAudioBalance":0.2}""", ObsEvents.AudioBalanceChanged, "inputName", "Music")]
    [InlineData("InputAudioSyncOffsetChanged", """{"inputName":"Music","inputAudioSyncOffset":120}""", ObsEvents.AudioSyncOffsetChanged, "inputName", "Music")]
    [InlineData("InputAudioTracksChanged", """{"inputName":"Music","inputAudioTracks":{"1":false}}""", ObsEvents.AudioTracksChanged, "inputAudioTracks", """{"1":false}""")]
    [InlineData("InputSettingsChanged", """{"inputName":"Overlay","inputSettings":{"text":"hi"}}""", ObsEvents.InputSettingsChanged, "inputSettings", """{"text":"hi"}""")]
    [InlineData("SceneItemListReindexed", """{"sceneName":"Gaming","sceneItems":[]}""", ObsEvents.SourceOrderChanged, "sceneName", "Gaming")]
    [InlineData("SourceFilterCreated", """{"sourceName":"Game","filterName":"Tint","filterKind":"color_filter_v2"}""", ObsEvents.FilterAdded, "filterKind", "color_filter_v2")]
    [InlineData("SourceFilterRemoved", """{"sourceName":"Game","filterName":"Tint"}""", ObsEvents.FilterRemoved, "filterName", "Tint")]
    [InlineData("SourceFilterNameChanged", """{"sourceName":"Game","oldFilterName":"A","filterName":"B"}""", ObsEvents.FilterRenamed, "oldFilterName", "A")]
    [InlineData("SourceFilterListReindexed", """{"sourceName":"Game","filters":[]}""", ObsEvents.FilterOrderChanged, "sourceName", "Game")]
    [InlineData("SourceFilterSettingsChanged", """{"sourceName":"Game","filterName":"Tint","filterSettings":{"a":1}}""", ObsEvents.FilterSettingsChanged, "filterSettings", """{"a":1}""")]
    [InlineData("CurrentProfileChanging", """{"profileName":"Default"}""", ObsEvents.ProfileChanging, "profileName", "Default")]
    [InlineData("CurrentSceneCollectionChanging", """{"sceneCollectionName":"Main"}""", ObsEvents.SceneCollectionChanging, "sceneCollectionName", "Main")]
    public async Task New_obs_events_fire_their_triggers(string obsEvent, string data, string botEvent, string arg, string expected)
    {
        using var document = JsonDocument.Parse(data);
        await _server.SendEventAsync(obsEvent, document.RootElement);

        var e = await _recorder.WaitForAsync(botEvent);
        Assert.Equal(expected, e.Args[arg]);
        Assert.NotNull(_services.GetRequiredService<Registry>().GetTrigger(botEvent));
    }

    [Fact]
    public async Task Lock_changes_name_the_source()
    {
        var webcam = _obsState.Scenes["Gaming"].Single(i => i.Source == "Webcam");
        await _server.SendEventAsync("SceneItemLockStateChanged", new { sceneName = "Gaming", sceneItemId = webcam.Id, sceneItemLocked = true });
        var e = await _recorder.WaitForAsync(ObsEvents.SourceLockChanged);
        Assert.Equal("Webcam", e.Args["sourceName"]);
        Assert.Equal(true, e.Args["locked"]);
    }

    private async Task<Dictionary<string, object?>> Run(string type, Dictionary<string, string> parameters) =>
        await TryRun(type, parameters) ?? throw new Xunit.Sdk.XunitException($"{type} failed: {LastError()}");

    private async Task<Dictionary<string, object?>?> TryRun(string type, Dictionary<string, string> parameters)
    {
        _capture.Reset();
        var action = new ActionDefinition
        {
            Name = "run " + type,
            SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = ObsFeatureTests.CaptureSubAction.Id }],
        };
        await _services.GetRequiredService<ActionEngine>().RunAsync(action);
        return _capture.Args;
    }

    private string LastError() => _services.GetRequiredService<ActionEngine>().History.Entries
        .FirstOrDefault(e => e.Error is not null)?.Error ?? "";
}
