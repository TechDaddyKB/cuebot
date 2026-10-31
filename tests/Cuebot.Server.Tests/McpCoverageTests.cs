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
        Assert.Contains("follow", kinds["platforms"]!["twitch"]!.AsArray().Select(S));

        var published = new List<BotEvent>();
        using var _ = Bot.Bus.Subscribe(published.Add);
        var overlays = await Ok("emulate_event", Args(("platform", "twitch"), ("kind", "follow")));
        Assert.Equal(("twitch.follow", "overlays"), (S(overlays["event"]), S(overlays["target"])));
        Assert.Empty(published);   // overlays only: no actions run

        await Ok("emulate_event", Args(("platform", "twitch"), ("kind", "raid"), ("target", "everything")));
        Assert.Contains(published, e => e.Type == "twitch.raid");
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
}
