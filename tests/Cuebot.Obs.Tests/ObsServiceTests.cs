using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Tests.Fakes;

namespace Cuebot.Obs.Tests;

public sealed class ObsServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-obs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly BusRecorder _recorder;
    private readonly FakeObsServer _server = FakeObsServer.Start(password: "pw");
    private readonly ObsService _obs;
    private string[] _sceneNames = ["Starting", "Gaming"];
    private bool _webcamEnabled = true;

    public ObsServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _bus = new EventBus(_log);
        _recorder = new BusRecorder(_bus);
        var settings = new JsonFile<ObsSettings>(Path.Combine(_directory, "obs.json"), _log);
        settings.Value.Host = "localhost";
        settings.Value.Port = _server.Port;
        settings.Value.Password = "pw";
        _obs = new ObsService(settings, _bus, _log, reconnectDelay: TimeSpan.FromMilliseconds(100));
        _server.Handler = Answer;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _obs.DisposeAsync();
        await _server.DisposeAsync();
        _recorder.Dispose();
        _log.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Connect_loads_scenes_and_inputs_and_publishes_connected()
    {
        await _obs.ConnectAsync();

        await _recorder.WaitForAsync(ObsEvents.Connected);
        Assert.Equal(ConnectionState.Connected, _obs.State);
        Assert.Equal(["Gaming", "Starting"], _obs.Scenes); // OBS lists the highest index first
        Assert.Equal(["Mic", "Webcam"], _obs.Inputs);
        Assert.Equal("Starting", _obs.CurrentScene);
    }

    [Fact]
    public async Task Wrong_password_sets_an_error_status()
    {
        _obs.Settings.Password = "nope";

        await Assert.ThrowsAsync<ObsAuthenticationException>(() => _obs.ConnectAsync());

        Assert.Equal(ConnectionState.Error, _obs.State);
        Assert.Contains("password", _obs.StatusMessage);
    }

    [Fact]
    public async Task Requests_fail_clearly_when_not_connected()
    {
        var error = await Assert.ThrowsAsync<ObsConnectionException>(() => _obs.SetCurrentSceneAsync("Gaming"));

        Assert.Equal("OBS is not connected.", error.Message);
    }

    [Fact]
    public async Task Scene_change_publishes_scene_and_previous_scene()
    {
        await _obs.ConnectAsync();

        await _server.SendEventAsync("CurrentProgramSceneChanged", new { sceneName = "Gaming" });

        var e = await _recorder.WaitForAsync(ObsEvents.SceneChanged);
        Assert.Equal("Gaming", e.Args["sceneName"]);
        Assert.Equal("Starting", e.Args["previousScene"]);
        Assert.Equal("Gaming", _obs.CurrentScene);
    }

    [Fact]
    public async Task Output_state_changes_publish_stream_and_record_events()
    {
        await _obs.ConnectAsync();

        await _server.SendEventAsync("StreamStateChanged", new { outputActive = false, outputState = "OBS_WEBSOCKET_OUTPUT_STARTING" });
        await _server.SendEventAsync("StreamStateChanged", new { outputActive = true, outputState = "OBS_WEBSOCKET_OUTPUT_STARTED" });
        await _server.SendEventAsync("RecordStateChanged",
            new { outputActive = false, outputState = "OBS_WEBSOCKET_OUTPUT_STOPPED", outputPath = "/videos/rec.mkv" });

        await _recorder.WaitForAsync(ObsEvents.StreamStarted);
        var stopped = await _recorder.WaitForAsync(ObsEvents.RecordStopped);
        Assert.Equal("/videos/rec.mkv", stopped.Args["outputPath"]);
        Assert.Single(_recorder.Events, e => e.Type == ObsEvents.StreamStarted);
    }

    [Fact]
    public async Task Scene_created_refreshes_the_scene_list()
    {
        await _obs.ConnectAsync();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _obs.ScenesChanged += () => { if (_obs.Scenes.Contains("BRB")) refreshed.TrySetResult(); };
        _sceneNames = ["Starting", "Gaming", "BRB"];

        await _server.SendEventAsync("SceneCreated", new { sceneName = "BRB", isGroup = false });

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["BRB", "Gaming", "Starting"], _obs.Scenes);
    }

    [Theory]
    [InlineData(VisibilityMode.Toggle, true, false)]
    [InlineData(VisibilityMode.Toggle, false, true)]
    [InlineData(VisibilityMode.Visible, false, true)]
    [InlineData(VisibilityMode.Hidden, true, false)]
    public async Task Source_visibility_resolves_the_target_state(VisibilityMode mode, bool before, bool expected)
    {
        _webcamEnabled = before;
        await _obs.ConnectAsync();

        var visible = await _obs.SetSourceVisibilityAsync("Gaming", "Webcam", mode);

        Assert.Equal(expected, visible);
        Assert.Equal(expected, _webcamEnabled);
        Assert.Equal(mode == VisibilityMode.Toggle, _server.Requests.Any(r => r.Type == "GetSceneItemEnabled"));
    }

    [Fact]
    public async Task Output_and_mute_commands_send_the_matching_requests()
    {
        await _obs.ConnectAsync();

        await _obs.ControlStreamAsync(OutputCommand.Toggle);
        await _obs.ControlRecordAsync(OutputCommand.Stop);
        var muted = await _obs.SetInputMuteAsync("Mic", MuteMode.Mute);
        await _obs.SetInputTextAsync("Title", "Hello");

        Assert.True(muted);
        var requests = _server.Requests.Select(r => r.Type).ToArray();
        Assert.Contains("ToggleStream", requests);
        Assert.Contains("StopRecord", requests);
        var text = _server.Requests.Single(r => r.Type == "SetInputSettings").Data;
        Assert.Equal("Hello", text.GetProperty("inputSettings").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Manual_disconnect_publishes_disconnected()
    {
        await _obs.ConnectAsync();

        await _obs.DisconnectAsync();

        await _recorder.WaitForAsync(ObsEvents.Disconnected);
        Assert.Equal(ConnectionState.Disconnected, _obs.State);
        Assert.Null(_obs.CurrentScene);
        Assert.NotEmpty(_obs.Scenes);
    }

    [Fact]
    public async Task Auto_connect_reconnects_after_obs_drops_the_connection()
    {
        _obs.Settings.AutoConnect = true;
        await _obs.StartAsync();
        await _recorder.WaitForAsync(ObsEvents.Connected);

        await _server.CloseSessionAsync();

        await _recorder.WaitForAsync(ObsEvents.Disconnected);
        await _recorder.WaitForAsync(ObsEvents.Connected, occurrence: 2);
        Assert.Equal(ConnectionState.Connected, _obs.State);
    }

    private FakeReply? Answer(string requestType, JsonElement data) => requestType switch
    {
        "GetSceneList" => FakeReply.Ok(new
        {
            currentProgramSceneName = "Starting",
            scenes = _sceneNames.Select((name, index) => new { sceneName = name, sceneIndex = index }).ToArray(),
        }),
        "GetInputList" => FakeReply.Ok(new { inputs = new[] { new { inputName = "Mic" }, new { inputName = "Webcam" } } }),
        "GetSceneItemId" => data.GetProperty("sourceName").GetString() == "Webcam"
            ? FakeReply.Ok(new { sceneItemId = 7 })
            : FakeReply.Fail(600, "No scene items were found."),
        "GetSceneItemEnabled" => FakeReply.Ok(new { sceneItemEnabled = _webcamEnabled }),
        "SetSceneItemEnabled" => SetWebcam(data.GetProperty("sceneItemEnabled").GetBoolean()),
        _ => FakeReply.Ok(),
    };

    private FakeReply SetWebcam(bool enabled)
    {
        _webcamEnabled = enabled;
        return FakeReply.Ok();
    }
}
