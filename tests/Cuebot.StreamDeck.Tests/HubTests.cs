using System.Text.Json.Nodes;
using Cuebot.Core;

namespace Cuebot.StreamDeck.Tests;

public class HubTests
{
    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task Hello_publishes_connected_and_info_and_lists_the_instance()
    {
        await using var h = new Harness();
        Assert.Equal(ConnectionState.Disconnected, h.Hub.State);
        Assert.Contains("Waiting", h.Hub.StatusMessage);

        var peer = await h.ConnectAsync(buttons: $"[{Harness.Button("c1", "k1")},{Harness.Button("c2", "k2", "run", device: "DEV2")}]");

        var welcome = Assert.Single(peer.OfType("welcome"));
        Assert.Equal(1, (int?)welcome["protocol"]);
        Assert.Equal("peer-1", (string?)welcome["session"]);

        var connected = Assert.Single(h.EventsOf(StreamDeckHub.ConnectedEvent));
        Assert.Equal("0.1.0", connected.Args["pluginVersion"]);
        Assert.Equal("7.1.0", connected.Args["streamDeckVersion"]);
        var info = Assert.Single(h.EventsOf(StreamDeckHub.InfoEvent));
        Assert.Equal(2, info.Args["deviceCount"]);
        Assert.Equal("My XL, My Plus", info.Args["deviceNames"]);
        Assert.Equal("Stream Deck +", info.Args["device1.type"]);
        // Streamer.bot's Stream Deck Info names (data.*, from its own Test Trigger run) with its types.
        Assert.Equal(("7.1.0", "windows", "en", ""), (info.Args["data.application.version"], info.Args["data.application.platform"],
            info.Args["data.application.language"], info.Args["data.application.font"]));
        Assert.Equal(("DEV2", "My Plus", 4L, 2L, 7L), (info.Args["data.devices[1].id"], info.Args["data.devices[1].name"],
            info.Args["data.devices[1].size.columns"], info.Args["data.devices[1].size.rows"], info.Args["data.devices[1].type"]));
        Assert.Equal(("io.github.cuebot.streamdeck", "0.1.0", 1L), (info.Args["data.plugin.uuid"], info.Args["data.plugin.version"], info.Args["data.devicePixelRatio"]));
        Assert.Equal("", info.Args["data.colors.highlightColor"]);

        Assert.Equal(ConnectionState.Connected, h.Hub.State);
        Assert.Equal("1 Stream Deck app, 2 devices, 2 visible keys", h.Hub.StatusMessage);
        var instance = Assert.Single(h.Hub.Instances);
        Assert.Equal(2, instance.Buttons.Count);
        Assert.Equal(["k1", "k2"], h.Hub.KnownButtonIds);
    }

    [Fact]
    public async Task Status_says_when_the_api_is_off()
    {
        await using var h = new Harness { ApiEnabled = false };
        Assert.Contains("REST API is off", h.Hub.StatusMessage);
    }

    [Fact]
    public async Task Connections_that_never_say_hello_are_not_listed_and_close_silently()
    {
        await using var h = new Harness();
        var peer = new FakePeer("p");
        await h.Hub.OpenAsync(peer);
        Assert.Empty(h.Hub.Instances);
        h.Hub.Close("p");
        Assert.Empty(h.EventsOf(StreamDeckHub.DisconnectedEvent));
    }

    [Fact]
    public async Task Appear_disappear_and_device_changes_update_the_instance()
    {
        await using var h = new Harness();
        await h.ConnectAsync();
        await h.Hub.HandleAsync("peer-1", $$"""{"type":"appear","button":{{Harness.Button("c9", "new-key", row: 2, column: 1)}}}""");
        Assert.Equal("new-key", Assert.Single(h.Hub.Instances[0].Buttons).ButtonId);
        await h.Hub.HandleAsync("peer-1", """{"type":"disappear","context":"c9"}""");
        Assert.Empty(h.Hub.Instances[0].Buttons);

        await h.Hub.HandleAsync("peer-1", """{"type":"devices","devices":[{"id":"DEV3","name":"Neo","type":9,"size":{"columns":4,"rows":2}}]}""");
        Assert.Equal("Neo", Assert.Single(h.Hub.Instances[0].Devices).Name);
        Assert.Equal(2, h.EventsOf(StreamDeckHub.InfoEvent).Length);
    }

    [Fact]
    public async Task Input_fires_the_stream_deck_action_event()
    {
        await using var h = new Harness();
        await h.ConnectAsync(buttons: $"[{Harness.Button("c1", "go-live", row: 1, column: 4)}]");
        await h.Hub.HandleAsync("peer-1", """{"type":"input","event":"keyDown","context":"c1","state":1,"args":{"scene":"Main"}}""");

        var e = Assert.Single(h.EventsOf(StreamDeckHub.ActionEvent));
        Assert.Equal("go-live", e.Args["buttonId"]);
        Assert.Equal("keyDown", e.Args["buttonEvent"]);
        Assert.Equal("My XL", e.Args["deviceName"]);
        Assert.Equal(1, e.Args["row"]);
        Assert.Equal(4, e.Args["column"]);
        Assert.Equal(1, e.Args["state"]);
        Assert.Equal("Main", e.Args["scene"]);
        Assert.Equal(1, h.Hub.Instances[0].Buttons[0].State);
    }

    [Fact]
    public async Task Malformed_messages_are_ignored()
    {
        await using var h = new Harness();
        var peer = await h.ConnectAsync();
        await h.Hub.HandleAsync("peer-1", "{oops");
        await h.Hub.HandleAsync("peer-1", "[]");
        await h.Hub.HandleAsync("peer-1", """{"type":"appear"}""");
        await h.Hub.HandleAsync("peer-1", """{"type":"whatever"}""");
        await h.Hub.HandleAsync("unknown-peer", """{"type":"ping"}""");
        await h.Hub.HandleAsync("peer-1", """{"type":"ping"}""");
        Assert.Single(peer.OfType("pong"));
    }

    [Fact]
    public async Task Close_publishes_disconnected()
    {
        await using var h = new Harness();
        await h.ConnectAsync();
        h.Hub.Close("peer-1");
        var e = Assert.Single(h.EventsOf(StreamDeckHub.DisconnectedEvent));
        Assert.Equal("peer-1", e.Args["instanceId"]);
        Assert.Equal(ConnectionState.Disconnected, h.Hub.State);
    }

    [Fact]
    public async Task Commands_go_to_every_instance_and_count_matching_keys()
    {
        await using var h = new Harness();
        var a = await h.ConnectAsync("a", $"[{Harness.Button("c1", "Live")},{Harness.Button("c2", "live")}]");
        var b = await h.ConnectAsync("b", $"[{Harness.Button("c3", "other")}]");

        var count = await h.Hub.SendCommandAsync(new StreamDeckCommand("setTitle", " live ", Title: "ON AIR"));
        Assert.Equal(2, count);
        var sent = Assert.Single(a.OfType("command"));
        Assert.Equal("live", (string?)sent["buttonId"]);
        Assert.Equal("ON AIR", (string?)sent["title"]);
        Assert.Single(b.OfType("command"));    // sent anyway: keys on hidden pages pick it up when they appear

        Assert.Equal(0, await h.Hub.SendCommandAsync(new StreamDeckCommand("showOk", "nobody")));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Hub.SendCommandAsync(new StreamDeckCommand("setTitle", " ")));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Hub.SendCommandAsync(new StreamDeckCommand("explode", "live")));
    }

    [Fact]
    public async Task State_commands_are_mirrored_in_the_key_list()
    {
        await using var h = new Harness();
        await h.ConnectAsync(buttons: $"[{Harness.Button("c1", "mic")}]");
        await h.Hub.SendCommandAsync(new StreamDeckCommand("toggleState", "mic"));
        Assert.Equal(1, h.Hub.Instances[0].Buttons[0].State);
        await h.Hub.SendCommandAsync(new StreamDeckCommand("setState", "mic", State: 0));
        Assert.Equal(0, h.Hub.Instances[0].Buttons[0].State);
    }

    [Fact]
    public async Task Sending_without_a_plugin_returns_zero()
    {
        await using var h = new Harness();
        Assert.Equal(0, await h.Hub.SendCommandAsync(new StreamDeckCommand("showAlert", "k")));
    }

    private static async Task<JsonObject> RequestAsync(Harness h, FakePeer peer, string method, string parameters = "{}")
    {
        var id = peer.Sent.Count + 100;
        await h.Hub.HandleAsync(peer.Id, $$"""{"type":"request","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
        return peer.OfType("response").Single(r => (int?)r["id"] == id);
    }

    [Fact]
    public async Task Requests_list_run_and_toggle_actions()
    {
        await using var h = new Harness();
        var action = await h.AddActionAsync("Scene: Gaming");
        await h.AddActionAsync("Disabled one", enabled: false);
        var peer = await h.ConnectAsync();

        var list = await RequestAsync(h, peer, "getActions");
        Assert.True((bool?)list["ok"]);
        var actions = list["result"]!["actions"]!.AsArray();
        Assert.Equal(2, actions.Count);
        Assert.Equal("Scene: Gaming", (string?)actions[0]!["name"]);

        var run = await RequestAsync(h, peer, "runAction", $$$"""{"action":"{{{action.Id}}}","args":{"x":1}}""");
        Assert.True((bool?)run["ok"], run.ToJsonString());
        Assert.True((bool?)run["result"]!["queued"]);

        var byName = await RequestAsync(h, peer, "runAction", """{"action":"scene: gaming"}""");
        Assert.True((bool?)byName["ok"]);

        var disabled = await RequestAsync(h, peer, "runAction", """{"action":"Disabled one"}""");
        Assert.False((bool?)disabled["ok"]);
        Assert.Contains("disabled", (string?)disabled["error"]);

        var missing = await RequestAsync(h, peer, "runAction", """{"action":"nope"}""");
        Assert.False((bool?)missing["ok"]);

        var toggled = await RequestAsync(h, peer, "setActionEnabled", $$"""{"action":"{{action.Id}}","enabled":"toggle"}""");
        Assert.False((bool?)toggled["result"]!["enabled"]);
        Assert.False(h.Profile.Snapshot.FindAction(action.Id)!.Enabled);
        var on = await RequestAsync(h, peer, "setActionEnabled", $$"""{"action":"{{action.Id}}","enabled":true}""");
        Assert.True((bool?)on["result"]!["enabled"]);

        var unknown = await RequestAsync(h, peer, "launchRockets");
        Assert.False((bool?)unknown["ok"]);
    }

    [Fact]
    public async Task Profile_changes_push_the_action_list()
    {
        await using var h = new Harness();
        var peer = await h.ConnectAsync();
        await h.AddActionAsync("Fresh");
        await Eventually(() => peer.OfType("actionsChanged").Length > 0, "actionsChanged was sent");
        var last = peer.OfType("actionsChanged").Last();
        Assert.Equal("Fresh", (string?)last["actions"]![0]!["name"]);
    }

    [Fact]
    public async Task Requests_set_globals()
    {
        await using var h = new Harness();
        var peer = await h.ConnectAsync();

        var set = await RequestAsync(h, peer, "setGlobal", """{"name":"count","value":"41","persisted":false}""");
        Assert.True((bool?)set["ok"], set.ToJsonString());
        Assert.Equal(41L, h.Globals.GetValue("count"));

        await RequestAsync(h, peer, "setGlobal", """{"name":"count","value":1,"op":"add","persisted":false}""");
        Assert.Equal("42", h.Globals.Get("count"));
        await RequestAsync(h, peer, "setGlobal", """{"name":"count","value":"-2.5","op":"add","persisted":false}""");
        Assert.Equal("39.5", h.Globals.Get("count"));

        await RequestAsync(h, peer, "setGlobal", """{"name":"flag","op":"toggle","persisted":false}""");
        Assert.Equal(true, h.Globals.GetValue("flag"));
        await RequestAsync(h, peer, "setGlobal", """{"name":"flag","op":"toggle","persisted":false}""");
        Assert.Equal(false, h.Globals.GetValue("flag"));

        await RequestAsync(h, peer, "setGlobal", """{"name":"title","value":"Hello, world","persisted":false}""");
        Assert.Equal("Hello, world", h.Globals.GetValue("title"));

        var bad = await RequestAsync(h, peer, "setGlobal", """{"name":"count","value":"abc","op":"add"}""");
        Assert.False((bool?)bad["ok"]);
        var noName = await RequestAsync(h, peer, "setGlobal", """{"value":1}""");
        Assert.False((bool?)noName["ok"]);

        var globals = await RequestAsync(h, peer, "getGlobals");
        Assert.Equal(3, (int?)globals["result"]!["count"]);
        await Eventually(() => peer.OfType("globalChanged").Any(m => (string?)m["name"] == "title"), "globalChanged was pushed");
    }
}
