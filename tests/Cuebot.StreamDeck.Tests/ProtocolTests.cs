using System.Text.Json.Nodes;
using Cuebot.Core.Events;

namespace Cuebot.StreamDeck.Tests;

public class ProtocolTests
{
    [Fact]
    public void Parses_devices_and_buttons()
    {
        var message = StreamDeckProtocol.TryParse(Harness.Hello($"[{Harness.Button("c1", "scene-1", "run", row: 1, column: 2)}]"))!.Value;
        var devices = StreamDeckProtocol.ParseDevices(message);
        Assert.Equal(2, devices.Count);
        Assert.Equal(new StreamDeckDevice("DEV1", "My XL", 2, 8, 4), devices[0]);
        Assert.Equal("Stream Deck XL", devices[0].TypeName);
        Assert.Equal("Stream Deck +", devices[1].TypeName);

        var button = Assert.Single(StreamDeckProtocol.ParseButtons(message));
        Assert.Equal("c1", button.Context);
        Assert.Equal("scene-1", button.ButtonId);
        Assert.Equal(StreamDeckActionKinds.Run, button.Kind);
        Assert.Equal("row 2, column 3", button.Position);
        Assert.False(button.IsDial);
    }

    [Fact]
    public void Buttons_without_context_and_non_objects_are_ignored()
    {
        Assert.Null(StreamDeckProtocol.TryParse("[1,2]"));
        Assert.Null(StreamDeckProtocol.TryParse("not json"));
        var message = StreamDeckProtocol.TryParse("""{"buttons":[{"buttonId":"x"}, 5, {"context":"ok"}]}""")!.Value;
        var b = Assert.Single(StreamDeckProtocol.ParseButtons(message));
        Assert.Equal("ok", b.Context);
        Assert.Equal(StreamDeckActionKinds.Trigger, b.Kind);
        Assert.Equal("Keypad", b.Controller);
    }

    [Fact]
    public void Commands_carry_only_the_fields_they_set()
    {
        var json = JsonNode.Parse(StreamDeckProtocol.Command(new StreamDeckCommand("setTitle", "k1", Title: "Live", State: 1)))!.AsObject();
        Assert.Equal("command", (string?)json["type"]);
        Assert.Equal("setTitle", (string?)json["command"]);
        Assert.Equal("k1", (string?)json["buttonId"]);
        Assert.Equal("Live", (string?)json["title"]);
        Assert.Equal(1, (int?)json["state"]);
        Assert.False(json.ContainsKey("image"));
        Assert.False(json.ContainsKey("value"));
    }

    [Fact]
    public void Input_arguments_include_button_device_and_event_details()
    {
        var button = new StreamDeckButton("c1", "dial-1", "trigger", "Encoder", "DEV2", 0, 3, 0, false);
        var device = new StreamDeckDevice("DEV2", "My Plus", 7, 4, 2);
        var message = StreamDeckProtocol.TryParse("""
            {"type":"input","event":"dialRotate","context":"c1","ticks":-3,"pressed":false,
             "args":{"mode":"volume","buttonId":"spoofed","level":5}}
            """)!.Value;
        var args = StreamDeckProtocol.InputArgs(message, button, device, "inst");
        Assert.Equal("dial-1", args["buttonId"]);          // built-in names win over the key's own args
        Assert.Equal("dialRotate", args["buttonEvent"]);
        Assert.Equal(-3, args["ticks"]);
        Assert.Equal(false, args["pressed"]);
        Assert.Equal(true, args["isDial"]);
        Assert.Equal("My Plus", args["deviceName"]);
        Assert.Equal("Stream Deck +", args["deviceType"]);
        Assert.Equal(3, args["column"]);
        Assert.Equal("volume", args["mode"]);
        Assert.Equal(5L, args["level"]);
        Assert.Equal("inst", args["instanceId"]);
    }

    [Fact]
    public void Touch_taps_report_position_and_hold()
    {
        var message = StreamDeckProtocol.TryParse("""{"event":"touchTap","context":"c","buttonId":"b","tapPos":[120,40],"hold":true}""")!.Value;
        var args = StreamDeckProtocol.InputArgs(message, null, null, "i");
        Assert.Equal("b", args["buttonId"]);
        Assert.Equal(120, args["tapX"]);
        Assert.Equal(40, args["tapY"]);
        Assert.Equal(true, args["hold"]);
    }

    [Fact]
    public void Images_resolve_files_to_data_uris_and_pass_urls_through()
    {
        Assert.Equal("", StreamDeckProtocol.ResolveImage("  "));
        Assert.Equal("https://example.com/a.png", StreamDeckProtocol.ResolveImage("https://example.com/a.png"));
        Assert.Equal("data:image/png;base64,AAAA", StreamDeckProtocol.ResolveImage("data:image/png;base64,AAAA"));

        var path = Path.Combine(Path.GetTempPath(), $"cuebot-sd-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Assert.Equal("data:image/png;base64,AQID", StreamDeckProtocol.ResolveImage($"\"{path}\""));
            Assert.Throws<ArgumentException>(() => StreamDeckProtocol.ResolveImage(path, maxBytes: 2));
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Throws<ArgumentException>(() => StreamDeckProtocol.ResolveImage(Path.Combine(Path.GetTempPath(), "missing-cuebot.png")));
    }

    [Theory]
    [InlineData("", "x", "Key down", "keyDown", true)]
    [InlineData("", "x", "Key down", "keyUp", false)]
    [InlineData("a, B", "b", "Any", "keyUp", true)]
    [InlineData("a,b", "c", "Any", "keyDown", false)]
    [InlineData("", "x", "Dial rotate", "dialRotate", true)]
    [InlineData("", "x", "Any key or dial press", "dialDown", true)]
    [InlineData("", "x", "Any key or dial press", "dialRotate", false)]
    [InlineData("", "x", "touchTap", "touchTap", true)]
    public void Action_trigger_filters_by_button_and_event(string ids, string buttonId, string choice, string ev, bool expected)
    {
        var e = new BotEvent(StreamDeckHub.ActionEvent, new Dictionary<string, object?> { ["buttonId"] = buttonId, ["buttonEvent"] = ev });
        Assert.Equal(expected, StreamDeckTriggers.MatchesAction(e, ids, choice));
    }

    [Fact]
    public void State_and_indicator_parameters_validate()
    {
        Assert.Null(StreamDeckSubAction.OptionalState("Current"));
        Assert.Null(StreamDeckSubAction.OptionalState(""));
        Assert.Equal(1, StreamDeckSubAction.OptionalState("1"));
        Assert.Throws<ArgumentException>(() => StreamDeckSubAction.OptionalState("2"));
        Assert.Equal(100, StreamDeckSubAction.OptionalIndicator("150"));
        Assert.Null(StreamDeckSubAction.OptionalIndicator(" "));
        Assert.Throws<ArgumentException>(() => StreamDeckSubAction.OptionalIndicator("lots"));
    }
}
