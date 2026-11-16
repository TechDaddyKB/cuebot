using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Server.Api;

namespace Cuebot.Server.Tests;

/// <summary>The companion app's Chat tab endpoints: chat with deleted flags, send-as, capabilities, moderation as the streamer, the mod log and chat events.</summary>
public sealed class ChatApiTests : IAsyncLifetime
{
    private ApiHost _host = null!;
    private TestBot Bot => _host.Bot;

    public async Task InitializeAsync() => _host = await ApiHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private void Say(string id, string user = "Ann", string userId = "u1", string text = "hello", string roles = "None") =>
        Bot.Bus.Publish(FakeChatAdapter.Message(id, user, userId, text, roles));

    [Fact]
    public async Task Chat_lists_messages_with_deleted_flags_and_bot_platforms()
    {
        Bot.Sender.CanSendAsBot = true;
        Say("m1", text: "first");
        Say("m2", text: "second");
        Bot.Bus.Publish(new BotEvent(ChatHub.MessageDeletedEvent, new Dictionary<string, object?> { ["platform"] = "Twitch", ["messageId"] = "m1", ["userId"] = "u1" }));

        var (status, body) = await _host.GetAsync("/api/v1/chat?limit=10&platform=Twitch");

        Assert.Equal(200, status);
        var messages = body!["messages"]!.AsArray();
        Assert.Equal(["m1", "m2"], messages.Select(m => m!["id"]!.GetValue<string>()));
        Assert.True(messages[0]!["deleted"]!.GetValue<bool>());
        Assert.False(messages[1]!["deleted"]!.GetValue<bool>());
        Assert.True(messages[1]!["canDelete"]!.GetValue<bool>());
        Assert.Equal("messageDeleted", body["moderation"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(["Twitch"], body["botPlatforms"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Empty((await _host.GetAsync("/api/v1/chat?platform=Kick")).Body!["messages"]!.AsArray());
    }

    [Theory]
    [InlineData("bot", true)]
    [InlineData("broadcaster", false)]
    [InlineData("default", null)]
    public async Task Send_as_picks_the_account(string sendAs, bool? bot)
    {
        Bot.Sender.CanSendAsBot = true;

        var (status, body) = await _host.PostAsync("/api/v1/chat/send", new { platform = "Twitch", text = "hi chat", @as = sendAs });

        Assert.Equal(200, status);
        Assert.Equal(sendAs, body!["as"]!.GetValue<string>());
        if (bot is null) Assert.Contains("hi chat", Bot.Sender.Sent);
        else Assert.Contains(Bot.Sender.SentWithOptions, s => s.Text == "hi chat" && s.Options.Bot == bot);
    }

    [Fact]
    public async Task Send_as_bot_without_a_bot_account_is_refused()
    {
        Assert.Equal(409, (await _host.PostAsync("/api/v1/chat/send", new { platform = "Twitch", text = "x", @as = "bot" })).Status);
        Assert.Equal(409, (await _host.PostAsync("/api/v1/chat/send", new { platform = "All", text = "x", @as = "bot" })).Status);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/chat/send", new { platform = "Twitch", text = "x", @as = "mod" })).Status);
        Assert.Equal(400, (await _host.PostAsync("/api/v1/chat/send", new { platform = "Twitch", text = " " })).Status);
        Assert.Empty(Bot.Sender.Sent);
    }

    [Fact]
    public async Task Capabilities_list_each_action_with_state_and_timeout_presets()
    {
        var (status, body) = await _host.GetAsync("/api/v1/chat/capabilities");

        Assert.Equal(200, status);
        var twitch = body!["platforms"]!.AsArray().Single()!;
        Assert.Equal("Twitch", twitch["platform"]!.GetValue<string>());
        var actions = twitch["actions"]!.AsArray().ToDictionary(a => a!["action"]!.GetValue<string>(), a => a!);
        Assert.Equal("works", actions["delete"]["state"]!.GetValue<string>());
        Assert.Equal("closest", actions["timeout"]["state"]!.GetValue<string>());
        Assert.Equal(60, actions["timeout"]["durationUnitSeconds"]!.GetValue<int>());
        Assert.Equal("onWebsite", actions["pin"]["state"]!.GetValue<string>());
        Assert.Equal("https://example.test/mod", actions["pin"]["websiteUrl"]!.GetValue<string>());
        Assert.False(actions["clear"]["canRun"]!.GetValue<bool>());
        Assert.Contains("slowMode", actions.Keys);
        var presets = twitch["timeoutPresets"]!.AsArray();
        Assert.Equal(ApiOperations.TimeoutPresets, presets.Select(p => p!["requestedSeconds"]!.GetValue<int>()));
        Assert.Equal(604800, presets.Last()!["appliedSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Moderation_runs_as_the_streamer_and_is_logged()
    {
        Say("m1", user: "Mod Mia", userId: "u9", text: "rude", roles: "Moderator");

        // A platform moderator is protected from portal members, but the streamer may act on them.
        var (status, body) = await _host.PostAsync("/api/v1/chat/moderate",
            new { platform = "Twitch", action = "timeout", messageId = "m1", durationSeconds = 90, reason = "calm down" });

        Assert.Equal(200, status);
        Assert.True(body!["ok"]!.GetValue<bool>());
        Assert.Equal(120, body["appliedDurationSeconds"]!.GetValue<int>());   // whole minutes, rounded up
        var request = Bot.Moderator.Requests.Single();
        Assert.Equal(ModerationAction.Timeout, request.Action);
        Assert.Equal("u9", request.UserId);
        Assert.Equal("calm down", request.Reason);   // no "[mod: ...]" prefix: the streamer acted
        var entry = body["entry"]!;
        Assert.Equal("Streamer", entry["actorKind"]!.GetValue<string>());
        Assert.Equal("timeout", entry["action"]!.GetValue<string>());

        (_, body) = await _host.GetAsync("/api/v1/chat/modlog?target=u9");
        Assert.Equal("Mod Mia", body!["entries"]![0]!["targetName"]!.GetValue<string>());
        Assert.Empty((await _host.GetAsync("/api/v1/chat/modlog?target=nobody")).Body!["entries"]!.AsArray());
        Assert.Single((await _host.GetAsync("/api/v1/chat/modlog")).Body!["entries"]!.AsArray());
    }

    [Fact]
    public async Task Delete_ban_unban_warn_and_website_tools()
    {
        Say("m1", userId: "u1");

        Assert.True((await _host.PostAsync("/api/v1/chat/moderate", new { platform = "Twitch", action = "delete", messageId = "m1" })).Body!["ok"]!.GetValue<bool>());
        Assert.Equal("m1", Bot.Moderator.Requests.Last().MessageId);
        Assert.True((await _host.PostAsync("/api/v1/chat/moderate", new { platform = "Twitch", action = "ban", userId = "u1", userName = "Ann" })).Body!["ok"]!.GetValue<bool>());
        var (_, unban) = await _host.PostAsync("/api/v1/chat/moderate", new { platform = "Twitch", action = "unban", userId = "u1", userName = "Ann" });
        Assert.NotNull(unban!["entry"]!["undoes"]);   // paired with the ban in the log
        Assert.True((await _host.PostAsync("/api/v1/chat/moderate", new { platform = "Twitch", action = "warn", userId = "u1", reason = "be nice" })).Body!["ok"]!.GetValue<bool>());

        var (_, pin) = await _host.PostAsync("/api/v1/chat/moderate", new { platform = "Twitch", action = "pin", messageId = "m1" });
        Assert.Equal("skipped", pin!["result"]!.GetValue<string>());
        Assert.Equal("https://example.test/mod", pin["websiteUrl"]!.GetValue<string>());
        Assert.Equal(4, Bot.Moderator.Requests.Count);
    }

    [Theory]
    [InlineData("""{"action":"ban","userId":"u1"}""")]
    [InlineData("""{"platform":"All","action":"ban","userId":"u1"}""")]
    [InlineData("""{"platform":"Twitch","action":"explode","userId":"u1"}""")]
    [InlineData("""{"platform":"Twitch","action":"ban"}""")]
    [InlineData("""{"platform":"Twitch","action":"delete"}""")]
    [InlineData("""{"platform":"Twitch","action":"timeout","userId":"u1","durationSeconds":-5}""")]
    public async Task Bad_moderation_requests_are_400(string body)
    {
        var (status, _) = await _host.PostAsync("/api/v1/chat/moderate", JsonNode.Parse(body));

        Assert.Equal(400, status);
        Assert.Empty(Bot.Moderator.Requests);
    }

    [Theory]
    [InlineData("GET", "/api/v1/chat")]
    [InlineData("GET", "/api/v1/chat/capabilities")]
    [InlineData("GET", "/api/v1/chat/modlog")]
    [InlineData("POST", "/api/v1/chat/send")]
    [InlineData("POST", "/api/v1/chat/moderate")]
    public async Task Chat_endpoints_need_the_api_token_and_refuse_the_deck_token(string method, string path)
    {
        Bot.Manager.Settings.Http.DeckToken = "deck-token-456";
        using var anonymous = new HttpClient { BaseAddress = _host.Http.BaseAddress };
        using var deck = new HttpClient { BaseAddress = _host.Http.BaseAddress };
        deck.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "deck-token-456");
        deck.DefaultRequestHeaders.Add("X-Cuebot-Deck-Token", "deck-token-456");
        var json = """{"platform":"Twitch","text":"x","action":"ban","userId":"u1"}""";

        foreach (var client in new[] { anonymous, deck })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(401, (int)response.StatusCode);
        }
        Assert.Empty(Bot.Moderator.Requests);
        Assert.Empty(Bot.Sender.Sent);
    }

    [Fact]
    public async Task Events_stream_chat_messages_moderation_and_the_mod_log_when_asked()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events?types=chat.received,chat.moderated,chat.modLog");
        using var response = await _host.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        Say("m1", text: "hi there");
        Bot.Bus.Publish(new BotEvent(ChatHub.UserTimedOutEvent, new Dictionary<string, object?> { ["platform"] = "Twitch", ["userId"] = "u1", ["duration"] = "60" }));
        await _host.PostAsync("/api/v1/chat/moderate", new { platform = "Twitch", action = "ban", userId = "u2", userName = "Bob" });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<(string Type, JsonNode Data)>();
        string? type = null;
        // The platform's timeout is also logged (as an outside action), so the mod log has two rows: that one and the ban.
        while (events.Count < 4)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) events.Add((type!, JsonNode.Parse(line[6..])!));
        }
        JsonNode Args(string t) => events.First(e => e.Type == t).Data["args"]!;
        Assert.Equal("hi there", Args("chat.received")["text"]!.GetValue<string>());
        Assert.False(Args("chat.received")["deleted"]!.GetValue<bool>());
        Assert.Equal(60, Args("chat.moderated")["durationSeconds"]!.GetValue<int>());
        var logged = events.Where(e => e.Type == "chat.modLog").Select(e => e.Data["args"]!).ToList();
        Assert.Contains(logged, e => e["actorKind"]!.GetValue<string>() == "External" && e["action"]!.GetValue<string>() == "timeout");
        Assert.Contains(logged, e => e["actorKind"]!.GetValue<string>() == "Streamer" && e["targetName"]!.GetValue<string>() == "Bob");
    }

    [Fact]
    public async Task Streams_that_do_not_ask_for_chat_get_none_of_it()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events?types=dashboard.changed,custom.done");
        using var response = await _host.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        Say("m1");
        Bot.Bus.Publish(new BotEvent("custom.done", new Dictionary<string, object?>()));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? first = null;
        while (first is null)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (line.StartsWith("event: ", StringComparison.Ordinal)) first = line[7..];
        }
        Assert.Equal("custom.done", first);
    }
}
