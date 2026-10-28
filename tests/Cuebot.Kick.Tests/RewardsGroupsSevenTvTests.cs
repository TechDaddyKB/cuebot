using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using Cuebot.Kick.Api;
using Cuebot.Kick.Auth;
using Cuebot.Kick.SevenTv;
using Cuebot.Kick.SubActions;
using Cuebot.Kick.Tests.Support;
using Cuebot.Kick.Triggers;

namespace Cuebot.Kick.Tests;

/// <summary>Channel reward management, the Kick user-group and random-user sub-actions, and 7TV emote triggers.</summary>
public sealed class RewardsGroupsSevenTvTests : IAsyncLifetime
{
    private static readonly KickEndpoints Endpoints = new(new Uri("https://id.test/"), new Uri("https://api.test/"),
        new Uri("https://web.test/"), new Uri("https://rt.test/api/"));
    private static readonly SevenTvEndpoints SevenTvHosts = new(new Uri("https://7tv.test/v3/"), new Uri("wss://events.7tv.test/v3"));

    private const string Rewards = """
        {"data":[
          {"id":"01REWARDA","title":"Hydrate","description":"Drink","cost":100,"background_color":"#00e701","is_enabled":true,"is_paused":false,"is_user_input_required":false,"should_redemptions_skip_request_queue":false},
          {"id":"01REWARDB","title":"Song request","description":"","cost":500,"background_color":"#123456","is_enabled":true,"is_paused":true,"is_user_input_required":true,"should_redemptions_skip_request_queue":false}
        ],"message":"OK"}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-kick-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeRealtimeServer _realtime = new();
    private readonly FakeRealtimeServer _sevenTv = new();
    private EventBus _bus = null!;
    private JsonFile<KickSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private KickService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _settings = new JsonFile<KickSettings>(Path.Combine(_dir, "kick.json"), _log);
        _settings.Value.ClientId = "cid";
        _settings.Value.ClientSecret = "csecret";
        _settings.Value.PollSeconds = 0;
        _settings.Value.UseRealtimeChat = false;
        _settings.Value.Broadcaster.AccessToken = "AT-1";
        _settings.Value.Broadcaster.RefreshToken = "RT-1";
        _settings.Value.Broadcaster.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        _settings.Value.Broadcaster.UserId = 100;
        _settings.Value.Broadcaster.UserName = "CuebotDev";
        _http = new FakeHttpHandler(Respond);
        _bus = new EventBus(_log);
        _bus.Subscribe(_published.Enqueue);
        _service = new KickService(_settings, new HttpClient(_http), _bus, _log, Endpoints, TestTimings.Fast, TimeProvider.System,
            _realtime.Create, _ => throw new InvalidOperationException("no sign-in in these tests"), _sevenTv.Create, SevenTvHosts);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r) => (r.Uri.Host, r.Method.Method, r.Path) switch
    {
        ("api.test", "GET", "/public/v1/users") => FakeHttpHandler.Json("""{"data":[{"user_id":100,"name":"CuebotDev","profile_picture":""}]}"""),
        ("api.test", "GET", "/public/v1/channels") => FakeHttpHandler.Json(
            """{"data":[{"broadcaster_user_id":100,"slug":"cuebotdev","stream_title":"t","stream":{"is_live":false,"viewer_count":0}}]}"""),
        ("api.test", "GET", "/public/v1/channels/rewards") => FakeHttpHandler.Json(Rewards),
        ("api.test", "POST", "/public/v1/channels/rewards") => FakeHttpHandler.Json(
            """{"data":{"id":"01NEW","title":"Created","cost":42,"is_enabled":true},"message":"OK"}"""),
        ("api.test", "PATCH", "/public/v1/channels/rewards/01REWARDB") => FakeHttpHandler.Json(
            """{"data":{"id":"01REWARDB","title":"Song request","cost":500,"is_enabled":true,"is_paused":false},"message":"OK"}"""),
        ("api.test", "DELETE", "/public/v1/channels/rewards/01REWARDA") => new HttpResponseMessage(HttpStatusCode.NoContent),
        // A reward made on kick.com: only the app that created a reward may change it.
        ("api.test", "PATCH", "/public/v1/channels/rewards/01REWARDA") => new HttpResponseMessage(HttpStatusCode.Forbidden),
        ("api.test", "GET", "/public/v1/channels/rewards/redemptions") => FakeHttpHandler.Json(r.Query.Contains("cursor=")
            ? """{"data":[{"reward":{"id":"01REWARDA","title":"Hydrate"},"redemptions":[{"id":"R3","redeemed_at":"2026-10-27T10:00:00Z","redeemer":{"user_id":7},"status":"pending","user_input":""}]}],"pagination":{"next_cursor":""}}"""
            : """{"data":[{"reward":{"id":"01REWARDA","title":"Hydrate"},"redemptions":[{"id":"R1","redeemed_at":"2026-10-27T09:00:00Z","redeemer":{"user_id":5},"status":"pending","user_input":"hi"},{"id":"R2","redeemed_at":"2026-10-27T09:30:00Z","redeemer":{"user_id":6},"status":"pending","user_input":""}]}],"pagination":{"next_cursor":"Q1VSU09S"}}"""),
        ("api.test", "POST", "/public/v1/channels/rewards/redemptions/accept") or ("api.test", "POST", "/public/v1/channels/rewards/redemptions/reject")
            => FakeHttpHandler.Json(r.Body.Contains("\"R2\"")
                ? """{"data":[{"id":"R2","reason":"NOT_PENDING"}],"message":"OK"}"""
                : """{"data":[],"message":"OK"}"""),
        ("api.test", "POST", "/public/v1/moderation/bans") or ("api.test", "DELETE", "/public/v1/moderation/bans")
            or ("api.test", "PATCH", "/public/v1/channels") => new HttpResponseMessage(HttpStatusCode.NoContent),
        ("api.test", "GET", "/public/v1/categories/7") => FakeHttpHandler.Json("""{"data":{"id":7,"name":"Just Chatting","thumbnail":"jc.png"}}"""),
        ("7tv.test", "GET", "/v3/users/kick/100") => FakeHttpHandler.Json(
            """{"id":"conn","platform":"KICK","username":"cuebotdev","emote_set":{"id":"SET1","name":"cuebot's set","emotes":[]},"user":{"id":"U7TV"}}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    private ActionHarness Harness(GroupStore? groups = null, UserRegistry? users = null)
    {
        groups ??= new GroupStore(null, _log);
        users ??= new UserRegistry(null, _bus, _log);
        ISubActionHandler[] handlers =
        [
            new CreateRewardSubAction(_service), new DeleteRewardSubAction(_service), new GetRewardInfoSubAction(_service),
            new UpdateRedemptionSubAction(_service), new UpdatePendingRedemptionsSubAction(_service),
            .. UpdateRewardSubAction.All(_service),
            new GetRewardCounterSubAction(_service), new ResetRewardCounterSubAction(_service, users: false),
            new ResetRewardCounterSubAction(_service, users: true), new SetRewardGroupStateSubAction(_service, paused: false),
            new SetRewardGroupStateSubAction(_service, paused: true), new ConfigureRewardsSubAction(_service),
            new KickAddUserToGroupSubAction(_service, groups), new KickRemoveUserFromGroupSubAction(_service, groups),
            new KickUserInGroupSubAction(_service, groups), new KickAddRandomUsersSubAction(_service, users),
        ];
        return new ActionHarness(_log, handlers);
    }

    // ---- rewards ----

    [Fact]
    public void Broadcaster_scopes_include_reward_management()
    {
        Assert.Contains("channel:rewards:read", KickScopes.Broadcaster);
        Assert.Contains("channel:rewards:write", KickScopes.Broadcaster);
        Assert.DoesNotContain("channel:rewards:write", KickScopes.Bot);
    }

    [Fact]
    public async Task Rewards_are_listed_and_found_by_title_or_id()
    {
        var rewards = await _service.GetRewardsAsync();
        Assert.Equal(2, rewards.Count);
        Assert.True(rewards[1].IsPaused);
        Assert.True(rewards[1].IsUserInputRequired);
        Assert.Equal("01REWARDB", (await _service.FindRewardAsync("song REQUEST")).Id);
        Assert.Equal("Hydrate", (await _service.FindRewardAsync("01rewarda")).Title);
        await Assert.ThrowsAsync<KickApiException>(() => _service.FindRewardAsync("nope"));
    }

    [Fact]
    public async Task Create_reward_sends_kicks_fields_and_reports_the_new_id()
    {
        using var harness = Harness();
        var (run, args) = await harness.RunAsync("kick.createReward", new()
        {
            ["title"] = "Created", ["cost"] = "42", ["description"] = "  A test  ", ["color"] = "abc", ["userInput"] = "true",
        });
        Assert.True(run.Success, run.Error);
        Assert.Equal("01NEW", args!["createdRewardId"]);
        var body = Body(_http.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/public/v1/channels/rewards"));
        Assert.Equal("Created", body.GetProperty("title").GetString());
        Assert.Equal(42, body.GetProperty("cost").GetInt32());
        Assert.Equal("A test", body.GetProperty("description").GetString());
        Assert.Equal("#AABBCC", body.GetProperty("background_color").GetString());
        Assert.True(body.GetProperty("is_enabled").GetBoolean());
        Assert.True(body.GetProperty("is_user_input_required").GetBoolean());
        Assert.False(body.TryGetProperty("is_paused", out _)); // not allowed on create
    }

    [Fact]
    public async Task Reward_field_sub_actions_patch_only_that_field_of_the_redeemed_reward()
    {
        using var harness = Harness();
        var (run, args) = await harness.RunAsync("kick.setRewardPaused", new() { ["paused"] = "false" },
            new() { ["rewardId"] = "01REWARDB" });
        Assert.True(run.Success, run.Error);
        var patch = _http.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/public/v1/channels/rewards/01REWARDB", patch.Path);
        Assert.Equal("""{"is_paused":false}""", patch.Body);
        Assert.Equal(false, args!["rewardPaused"]);

        (run, _) = await harness.RunAsync("kick.setRewardCost", new() { ["reward"] = "Song request", ["cost"] = "0" });
        Assert.False(run.Success); // Kick needs a cost of at least 1; nothing is sent
        Assert.Single(_http.Requests, r => r.Method == HttpMethod.Patch);

        (run, _) = await harness.RunAsync("kick.setRewardColor", new() { ["reward"] = "Song request", ["color"] = "not a colour" });
        Assert.False(run.Success);
    }

    [Fact]
    public async Task Delete_and_get_info_by_title()
    {
        using var harness = Harness();
        var (run, args) = await harness.RunAsync("kick.getRewardInfo", new() { ["reward"] = "hydrate" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(100, args!["rewardCost"]);
        Assert.Equal("Drink", args["rewardDescription"]);

        (run, _) = await harness.RunAsync("kick.deleteReward", new() { ["reward"] = "Hydrate" });
        Assert.True(run.Success, run.Error);
        Assert.Contains(_http.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/public/v1/channels/rewards/01REWARDA");
        Assert.DoesNotContain(_service.Rewards, r => r.Id == "01REWARDA");
    }

    [Fact]
    public async Task Redemptions_are_accepted_or_rejected_and_failures_reported()
    {
        using var harness = Harness();
        var (run, args) = await harness.RunAsync("kick.updateRedemption", new() { ["status"] = "Reject" },
            new() { ["redemptionId"] = "R1" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(true, args!["redemptionUpdated"]);
        var reject = _http.Requests.Single(r => r.Path.EndsWith("/reject", StringComparison.Ordinal));
        Assert.Equal("""{"ids":["R1"]}""", reject.Body);

        (run, args) = await harness.RunAsync("kick.updatePendingRedemptions", new() { ["reward"] = "Hydrate" });
        Assert.True(run.Success, run.Error);
        var list = _http.Requests.Where(r => r.Path == "/public/v1/channels/rewards/redemptions").ToArray();
        Assert.Equal(2, list.Length); // followed the cursor
        Assert.Contains("reward_id=01REWARDA", list[0].Query);
        Assert.Contains("status=pending", list[0].Query);
        var accept = Body(_http.Requests.Single(r => r.Path.EndsWith("/accept", StringComparison.Ordinal)));
        Assert.Equal(["R1", "R2", "R3"], accept.GetProperty("ids").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(2, args!["redemptionsUpdated"]);
        Assert.Equal("R2: NOT_PENDING", args["redemptionFailures"]);
    }

    [Fact]
    public async Task Reward_counters_are_read_and_reset_by_reward_title_or_id()
    {
        using var harness = Harness();
        _service.Counters.CountRedemption("01REWARDB", "5");
        _service.Counters.CountRedemption("01REWARDB", "5");
        _service.Counters.CountRedemption("01REWARDB", "6");

        var (run, args) = await harness.RunAsync("kick.getRewardCounter", new() { ["reward"] = "Song request" }, new() { ["userId"] = "5" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(3, args!["counter"]);
        Assert.Equal(2, args["userCounter"]);

        (run, _) = await harness.RunAsync("kick.resetRewardUserCounters", new(), new() { ["rewardId"] = "01REWARDB" });
        Assert.True(run.Success, run.Error);
        Assert.Equal((3, 0), _service.Counters.GetRewardCounter("01REWARDB", "5"));
        (run, _) = await harness.RunAsync("kick.resetRewardCounter", new() { ["reward"] = "song request" });
        Assert.True(run.Success, run.Error);
        Assert.Equal((0, 0), _service.Counters.GetRewardCounter("01REWARDB", "6"));
        Assert.DoesNotContain(_http.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Reward_groups_change_only_rewards_not_in_the_state_and_count_ones_kick_refuses()
    {
        _settings.Value.Broadcaster.Scopes = [.. KickScopes.Broadcaster];
        using var harness = Harness();
        _service.Counters.SetGroup("01REWARDA", "Drinks");
        _service.Counters.SetGroup("01REWARDB", "drinks");

        // Both are enabled: nothing to do.
        var (run, args) = await harness.RunAsync("kick.setRewardGroupEnabled", new() { ["group"] = "Drinks", ["state"] = "Enabled" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(0, args!["rewardsChanged"]);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Patch);

        // Only Song request is paused.
        (run, args) = await harness.RunAsync("kick.setRewardGroupPaused", new() { ["group"] = "DRINKS", ["state"] = "Unpaused" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(1, args!["rewardsChanged"]);
        Assert.Equal("""{"is_paused":false}""", _http.Requests.Single(r => r.Method == HttpMethod.Patch).Body);

        (run, args) = await harness.RunAsync("kick.setRewardGroupEnabled", new() { ["group"] = "Drinks", ["state"] = "Disabled" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(1, args!["rewardsChanged"]); // Song request
        Assert.Equal(1, args["rewardsSkipped"]); // Hydrate was made on kick.com (403)

        (run, _) = await harness.RunAsync("kick.setRewardGroupPaused", new() { ["group"] = "Snacks", ["state"] = "Paused" });
        Assert.False(run.Success);
    }

    [Fact]
    public async Task Configure_rewards_sends_one_update_per_reward()
    {
        _settings.Value.Broadcaster.Scopes = [.. KickScopes.Broadcaster];
        using var harness = Harness();
        _service.Counters.SetGroup("01REWARDB", "Music");

        var (run, args) = await harness.RunAsync("kick.configureRewards", new() { ["disable"] = "Music", ["unpause"] = "song request" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(1, args!["rewardsChanged"]);
        var patch = Body(_http.Requests.Single(r => r.Method == HttpMethod.Patch));
        Assert.False(patch.GetProperty("is_enabled").GetBoolean());
        Assert.False(patch.GetProperty("is_paused").GetBoolean());

        // Hydrate is in no group; Kick refuses the change (made on kick.com).
        (run, args) = await harness.RunAsync("kick.configureRewards", new() { ["disableNotConfigured"] = "true" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(0, args!["rewardsChanged"]);
        Assert.Equal(1, args["rewardsSkipped"]);

        (run, _) = await harness.RunAsync("kick.configureRewards", new() { ["enable"] = "No such reward" });
        Assert.False(run.Success);

        _settings.Value.Broadcaster.Scopes = [KickScopes.RewardsRead];
        (run, _) = await harness.RunAsync("kick.configureRewards", new() { ["pause"] = "Hydrate" });
        Assert.False(run.Success); // signed in without the rewards write permission
    }

    [Fact]
    public async Task Redemption_updates_are_split_into_batches_of_25()
    {
        var ids = Enumerable.Range(1, 30).Select(i => "X" + i).ToArray();
        await _service.UpdateRedemptionsAsync(ids, accept: true);
        var calls = _http.Requests.Where(r => r.Path.EndsWith("/accept", StringComparison.Ordinal)).Select(Body).ToArray();
        Assert.Equal([25, 5], calls.Select(c => c.GetProperty("ids").GetArrayLength()));
    }

    // ---- groups and random users ----

    [Fact]
    public async Task Group_sub_actions_use_kick_ids_from_names_seen_in_events()
    {
        var groups = new GroupStore(null, _log);
        using var harness = Harness(groups);

        var (run, args) = await harness.RunAsync("kick.addUserToGroup", new() { ["group"] = "VIPs", ["user"] = "12345", ["userId"] = "%userId%" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(true, args!["groupUserAdded"]);
        Assert.True(groups.Contains("VIPs", "Kick", "12345", ""));

        (run, args) = await harness.RunAsync("kick.userInGroup", new() { ["group"] = "VIPs", ["user"] = "someone", ["userId"] = "12345" }, branch: "True");
        Assert.True(run.Success, run.Error);
        Assert.Equal(true, args!["inGroup"]);

        (run, args) = await harness.RunAsync("kick.userInGroup", new() { ["group"] = "VIPs", ["user"] = "other", ["userId"] = "" }, branch: "False");
        Assert.NotNull(args);
        Assert.Equal(false, args["inGroup"]);

        (run, args) = await harness.RunAsync("kick.removeUserFromGroup", new() { ["group"] = "VIPs", ["user"] = "@12345", ["userId"] = "" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(true, args!["groupUserRemoved"]);
        Assert.False(groups.Contains("VIPs", "Kick", "12345", ""));
    }

    [Fact]
    public async Task Random_users_come_from_present_kick_chatters_without_the_broadcaster()
    {
        var users = new UserRegistry(null, _bus, _log);
        void Chat(string platform, string id, string name) => users.Handle(new BotEvent(ChatHub.MessageEvent,
            new Dictionary<string, object?> { ["platform"] = platform, ["userId"] = id, ["user"] = name, ["userName"] = name.ToLowerInvariant() }));
        Chat("Kick", "100", "CuebotDev");
        Chat("Kick", "1", "Alice");
        Chat("Kick", "2", "Bob");
        Chat("Twitch", "3", "Carol");
        using var harness = Harness(users: users);

        var (run, args) = await harness.RunAsync("kick.addRandomUsers", new() { ["count"] = "5" });
        Assert.True(run.Success, run.Error);
        Assert.Equal(2, args!["randomUserCount"]);
        var picked = new[] { args["randomUser0"], args["randomUser1"] }.Order().ToArray();
        Assert.Equal(["Alice", "Bob"], picked);
        Assert.Contains(args["randomUserId"], new object?[] { "1", "2" });

        (_, args) = await harness.RunAsync("kick.addRandomUsers", new() { ["count"] = "1", ["excludeCaller"] = "true" },
            new() { ["userId"] = "1", ["user"] = "Alice" });
        Assert.Equal("Bob", args!["randomUser0"]);
    }

    // ---- Streamer.bot output names (docs: 3.api/1.sub-actions/kick/**) ----

    public static TheoryData<string, string, string> StreamerBotOutputs => new()
    {
        { "kick.banUser", "user=Fan", "bannedUser,bannedUserName,bannedUserId,bannedUserType,banResult" },
        { "kick.timeoutUser", "user=Fan;duration=60", "timedOutUser,timedOutUserName,timedOutUserId,timedOutUserType,timeoutResult" },
        { "kick.unbanUser", "user=Fan", "unbannedUser,unbannedUserName,unbannedUserId,unbannedUserType,unbanResult" },
        { "kick.untimeoutUser", "user=5", "unTimedOutUser,unTimedOutUserName,unTimedOutUserId,unTimedOutUserType,unTimedOutResult" },
        { "kick.setTitle", "title=New", "titleSuccess" },
        { "kick.setCategory", "category=7", "category.success,category.id,category.name,category.thumbnail" },
        { "kick.addBroadcasterInfo", "", "broadcastUser,broadcastUserName,broadcastUserId,broadcastUserType,broadcasterProfileUrl" },
        { "kick.getUserInfo", "user=100", "targetUser,targetUserName,targetUserId,targetDescription,targetDescriptionEscaped,targetUserProfileImageUrl,targetUserProfileImageUrlEscaped,targetUserType" },
        { "kick.addUserToGroup", "group=VIPs;user=5", "addResult" },
        { "kick.removeUserFromGroup", "group=VIPs;user=5", "removeResult" },
        { "kick.userInGroup", "group=VIPs;user=Fan;userId=5", "inGroup,foundUser,foundUserName,foundUserId,foundUserType" },
    };

    [Theory]
    [MemberData(nameof(StreamerBotOutputs))]
    public async Task Sub_actions_write_Streamer_bot_output_names(string type, string parameters, string expected)
    {
        ISubActionHandler[] handlers =
        [
            new BanUserSubAction(_service), new TimeoutUserSubAction(_service),
            new UnbanUserSubAction(_service, "kick.unbanUser", "Unban user"), new UnbanUserSubAction(_service, "kick.untimeoutUser", "Remove timeout"),
            new SetTitleSubAction(_service), new SetCategorySubAction(_service), new AddBroadcasterInfoSubAction(_service),
            new GetUserInfoSubAction(_service),
        ];
        var groups = new GroupStore(null, _log);
        using var harness = new ActionHarness(_log, [.. handlers, new KickAddUserToGroupSubAction(_service, groups),
            new KickRemoveUserFromGroupSubAction(_service, groups), new KickUserInGroupSubAction(_service, groups)]);
        _service.Hub.Handle(Cuebot.Kick.Events.KickEventFactory.Follow("realtime", new Cuebot.Kick.Events.KickActor(5, "Fan")));
        var values = parameters.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

        var (run, args) = await harness.RunAsync(type, values, new() { ["user"] = "Fan", ["userName"] = "Fan", ["userId"] = "5" },
            branch: type == "kick.userInGroup" ? "False" : null);

        Assert.True(run.Success, run.Error);
        foreach (var name in expected.Split(','))
            Assert.True(args!.ContainsKey(name), $"{type} did not set Streamer.bot's '{name}'");
        if (type == "kick.banUser")
        {
            Assert.Equal("Fan", args!["bannedUser"]);
            Assert.Equal("5", args["bannedUserId"]);
            Assert.Equal("kick", args["bannedUserType"]);
            Assert.Equal(true, args["banResult"]);
        }
        if (type == "kick.untimeoutUser") Assert.Equal("Fan", args!["unTimedOutUser"]);
    }

    [Fact]
    public async Task Random_users_have_a_platform_type()
    {
        var users = new UserRegistry(null, _bus, _log);
        users.Handle(new BotEvent(ChatHub.MessageEvent,
            new Dictionary<string, object?> { ["platform"] = "Kick", ["userId"] = "1", ["user"] = "Alice", ["userName"] = "alice" }));
        using var harness = Harness(users: users);

        var (_, args) = await harness.RunAsync("kick.addRandomUsers", new() { ["count"] = "1" });

        Assert.Equal("kick", args!["randomUserType0"]);
    }

    // ---- 7TV ----

    [Fact]
    public void Emote_set_update_decodes_pushed_and_pulled_emotes()
    {
        using var doc = JsonDocument.Parse(SevenTvChange);
        var events = SevenTvEventSource.Decode(doc.RootElement, "SET1");
        Assert.Equal(2, events.Count);
        var added = events[0];
        Assert.Equal(KickEventTypes.SevenTvEmoteAdded, added.Type);
        Assert.Equal("catJAM", added.Args["emoteName"]);
        Assert.Equal("E1", added.Args["emoteId"]);
        Assert.Equal("https://cdn.7tv.app/emote/E1/1x.webp", added.Args["imageUrl"]);
        Assert.Equal("Cuebot Editor", added.Args["sevenTvUser"]);
        Assert.False(added.Args.ContainsKey("user")); // never mistaken for a Kick user
        Assert.Equal("Kick", added.Args["platform"]);
        Assert.Equal(KickEventTypes.SevenTvEmoteRemoved, events[1].Type);
        Assert.Equal("KEKW", events[1].Args["emoteName"]);
    }

    private const string SevenTvChange = """
        {"id":"SET1","kind":3,"actor":{"id":"A1","username":"editor","display_name":"Cuebot Editor"},
         "pushed":[{"key":"emotes","index":3,"type":"object","value":{"id":"E1","name":"catJAM","flags":0,"data":{"id":"E1","name":"catJAM","animated":true,"host":{"url":"//cdn.7tv.app/emote/E1","files":[]}}}}],
         "pulled":[{"key":"emotes","index":1,"type":"object","old_value":{"id":"E2","name":"KEKW","data":{"host":{"url":"//cdn.7tv.app/emote/E2"}}}}],
         "updated":[{"key":"name","old_value":"old","value":"new"}]}
        """;

    [Fact]
    public async Task Connect_watches_the_kick_channels_7tv_set_and_publishes_triggers()
    {
        await _service.ConnectAsync();
        var conn = await _sevenTv.NextConnectionAsync();
        conn.Push("""{"op":1,"t":0,"d":{"heartbeat_interval":30000,"session_id":"s","subscription_limit":500}}""");
        var subscribe = await conn.NextSentAsync();
        Assert.Equal(SevenTvHosts.Events, conn.Url); // set once connected; the fake hands the connection over earlier
        Assert.Equal(35, subscribe.GetProperty("op").GetInt32());
        Assert.Equal("emote_set.update", subscribe.GetProperty("d").GetProperty("type").GetString());
        Assert.Equal("SET1", subscribe.GetProperty("d").GetProperty("condition").GetProperty("object_id").GetString());
        await Wait.UntilAsync(() => _service.SevenTv!.IsConnected, "7TV connected");
        Assert.Contains("SET1", _service.SevenTvStatus);

        var refreshed = 0;
        _service.EmoteRefresh = _ => { Interlocked.Increment(ref refreshed); return Task.CompletedTask; };
        conn.Push("""{"op":2,"d":{"count":1}}""");
        conn.Push($$$"""{"op":0,"d":{"type":"emote_set.update","body":{{{SevenTvChange}}}}}""");
        conn.Push($$$"""{"op":0,"d":{"type":"emote_set.update","body":{{{SevenTvChange.Replace("\"SET1\"", "\"OTHER\"")}}}}}"""); // another set
        await Wait.UntilAsync(() => _published.Any(e => e.Type == KickEventTypes.SevenTvEmoteRemoved)
                                    && _published.Any(e => e.Type == KickEventTypes.SevenTvEmoteAdded), "7TV addition and removal");
        Assert.Single(_published, e => e.Type == KickEventTypes.SevenTvEmoteAdded);
        await Wait.UntilAsync(() => Volatile.Read(ref refreshed) == 1, "emote cache refresh");

        // While the EventAPI is connected, the emote cache's own diff is not reported a second time.
        _bus.Publish(new BotEvent(EmoteService.EmoteAddedEvent, new Dictionary<string, object?>
            { ["provider"] = "7TV", ["platform"] = "Kick", ["emote"] = "Other", ["emoteId"] = "E9" }));
        await Task.Delay(50);
        Assert.Single(_published, e => e.Type == KickEventTypes.SevenTvEmoteAdded);

        // 7TV asks clients to reconnect from time to time.
        conn.Push("""{"op":4,"d":{"reason":"restart"}}""");
        var again = await _sevenTv.NextConnectionAsync();
        Assert.NotSame(conn, again);
    }

    [Fact]
    public async Task Without_the_event_api_emote_cache_changes_become_kick_7tv_triggers_once()
    {
        _settings.Value.UseSevenTvEvents = false;
        await _service.ConnectAsync();
        Assert.Equal("Off", _service.SevenTvStatus);
        void Publish(string type, string provider, string platform, string emote) => _bus.Publish(new BotEvent(type,
            new Dictionary<string, object?> { ["provider"] = provider, ["platform"] = platform, ["emote"] = emote, ["emoteId"] = "id-" + emote }));
        Publish(EmoteService.EmoteAddedEvent, "7TV", "Kick", "Pog");
        Publish(EmoteService.EmoteAddedEvent, "7TV", "Kick", "Pog");      // same change twice: one trigger
        Publish(EmoteService.EmoteAddedEvent, "BTTV", "Kick", "Other");   // other provider
        Publish(EmoteService.EmoteAddedEvent, "7TV", "Twitch", "Other");  // other platform
        Publish(EmoteService.EmoteRemovedEvent, "7TV", "Kick", "Gone");
        var added = Assert.Single(_published, e => e.Type == KickEventTypes.SevenTvEmoteAdded);
        Assert.Equal("Pog", added.Args["emoteName"]);
        Assert.Equal("Gone", Assert.Single(_published, e => e.Type == KickEventTypes.SevenTvEmoteRemoved).Args["emoteName"]);
        Assert.Equal(0, _sevenTv.Attempts);
    }

    [Fact]
    public void Seven_tv_triggers_filter_by_emote_name()
    {
        var trigger = new KickTriggers().GetTriggers().OfType<EventTriggerType>().Single(t => t.TypeId == KickEventTypes.SevenTvEmoteAdded);
        Assert.Equal("Kick", trigger.Category);
        Assert.Equal([KickEventTypes.SevenTvEmoteAdded], trigger.EventTypes);
        var e = new BotEvent(KickEventTypes.SevenTvEmoteAdded, new Dictionary<string, object?> { ["emoteName"] = "catJAM" });
        bool Matches(string emote) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["emote"] = emote }), new Dictionary<string, object?>());
        Assert.True(Matches(""));
        Assert.True(Matches("catJAM"));
        Assert.False(Matches("catjam")); // emote codes are case-sensitive
    }

    [Fact]
    public void Emote_channel_source_reports_the_broadcaster_user_id()
    {
        var source = new KickEmoteChannelSource(_service);
        Assert.Equal("Kick", source.Platform);
        Assert.Equal("100", source.ChannelId);
        _settings.Value.Broadcaster.UserId = 0;
        Assert.Null(source.ChannelId);
    }
}
