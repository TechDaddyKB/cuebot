using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Server.Mcp;

namespace Cuebot.Server.Tests;

/// <summary>
/// The MCP tools beyond actions, commands, timers and globals: running sub-actions now, testing actions, sample events,
/// queues, polls, giveaways, the store, media requests, quotes, points, groups and viewers.
/// </summary>
public sealed class McpCoverageTests : IAsyncLifetime
{
    /// <summary>The tools these tests cover (McpTests checks every listed tool is covered somewhere).</summary>
    public static readonly string[] Tools =
    [
        "run_subactions", "test_action", "list_emulated_events", "emulate_event",
        "list_queues", "create_queue", "update_queue", "delete_queue", "control_queue",
        "get_polls", "start_poll", "end_poll", "start_prediction", "control_prediction",
        "get_giveaway", "start_giveaway", "control_giveaway",
        "get_store", "add_store_item", "update_store_item", "delete_store_item", "review_redemption",
        "get_media_requests", "add_media_request", "control_media", "remove_media_request",
        "list_quotes", "add_quote", "update_quote", "delete_quote",
        "list_points_currencies", "get_user_points", "change_user_points", "points_leaderboard",
        "list_groups", "manage_group", "set_group_member",
        "find_viewers", "get_viewer_variables", "set_viewer_variable", "delete_viewer_variable",
        "list_overlays", "get_overlay", "get_overlay_link", "create_overlay", "update_overlay", "delete_overlay",
        "list_widget_kinds", "add_widget", "add_custom_widget", "update_layer", "remove_layer",
        "get_widget", "update_widget_settings", "update_widget_code", "get_alert_designs", "update_alert_designs",
        "list_gallery", "import_gallery_item", "list_streamelements_overlays", "import_streamelements_overlay",
        "list_connections", "connect_service", "get_speech_settings", "update_speech_settings", "list_speech_voices", "save_voice_alias",
        "delete_voice_alias", "speak", "set_viewer_voice", "get_feature_settings", "update_feature_settings", "get_stream_totals",
        "set_stream_totals", "list_hotkeys", "save_hotkey", "delete_hotkey", "list_decks", "get_deck", "save_deck", "delete_deck",
    ];

    private ApiHost _host = null!;
    private TestBot Bot => _host.Bot;

    public async Task InitializeAsync()
    {
        _host = await ApiHost.StartAsync();
        await _host.InitializeAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static JsonObject Args(params (string Key, JsonNode? Value)[] values)
    {
        var args = new JsonObject();
        foreach (var (key, value) in values) args[key] = value;
        return args;
    }

    private async Task<JsonObject> Ok(string tool, JsonObject args)
    {
        var (isError, content) = await _host.CallAsync(tool, args);
        Assert.False(isError, $"{tool}: {content.ToJsonString()}");
        return content;
    }

    private async Task<JsonObject> Fails(string tool, JsonObject args)
    {
        var (isError, content) = await _host.CallAsync(tool, args);
        Assert.True(isError, $"{tool} should fail: {content.ToJsonString()}");
        return content;
    }

    /// <summary>Calls a destructive tool through the confirmation flow.</summary>
    private async Task<JsonObject> Confirmed(string tool, JsonObject args)
    {
        var first = await Ok(tool, args);
        Assert.True(first["confirmationRequired"]!.GetValue<bool>(), first.ToJsonString());
        var confirmed = (JsonObject)args.DeepClone();
        confirmed[McpConfirmations.ArgumentName] = first["confirmationToken"]!.GetValue<string>();
        return await Ok(tool, confirmed);
    }

    private static string S(JsonNode? node) => node!.GetValue<string>();

    [Fact]
    public async Task Sub_actions_run_now_and_saved_actions_are_tested_through_their_trigger()
    {
        var run = await Confirmed("run_subactions", Args(
            ("subActions", new JsonArray(
                new JsonObject { ["type"] = "core.log", ["parameters"] = new JsonObject { ["message"] = "hi %who%" } },
                new JsonObject { ["type"] = "test.capture" })),
            ("args", new JsonObject { ["who"] = "MCP" })));
        Assert.True(run["success"]!.GetValue<bool>());
        Assert.Equal("MCP", Bot.Capture.ByAction["MCP: run sub-actions"]["who"]);
        Assert.Empty(Bot.Profile.Snapshot.Actions);   // nothing was saved

        // Checked before the confirmation is asked for.
        var invalid = await Fails("run_subactions", Args(("subActions", new JsonArray(new JsonObject { ["type"] = "no.such.type" }))));
        Assert.Contains("no.such.type", invalid.ToJsonString());

        Bot.AddAction("Greeter");
        var tested = await Ok("test_action", Args(("action", "Greeter"), ("args", new JsonObject { ["user"] = "tester" })));
        Assert.True(tested["success"]!.GetValue<bool>());
        Assert.Equal(true, Bot.Capture.ByAction["Greeter"]["isTest"]);
        Assert.Equal("tester", Bot.Capture.ByAction["Greeter"]["user"]);
        await Fails("test_action", Args(("action", "Greeter"), ("trigger", "no.such.trigger")));
    }

    [Fact]
    public async Task Sample_events_go_to_overlays_or_everywhere()
    {
        var kinds = await Ok("list_emulated_events", []);
        Assert.Equal(42, kinds["platforms"]!["twitch"]!["raid"]!["args"]!["viewers"]!.GetValue<int>());   // what an override can change

        var published = new List<BotEvent>();
        using var _ = Bot.Bus.Subscribe(published.Add);
        var overlays = await Ok("emulate_event", Args(("platform", "twitch"), ("kind", "follow")));
        Assert.Equal(("twitch.follow", "overlays"), (S(overlays["event"]), S(overlays["target"])));
        Assert.Empty(published);   // overlays only: no actions run

        await Ok("emulate_event", Args(("platform", "twitch"), ("kind", "raid"), ("target", "everything"), ("args", new JsonObject { ["viewers"] = 75 })));
        Assert.Contains(published, e => e.Type == "twitch.raid" && Convert.ToInt32(e.Args["viewers"]) == 75);
        await Fails("emulate_event", Args(("platform", "twitch"), ("kind", "superchat")));
    }

    [Fact]
    public async Task Queues_are_created_renamed_controlled_and_deleted()
    {
        await Ok("create_queue", Args(("name", "Sounds"), ("blocking", true)));
        await Fails("create_queue", Args(("name", "sounds")));
        var action = Bot.AddAction("Play sound");
        await Bot.Profile.EditAsync(p => p.Actions.Single(a => a.Id == action.Id).Queue = "Sounds");

        var renamed = await Ok("update_queue", Args(("queue", "Sounds"), ("name", "Audio")));
        Assert.Contains(renamed["queues"]!.AsArray(), q => S(q!["name"]) == "Audio" && q["blocking"]!.GetValue<bool>());
        Assert.Equal("Audio", Bot.Profile.Snapshot.FindAction(action.Id)!.Queue);   // its action moved with it

        var paused = await Ok("control_queue", Args(("queue", "Audio"), ("command", "pause")));
        Assert.True(paused["queues"]!.AsArray().Single(q => S(q!["name"]) == "Audio")!["paused"]!.GetValue<bool>());
        var cleared = await Ok("control_queue", Args(("queue", "all"), ("command", "clear")));
        Assert.Equal(0, cleared["cleared"]!.GetValue<int>());
        await Ok("control_queue", Args(("queue", "all"), ("command", "resume")));

        await Fails("update_queue", Args(("queue", "Default"), ("blocking", true)));
        await Confirmed("delete_queue", Args(("queue", "Audio")));
        Assert.Empty(Bot.Profile.Snapshot.Queues);
        Assert.Equal("", Bot.Profile.Snapshot.FindAction(action.Id)!.Queue);
        Assert.Contains((await Ok("list_queues", []))["queues"]!.AsArray(), q => q!["isDefault"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Polls_predictions_and_giveaways()
    {
        var poll = await Ok("start_poll", Args(("title", "Next game?"), ("choices", new JsonArray("Horror", "Racing")), ("durationSeconds", 0)));
        Assert.Equal("Next game?", S(poll["title"]));
        Assert.Contains((await Ok("get_polls", []))["polls"]!.AsArray(), p => S(p!["title"]) == "Next game?");
        await Ok("end_poll", []);
        await Fails("end_poll", []);
        // The test bot's chat polls have no points, so a prediction is refused with a reason.
        var prediction = await Fails("start_prediction", Args(("title", "Win?"), ("outcomes", new JsonArray("Yes", "No"))));
        Assert.Contains("points", prediction.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        await Fails("control_prediction", Args(("command", "lock")));

        var opened = await Ok("start_giveaway", Args(("title", "A game key"), ("command", "!enter")));
        Assert.Equal("A game key", S(opened["title"]));
        Assert.Equal("A game key", S((await Ok("get_giveaway", []))["giveaway"]!["title"]));
        await Ok("control_giveaway", Args(("command", "cancel")));
    }

    [Fact]
    public async Task Store_items_and_media_requests()
    {
        var added = await Ok("add_store_item", Args(("name", "Hydrate"), ("cost", 500), ("needsApproval", true)));
        Assert.Contains(added["items"]!.AsArray(), i => S(i!["name"]) == "Hydrate" && i["cost"]!.GetValue<long>() == 500);
        await Fails("add_store_item", Args(("name", "hydrate"), ("cost", 1)));
        var updated = await Ok("update_store_item", Args(("item", "Hydrate"), ("changes", new JsonObject { ["cost"] = 250, ["stock"] = 3 })));
        Assert.Contains(updated["items"]!.AsArray(), i => i!["cost"]!.GetValue<long>() == 250 && i["stock"]!.GetValue<int>() == 3 && i["needsApproval"]!.GetValue<bool>());
        Assert.Single((await Ok("get_store", []))["items"]!.AsArray());
        await Fails("review_redemption", Args(("id", "nope"), ("command", "approve")));
        await Confirmed("delete_store_item", Args(("item", "Hydrate")));
        Assert.Empty(Bot.Store.Items);

        var media = await Ok("add_media_request", Args(("link", "https://youtu.be/okVideo0001"), ("user", "Streamer")));
        Assert.Equal("okVideo0001", S(media["current"]!["videoId"]));
        await Ok("add_media_request", Args(("link", "okVideo0002")));
        var queued = (await Ok("get_media_requests", []))["queue"]!.AsArray();
        var waiting = queued.First(q => S(q!["videoId"]) == "okVideo0002")!;
        await Ok("remove_media_request", Args(("id", S(waiting["id"]))));
        await Ok("control_media", Args(("command", "close")));
        Assert.False(Bot.Media.Settings.Open);
        await Fails("add_media_request", Args(("link", "not a video")));
    }

    [Fact]
    public async Task Quotes_points_groups_and_viewers()
    {
        var quote = await Ok("add_quote", Args(("text", "That was intentional"), ("user", "Streamer"), ("game", "Tetris")));
        var id = quote["quote"]!["id"]!.GetValue<int>();
        await Ok("update_quote", Args(("id", id), ("text", "That was totally intentional")));
        var found = await Ok("list_quotes", Args(("search", "totally")));
        Assert.Equal(1, found["count"]!.GetValue<int>());
        await Confirmed("delete_quote", Args(("id", id)));
        await Fails("update_quote", Args(("id", id), ("text", "gone")));

        // A viewer Cuebot knows from points.
        Bot.Points.Add("twitch", "u1", "Alice", 10);
        Assert.Contains("points", S((await Ok("list_points_currencies", []))["main"]!["name"]));
        var balance = await Ok("change_user_points", Args(("platform", "twitch"), ("user", "alice"), ("command", "add"), ("amount", 90)));
        Assert.Equal(100, balance["points"]!.GetValue<long>());
        var read = await Ok("get_user_points", Args(("platform", "twitch"), ("user", "Alice")));
        Assert.Equal((100L, 1), (read["points"]!.GetValue<long>(), read["rank"]!.GetValue<int>()));
        Assert.Equal("Alice", S((await Ok("points_leaderboard", []))["leaderboard"]![0]!["userName"]));
        await Fails("get_user_points", Args(("platform", "twitch"), ("user", "nobody")));
        await Fails("get_user_points", Args(("platform", "twitch"), ("user", "Alice"), ("currency", "gems")));

        await Ok("manage_group", Args(("group", "Regulars"), ("command", "create")));   // creating needs no confirmation
        await Ok("set_group_member", Args(("group", "Regulars"), ("platform", "twitch"), ("user", "Alice"), ("member", true)));
        var groups = await Ok("list_groups", []);
        Assert.Equal("Alice", S(groups["groups"]![0]!["members"]![0]!["userName"]));
        await Ok("set_group_member", Args(("group", "Regulars"), ("platform", "twitch"), ("user", "Alice"), ("member", false)));
        await Ok("manage_group", Args(("group", "Regulars"), ("command", "rename"), ("newName", "VIPs")));
        await Confirmed("manage_group", Args(("group", "VIPs"), ("command", "delete")));
        Assert.Empty(Bot.Groups.All());

        Bot.Users.Handle(new BotEvent(Cuebot.Core.Chat.ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["user"] = "Bob", ["userName"] = "bob", ["userId"] = "u2", ["message"] = "hi", ["platform"] = "twitch",
        }));
        var viewers = await Ok("find_viewers", Args(("search", "bo")));
        Assert.Equal("u2", S(viewers["viewers"]![0]!["userId"]));
        await Ok("set_viewer_variable", Args(("platform", "twitch"), ("user", "bob"), ("name", "deaths"), ("value", 3)));
        var vars = await Ok("get_viewer_variables", Args(("platform", "twitch"), ("user", "bob")));
        Assert.Equal(3, vars["variables"]!["deaths"]!.GetValue<long>());
        await Confirmed("delete_viewer_variable", Args(("platform", "twitch"), ("user", "bob"), ("name", "deaths")));
        await Fails("delete_viewer_variable", Args(("platform", "twitch"), ("user", "bob"), ("name", "deaths"), (McpConfirmations.ArgumentName, "stale")));
    }

    [Fact]
    public async Task Overlays_widgets_settings_and_alert_designs()
    {
        var created = await Ok("create_overlay", Args(("name", "Main scene"), ("width", 1280), ("height", 720)));
        Assert.Equal(1280, created["width"]!.GetValue<int>());
        await Fails("create_overlay", Args(("name", "Tiny"), ("width", 2)));
        await Ok("update_overlay", Args(("overlay", "main scene"), ("name", "Gameplay")));
        Assert.Contains("token=", S((await Ok("get_overlay_link", Args(("overlay", "Gameplay"))))["link"]));
        Assert.Contains((await Ok("list_widget_kinds", []))["widgets"]!.AsArray(), k => S(k!["kind"]) == "hypecup");

        // A label for the latest follower, placed and restyled.
        var label = await Ok("add_widget", Args(("overlay", "Gameplay"), ("kind", "label"),
            ("settings", new JsonObject { ["type"] = "follower", ["prefix"] = "New follower: " }),
            ("place", new JsonObject { ["name"] = "Follower label", ["x"] = 20, ["y"] = 650, ["width"] = 500, ["height"] = 50 })));
        Assert.Equal(("label", "Follower label", 20.0), (S(label["layer"]!["widget"]), S(label["layer"]!["name"]), label["layer"]!["x"]!.GetValue<double>()));
        var widget = await Ok("get_widget", Args(("overlay", "Gameplay"), ("layer", "Follower label")));
        Assert.Equal("New follower: ", S(widget["settings"]!["prefix"]!["value"]));
        Assert.NotNull(widget["settings"]!["type"]!["options"]);
        var changed = await Ok("update_widget_settings", Args(("overlay", "Gameplay"), ("layer", "Follower label"),
            ("values", new JsonObject { ["fontSize"] = 48, ["madeUp"] = 1 })));
        Assert.Equal(48, changed["settings"]!["fontSize"]!["value"]!.GetValue<int>());
        Assert.Single(changed["warnings"]!.AsArray());
        await Ok("update_layer", Args(("overlay", "Gameplay"), ("layer", "Follower label"),
            ("changes", new JsonObject { ["opacity"] = 0.5, ["enter"] = "fadeInUp", ["visible"] = false })));
        await Fails("update_layer", Args(("overlay", "Gameplay"), ("layer", "Follower label"), ("changes", new JsonObject { ["enter"] = "spin" })));

        // A text layer and a custom widget.
        await Ok("add_widget", Args(("overlay", "Gameplay"), ("kind", "text"), ("settings", new JsonObject { ["text"] = "Be nice", ["size"] = 30 })));
        var custom = await Ok("add_custom_widget", Args(("overlay", "Gameplay"), ("name", "Clock"),
            ("code", new JsonObject { ["html"] = "<div id=c>{label}</div>", ["js"] = "setInterval(() => {}, 1000);",
                ["fields"] = "{\"label\": {\"type\": \"text\", \"value\": \"Time\"}}" })));
        Assert.Equal("custom", S(custom["layer"]!["widget"]));
        await Ok("update_widget_code", Args(("overlay", "Gameplay"), ("layer", "Clock"), ("code", new JsonObject { ["css"] = "#c { color: red; }" })));
        var code = await Ok("get_widget", Args(("overlay", "Gameplay"), ("layer", "Clock"), ("includeCode", true)));
        Assert.Equal(("#c { color: red; }", "<div id=c>{label}</div>"), (S(code["code"]!["css"]), S(code["code"]!["html"])));
        await Fails("update_widget_settings", Args(("overlay", "Gameplay"), ("layer", "Text"), ("values", new JsonObject { ["x"] = 1 })));

        // The alert box: raids on with a new template, follows off, a big-raid variation.
        await Ok("add_widget", Args(("overlay", "Gameplay"), ("kind", "alertbox")));
        var designs = await Ok("get_alert_designs", Args(("overlay", "Gameplay"), ("layer", "Alert box")));
        Assert.NotNull(designs["alerts"]!["types"]!["raid"]);
        Assert.NotNull(designs["designFields"]!["template"]);
        var updated = await Ok("update_alert_designs", Args(("overlay", "Gameplay"), ("layer", "Alert box"), ("types", new JsonObject
        {
            ["raid"] = new JsonObject
            {
                ["enabled"] = true, ["design"] = new JsonObject { ["template"] = "{name} raided with {amount}!" },
                ["variations"] = new JsonArray(new JsonObject { ["name"] = "Big raid", ["param"] = "amount", ["op"] = "atLeast", ["value"] = 50,
                    ["design"] = new JsonObject { ["sound"] = "none" } }),
            },
            ["follower"] = new JsonObject { ["enabled"] = false },
        })));
        var raid = updated["alerts"]!["types"]!["raid"]!;
        Assert.Equal("{name} raided with {amount}!", S(raid["design"]!["template"]));
        Assert.NotNull(raid["design"]!["duration"]);   // other design fields kept
        Assert.Equal("Big raid", S(raid["variations"]![0]!["name"]));
        Assert.False(updated["alerts"]!["types"]!["follower"]!["enabled"]!.GetValue<bool>());
        await Fails("update_alert_designs", Args(("overlay", "Gameplay"), ("layer", "Alert box"), ("types", new JsonObject { ["dance"] = new JsonObject() })));
        await Fails("get_alert_designs", Args(("overlay", "Gameplay"), ("layer", "Clock")));
        await Fails("update_widget_settings", Args(("overlay", "Gameplay"), ("layer", "Alert box"), ("values", new JsonObject { ["alerts"] = new JsonObject() })));

        var overlay = await Ok("get_overlay", Args(("overlay", "Gameplay")));
        Assert.Equal(["Follower label", "Text", "Clock", "Alert box"], overlay["layers"]!.AsArray().Select(l => S(l!["name"])));
        await Ok("remove_layer", Args(("overlay", "Gameplay"), ("layer", "Clock")));
        Assert.Equal(3, (await Ok("list_overlays", []))["overlays"]![0]!["layers"]!.AsArray().Count);
        await Confirmed("delete_overlay", Args(("overlay", "Gameplay")));
        Assert.Empty(Bot.Overlays.All());
    }

    [Fact]
    public async Task Gallery_and_streamelements_imports()
    {
        // A gallery with one overlay item, packaged from a real overlay.
        var source = Bot.Overlays.Create("Neon", 1920, 1080);
        Bot.Widgets.AddAlertBox(source.Id);
        using var package = new MemoryStream();
        Bot.Packages.ExportOverlay(source.Id, package, includeCustomCode: false);
        Bot.Overlays.Delete(source.Id);
        var bytes = package.ToArray();
        Bot.GalleryFiles["index.json"] = System.Text.Encoding.UTF8.GetBytes($$"""
            {"format":"cuebot-gallery","version":1,"items":[{"id":"neon","name":"Neon","author":"Me","description":"d","kind":"overlay","license":"MIT",
             "tags":[],"package":"items/neon/package.cuebot-overlay","preview":"items/neon/preview.png","size":{{bytes.Length}},
             "sha256":"{{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}}"}]}
            """);
        Bot.GalleryFiles["items/neon/package.cuebot-overlay"] = bytes;
        Assert.Equal("neon", S((await Ok("list_gallery", []))["items"]![0]!["id"]));
        var imported = await Ok("import_gallery_item", Args(("id", "neon")));
        Assert.Equal(("Neon", 1), (S(imported["overlay"]!["name"]), imported["layers"]!.GetValue<int>()));
        await Fails("import_gallery_item", Args(("id", "missing")));

        Bot.StreamElements.Overlays["se1"] = JsonNode.Parse("""
            {"name":"Old scene","settings":{"width":1920,"height":1080},"widgets":[
              {"type":"text","css":{"top":"10px","left":"10px","width":"200px","height":"40px"},"text":{"value":"Hi"}}]}
            """)!.AsObject();
        Assert.Equal("Old scene", S((await Ok("list_streamelements_overlays", []))["overlays"]![0]!["name"]));
        var se = await Ok("import_streamelements_overlay", Args(("id", "se1")));
        Assert.Equal(("Old scene", 1), (S(se["overlay"]!["name"]), se["imported"]!.GetValue<int>()));
        Bot.StreamElements.HasToken = false;
        Assert.Contains("JWT", (await Fails("list_streamelements_overlays", [])).ToJsonString());
    }

    [Fact]
    public async Task Connections_speech_settings_totals_hotkeys_and_decks()
    {
        var connections = await Ok("list_connections", []);
        Assert.Contains(connections["connections"]!.AsArray(), c => S(c!["name"]) == "OBS" && c["controllable"]!.GetValue<bool>());
        Assert.Equal("Connected", S((await Ok("connect_service", Args(("name", "obs"), ("connect", true))))["state"]));
        Assert.Equal("Disconnected", S((await Ok("connect_service", Args(("name", "OBS"), ("connect", false))))["state"]));
        await Fails("connect_service", Args(("name", "Twitch"), ("connect", true)));   // not controllable
        await Fails("connect_service", Args(("name", "Nope"), ("connect", true)));

        // Speech: never the engine keys; aliases, voices, viewer voices.
        Bot.Speech.Settings.ElevenLabsApiKey = "secret-key";
        var speech = await Ok("get_speech_settings", []);
        Assert.DoesNotContain("secret-key", speech.ToJsonString());
        await Fails("update_speech_settings", Args(("changes", new JsonObject { ["elevenLabsApiKey"] = "x" })));
        var louder = await Ok("update_speech_settings", Args(("changes", new JsonObject { ["masterVolume"] = 80 })));
        Assert.Equal(80, louder["masterVolume"]!.GetValue<int>());
        var voices = await Ok("list_speech_voices", []);
        Assert.Equal("Ann", S(voices["engines"]![0]!["voices"]![0]!["name"]));
        await Fails("save_voice_alias", Args(("alias", new JsonObject { ["name"] = "Narrator", ["engineId"] = "nope" })));
        var saved = await Ok("save_voice_alias", Args(("alias", new JsonObject { ["name"] = "Narrator", ["engineId"] = "fake", ["voiceId"] = "bob", ["rate"] = 2 })));
        Assert.Contains(saved["aliases"]!.AsArray(), a => S(a!["name"]) == "Narrator" && S(a["voiceId"]) == "bob");
        Bot.Points.Add("twitch", "u1", "Alice", 1);
        Assert.Equal("Narrator", S((await Ok("set_viewer_voice", Args(("platform", "twitch"), ("user", "Alice"), ("alias", "Narrator"))))["alias"]));
        await Ok("speak", Args(("text", "Hello chat"), ("alias", "Narrator")));
        Assert.Contains(Bot.Speaker.Log, l => l.Contains("Hello chat", StringComparison.Ordinal));
        await Confirmed("delete_voice_alias", Args(("name", "Narrator")));

        // Feature settings: merged, unknown keys refused.
        var media = await Ok("update_feature_settings", Args(("feature", "media"), ("changes", new JsonObject { ["maxSeconds"] = 240 })));
        Assert.Equal(240, media["maxSeconds"]!.GetValue<int>());
        Assert.Equal(240, Bot.Media.Settings.MaxSeconds);
        await Fails("update_feature_settings", Args(("feature", "media"), ("changes", new JsonObject { ["madeUp"] = 1 })));
        var polls = await Ok("update_feature_settings", Args(("feature", "polls"), ("changes", new JsonObject { ["voteCommand"] = "!pick" })));
        Assert.Equal("!pick", S(polls["voteCommand"]));
        Assert.NotNull((await Ok("get_feature_settings", Args(("feature", "points"))))["name"]);
        await Fails("get_feature_settings", Args(("feature", "secrets")));

        // Stream totals: setting a goal needs no confirmation, resetting does.
        await Ok("set_stream_totals", Args(("command", "setGoal"), ("type", "follower"), ("amount", 100)));
        Assert.NotNull((await Ok("get_stream_totals", []))["session"]);
        await Confirmed("set_stream_totals", Args(("command", "reset")));

        // Hotkeys run actions; decks hold buttons.
        Bot.AddAction("Scene: BRB");
        var hotkeys = await Ok("save_hotkey", Args(("hotkey", new JsonObject { ["name"] = "BRB", ["gesture"] = "Ctrl+Shift+F1", ["action"] = "scene: brb" })));
        Assert.Equal("Scene: BRB", S(hotkeys["hotkeys"]![0]!["action"]));
        await Fails("save_hotkey", Args(("hotkey", new JsonObject { ["name"] = "Bad", ["gesture"] = "" })));
        Assert.Single((await Ok("list_hotkeys", []))["hotkeys"]!.AsArray());
        await Confirmed("delete_hotkey", Args(("hotkey", "BRB")));

        var deck = await Ok("save_deck", Args(("deck", new JsonObject { ["name"] = "Phone" })));
        Assert.Equal("Phone", S(deck["name"]));
        Assert.Equal("Phone", S((await Ok("get_deck", Args(("deck", "phone"))))["name"]));
        Assert.Single((await Ok("list_decks", []))["decks"]!.AsArray());
        await Confirmed("delete_deck", Args(("deck", "Phone")));
        Assert.Empty(Bot.Decks.Decks);
    }
}
