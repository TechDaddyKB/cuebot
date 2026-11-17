using System.Text.Json;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

/// <summary>
/// Phase 3 of the unified mod chat ("Twitch depth"): channel.moderate v2 into the mod log, AutoMod held messages,
/// Shield Mode, suspicious users, unban requests and blocked terms. Payloads follow the shapes of Twitch's EventSub
/// reference examples.
/// </summary>
public sealed class ModerationDepthTests : IDisposable
{
    private const string B = """ "broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer" """;
    private const string Src = """ "source_broadcaster_user_id":null,"source_broadcaster_user_login":null,"source_broadcaster_user_name":null """;
    private const string M = """ "moderator_user_id":"999","moderator_user_login":"mod_login","moderator_user_name":"Mod" """;
    private const string U = """ "user_id":"1234","user_login":"cool_user","user_name":"Cool_User" """;

    private readonly ApiHarness _h = new();
    private bool _connected = true;

    public void Dispose() => _h.Dispose();

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>A channel.moderate v2 event: every detail object is present and null except the action's own.</summary>
    private static string Moderate(string action, string? detail = null, string source = Src)
    {
        var details = new[]
        {
            "followers", "slow", "vip", "unvip", "mod", "unmod", "ban", "unban", "timeout", "untimeout", "raid", "unraid", "delete",
            "automod_terms", "unban_request", "warn", "shared_chat_ban", "shared_chat_unban", "shared_chat_timeout",
            "shared_chat_untimeout", "shared_chat_delete",
        };
        var fields = string.Join(",", details.Select(d => $"\"{d}\":" + (detail is not null && DetailKey(action) == d ? detail : "null")));
        return "{" + string.Join(",", B, source, M, $"\"action\":\"{action}\"", fields) + "}";
    }

    private static string DetailKey(string action) => action switch
    {
        "add_blocked_term" or "remove_blocked_term" or "add_permitted_term" or "remove_permitted_term" => "automod_terms",
        "approve_unban_request" or "deny_unban_request" => "unban_request",
        _ => action,
    };

    private static BotEvent Logged(string json) =>
        EventSubMapper.ModerationAction(Json(json)) ?? throw new Xunit.Sdk.XunitException("not mapped");

    // ---------------------------------------------------------------- channel.moderate v2

    [Theory]
    [InlineData("ban", "{" + U + ",\"reason\":\"spam\"}", "ban", "spam")]
    [InlineData("unban", "{" + U + "}", "unban", "")]
    [InlineData("untimeout", "{" + U + "}", "unban", "timeout lifted")]
    [InlineData("warn", "{" + U + ",\"reason\":\"be nice\",\"chat_rules_cited\":[\"Rule 1\"]}", "warn", "be nice")]
    [InlineData("vip", "{" + U + "}", "vip", "")]
    [InlineData("unvip", "{" + U + "}", "unvip", "")]
    [InlineData("mod", "{" + U + "}", "mod", "")]
    [InlineData("unmod", "{" + U + "}", "unmod", "")]
    [InlineData("raid", "{" + U + ",\"viewer_count\":42}", "raid", "42 viewers")]
    [InlineData("unraid", "{" + U + "}", "unraid", "")]
    [InlineData("approve_unban_request", "{\"is_approved\":true," + U + ",\"moderator_message\":\"one more chance\"}", "unban-request-approve", "one more chance")]
    [InlineData("deny_unban_request", "{\"is_approved\":false," + U + ",\"moderator_message\":\"no\"}", "unban-request-deny", "no")]
    public void Channel_moderate_actions_about_a_person_name_the_moderator_and_the_person(string action, string detail, string logName, string reason)
    {
        var e = Logged(Moderate(action, detail));
        Assert.Equal(NeutralChatEvents.ModerationAction, e.Type);
        Assert.Equal(ChatHub.ModerationActionEvent, e.Type);
        Assert.Equal(("Twitch", logName, "Mod", "999"), (e.Args["platform"], e.Args["action"], e.Args["moderator"], e.Args["moderatorUserId"]));
        Assert.Equal(("1234", "Cool_User", "cool_user"), (e.Args["userId"], e.Args["user"], e.Args["userName"]));
        Assert.Equal(reason, e.Args.GetValueOrDefault("reason") ?? "");
    }

    [Fact]
    public void Channel_moderate_timeout_delete_and_clear()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToString("o");
        var timeout = Logged(Moderate("timeout", $$"""{{{U}},"reason":"calm down","expires_at":"{{expires}}"}"""));
        Assert.Equal("timeout", timeout.Args["action"]);
        Assert.InRange((int)timeout.Args["duration"]!, 590, 600);

        var delete = Logged(Moderate("delete", "{" + U + ""","message_id":"m-1","message_body":"buy followers"}"""));
        Assert.Equal(("delete", "m-1", "buy followers"), (delete.Args["action"], delete.Args["messageId"], delete.Args["message"]));

        Assert.Equal("clear", Logged(Moderate("clear")).Args["action"]);
    }

    [Theory]
    [InlineData("slow", """{"wait_time_seconds":30}""", "Slow mode on (30)")]
    [InlineData("slowoff", null, "Slow mode off")]
    [InlineData("followers", """{"follow_duration_minutes":10}""", "Followers-only on (10)")]
    [InlineData("followersoff", null, "Followers-only off")]
    [InlineData("emoteonly", null, "Emote-only on")]
    [InlineData("emoteonlyoff", null, "Emote-only off")]
    [InlineData("subscribers", null, "Subscribers-only on")]
    [InlineData("subscribersoff", null, "Subscribers-only off")]
    [InlineData("uniquechat", null, "Unique chat on")]
    [InlineData("uniquechatoff", null, "Unique chat off")]
    public void Channel_moderate_chat_modes_say_what_changed(string action, string? detail, string reason)
    {
        var e = Logged(Moderate(action, detail));
        Assert.Equal(("mode", reason), (e.Args["action"], e.Args["reason"]));
    }

    [Theory]
    [InlineData("add_blocked_term", "term-add")]
    [InlineData("remove_blocked_term", "term-remove")]
    [InlineData("add_permitted_term", "permit-add")]
    [InlineData("remove_permitted_term", "permit-remove")]
    public void Channel_moderate_automod_terms(string action, string logName)
    {
        var list = action.Contains("blocked", StringComparison.Ordinal) ? "blocked" : "permitted";
        var e = Logged(Moderate(action, $$"""{"action":"{{(action.StartsWith("add", StringComparison.Ordinal) ? "add" : "remove")}}","list":"{{list}}","terms":["badword","worse"],"from_automod":false}"""));
        Assert.Equal((logName, "badword, worse"), (e.Args["action"], e.Args["reason"]));
    }

    [Fact]
    public void Channel_moderate_shared_chat_actions_are_logged_with_the_other_channel()
    {
        const string source = """ "source_broadcaster_user_id":"2002","source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner" """;
        var ban = Logged(Moderate("shared_chat_ban", "{" + U + ",\"reason\":\"spam\"}", source));
        Assert.Equal(("shared-ban", "in Partner's chat: spam", "2002"), (ban.Args["action"], ban.Args["reason"], ban.Args["channelId"]));
        Assert.Equal("shared-unban", Logged(Moderate("shared_chat_untimeout", "{" + U + "}", source)).Args["action"]);
        var deleted = Logged(Moderate("shared_chat_delete", "{" + U + ",\"message_id\":\"m-5\",\"message_body\":\"rude\"}", source));
        Assert.Equal(("shared-delete", "m-5", "rude"), (deleted.Args["action"], deleted.Args["messageId"], deleted.Args["message"]));
        Assert.Null(EventSubMapper.ModerationAction(Json(Moderate("some_new_action"))));
    }

    [Fact]
    public void Router_publishes_the_mod_log_event_and_marks_older_events_when_the_feed_runs()
    {
        var feed = false;
        var router = new TwitchEventRouter(new FakeTimeProvider(), () => "1337", () => "", new SentMessageTracker(), moderationFeed: () => feed);

        // An own-channel ban: channel.moderate maps only to the mod log event (channel.ban gives the Twitch event).
        var events = router.Route("channel.moderate", Json(Moderate("ban", "{" + U + ",\"reason\":\"spam\"}")));
        Assert.Equal([NeutralChatEvents.ModerationAction], events.Select(e => e.Type));
        // A shared chat ban: the Twitch event and the mod log event.
        const string source = """ "source_broadcaster_user_id":"2002","source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner" """;
        events = router.Route("channel.moderate", Json(Moderate("shared_chat_ban", "{" + U + ",\"reason\":\"x\"}", source)));
        Assert.Equal([TwitchEventTypes.SharedChatUserBanned, NeutralChatEvents.ModerationAction], events.Select(e => e.Type));

        const string ban = "{" + B + "," + U + "," + M + ""","reason":"spam","banned_at":"2026-01-01T12:00:00Z","ends_at":null,"is_permanent":true}""";
        var neutral = router.Route("channel.ban", Json(ban)).Single(e => e.Type == NeutralChatEvents.UserBanned);
        Assert.False(neutral.Args.ContainsKey("modLogged"));
        feed = true;
        neutral = router.Route("channel.ban", Json(ban)).Single(e => e.Type == NeutralChatEvents.UserBanned);
        Assert.Equal(true, neutral.Args["modLogged"]);
    }

    [Fact]
    public void Router_logs_shield_mode_moderator_suspicious_changes_and_held_decisions()
    {
        var router = new TwitchEventRouter(new FakeTimeProvider(), () => "1337", () => "", new SentMessageTracker());
        var shield = router.Route("channel.shield_mode.begin", Json("{" + B + "," + M + ",\"started_at\":\"2026-11-16T17:00:03Z\"}"));
        var logged = shield.Single(e => e.Type == NeutralChatEvents.ModerationAction);
        Assert.Equal(("shield", "Shield Mode on", "Mod"), (logged.Args["action"], logged.Args["reason"], logged.Args["moderator"]));

        var suspicious = router.Route("channel.suspicious_user.update", Json("{" + B + "," + U + "," + M + ",\"low_trust_status\":\"restricted\"}"));
        logged = suspicious.Single(e => e.Type == NeutralChatEvents.ModerationAction);
        Assert.Equal(("suspicious-restrict", "1234", "Mod"), (logged.Args["action"], logged.Args["userId"], logged.Args["moderator"]));
        // Twitch's own detection names no moderator: no mod log row.
        Assert.DoesNotContain(router.Route("channel.suspicious_user.update",
                Json("{" + B + "," + U + ""","moderator_user_id":null,"moderator_user_login":null,"moderator_user_name":null,"low_trust_status":"active_monitoring"}""")),
            e => e.Type == NeutralChatEvents.ModerationAction);

        var update = router.Route("automod.message.update", Json("{" + B + "," + U + "," + M +
            ""","message_id":"held-1","message":{"text":"you are bad","fragments":[]},"reason":"automod","automod":{"category":"aggressive","level":2},"blocked_term":null,"status":"Denied","held_at":"2026-11-16T17:00:00Z"}"""));
        logged = update.Single(e => e.Type == NeutralChatEvents.ModerationAction);
        Assert.Equal(("held-deny", "held-1", "you are bad"), (logged.Args["action"], logged.Args["messageId"], logged.Args["message"]));
    }

    // ---------------------------------------------------------------- queues

    private TwitchChatModerator Moderator() => new(() => _h.Api, () => _connected);

    private static BotEvent Mapped(string type, string json) =>
        EventSubMapper.Map(type, Json(json), "1337") ?? throw new Xunit.Sdk.XunitException(type);

    private const string HoldV2 = "{" + B + "," + U + """
        ,"message_id":"held-1","message":{"text":"you are bad","fragments":[{"type":"text","text":"you are bad","cheermote":null,"emote":null}]},
        "reason":"automod","automod":{"category":"aggressive","level":2,"boundaries":[{"start_pos":8,"end_pos":10}]},"blocked_term":null,
        "held_at":"2026-11-16T17:00:00Z"}
        """;

    [Fact]
    public async Task Held_messages_appear_and_are_allowed_or_denied_through_helix()
    {
        var moderator = Moderator();
        var changes = new List<ModerationQueueChange>();
        moderator.Changed += changes.Add;

        moderator.OnEvent(Mapped("automod.message.hold", HoldV2));
        moderator.OnEvent(Mapped("automod.message.hold", "{" + B + "," + """
            "user_id":"55","user_login":"linker","user_name":"Linker","message_id":"held-2","message":{"text":"see example.com","fragments":[]},
            "reason":"blocked_term","automod":null,"blocked_term":{"terms_found":[{"term_id":"t-1","boundary":{"start_pos":4,"end_pos":14},"owner_broadcaster_user_id":"1337","owner_broadcaster_user_login":"streamer","owner_broadcaster_user_name":"Streamer"}]},
            "held_at":"2026-11-16T17:00:05Z"}
            """));
        var held = moderator.HeldMessages;
        Assert.Equal(["held-1", "held-2"], held.Select(h => h.Id));
        Assert.Equal(("1234", "Cool_User", "cool_user", "you are bad", "AutoMod: aggressive, level 2"),
            (held[0].UserId, held[0].UserName, held[0].UserLogin, held[0].Text, held[0].Reason));
        Assert.Equal("Blocked term", held[1].Reason);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 17, 0, 0, TimeSpan.Zero), held[0].HeldAt);
        Assert.Equal(2, changes.Count(c => c.Kind == ModerationQueueKind.HeldMessages));

        _h.On("POST /helix/moderation/automod/message", "");
        Assert.True((await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.ApproveHeld) { MessageId = "held-1" })).Ok);
        Assert.Equal(("1337", "held-1", "ALLOW"), (_h.LastBody.GetProperty("user_id").GetString(), _h.LastBody.GetProperty("msg_id").GetString(),
            _h.LastBody.GetProperty("action").GetString()));
        Assert.Equal(HeldMessageStatus.Approved, moderator.HeldMessages[0].Status);

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.DenyHeld) { MessageId = "held-2" });
        Assert.Equal("DENY", _h.LastBody.GetProperty("action").GetString());

        // Twitch's update names the moderator who decided (here someone on the website).
        moderator.OnEvent(Mapped("automod.message.update", "{" + B + "," + U + "," + M + """
            ,"message_id":"held-1","message":{"text":"you are bad","fragments":[]},"reason":"automod","automod":{"category":"aggressive","level":2},
            "blocked_term":null,"status":"Approved","held_at":"2026-11-16T17:00:00Z"}
            """));
        Assert.Equal(("Mod", HeldMessageStatus.Approved), (moderator.HeldMessages[0].DecidedBy, moderator.HeldMessages[0].Status));
        Assert.All(moderator.HeldMessages, h => Assert.False(h.IsPending));
    }

    [Fact]
    public async Task Shield_mode_follows_events_toggles_and_refreshes()
    {
        var moderator = Moderator();
        Assert.Null(moderator.ShieldMode);
        moderator.OnEvent(Mapped("channel.shield_mode.begin", "{" + B + "," + M + ",\"started_at\":\"2026-11-16T17:00:03.17106713Z\"}"));
        Assert.Equal((true, "Mod"), (moderator.ShieldMode!.IsActive, moderator.ShieldMode.ModeratorName));
        moderator.OnEvent(Mapped("channel.shield_mode.end", "{" + B + "," + M + ",\"ended_at\":\"2026-11-16T18:00:00Z\"}"));
        Assert.False(moderator.ShieldMode!.IsActive);

        _h.On("PUT /helix/moderation/shield_mode",
            """{"data":[{"is_active":true,"moderator_id":"1337","moderator_login":"streamer","moderator_name":"Streamer","last_activated_at":"2026-11-16T18:05:00Z"}]}""");
        Assert.True((await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.ShieldMode) { Enabled = true })).Ok);
        Assert.True(_h.LastBody.GetProperty("is_active").GetBoolean());
        Assert.Equal(("1337", "1337"), (_h.LastQuery["broadcaster_id"], _h.LastQuery["moderator_id"]));
        Assert.Equal((true, "Streamer"), (moderator.ShieldMode!.IsActive, moderator.ShieldMode.ModeratorName));

        _h.On("GET /helix/moderation/shield_mode",
            """{"data":[{"is_active":false,"moderator_id":"999","moderator_login":"mod_login","moderator_name":"Mod","last_activated_at":"2026-11-16T18:05:00Z"}]}""");
        await moderator.RefreshAsync();
        Assert.False(moderator.ShieldMode!.IsActive);
    }

    [Fact]
    public async Task Suspicious_users_are_flagged_from_events_and_set_or_cleared_through_helix()
    {
        var moderator = Moderator();
        moderator.OnEvent(Mapped("channel.suspicious_user.message", "{" + B + "," + U + """
            ,"low_trust_status":"active_monitoring","shared_ban_channel_ids":["100","200"],"types":["ban_evader_detector"],
            "ban_evasion_evaluation":"likely","message":{"message_id":"m-9","text":"hi again","fragments":[]}}
            """));
        var flagged = moderator.SuspiciousUser("1234");
        Assert.Equal(SuspiciousLevel.Monitored, flagged!.Level);
        Assert.Equal("likely ban evader, banned in 2 shared channels", flagged.Why);

        moderator.OnEvent(Mapped("channel.suspicious_user.update", "{" + B + "," + U + "," + M + ",\"low_trust_status\":\"no_treatment\"}"));
        Assert.Null(moderator.SuspiciousUser("1234"));

        _h.On("POST /helix/moderation/suspicious_users", """{"data":[{"user_id":"1234","broadcaster_id":"1337","moderator_id":"1337","updated_at":"2026-11-16T18:00:00Z","status":"RESTRICTED","types":["MANUALLY_ADDED"]}]}""");
        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.RestrictUser) { UserId = "1234", UserName = "Cool_User" });
        Assert.Equal(("1234", "RESTRICTED"), (_h.LastBody.GetProperty("user_id").GetString(), _h.LastBody.GetProperty("status").GetString()));
        Assert.Equal(SuspiciousLevel.Restricted, moderator.SuspiciousUser("1234")!.Level);

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.MonitorUser) { UserId = "1234" });
        Assert.Equal("ACTIVE_MONITORING", _h.LastBody.GetProperty("status").GetString());

        _h.On("DELETE /helix/moderation/suspicious_users", """{"data":[{"user_id":"1234","status":"NO_TREATMENT","types":[]}]}""");
        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.ClearSuspicious) { UserId = "1234" });
        Assert.Equal(("DELETE", "1234"), (_h.Last.Method.Method, _h.LastQuery["user_id"]));
        Assert.Null(moderator.SuspiciousUser("1234"));
    }

    [Fact]
    public async Task Unban_requests_are_listed_and_resolved_with_an_answer()
    {
        var moderator = Moderator();
        _h.On("GET /helix/moderation/unban_requests", """
            {"data":[{"id":"92af127c","broadcaster_id":"1337","broadcaster_login":"streamer","broadcaster_name":"Streamer","moderator_id":null,
              "moderator_login":null,"moderator_name":null,"user_id":"1234","user_login":"cool_user","user_name":"Cool_User",
              "text":"I am sorry","status":"pending","created_at":"2026-11-16T10:00:00Z","resolved_at":null,"resolution_text":null}],
             "pagination":{}}
            """);
        var request = Assert.Single(await moderator.GetUnbanRequestsAsync());
        Assert.Equal(("92af127c", "1234", "Cool_User", "I am sorry", UnbanRequestStatus.Pending),
            (request.Id, request.UserId, request.UserName, request.Text, request.Status));
        Assert.Equal("pending", _h.LastQuery["status"]);

        var changes = new List<ModerationQueueChange>();
        moderator.Changed += changes.Add;
        _h.On("PATCH /helix/moderation/unban_requests", """{"data":[{"id":"92af127c","status":"denied","user_id":"1234"}]}""");
        Assert.True((await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.DenyUnbanRequest)
        {
            ItemId = "92af127c", UserId = "1234", Reason = "Not this time",
        })).Ok);
        Assert.Equal(("92af127c", "denied", "Not this time"),
            (_h.LastQuery["unban_request_id"], _h.LastQuery["status"], _h.LastQuery["resolution_text"]));
        Assert.Contains(changes, c => c.Kind == ModerationQueueKind.UnbanRequests);

        moderator.OnEvent(Mapped("channel.unban_request.create", "{" + B + "," + U + ",\"id\":\"60\",\"text\":\"again\",\"created_at\":\"2026-11-16T11:00:00Z\"}"));
        Assert.Equal("60", changes[^1].ItemId);
    }

    [Fact]
    public async Task Blocked_terms_are_listed_added_and_removed()
    {
        var moderator = Moderator();
        _h.On("GET /helix/moderation/blocked_terms",
            """{"data":[{"broadcaster_id":"1337","moderator_id":"1337","id":"t-2","text":"zebra","created_at":"2026-11-16T10:00:00Z"},{"id":"t-1","text":"Apple"}],"pagination":{}}""");
        Assert.Equal(["Apple", "zebra"], (await moderator.GetBlockedTermsAsync()).Select(t => t.Text));

        _h.On("POST /helix/moderation/blocked_terms", """{"data":[{"broadcaster_id":"1337","moderator_id":"1337","id":"t-3","text":"spoiler"}]}""");
        var added = await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.AddBlockedTerm) { Term = " spoiler " });
        Assert.Equal((true, "t-3"), (added.Ok, added.PlatformRef));
        Assert.Equal("spoiler", _h.LastBody.GetProperty("text").GetString());

        Assert.False((await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.AddBlockedTerm) { Term = "x" })).Ok);

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.RemoveBlockedTerm) { ItemId = "t-3" });
        Assert.Equal(("DELETE", "t-3"), (_h.Last.Method.Method, _h.LastQuery["id"]));
    }

    [Fact]
    public void Queue_tools_need_their_scope_and_say_to_sign_in_again()
    {
        var moderator = Moderator();
        foreach (var action in ModerationCapability.AllActions.Where(ModerationCapability.IsQueueTool))
            Assert.Equal(ModerationToolState.Works, moderator.Capability(action).State);

        _h.Settings.Value.Scopes = [TwitchScopes.ReadChat, TwitchScopes.ManageBannedUsers];
        foreach (var action in ModerationCapability.AllActions.Where(ModerationCapability.IsQueueTool))
        {
            var capability = moderator.Capability(action);
            Assert.Equal(ModerationToolState.NotAvailable, capability.State);
            Assert.StartsWith("Sign in to Twitch again and allow", capability.Note);
        }
        Assert.Contains("Shield mode", moderator.Capability(ModerationAction.ShieldMode).Note);
        Assert.Contains("AutoMod held messages", moderator.Capability(ModerationAction.ApproveHeld).Note);
    }

    // ---------------------------------------------------------------- end to end: the mod log

    [Fact]
    public async Task Cuebots_own_ban_is_logged_once_and_a_website_ban_names_the_mod()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-twitch-modlog-").FullName;
        var log = new AppLog();
        var bus = new EventBus(log);
        using var hub = new ChatHub(bus, log, [], []);
        using var modLog = new ModLog(Path.Combine(dir, ModLog.FileName));
        var moderator = Moderator();
        using var service = new ModerationService(new ChatModerationService([moderator], []), modLog, hub);
        var router = new TwitchEventRouter(TimeProvider.System, () => "1337", () => "", new SentMessageTracker(), moderationFeed: () => true);
        void Deliver(string type, string json)
        {
            foreach (var e in router.Route(type, Json(json))) bus.Publish(e);
        }

        _h.On("POST /helix/moderation/bans", """{"data":[{}]}""");
        var result = await service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Ban)
        {
            TargetUserId = "1234", TargetName = "Cool_User", Reason = "spam",
        }, ModActor.Streamer);
        Assert.True(result.Ok);
        // Twitch reports Cuebot's own ban twice (channel.ban and channel.moderate as the broadcaster).
        const string streamerAsMod = """ "moderator_user_id":"1337","moderator_user_login":"streamer","moderator_user_name":"Streamer" """;
        Deliver("channel.ban", "{" + B + "," + U + "," + streamerAsMod + ""","reason":"spam","banned_at":"2026-11-16T12:00:00Z","ends_at":null,"is_permanent":true}""");
        Deliver("channel.moderate", "{" + string.Join(",", B, Src, streamerAsMod, "\"action\":\"ban\"", "\"ban\":{" + U + ",\"reason\":\"spam\"}") + "}");
        Assert.Single(modLog.Recent());

        // A mod unbans on the website: logged with their name, and the ban is marked lifted.
        Deliver("channel.moderate", Moderate("unban", "{" + U + "}"));
        var rows = modLog.Recent();
        Assert.Equal(2, rows.Count);
        Assert.Equal((ModActorKind.External, "Mod", "unban"), (rows[0].ActorKind, rows[0].ActorName, rows[0].Action));
        Assert.Equal(rows[0].Id, modLog.Get(rows[1].Id)!.UndoneBy);

        // A chat mode switched on the website.
        Deliver("channel.moderate", Moderate("slow", """{"wait_time_seconds":10}"""));
        Assert.Equal(("mode", "Slow mode on (10)"), (modLog.Recent()[0].Action, modLog.Recent()[0].Reason));
        modLog.Dispose();   // Windows cannot delete the database while it is open
        Directory.Delete(dir, true);
    }
}
