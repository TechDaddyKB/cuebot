using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Tests;

public sealed class StreamerBotWebSocketTests : IAsyncLifetime
{
    private TestBot _bot = null!;
    private WsClient _client = null!;

    private static ServerSettings Settings(bool auth = false, bool enforce = true) => new()
    {
        WebSocket = new StreamerBotWebSocketSettings
        {
            Enabled = true, Port = 0, AuthenticationEnabled = auth, Password = auth ? "hunter2" : "", EnforceAuthentication = enforce,
        },
    };

    public async Task InitializeAsync()
    {
        _bot = new TestBot(Settings());
        await _bot.Manager.StartAsync();
        _client = await WsClient.ConnectAsync(_bot.Manager.WebSocket.Port);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _bot.DisposeAsync();
    }

    private static string? S(JsonNode? node) => node?.GetValue<string>();

    [Fact]
    public async Task Hello_is_sent_on_connect_without_authentication_when_no_password()
    {
        var hello = await _client.NextAsync(m => S(m["request"]) == "Hello");
        Assert.Equal("Cuebot", S(hello["info"]!["name"]));
        Assert.False(string.IsNullOrEmpty(S(hello["info"]!["instanceId"])));
        Assert.Null(hello["authentication"]);
        Assert.False(string.IsNullOrEmpty(S(hello["session"])));
    }

    [Fact]
    public async Task GetInfo_and_GetEvents()
    {
        var info = await _client.RequestAsync("GetInfo");
        Assert.Equal("ok", S(info["status"]));
        Assert.Equal("Cuebot", S(info["info"]!["name"]));

        var events = await _client.RequestAsync("GetEvents");
        var twitch = events["events"]!["Twitch"]!.AsArray().Select(S).ToList();
        Assert.Contains("ChatMessage", twitch);
        Assert.Contains("Follow", twitch);
        Assert.Contains("StreamingStarted", events["events"]!["Obs"]!.AsArray().Select(S));
    }

    [Fact]
    public async Task GetActions_lists_actions()
    {
        var action = _bot.AddAction("Hello world");
        var response = await _client.RequestAsync("GetActions");

        Assert.Equal("ok", S(response["status"]));
        Assert.Equal(1, response["count"]!.GetValue<int>());
        var first = response["actions"]![0]!;
        Assert.Equal(action.Id.ToString(), S(first["id"]));
        Assert.Equal("Hello world", S(first["name"]));
        Assert.Equal("Tests", S(first["group"]));
        Assert.True(first["enabled"]!.GetValue<bool>());
        Assert.Equal(1, first["subaction_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task DoAction_by_name_and_by_id_passes_args()
    {
        var action = _bot.AddAction("Greet");

        var byName = await _client.RequestAsync("DoAction", new { action = new { name = "greet" }, args = new { user = "Ann", count = 3 } });
        Assert.Equal("ok", S(byName["status"]));
        await _bot.WaitForRunAsync("Greet");
        Assert.Equal("Ann", _bot.Capture.ByAction["Greet"]["user"]);
        Assert.Equal(3L, _bot.Capture.ByAction["Greet"]["count"]);

        var byId = await _client.RequestAsync("DoAction", new { action = new { id = action.Id.ToString() }, args = new { user = "Bob" } });
        Assert.Equal("ok", S(byId["status"]));
        await WaitUntil(() => Equals(_bot.Capture.ByAction["Greet"]["user"], "Bob"));
    }

    [Fact]
    public async Task DoAction_unknown_or_disabled_is_an_error()
    {
        var missing = await _client.RequestAsync("DoAction", new { action = new { name = "nope" } });
        Assert.Equal("error", S(missing["status"]));

        var action = _bot.AddAction("Off");
        action.Enabled = false;
        _bot.Profile.NotifyChanged();
        var disabled = await _client.RequestAsync("DoAction", new { action = new { name = "Off" } });
        Assert.Equal("error", S(disabled["status"]));
    }

    [Fact]
    public async Task Unknown_request_and_bad_json_are_errors()
    {
        var unknown = await _client.RequestAsync("MakeCoffee");
        Assert.Equal("error", S(unknown["status"]));
        Assert.Contains("MakeCoffee", S(unknown["error"]));

        await _client.SendRawAsync("{not json");
        var bad = await _client.NextAsync(m => S(m["status"]) == "error" && S(m["error"]) == "Invalid JSON");
        Assert.NotNull(bad);
    }

    [Fact]
    public async Task GetBroadcaster_reports_connected_platforms()
    {
        var response = await _client.RequestAsync("GetBroadcaster");
        Assert.Contains("twitch", response["connected"]!.AsArray().Select(S));
        Assert.Contains("youtube", response["disconnected"]!.AsArray().Select(S));
        Assert.NotNull(response["platforms"]!["twitch"]);
    }

    [Fact]
    public async Task GetActiveViewers_lists_present_chatters()
    {
        _bot.Bus.Publish(new BotEvent("chat.message", new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["user"] = "Ann", ["userId"] = "42", ["message"] = "hi", ["isModerator"] = true,
        }));
        var response = await _client.RequestAsync("GetActiveViewers");
        Assert.Equal(1, response["count"]!.GetValue<int>());
        var viewer = response["viewers"]![0]!;
        Assert.Equal("Ann", S(viewer["display"]));
        Assert.Equal("42", S(viewer["id"]));
        Assert.Equal("Moderator", S(viewer["role"]));
    }

    [Fact]
    public async Task Code_triggers_are_listed_and_executed()
    {
        _bot.CodeTriggers.Register("My trigger", "myEvent", ["Custom", "Tests"]);
        _bot.AddAction("On code event", null, new TriggerDefinition
        {
            Type = CodeTriggerRegistry.TriggerTypeId, Settings = { ["eventName"] = "myEvent" },
        });

        var list = await _client.RequestAsync("GetCodeTriggers");
        Assert.Equal(1, list["count"]!.GetValue<int>());
        Assert.Equal("myEvent", S(list["triggers"]![0]!["eventName"]));
        Assert.Equal("My trigger", S(list["triggers"]![0]!["name"]));

        var run = await _client.RequestAsync("ExecuteCodeTrigger", new { triggerName = "myEvent", args = new { x = "y" } });
        Assert.Equal("ok", S(run["status"]));
        await _bot.WaitForRunAsync("On code event");
        Assert.Equal("y", _bot.Capture.ByAction["On code event"]["x"]);
    }

    [Fact]
    public async Task GetCommands_lists_commands()
    {
        _bot.Commands.Save(new CommandDefinition { Name = "Hello", Commands = ["!hello", "!hi"], GlobalCooldownSeconds = 5 });
        var response = await _client.RequestAsync("GetCommands");
        Assert.Equal(1, response["count"]!.GetValue<int>());
        var command = response["commands"]![0]!;
        Assert.Equal("Hello", S(command["name"]));
        Assert.Equal(["!hello", "!hi"], command["commands"]!.AsArray().Select(S));
        Assert.Equal(5, command["globalCooldown"]!.GetValue<double>());
    }

    [Fact]
    public async Task Globals_and_user_globals()
    {
        _bot.Globals.SetValue("score", 10L, persist: true);
        _bot.Globals.SetValue("temp", "x", persist: false);
        _bot.UserVariables.Set("twitch", "42", "Ann", "points", 7L, persist: true);

        var all = await _client.RequestAsync("GetGlobals", new { persisted = true });
        Assert.Equal(1, all["count"]!.GetValue<int>());
        Assert.Equal(10, all["variables"]!["score"]!["value"]!.GetValue<long>());

        var temp = await _client.RequestAsync("GetGlobals", new { persisted = false });
        Assert.Equal("x", S(temp["variables"]!["temp"]!["value"]));

        var one = await _client.RequestAsync("GetGlobal", new { variable = "score" });
        Assert.Equal("score", S(one["variable"]!["name"]));
        Assert.Equal("error", S((await _client.RequestAsync("GetGlobal", new { variable = "missing" }))["status"]));

        var users = await _client.RequestAsync("TwitchGetUserGlobals", new { variable = "points" });
        Assert.Equal(1, users["count"]!.GetValue<int>());
        Assert.Equal("42", S(users["variables"]![0]!["userId"]));
        Assert.Equal(7, users["variables"]![0]!["value"]!.GetValue<long>());

        var user = await _client.RequestAsync("TwitchGetUserGlobal", new { userId = "42" });
        Assert.Equal("points", S(user["variables"]![0]!["name"]));
    }

    [Fact]
    public async Task Credits_emotes_broadcasts_and_pronouns_return_empty_shapes()
    {
        Assert.NotNull((await _client.RequestAsync("GetCredits"))["credits"]!["events"]);
        var test = (await _client.RequestAsync("TestCredits"))["credits"]!;
        Assert.True(test["cuebot"]!["isTest"]!.GetValue<bool>());
        Assert.Contains(test["events"]!.AsObject(), section => section.Value!.AsArray().Count > 0);
        Assert.Equal("ok", S((await _client.RequestAsync("ClearCredits"))["status"]));
        Assert.NotNull((await _client.RequestAsync("TwitchGetEmotes"))["emotes"]);
        Assert.NotNull((await _client.RequestAsync("YouTubeGetEmotes"))["emotes"]);
        Assert.Empty((await _client.RequestAsync("GetMonitoredYouTubeBroadcasts"))["broadcasts"]!.AsArray());
        var pronouns = await _client.RequestAsync("GetUserPronouns", new { platform = "twitch", userLogin = "ann" });
        Assert.False(pronouns["pronoun"]!["userFound"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SendMessage_goes_to_chat()
    {
        var response = await _client.RequestAsync("SendMessage", new { message = "hello chat", platform = "twitch" });
        Assert.Equal("ok", S(response["status"]));
        Assert.Contains("hello chat", _bot.Sender.Sent);
    }

    [Fact]
    public async Task Subscribed_events_arrive_in_the_streamerbot_envelope()
    {
        var sub = await _client.RequestAsync("Subscribe", new { events = new { Twitch = new[] { "ChatMessage" } } });
        Assert.Equal(["ChatMessage"], sub["events"]!["Twitch"]!.AsArray().Select(S));

        _bot.Bus.Publish(new BotEvent("twitch.follow", new Dictionary<string, object?> { ["user"] = "Zed" }));
        _bot.Bus.Publish(new BotEvent("twitch.chatMessage", new Dictionary<string, object?>
        {
            ["user"] = "Ann", ["userId"] = "42", ["message"] = "hi there", ["color"] = "#ff0000",
        }));

        var e = await _client.NextAsync(m => m["event"] is not null);
        Assert.Equal("Twitch", S(e["event"]!["source"]));
        Assert.Equal("ChatMessage", S(e["event"]!["type"]));
        Assert.True(DateTimeOffset.TryParse(S(e["timeStamp"]), out _));
        Assert.Equal("hi there", S(e["data"]!["message"]));
        Assert.Equal("hi there", S(e["data"]!["text"]));
        Assert.Equal("Ann", S(e["data"]!["user"]!["name"]));
        Assert.Equal("42", S(e["data"]!["user"]!["id"]));
        Assert.Equal("Ann", S(e["data"]!["userName"]));
        Assert.Equal("twitch.chatMessage", S(e["data"]!["cuebotEvent"]));

        var unsub = await _client.RequestAsync("UnSubscribe", new { events = new { Twitch = new[] { "ChatMessage" } } });
        Assert.Null(unsub["events"]!["Twitch"]);
        _bot.Bus.Publish(new BotEvent("twitch.chatMessage", new Dictionary<string, object?> { ["message"] = "again" }));
        Assert.True(await _client.NothingAsync(m => m["event"] is not null, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Wildcard_subscription_receives_application_and_raw_events()
    {
        await _client.RequestAsync("Subscribe", new { events = new { Application = new[] { "*" }, Raw = new[] { "ActionCompleted" } } });
        _bot.AddAction("Fresh");
        var added = await _client.NextAsync(WsClient.IsEvent("Application", "ActionAdded"));
        Assert.Equal("Fresh", S(added["data"]!["name"]));

        await _client.RequestAsync("DoAction", new { action = new { name = "Fresh" } });
        var done = await _client.NextAsync(WsClient.IsEvent("Raw", "ActionCompleted"));
        Assert.Equal("Fresh", S(done["data"]!["actionName"]));
        Assert.True(done["data"]!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Broadcast_json_reaches_clients_as_General_Custom()
    {
        await _client.NextAsync(m => S(m["request"]) == "Hello");
        await _bot.Manager.WebSocket.BroadcastCustomAsync("""{"hello":"overlay"}""", json: true);
        var e = await _client.NextAsync(WsClient.IsEvent("General", "Custom"));
        Assert.Equal("overlay", S(e["data"]!["hello"]));
    }

    [Fact]
    public async Task Clients_are_listed()
    {
        await _client.NextAsync(m => S(m["request"]) == "Hello");
        await WaitUntil(() => _bot.Manager.WebSocket.Clients.Count == 1);
        Assert.Equal(1, _bot.Manager.Statuses.Single(s => s.Kind == ServerKind.WebSocket).Clients);
    }

    [Fact]
    public async Task Foreign_web_origins_are_rejected()
    {
        await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(() =>
            WsClient.ConnectAsync(_bot.Manager.WebSocket.Port, origin: "https://evil.example"));
        await using var local = await WsClient.ConnectAsync(_bot.Manager.WebSocket.Port, origin: "http://localhost:7474");
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, local.State);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met");
            await Task.Delay(20);
        }
    }
}

public sealed class StreamerBotAuthenticationTests
{
    private static string? S(JsonNode? node) => node?.GetValue<string>();

    private static async Task<(TestBot Bot, WsClient Client, JsonObject Hello)> ConnectAsync(bool enforce)
    {
        var bot = new TestBot(new ServerSettings
        {
            WebSocket = new StreamerBotWebSocketSettings
            {
                Enabled = true, Port = 0, AuthenticationEnabled = true, Password = "hunter2", EnforceAuthentication = enforce,
            },
        });
        await bot.Manager.StartAsync();
        var client = await WsClient.ConnectAsync(bot.Manager.WebSocket.Port);
        var hello = await client.NextAsync(m => S(m["request"]) == "Hello");
        return (bot, client, hello);
    }

    [Fact]
    public void Compute_matches_the_documented_formula()
    {
        // base64(sha256(base64(sha256(password + salt)) + challenge)), computed independently here.
        var secret = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("pwsalt"u8.ToArray()));
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret + "chal")));
        Assert.Equal(expected, StreamerBotAuth.Compute("pw", "salt", "chal"));
        Assert.True(StreamerBotAuth.Verify("pw", "salt", "chal", expected));
        Assert.False(StreamerBotAuth.Verify("pw", "salt", "chal", "nope"));
    }

    [Fact]
    public async Task Enforced_authentication_blocks_requests_until_authenticated()
    {
        var (bot, client, hello) = await ConnectAsync(enforce: true);
        await using var _ = bot;
        await using var __ = client;
        var salt = S(hello["authentication"]!["salt"])!;
        var challenge = S(hello["authentication"]!["challenge"])!;

        var blocked = await client.RequestAsync("GetActions");
        Assert.Equal("error", S(blocked["status"]));

        var wrong = await client.RequestAsync("Authenticate", new { authentication = StreamerBotAuth.Compute("wrong", salt, challenge) });
        Assert.Equal("error", S(wrong["status"]));

        var ok = await client.RequestAsync("Authenticate", new { authentication = StreamerBotAuth.Compute("hunter2", salt, challenge) });
        Assert.Equal("ok", S(ok["status"]));
        Assert.Equal("ok", S((await client.RequestAsync("GetActions"))["status"]));
        Assert.True(bot.Manager.WebSocket.Clients.Single().Authenticated);
    }

    [Fact]
    public async Task Without_enforcement_only_privileged_requests_need_authentication()
    {
        var (bot, client, _) = await ConnectAsync(enforce: false);
        await using var b = bot;
        await using var c = client;

        Assert.Equal("ok", S((await client.RequestAsync("GetActions"))["status"]));
        Assert.Equal("error", S((await client.RequestAsync("SendMessage", new { message = "x" }))["status"]));
        Assert.Equal("error", S((await client.RequestAsync("DoAction", new { action = new { name = "x" } }))["status"]));
        Assert.Empty(bot.Sender.Sent);
    }
}
