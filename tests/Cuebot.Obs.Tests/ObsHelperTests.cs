using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Streamlabs;
using Cuebot.Obs.SubActions;

namespace Cuebot.Obs.Tests;

public class ObsHelperTests
{
    [Theory]
    [InlineData("#112233", 0xFF332211L)]
    [InlineData("112233", 0xFF332211L)]
    [InlineData("#80FF0000", 0x800000FFL)]
    [InlineData("4278255360", 4278255360L)]
    public void Colors_convert_to_obs_abgr(string color, long expected) => Assert.Equal(expected, ObsTransforms.ParseColor(color));

    [Fact]
    public void Bad_colors_are_rejected() => Assert.Throws<ArgumentException>(() => ObsTransforms.ParseColor("#12345"));

    [Theory]
    [InlineData(5, 1.0, 100.0, 300.0)]     // top-left: shift right by the width
    [InlineData(5, -1.0, 300.0, 100.0)]    // unflip moves it back
    [InlineData(6, 1.0, 300.0, 100.0)]     // top-right: shift left
    [InlineData(0, 1.0, 100.0, 100.0)]     // centered: no shift
    public void Flip_keeps_the_item_in_place(int alignment, double scaleX, double positionX, double expectedX)
    {
        var transform = Json($$"""{"alignment":{{alignment}},"scaleX":{{scaleX}},"positionX":{{positionX}},"sourceWidth":200}""");

        var patch = ObsTransforms.Flip(transform, FlipAxis.Horizontal);

        Assert.Equal(-scaleX, patch["scaleX"]);
        Assert.Equal(expectedX, patch["positionX"]);
    }

    [Fact]
    public void Volume_and_image_format_helpers()
    {
        Assert.Equal(0, ObsTransforms.MulToDb(1), 6);
        Assert.Equal(-100, ObsTransforms.MulToDb(0));
        Assert.Equal(26, ObsTransforms.ClampDb(40));
        Assert.Equal("jpg", ObsTransforms.ImageFormatFor("a.JPEG"));
        Assert.Equal("png", ObsTransforms.ImageFormatFor("noext"));
    }

    [Fact]
    public void Maps_events_with_their_arguments()
    {
        var media = ObsEventMapper.Map(new ObsEvent("MediaInputActionTriggered",
            Json("""{"inputName":"Music","mediaAction":"OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE"}""")))!;
        Assert.Equal(ObsEvents.MediaAction, media.Type);
        Assert.Equal("pause", media.Args["mediaAction"]);

        var vendor = ObsEventMapper.Map(new ObsEvent("VendorEvent", Json("""{"vendorName":"v","eventType":"t","eventData":{"x":1}}""")))!;
        Assert.Equal("t", vendor.Args["vendorEventType"]);
        Assert.Equal("""{"x":1}""", vendor.Args["eventData"]);

        var saved = ObsEventMapper.Map(new ObsEvent("ReplayBufferSaved", Json("""{"savedReplayPath":"/r.mkv"}""")))!;
        Assert.Equal("/r.mkv", saved.Args["savedReplayPath"]);

        Assert.Null(ObsEventMapper.Map(new ObsEvent("InputActiveStateChanged", Json("{}"))));
        Assert.Equal("{}", ObsEventMapper.Generic(new ObsEvent("ExitStarted", default)).Args["eventData"]);
        Assert.Equal("X", ObsEventMapper.WithConnection(saved, "X").Args["obsConnection"]);
    }

    [Theory]
    [InlineData("", "", true, true)]
    [InlineData("Unmuted", "", true, false)]
    [InlineData("Muted", "", true, true)]
    [InlineData("Muted", "", false, false)]
    [InlineData("Unmuted", "", false, true)]
    [InlineData("Any", "mic", true, true)]
    [InlineData("Any", "cam", true, false)]
    public void Triggers_filter_by_state_and_text(string state, string input, bool muted, bool expected)
    {
        var e = new BotEvent(ObsEvents.InputMuteChanged, new Dictionary<string, object?> { ["inputName"] = "Mic", ["inputMuted"] = muted, ["obsConnection"] = "OBS" });
        var settings = new ParameterValues(new Dictionary<string, string> { ["state"] = state, ["input"] = input, ["connection"] = "obs" });

        var matches = ObsTriggers.MatchesAll(e, settings, [("connection", "obsConnection"), ("input", "inputName")]) &&
                      ObsTriggers.MatchesState("inputMuted", "Muted", "Unmuted")(e, settings);

        Assert.Equal(expected, matches);
    }

    [Fact]
    public void Parses_scene_items_and_filters()
    {
        var items = ObsResponseParser.ParseSceneItems(Json(
            """{"sceneItems":[{"sceneItemId":1,"sourceName":"Bottom","sceneItemIndex":0,"sceneItemEnabled":false},{"sceneItemId":2,"sourceName":"Top","sceneItemIndex":1,"isGroup":true},{"sourceName":"broken"}]}"""));
        Assert.Equal(["Top", "Bottom"], items.Select(i => i.SourceName));
        Assert.True(items[0].IsGroup);
        Assert.False(items[1].Enabled);

        var filters = ObsResponseParser.ParseFilters(Json("""{"filters":[{"filterName":"B","filterIndex":1},{"filterName":"A","filterIndex":0,"filterEnabled":false}]}"""));
        Assert.Equal(["A", "B"], filters.Select(f => f.Name));
        Assert.Equal(["P1", "P2"], ObsResponseParser.ParseStrings(Json("""{"profiles":["P1",3,"P2"]}"""), "profiles"));
    }

    [Fact]
    public void Batch_requests_must_be_a_non_empty_array_of_typed_requests()
    {
        Assert.Equal(2, RawRequestSubAction.ParseBatch("""[{"requestType":"A"},{"requestType":"B","requestData":{}}]""").Count);
        Assert.Throws<ArgumentException>(() => RawRequestSubAction.ParseBatch("[]"));
        Assert.Throws<ArgumentException>(() => RawRequestSubAction.ParseBatch("""[{"x":1}]"""));
        Assert.Throws<ArgumentException>(() => RawRequestSubAction.ParseBatch("""{"requestType":"A"}"""));
    }

    [Fact]
    public void SockJs_frames_decode()
    {
        Assert.Equal('o', SockJsFraming.Decode("o").Type);
        Assert.Equal('h', SockJsFraming.Decode("h").Type);
        Assert.Equal(["{\"a\":1}", "x"], SockJsFraming.Decode("a[\"{\\\"a\\\":1}\",\"x\"]").Messages);
        Assert.Equal(["solo"], SockJsFraming.Decode("m\"solo\"").Messages);
        Assert.Contains("3000: Go away!", SockJsFraming.Decode("c[3000,\"Go away!\"]").CloseReason);
        Assert.Equal(["{\"id\":1}"], SockJsFraming.Decode("{\"id\":1}").Messages);
        Assert.Equal(["{\"id\":1}"], JsonSerializer.Deserialize<string[]>(SockJsFraming.Encode("{\"id\":1}"))!);
        Assert.Matches(@"^ws://h:1/api/\d{3}/[0-9a-f]{12}/websocket$", SockJsFraming.SessionUri(new Uri("ws://h:1/api")).ToString());
    }

    [Fact]
    public void Streamlabs_models_parse_leniently()
    {
        var scenes = StreamlabsParser.ParseScenes(Json(
            """[{"id":"s1","name":"Main","resourceId":"Scene[\"s1\"]","nodes":[{"sceneItemId":"i1","sourceId":"src1","name":"Cam","visible":false,"resourceId":"SceneItem[\"s1\",\"i1\",\"src1\"]"},{"id":"f1","name":"Folder","sceneNodeType":"folder"}]},{"name":"no id"}]"""));
        Assert.Single(scenes);
        Assert.Equal("Cam", scenes[0].Items[0].Name);
        Assert.False(scenes[0].Items[0].Visible);
        Assert.True(scenes[0].Items[1].IsFolder);

        var sources = StreamlabsParser.ParseSources(Json("""[{"sourceId":"a","name":"Mic","muted":true},{"id":"b"},{}]"""));
        Assert.Equal(["Mic", "b"], sources.Select(s => s.Name));
        Assert.Equal("Source[\"b\"]", sources[1].ResourceId);

        Assert.True(StreamlabsParser.StreamingState("live"));
        Assert.Null(StreamlabsParser.StreamingState("starting"));
        Assert.False(StreamlabsParser.RecordingState("offline"));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
