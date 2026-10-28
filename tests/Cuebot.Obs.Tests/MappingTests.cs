using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.SubActions;

namespace Cuebot.Obs.Tests;

public class MappingTests
{
    [Theory]
    [InlineData("StreamStateChanged", "OBS_WEBSOCKET_OUTPUT_STARTED", ObsEvents.StreamStarted)]
    [InlineData("StreamStateChanged", "OBS_WEBSOCKET_OUTPUT_STOPPED", ObsEvents.StreamStopped)]
    [InlineData("RecordStateChanged", "OBS_WEBSOCKET_OUTPUT_STARTED", ObsEvents.RecordStarted)]
    [InlineData("RecordStateChanged", "OBS_WEBSOCKET_OUTPUT_STOPPED", ObsEvents.RecordStopped)]
    [InlineData("StreamStateChanged", "OBS_WEBSOCKET_OUTPUT_STOPPING", null)]
    [InlineData("ReplayBufferStateChanged", "OBS_WEBSOCKET_OUTPUT_STARTED", ObsEvents.ReplayBufferStarted)]
    [InlineData("RecordStateChanged", "OBS_WEBSOCKET_OUTPUT_PAUSED", ObsEvents.RecordPaused)]
    [InlineData("VirtualcamStateChanged", "OBS_WEBSOCKET_OUTPUT_STOPPED", ObsEvents.VirtualCamStopped)]
    public void Maps_output_states(string eventType, string outputState, string? expected)
    {
        var e = new ObsEvent(eventType, Json($$"""{"outputActive":true,"outputState":"{{outputState}}"}"""));

        Assert.Equal(expected, ObsEventMapper.MapOutputEvent(e)?.Type);
    }

    /// <summary>Streamer.bot 1.0.7's OBS names (from its own Test Trigger runs and the trigger docs).</summary>
    [Fact]
    public void Events_carry_streamerbots_obs_names()
    {
        var source = new ObsEventMapper.ObsSource("Main OBS", "10.0.0.2", "31.0.0", "5.5.0");

        var connected = ObsEventMapper.WithConnection(new BotEvent(ObsEvents.Connected), source);
        Assert.Equal(ObsEventMapper.ConnectionId("Main OBS"), Assert.IsType<Guid>(connected.Args["obs.id"]));
        Assert.Equal(("Main OBS", "10.0.0.2", "31.0.0", "5.5.0", "Main OBS"), (connected.Args["obs.name"], connected.Args["obs.host"],
            connected.Args["obs.studioVersion"], connected.Args["obs.websocketVersion"], connected.Args[ObsEventMapper.ConnectionArg]));

        var scene = ObsEventMapper.WithConnection(ObsEventMapper.SceneChanged("Game", "Intro"), source);
        Assert.Equal(("Game", "Intro"), (scene.Args["obs.sceneName"], scene.Args["obs.oldSceneName"]));

        var vendor = ObsEventMapper.WithConnection(ObsEventMapper.Map(new ObsEvent("VendorEvent",
            Json("""{"vendorName":"streamerbot","eventType":"test_event","eventData":{}}""")))!, source);
        Assert.Equal(("streamerbot", "test_event"), (vendor.Args["obsVendorEvent.vendor"], vendor.Args["obsVendorEvent.event"]));

        var generic = ObsEventMapper.WithConnection(ObsEventMapper.Generic(new ObsEvent("InputMuteStateChanged",
            Json("""{"inputName":"Mic","inputMuted":true,"inputUuid":null}"""))), source);
        Assert.Equal(("InputMuteStateChanged", "Mic", "true", ""), (generic.Args["obsEvent.event"], generic.Args["obsEvent.inputName"],
            generic.Args["obsEvent.inputMuted"], generic.Args["obsEvent.inputUuid"]));
    }

    [Fact]
    public void Record_events_carry_the_output_path_only_when_present()
    {
        var withPath = ObsEventMapper.MapOutputEvent(new ObsEvent("RecordStateChanged",
            Json("""{"outputState":"OBS_WEBSOCKET_OUTPUT_STOPPED","outputPath":"C:\\rec.mkv"}""")));
        var withoutPath = ObsEventMapper.MapOutputEvent(new ObsEvent("RecordStateChanged",
            Json("""{"outputState":"OBS_WEBSOCKET_OUTPUT_STOPPED","outputPath":null}""")));

        Assert.Equal(@"C:\rec.mkv", withPath!.Args["outputPath"]);
        Assert.False(withoutPath!.Args.ContainsKey("outputPath"));
    }

    [Fact]
    public void Parses_scene_and_input_names_leniently()
    {
        var scenes = ObsResponseParser.ParseScenes(Json(
            """{"scenes":[{"sceneName":"A","sceneIndex":0},{"sceneName":"C","sceneIndex":2},{"sceneIndex":3},{"sceneName":"B","sceneIndex":1}]}"""));
        var inputs = ObsResponseParser.ParseInputs(Json("""{"inputs":[{"inputName":"Mic","inputKind":"wasapi_input_capture"},{}]}"""));

        Assert.Equal(["C", "B", "A"], scenes);
        Assert.Equal(["Mic"], inputs);
        Assert.Empty(ObsResponseParser.ParseScenes(default));
    }

    [Theory]
    [InlineData("", "Gaming", true)]
    [InlineData("gaming", "Gaming", true)]
    [InlineData("Starting", "Gaming", false)]
    public void Scene_trigger_filters_by_scene_name(string filter, string sceneName, bool expected)
    {
        var e = new BotEvent(ObsEvents.SceneChanged, new Dictionary<string, object?> { ["sceneName"] = sceneName });
        var settings = new ParameterValues(new Dictionary<string, string> { ["scene"] = filter });

        Assert.Equal(expected, ObsTriggers.MatchesSceneFilter(e, settings, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Raw_request_data_is_optional_but_must_be_a_json_object()
    {
        Assert.Null(RawRequestSubAction.ParseRequestData("  "));
        Assert.Equal("Main", RawRequestSubAction.ParseRequestData("""{"sceneName":"Main"}""")!.Value.GetProperty("sceneName").GetString());
        Assert.Contains("not valid JSON", Assert.Throws<ArgumentException>(() => RawRequestSubAction.ParseRequestData("{oops")).Message);
        Assert.Contains("JSON object", Assert.Throws<ArgumentException>(() => RawRequestSubAction.ParseRequestData("[1]")).Message);
    }

    [Fact]
    public void Choice_parsing_is_case_insensitive_and_lists_valid_options()
    {
        Assert.Equal(VisibilityMode.Hidden, ObsSubActionSupport.ParseChoice<VisibilityMode>("hidden", "State"));
        var error = Assert.Throws<ArgumentException>(() => ObsSubActionSupport.ParseChoice<MuteMode>("Loud", "State"));
        Assert.Contains("Mute, Unmute, Toggle", error.Message);
        Assert.Throws<ArgumentException>(() => ObsSubActionSupport.ParseChoice<MuteMode>("5", "State"));
    }

    [Fact]
    public void Close_code_4009_is_described_as_an_authentication_failure()
    {
        Assert.Contains("password", ObsCloseCodes.Describe((WebSocketCloseStatus)4009, "Authentication failed."));
        Assert.Contains("code 4012", ObsCloseCodes.Describe((WebSocketCloseStatus)4012, "Unsupported"));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
