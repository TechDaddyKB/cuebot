using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Obs.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs.Tests;

/// <summary>
/// The corrections and tighter integration from the obs-websocket 5.x review, end to end through DI, the action
/// engine and a fake OBS: group items, stale names, batches, cached output state, OBS closing, high-volume events
/// on demand, talking detection, custom events, plugin requests and following renames.
/// </summary>
public sealed class ObsIntegrationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-obs-integration-" + Guid.NewGuid().ToString("N"));
    private readonly FakeObsServer _server = FakeObsServer.Start();
    private readonly FakeObsState _obsState;
    private readonly ObsFeatureTests.CaptureSubAction _capture = new();
    private ServiceProvider _services = null!;
    private BusRecorder _recorder = null!;
    private ObsService _obs = null!;

    public ObsIntegrationTests() => _obsState = new FakeObsState(_server, "A");

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

    private ProfileService Profile => _services.GetRequiredService<ProfileService>();

    // ---- Corrections ----

    [Fact]
    public async Task Visibility_of_a_source_inside_a_group_names_the_source()
    {
        var follow = _obsState.Groups["Alerts"].Single(i => i.Source == "Follow");
        await _server.SendEventAsync("SceneItemEnableStateChanged", new { sceneName = "Alerts", sceneItemId = follow.Id, sceneItemEnabled = false });

        var e = await _recorder.WaitForAsync(ObsEvents.SourceVisibilityChanged);
        Assert.Equal("Follow", e.Args["sourceName"]);
        Assert.Equal("Alerts", e.Args["sceneName"]);
    }

    [Fact]
    public async Task Visibility_of_a_group_item_also_resolves_on_obs_before_30_1()
    {
        _obsState.SupportsSceneItemSource = false;
        var raid = _obsState.Groups["Alerts"].Single(i => i.Source == "Raid");
        await _server.SendEventAsync("SceneItemEnableStateChanged", new { sceneName = "Alerts", sceneItemId = raid.Id, sceneItemEnabled = true });

        var e = await _recorder.WaitForAsync(ObsEvents.SourceVisibilityChanged);
        Assert.Equal("Raid", e.Args["sourceName"]);
        Assert.Contains(_server.Requests, r => r.Type == "GetGroupSceneItemList");
    }

    [Fact]
    public async Task Renamed_source_is_not_reported_under_its_old_name()
    {
        var items = _obsState.Scenes["Gaming"];
        var overlay = items.Single(i => i.Source == "Overlay");
        await _server.SendEventAsync("SceneItemEnableStateChanged", new { sceneName = "Gaming", sceneItemId = overlay.Id, sceneItemEnabled = false });
        Assert.Equal("Overlay", (await _recorder.WaitForAsync(ObsEvents.SourceVisibilityChanged)).Args["sourceName"]);

        items[items.IndexOf(overlay)] = overlay with { Source = "Chat box" };
        await _server.SendEventAsync("InputNameChanged", new { oldInputName = "Overlay", inputName = "Chat box", inputUuid = "u1" });
        await _server.SendEventAsync("SceneItemEnableStateChanged", new { sceneName = "Gaming", sceneItemId = overlay.Id, sceneItemEnabled = true });

        Assert.Equal("Chat box", (await _recorder.WaitForAsync(ObsEvents.SourceVisibilityChanged, 2)).Args["sourceName"]);
    }

    [Fact]
    public async Task Duplicate_sources_can_target_the_top_copy_or_all_copies()
    {
        _obsState.Scenes["Gaming"].Insert(0, new FakeObsState.Item(900, "Webcam"));   // a second Webcam on top
        var bottom = _obsState.Scenes["Gaming"].Single(i => i.Source == "Webcam" && i.Id != 900);

        await Run("obs.setSourceVisibility", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["state"] = "Hidden" });
        Assert.False(bottom.Enabled);
        Assert.True(_obsState.Scenes["Gaming"][0].Enabled);

        await Run("obs.setSourceVisibility", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["state"] = "Hidden", ["copy"] = "Top" });
        Assert.False(_obsState.Scenes["Gaming"][0].Enabled);

        await Run("obs.setSourceVisibility", new() { ["scene"] = "Gaming", ["source"] = "Webcam", ["state"] = "Visible", ["copy"] = "All" });
        Assert.True(bottom.Enabled && _obsState.Scenes["Gaming"][0].Enabled);
        Assert.Equal(1, _server.LastBatchExecutionType);
    }

    [Fact]
    public async Task Screenshot_needs_a_full_path()
    {
        Assert.Null(await TryRun("obs.screenshot", new() { ["file"] = "shots/a.png" }));
        Assert.Contains("not a full path", LastError());
        Assert.Empty(_obsState.Screenshots);

        await Run("obs.screenshot", new() { ["file"] = @"D:\shots\a.png" });
        await Run("obs.screenshot", new() { ["file"] = "/home/me/a.png" });
        Assert.Equal(2, _obsState.Screenshots.Count);
    }

    [Fact]
    public void Any_obs_event_suggests_an_event_that_is_always_delivered()
    {
        var trigger = _services.GetRequiredService<Registry>().GetTrigger(ObsEvents.Event)!;
        var help = trigger.Parameters.Single(p => p.Key == "eventType").Help ?? "";
        Assert.DoesNotContain("InputActiveStateChanged", help);
        Assert.Contains("SceneItemLockStateChanged", help);
    }

    [Fact]
    public async Task Raw_batch_passes_the_execution_type()
    {
        var args = await Run("obs.rawBatch", new()
        {
            ["requests"] = """[{"requestType":"SetCurrentProgramScene","requestData":{"sceneName":"Gaming"}},{"requestType":"Sleep","requestData":{"sleepFrames":1}}]""",
            ["executionType"] = "SerialFrame",
        });
        Assert.Equal(1, _server.LastBatchExecutionType);
        Assert.Equal("Gaming", _obsState.CurrentScene);
        Assert.Equal(2, JsonDocument.Parse((string)args["obsResult"]!).RootElement.GetArrayLength());
    }

    // ---- Tighter integration ----

    [Fact]
    public void Connecting_loads_everything_in_one_batch()
    {
        var first = _server.Batches.First();
        Assert.Equal(ObsConnection.SnapshotRequests, first);
        Assert.DoesNotContain(_server.Requests, r => r.Type == "GetSceneList" && !first.Contains(r.Type));
        Assert.Equal(["Alerts"], _obs.Default.Groups);
        Assert.Equal(["Gaming", "Starting"], _obs.Default.Scenes.OrderBy(s => s));
    }

    [Fact]
    public async Task Hide_all_and_random_sources_change_in_one_frame()
    {
        await Run("obs.hideSceneSources", new() { ["scene"] = "Gaming" });
        Assert.Equal(1, _server.LastBatchExecutionType);
        Assert.All(_obsState.Scenes["Gaming"], i => Assert.False(i.Enabled));

        var batches = _server.Batches.Count;
        await Run("obs.setAllFiltersState", new() { ["source"] = "Webcam", ["state"] = "Enable" });
        Assert.Equal(batches + 1, _server.Batches.Count);
        Assert.All(_obsState.Filters["Webcam"], f => Assert.True(f.Enabled));
    }

    [Fact]
    public async Task Status_comes_from_the_cached_output_state()
    {
        await Run("obs.streaming", new() { ["mode"] = "Start" });
        await Run("obs.recording", new() { ["mode"] = "Start" });
        await Run("obs.recording", new() { ["mode"] = "Pause" });
        var requests = _server.Requests.Count;

        var args = await Run("obs.getStatus", new());

        Assert.Equal(requests, _server.Requests.Count);   // no round trip
        Assert.Equal(true, args["obsStreaming"]);
        Assert.Equal(true, args["obsRecording"]);
        Assert.Equal(true, args["obsRecordingPaused"]);
        Assert.Equal(false, args["obsVirtualCam"]);

        await _server.SendEventAsync("StreamStateChanged", new { outputActive = false, outputState = "OBS_WEBSOCKET_OUTPUT_STOPPED" });
        await _recorder.WaitForAsync(ObsEvents.StreamStopped);
        Assert.False(_obs.Default.IsStreaming);
        var status = (Dictionary<string, object?>)(await _services.GetRequiredService<ScriptApi>()
            .CallAsync(null!, "obs.status", JsonDocument.Parse("{}").RootElement, CancellationToken.None))!;
        Assert.Equal(false, status["obsStreaming"]);
    }

    [Fact]
    public async Task Obs_closing_fires_a_trigger_and_is_not_a_warning()
    {
        var log = _services.GetRequiredService<AppLog>();
        var warnings = new List<LogEntry>();
        log.EntryAdded += e => { if (e.Level >= LogLevel.Warning && e.Source.StartsWith("OBS", StringComparison.Ordinal)) lock (warnings) warnings.Add(e); };

        await _server.SendEventAsync("ExitStarted");
        await _recorder.WaitForAsync(ObsEvents.Exiting);
        Assert.True(_obs.Default.IsExiting);
        await _server.CloseSessionAsync();

        var disconnected = await _recorder.WaitForAsync(ObsEvents.Disconnected);
        Assert.Equal(true, disconnected.Args["obsClosing"]);
        Assert.Equal(ConnectionState.Disconnected, _obs.Default.State);
        lock (warnings) Assert.Empty(warnings);
    }

    [Fact]
    public async Task High_volume_events_are_subscribed_only_while_a_trigger_needs_them()
    {
        Assert.Equal(0xFFF, _server.Subscriptions);
        var action = AddAction(new TriggerDefinition { Type = ObsEvents.SourceActiveChanged, Settings = { ["input"] = "Webcam" } });
        await WaitUntil(() => _server.Subscriptions == (0xFFF | (1 << 17)));

        await Profile.EditAsync(p => p.Actions.Single(a => a.Id == action.Id).Triggers.Add(
            new TriggerDefinition { Type = ObsEvents.Event, Settings = { ["eventType"] = "SceneItemTransformChanged" } }));
        await WaitUntil(() => _server.Subscriptions == (0xFFF | (1 << 17) | (1 << 19)));

        await Profile.EditAsync(p => p.Actions.Single(a => a.Id == action.Id).Enabled = false);
        await WaitUntil(() => _server.Subscriptions == 0xFFF);

        // A trigger for another connection does not subscribe this one.
        AddAction(new TriggerDefinition { Type = ObsEvents.Talking, Settings = { ["connection"] = "Other" } });
        await Task.Delay(200);
        Assert.Equal(0xFFF, _server.Subscriptions);
    }

    [Fact]
    public async Task Source_live_and_transform_triggers()
    {
        AddAction(new TriggerDefinition { Type = ObsEvents.SourceActiveChanged, Settings = { ["input"] = "webcam", ["state"] = "Live" } });
        await _server.SendEventAsync("InputActiveStateChanged", new { inputName = "Webcam", inputUuid = "u", videoActive = true });
        var live = await _recorder.WaitForAsync(ObsEvents.SourceActiveChanged);
        Assert.Equal(true, live.Args["videoActive"]);
        await WaitUntil(() => _capture.Args is not null);
        Assert.Equal("Webcam", _capture.Args!["inputName"]);

        await _server.SendEventAsync("InputShowStateChanged", new { inputName = "Webcam", videoShowing = false });
        Assert.Equal(false, (await _recorder.WaitForAsync(ObsEvents.SourceShowChanged)).Args["videoShowing"]);

        var webcam = _obsState.Scenes["Gaming"].Single(i => i.Source == "Webcam");
        await _server.SendEventAsync("SceneItemTransformChanged",
            new { sceneName = "Gaming", sceneItemId = webcam.Id, sceneItemTransform = new { positionX = 10.0 } });
        var moved = await _recorder.WaitForAsync(ObsEvents.SourceTransformChanged);
        Assert.Equal("Webcam", moved.Args["sourceName"]);
        Assert.Contains("positionX", (string)moved.Args["transform"]!);
    }

    [Fact]
    public async Task Talking_starts_above_the_level_and_stops_after_the_quiet_time()
    {
        long now = 0;
        _obs.Default.Clock = () => now;
        AddAction(new TriggerDefinition { Type = ObsEvents.Talking, Settings = { ["input"] = "Mic", ["threshold"] = "-20", ["quietMs"] = "500", ["state"] = "Stopped" } });
        await WaitUntil(() => _server.Subscriptions == (0xFFF | (1 << 16)));

        await Meter(0.05);          // -26 dB: quiet
        now = 100;
        await Meter(0.5);           // -6 dB: talking
        var started = await _recorder.WaitForAsync(ObsEvents.Talking);
        Assert.Equal(true, started.Args["talking"]);
        Assert.Equal(-6.0, (double)started.Args["levelDb"]!, 0);
        now = 400;
        await Meter(0.01);          // quiet, but not for 500 ms yet
        now = 700;
        await Meter(0.01);
        var stopped = await _recorder.WaitForAsync(ObsEvents.Talking, 2);
        Assert.Equal(false, stopped.Args["talking"]);
        await WaitUntil(() => _capture.Args is not null);   // the "Stopped" trigger ran
        Assert.Equal("Mic", _capture.Args!["inputName"]);

        // Meters are never republished as "Any OBS event" unless a trigger asks for exactly that.
        Assert.DoesNotContain(_recorder.Events, e => e.Type == ObsEvents.Event && (string?)e.Args["eventType"] == "InputVolumeMeters");

        Task Meter(double peak) => _server.SendEventAsync("InputVolumeMeters", new
        {
            inputs = new object[]
            {
                new { inputName = "Mic", inputUuid = "m", inputLevelsMul = new[] { new[] { peak / 2, peak, peak }, new[] { 0.0, peak / 3, 0.0 } } },
                new { inputName = "Music", inputUuid = "n", inputLevelsMul = new[] { new[] { 1.0, 1.0, 1.0 } } },
            },
        });
    }

    [Fact]
    public async Task Custom_events_go_out_and_come_back_as_a_filtered_trigger()
    {
        AddAction(new TriggerDefinition { Type = ObsEvents.CustomEvent, Settings = { ["field"] = "user.name", ["value"] = "KRIS" } });

        await Run("obs.sendCustomEvent", new() { ["eventData"] = """{"type":"alert","user":{"name":"Someone"}}""" });
        var first = await _recorder.WaitForAsync(ObsEvents.CustomEvent);
        Assert.Equal("alert", first.Args["customEvent.type"]);
        await Task.Delay(200);
        Assert.Null(_capture.Args);   // wrong user: the trigger did not run

        await _server.SendEventAsync("CustomEvent", new { eventData = new { type = "alert", user = new { name = "kris" } } });
        await WaitUntil(() => _capture.Args is not null);
        Assert.Contains("\"kris\"", (string)_capture.Args!["eventData"]!);
        Assert.Single(_obsState.CustomEvents);
    }

    [Fact]
    public async Task Plugin_requests_return_the_plugin_answer()
    {
        var args = await Run("obs.callVendorRequest", new() { ["vendor"] = "fake-vendor", ["requestType"] = "echo", ["requestData"] = """{"a":1}""" });
        Assert.Equal("""{"a":1}""", args["obsVendorResult"]);
        Assert.Null(await TryRun("obs.callVendorRequest", new() { ["vendor"] = "nope", ["requestType"] = "echo" }));
    }

    [Fact]
    public async Task Renames_in_obs_are_followed_in_saved_steps_and_triggers()
    {
        var action = AddAction(new TriggerDefinition { Type = ObsEvents.SceneChanged, Settings = { ["scene"] = "Gaming" } },
            new SubActionDefinition { Type = "obs.setScene", Parameters = { ["scene"] = "Gaming" } },
            new SubActionDefinition { Type = "obs.setScene", Parameters = { ["connection"] = "Elsewhere", ["scene"] = "Gaming" } },
            new SubActionDefinition { Type = "obs.setSourceVisibility", Parameters = { ["scene"] = "%scene%", ["source"] = "Webcam" } },
            new SubActionDefinition { Type = "obs.setFilterState", Parameters = { ["source"] = "Webcam", ["filter"] = "Blur" } });

        await _server.SendEventAsync("SceneNameChanged", new { sceneUuid = "s", oldSceneName = "Gaming", sceneName = "Gameplay" });
        await _server.SendEventAsync("InputNameChanged", new { inputUuid = "i", oldInputName = "Webcam", inputName = "Camera" });
        await _server.SendEventAsync("SourceFilterNameChanged", new { sourceName = "Camera", oldFilterName = "Blur", filterName = "Soft" });

        await WaitUntil(() => Saved().SubActions[3].Parameters["filter"] == "Soft");
        var saved = Saved();
        Assert.Equal("Gameplay", saved.Triggers[0].Settings["scene"]);
        Assert.Equal("Gameplay", saved.SubActions[0].Parameters["scene"]);
        Assert.Equal("Gaming", saved.SubActions[1].Parameters["scene"]);          // another connection
        Assert.Equal("%scene%", saved.SubActions[2].Parameters["scene"]);         // variables are left alone
        Assert.Equal("Camera", saved.SubActions[2].Parameters["source"]);
        Assert.Equal("Camera", saved.SubActions[3].Parameters["source"]);

        _obs.Default.Settings.FollowRenames = false;
        await _server.SendEventAsync("InputNameChanged", new { inputUuid = "i", oldInputName = "Camera", inputName = "Cam 2" });
        await Task.Delay(300);
        Assert.Equal("Camera", Saved().SubActions[3].Parameters["source"]);

        ActionDefinition Saved() => Profile.Snapshot.Actions.Single(a => a.Id == action.Id);
    }

    // ---- Helpers ----

    private ActionDefinition AddAction(TriggerDefinition trigger, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition
        {
            Name = "test " + Guid.NewGuid().ToString("N"),
            Triggers = [trigger],
            SubActions = steps.Length > 0 ? [.. steps] : [new SubActionDefinition { Type = ObsFeatureTests.CaptureSubAction.Id }],
        };
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return action;
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
        var args = _capture.Args;
        _capture.Reset();
        return args;
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
}
