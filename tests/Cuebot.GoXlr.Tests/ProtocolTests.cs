using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.GoXlr.Model;
using Cuebot.GoXlr.Protocol;
using Cuebot.GoXlr.Tests.Support;
using static Cuebot.GoXlr.Protocol.GoXlrRequests;

namespace Cuebot.GoXlr.Tests;

public sealed class JsonPatchTests
{
    private static JsonElement Ops(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Apply(string doc, string patch) => JsonPatch.Apply(JsonNode.Parse(doc), Ops(patch))!.ToJsonString();

    [Fact]
    public void Add_remove_replace_follow_rfc_6902()
    {
        Assert.Equal("""{"a":{"b":2,"c":3}}""", Apply("""{"a":{"b":1}}""", """[{"op":"replace","path":"/a/b","value":2},{"op":"add","path":"/a/c","value":3}]"""));
        Assert.Equal("""{"a":{}}""", Apply("""{"a":{"b":1}}""", """[{"op":"remove","path":"/a/b"}]"""));
        Assert.Equal("""{"x":[1,9,2,3]}""", Apply("""{"x":[1,2]}""", """[{"op":"add","path":"/x/1","value":9},{"op":"add","path":"/x/-","value":3}]"""));
        Assert.Equal("""{"x":[2]}""", Apply("""{"x":[1,2]}""", """[{"op":"remove","path":"/x/0"}]"""));
        Assert.Equal("""{"new":true}""", Apply("""{"a":1}""", """[{"op":"replace","path":"","value":{"new":true}}]"""));
    }

    [Fact]
    public void Move_copy_test_and_pointer_escapes()
    {
        Assert.Equal("""{"b":{"c":1}}""", Apply("""{"a":{"c":1},"b":{}}""", """[{"op":"move","from":"/a/c","path":"/b/c"},{"op":"remove","path":"/a"}]"""));
        Assert.Equal("""{"a":[1],"b":[1]}""", Apply("""{"a":[1]}""", """[{"op":"copy","from":"/a","path":"/b"}]"""));
        Assert.Equal("""{"a/b":5,"m~n":6}""", Apply("""{"a/b":1,"m~n":2}""", """[{"op":"replace","path":"/a~1b","value":5},{"op":"replace","path":"/m~0n","value":6}]"""));
        Assert.Equal("""{"a":1}""", Apply("""{"a":1}""", """[{"op":"test","path":"/a","value":1}]"""));
        Assert.Throws<JsonPatchException>(() => Apply("""{"a":1}""", """[{"op":"test","path":"/a","value":2}]"""));
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"/missing/x","value":1}]""")]
    [InlineData("""[{"op":"remove","path":"/nope"}]""")]
    [InlineData("""[{"op":"add","path":"/x/5","value":1}]""")]
    [InlineData("""[{"op":"add","path":"/x/01","value":1}]""")]
    [InlineData("""[{"op":"jump","path":"/a"}]""")]
    [InlineData("""[{"path":"/a"}]""")]
    [InlineData("""{"op":"add"}""")]
    public void Bad_patches_throw(string patch) =>
        Assert.Throws<JsonPatchException>(() => Apply("""{"a":1,"x":[0]}""", patch));
}

public sealed class WireFormatTests
{
    [Fact]
    public void Requests_use_serdes_externally_tagged_form()
    {
        Assert.Equal("""{"id":1,"data":"Ping"}""", Envelope(1, Ping));
        Assert.Equal("""{"id":2,"data":"GetStatus"}""", Envelope(2, GetStatus));
        Assert.Equal("""{"id":3,"data":{"Command":["S1",{"SetVolume":["Mic",255]}]}}""", Envelope(3, Command("S1", Cmd("SetVolume", V("Mic"), V(255)))));
        Assert.Equal("""{"SetFXEnabled":true}""", Cmd("SetFXEnabled", V(true)).ToJsonString());
        Assert.Equal("""{"SaveProfile":[]}""", Cmd("SaveProfile").ToJsonString());
        Assert.Equal("""{"SetButtonColours":["Cough","FF0000",null]}""", Cmd("SetButtonColours", V("Cough"), V("FF0000"), null).ToJsonString());
    }

    [Fact]
    public void Names_parse_leniently_and_convert_volume()
    {
        Assert.Equal("LineIn", GoXlrNames.Channel("line in"));
        Assert.Equal("MicMonitor", GoXlrNames.Channel("Mic-Monitor"));
        Assert.Equal("Mic", GoXlrNames.Channel("microphone"));
        Assert.Throws<ArgumentException>(() => GoXlrNames.Channel("Kazoo"));
        Assert.Equal("C", GoXlrNames.Fader("Fader 3"));
        Assert.Equal("Preset4", GoXlrNames.FindPreset("preset 4"));
        Assert.Null(GoXlrNames.FindPreset("Spooky"));
        Assert.Equal("HardTune", GoXlrNames.Effect("Hard Tune"));
        Assert.Equal("Fader 1 Mute", GoXlrNames.Label("Fader1Mute"));
        Assert.Equal("Stream Mix 2", GoXlrNames.Label("StreamMix2"));
        Assert.Equal("Line In", GoXlrNames.Label("LineIn"));
        Assert.Equal("FF8800", GoXlrNames.Colour("#ff8800"));
        Assert.Equal("FF8800", GoXlrNames.Colour("f80"));
        Assert.Throws<ArgumentException>(() => GoXlrNames.Colour("orange"));
        Assert.Equal(100, GoXlrNames.Percent(255));
        Assert.Equal(50, GoXlrNames.Percent(128));
        Assert.Equal(128, GoXlrNames.Raw(50));
        Assert.Equal(255, GoXlrNames.Raw(150));
        Assert.Equal(GoXlrMuteAction.MuteToAll, GoXlrService.ParseMuteAction("Mute to all"));
        Assert.Equal("Unmuted", GoXlrService.NextMuteState(GoXlrMuteAction.Toggle, "MutedToAll"));
        Assert.Equal("MutedToX", GoXlrService.NextMuteState(GoXlrMuteAction.Toggle, "Unmuted"));
    }

    [Fact]
    public void Url_defaults_to_the_utilitys_port_and_path()
    {
        Assert.Equal("ws://localhost:14564/api/websocket", GoXlrService.BuildUrl(new GoXlrSettings()).ToString());
        Assert.Equal("ws://192.168.1.20:1234/api/websocket", GoXlrService.BuildUrl(new GoXlrSettings { Host = " 192.168.1.20 ", Port = 1234 }).ToString());
        Assert.Equal("ws://[::1]:14564/api/websocket", GoXlrService.BuildUrl(new GoXlrSettings { Host = "::1", Port = 0 }).ToString());
    }
}

public sealed class StatusAndDiffTests
{
    private static GoXlrMixer Parse(JsonNode status) => GoXlrStatusParser.Parse(status).Mixers.Single();

    [Fact]
    public void Parses_the_utilitys_status_object()
    {
        var s = GoXlrStatusParser.Parse(FakeGoXlrUtility.Status());
        Assert.Equal("1.2.3", s.DaemonVersion);
        Assert.Equal(["Default", "Gaming", "Just Chatting"], s.Profiles);
        Assert.Equal(["DEFAULT", "Podcast"], s.MicProfiles);
        Assert.Equal(["clip1.wav"], s.Samples);
        var m = s.Mixers.Single();
        Assert.Equal(FakeGoXlrUtility.Serial, m.Serial);
        Assert.False(m.IsMini);
        Assert.Equal("Music", m.Faders["B"].Channel);
        Assert.Equal(191, m.Volume("Music"));
        Assert.Equal("B", m.FaderFor("Music")!.Name);
        Assert.Equal("Default", m.ProfileName);
        Assert.Equal("Spooky", m.Effects!.PresetName("Preset2"));
        Assert.Equal("A", m.Sampler!.ActiveBank);
        Assert.Equal(2, m.Sampler.SampleCounts["A/TopLeft"]);
        Assert.True(m.Routed("Music", "BroadcastMix"));
        Assert.False(m.Routed("Music", "ChatMic"));
        Assert.Equal(24, m.ButtonsDown.Count);
        Assert.Equal("Dynamic", m.MicType);

        var mini = Parse(FakeGoXlrUtility.Status(mini: true));
        Assert.True(mini.IsMini);
        Assert.Null(mini.Effects);
        Assert.Null(mini.Sampler);
        Assert.Same(GoXlrStatus.Empty, GoXlrStatusParser.Parse(null));
    }

    [Fact]
    public void Differences_become_events_with_variables()
    {
        var before = FakeGoXlrUtility.Status();
        var after = before.DeepClone();
        var m = after["mixers"]![FakeGoXlrUtility.Serial]!;
        m["levels"]!["volumes"]!["Music"] = 64;
        m["fader_status"]!["C"]!["mute_state"] = "MutedToAll";
        m["fader_status"]!["D"]!["channel"] = "Game";
        m["cough_button"]!["state"] = "MutedToX";
        m["profile_name"] = "Gaming";
        m["effects"]!["current"]!["robot"]!["is_enabled"] = true;
        m["effects"]!["active_preset"] = "Preset2";
        m["sampler"]!["active_bank"] = "C";
        m["sampler"]!["banks"]!["A"]!["TopLeft"]!["is_playing"] = true;
        m["button_down"]!["Cough"] = true;
        m["router"]!["Music"]!["ChatMic"] = true;

        var events = GoXlrDiff.Compare(Parse(before), Parse(after)).ToDictionary(e => e.Type + ":" + (e.Args.GetValueOrDefault("goXlrChannel") ?? e.Args.GetValueOrDefault("goXlrEffect") ?? ""), e => e.Args);
        var vol = events["goxlr.volumeChanged:Music"];
        Assert.Equal(25, vol["goXlrVolume"]);
        Assert.Equal(64, vol["goXlrVolumeRaw"]);
        Assert.Equal(75, vol["goXlrPreviousVolume"]);
        Assert.Equal("B", vol["goXlrFader"]);
        Assert.Equal(FakeGoXlrUtility.Serial, vol["goXlrSerial"]);
        var mute = events["goxlr.faderMuteChanged:Chat"];
        Assert.Equal(true, mute["goXlrMuted"]);
        Assert.Equal("MutedToAll", mute["goXlrMuteState"]);
        Assert.Equal("C", mute["goXlrFader"]);
        Assert.Equal("System", events["goxlr.faderAssignmentChanged:Game"]["goXlrPreviousChannel"]);
        Assert.Equal(true, events["goxlr.coughMuteChanged:"]["goXlrMuted"]);
        Assert.Equal("Default", events["goxlr.profileChanged:"]["goXlrPreviousProfile"]);
        Assert.Equal("Gaming", events["goxlr.profileChanged:"]["goXlrProfile"]);
        Assert.Equal(true, events["goxlr.effectStateChanged:Robot"]["goXlrEnabled"]);
        var preset = events["goxlr.effectPresetChanged:"];
        Assert.Equal(2, preset["goXlrPresetNumber"]);
        Assert.Equal("Spooky", preset["goXlrPresetName"]);
        Assert.Equal("Default", preset["goXlrPreviousPresetName"]);
        Assert.Equal("A", events["goxlr.samplerBankChanged:"]["goXlrPreviousBank"]);
        Assert.Equal("TopLeft", events["goxlr.samplePlaybackChanged:"]["goXlrSampleButton"]);
        Assert.Equal("Cough", events["goxlr.buttonPressed:"]["goXlrButton"]);
        Assert.Equal("ChatMic", events["goxlr.routingChanged:"]["goXlrOutput"]);
        Assert.Equal(11, events.Count);

        Assert.Empty(GoXlrDiff.Compare(Parse(before), Parse(before)));
        Assert.Equal("goxlr.deviceConnected", Assert.Single(GoXlrDiff.Compare(null, Parse(before))).Type);
        Assert.Equal("goxlr.deviceDisconnected", Assert.Single(GoXlrDiff.Compare(Parse(before), null)).Type);
    }
}
