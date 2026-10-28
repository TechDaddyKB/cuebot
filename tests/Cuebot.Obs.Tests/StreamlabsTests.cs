using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Storage;
using Cuebot.Obs.Streamlabs;
using Cuebot.Obs.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs.Tests;

public sealed class StreamlabsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-slobs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStreamlabsServer _server = new();
    private readonly ObsFeatureTests.CaptureSubAction _capture = new();
    private ServiceProvider _services = null!;
    private BusRecorder _recorder = null!;
    private StreamlabsDesktopService _slobs = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_directory)).AddObs();
        services.AddSingleton<ISubActionHandler>(_capture);
        _services = services.BuildServiceProvider();
        _recorder = new BusRecorder(_services.GetRequiredService<EventBus>());
        var settings = _services.GetRequiredService<JsonFile<StreamlabsSettings>>().Value;
        settings.Host = "localhost";
        settings.Port = _server.Port;
        settings.Token = FakeStreamlabsServer.Token;
        _slobs = _services.GetRequiredService<StreamlabsDesktopService>();
        Assert.Same(_slobs, _services.GetRequiredService<ObsService>().StreamlabsDesktop);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _recorder.Dispose();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Connects_through_sockjs_authenticates_subscribes_and_loads_scenes()
    {
        await _slobs.ConnectAsync();

        await _recorder.WaitForAsync(StreamlabsEvents.Connected);
        Assert.Matches(@"^/api/\d{3}/[0-9a-f]+/websocket$", _server.LastPath);
        Assert.Equal(ConnectionState.Connected, _slobs.State);
        Assert.Equal(["Main", "BRB"], _slobs.SceneNames);
        Assert.Equal(["Cam", "Title", "Mic"], _slobs.SourceNames);
        Assert.Equal("Main", _slobs.CurrentScene);
        var calls = _server.Calls.ToArray();
        Assert.Equal(("TcpServerService", "auth"), (calls[0].Resource, calls[0].Method));
        Assert.Contains(calls, c => c is { Resource: "ScenesService", Method: "sceneSwitched" });
        Assert.Contains(calls, c => c is { Resource: "StreamingService", Method: "streamingStatusChange" });
    }

    [Fact]
    public async Task Wrong_token_is_an_authentication_error()
    {
        _slobs.Settings.Token = "wrong";

        await Assert.ThrowsAsync<StreamlabsAuthenticationException>(() => _slobs.ConnectAsync());

        Assert.Equal(ConnectionState.Error, _slobs.State);
        Assert.Contains("token", _slobs.StatusMessage);
    }

    [Fact]
    public async Task Events_publish_scene_stream_and_record_changes()
    {
        await _slobs.ConnectAsync();

        await _server.SendEventAsync("ScenesService.sceneSwitched", new { id = "s2", name = "BRB" });
        await _server.SendEventAsync("StreamingService.streamingStatusChange", "starting");
        await _server.SendEventAsync("StreamingService.streamingStatusChange", "live");
        await _server.SendEventAsync("StreamingService.recordingStatusChange", "recording");
        await _server.SendEventAsync("StreamingService.recordingStatusChange", "offline");
        await _server.SendEventAsync("StreamingService.replayBufferFileWrite", "C:/replay.mp4");

        var scene = await _recorder.WaitForAsync(StreamlabsEvents.SceneChanged);
        Assert.Equal("BRB", scene.Args["sceneName"]);
        Assert.Equal("Main", scene.Args["previousScene"]);
        await _recorder.WaitForAsync(StreamlabsEvents.StreamStarted);
        await _recorder.WaitForAsync(StreamlabsEvents.RecordStarted);
        await _recorder.WaitForAsync(StreamlabsEvents.RecordStopped);
        Assert.Equal("C:/replay.mp4", (await _recorder.WaitForAsync(StreamlabsEvents.ReplayBufferSaved)).Args["savedReplayPath"]);
        Assert.Single(_recorder.Events, e => e.Type == StreamlabsEvents.StreamStarted);
        Assert.Equal("BRB", _slobs.CurrentScene);
    }

    [Fact]
    public async Task Sub_actions_drive_streamlabs_desktop()
    {
        await _slobs.ConnectAsync();

        await Run("slobs.setScene", new() { ["scene"] = "brb" });
        Assert.Equal("s2", _server.ActiveScene);
        Assert.Equal("BRB", (await Run("slobs.getCurrentScene", new()))["slobsScene"]);

        await Run("slobs.setSourceVisibility", new() { ["scene"] = "Main", ["source"] = "Title", ["state"] = "Toggle" });
        Assert.True(_server.Visible["i2"]);
        await Run("slobs.hideSceneSources", new() { ["scene"] = "Main" });
        Assert.False(_server.Visible["i1"] || _server.Visible["i2"]);

        await Run("slobs.streaming", new() { ["mode"] = "Start" });
        await Run("slobs.streaming", new() { ["mode"] = "Start" });   // already live: no second toggle
        Assert.Equal("live", _server.StreamingStatus);
        Assert.Single(_server.Calls, c => c.Method == "toggleStreaming");
        await Run("slobs.recording", new() { ["mode"] = "Toggle" });
        Assert.Equal("recording", _server.RecordingStatus);
        await Run("slobs.replayBuffer", new() { ["mode"] = "Toggle" });
        Assert.Equal("running", _server.ReplayBufferStatus);
        await Run("slobs.replayBuffer", new() { ["mode"] = "Save" });
        Assert.Contains(_server.Calls, c => c.Method == "saveReplay");

        await Run("slobs.setSourceMute", new() { ["source"] = "Mic", ["state"] = "Toggle" });
        Assert.True(_server.Muted["src-mic"]);
        await Run("slobs.setText", new() { ["source"] = "Title", ["text"] = "Hi" });
        Assert.Equal("Hi", _server.SourceSettings["src-text"].GetProperty("text").GetString());
        await Run("slobs.setBrowserUrl", new() { ["source"] = "Cam", ["url"] = "https://x" });
        Assert.Equal("https://x", _server.SourceSettings["src-cam"].GetProperty("url").GetString());
        await Run("slobs.setFilterState", new() { ["source"] = "Cam", ["filter"] = "Blur", ["state"] = "Disable" });
        Assert.Equal(["src-cam|Blur|False"], _server.FilterCalls);

        await Run("slobs.flipSource", new() { ["scene"] = "Main", ["source"] = "Cam", ["axis"] = "Vertical" });
        await Run("slobs.rotateSource", new() { ["scene"] = "Main", ["source"] = "Cam", ["degrees"] = "-90" });
        Assert.Equal(["""{"scale":{"x":1,"y":-2}}""", """{"rotation":270}"""], _server.Transforms);

        var raw = await Run("slobs.raw", new() { ["resource"] = "ScenesService", ["method"] = "activeSceneId" });
        Assert.Equal("\"s2\"", raw["slobsResult"]);
    }

    [Fact]
    public async Task Events_carry_streamer_bot_sd_args()
    {
        await _slobs.ConnectAsync();

        var connected = await _recorder.WaitForAsync(StreamlabsEvents.Connected);
        Assert.Equal(StreamlabsDesktopService.ConnectionId, connected.Args["sd.id"]);
        Assert.Equal("Main", connected.Args["sd.name"]);
        Assert.Equal("localhost", connected.Args["sd.host"]);

        await _server.SendEventAsync("ScenesService.sceneSwitched", new { id = "s2", name = "BRB" });
        await _server.SendEventAsync("StreamingService.recordingStatusChange", "recording");
        var scene = await _recorder.WaitForAsync(StreamlabsEvents.SceneChanged);
        Assert.Equal("BRB", scene.Args["sd.sceneName"]);
        Assert.Equal("BRB", scene.Args["sceneName"]);
        Assert.Equal("localhost", (await _recorder.WaitForAsync(StreamlabsEvents.RecordStarted)).Args["sd.host"]);
    }

    [Fact]
    public async Task Get_current_scene_also_sets_currentScene()
    {
        await _slobs.ConnectAsync();

        var args = await Run("slobs.getCurrentScene", new());

        Assert.Equal("Main", args["currentScene"]);
        Assert.Equal("Main", args["slobsScene"]);
    }

    [Fact]
    public async Task Filter_sub_actions_set_scene_random_and_all_source_filters()
    {
        await _slobs.ConnectAsync();

        await Run("slobs.setSceneFilterState", new() { ["scene"] = "Main", ["filter"] = "shake", ["state"] = "Toggle" });
        Assert.True(_server.Filters["s1"]["Shake"]);
        await Run("slobs.setSceneFilterState", new() { ["scene"] = "Main", ["filter"] = "Shake", ["state"] = "Hidden" });
        Assert.False(_server.Filters["s1"]["Shake"]);

        var random = await Run("slobs.setRandomFilterState", new() { ["scene"] = "Main", ["source"] = "Cam", ["state"] = "Hidden" });
        var chosen = Assert.IsType<string>(random["slobsFilterName"]);
        Assert.Contains(chosen, new[] { "Blur", "Tint" });
        Assert.False(_server.Filters["src-cam"][chosen]);

        var sceneOwn = await Run("slobs.setRandomFilterState", new() { ["scene"] = "Main", ["state"] = "Visible" });
        Assert.Equal("Shake", sceneOwn["slobsFilterName"]);
        Assert.True(_server.Filters["s1"]["Shake"]);

        _server.FilterCalls.Clear();
        await Run("slobs.hideSourceFilters", new() { ["scene"] = "Main", ["source"] = "Cam" });
        Assert.False(_server.Filters["src-cam"]["Blur"] || _server.Filters["src-cam"]["Tint"]);
        Assert.Single(_server.FilterCalls);   // the already hidden filter is left alone
    }

    [Fact]
    public async Task Group_sub_actions_hide_and_randomize_folder_children()
    {
        await _slobs.ConnectAsync();

        var args = await Run("slobs.randomGroupSource", new() { ["scene"] = "Main", ["group"] = "alerts" });
        var chosen = Assert.IsType<string>(args["randomSource"]);
        Assert.Equal(chosen == "AlertA", _server.Visible["i3"]);
        Assert.Equal(chosen == "AlertB", _server.Visible["i4"]);
        Assert.True(_server.Visible["i1"]);   // sources outside the group are untouched

        await Run("slobs.hideGroupSources", new() { ["scene"] = "Main", ["group"] = "Alerts" });
        Assert.False(_server.Visible["i3"] || _server.Visible["i4"]);
        Assert.True(_server.Visible["i1"]);
    }

    [Fact]
    public async Task Auto_connect_reconnects_after_the_session_closes()
    {
        await using var slobs = new StreamlabsDesktopService(_services.GetRequiredService<JsonFile<StreamlabsSettings>>(),
            _services.GetRequiredService<EventBus>(), new Core.Logging.AppLog(), TimeSpan.FromMilliseconds(100));
        slobs.Settings.AutoConnect = true;
        await slobs.StartAsync();
        await _recorder.WaitForAsync(StreamlabsEvents.Connected);

        await _server.CloseSessionAsync();

        await _recorder.WaitForAsync(StreamlabsEvents.Disconnected);
        await _recorder.WaitForAsync(StreamlabsEvents.Connected, 2);
        Assert.Equal(ConnectionState.Connected, slobs.State);
    }

    private async Task<Dictionary<string, object?>> Run(string type, Dictionary<string, string> parameters)
    {
        _capture.Reset();
        var engine = _services.GetRequiredService<ActionEngine>();
        await engine.RunAsync(new ActionDefinition
        {
            Name = "test " + type,
            SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = ObsFeatureTests.CaptureSubAction.Id }],
        });
        return _capture.Args ?? throw new Xunit.Sdk.XunitException(
            $"{type} failed: {engine.History.Entries.FirstOrDefault(e => e.Error is not null)?.Error}");
    }
}
