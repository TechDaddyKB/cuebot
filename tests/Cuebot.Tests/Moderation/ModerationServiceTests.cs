using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.Moderation;

/// <summary>ModerationService: protection, rate limits, reason prefix, quota, unban pairing, external actions, ChatHub events.</summary>
public sealed class ModerationServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-moderation-").FullName;
    private readonly ManualClock _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly AppLog _appLog = new();
    private readonly EventBus _bus;
    private readonly ChatHub _hub;
    private readonly ModLog _log;
    private readonly FakeModerator _twitch = new("Twitch");
    private readonly FakeModerator _youtube = new("YouTube") { QuotaCost = 50 };
    private readonly FakeQuota _quota = new();
    private readonly FakeRoles _roles = new();
    private readonly ModerationSettings _settings = new();
    private readonly ModerationService _service;

    public ModerationServiceTests()
    {
        _bus = new EventBus(_appLog);
        _hub = new ChatHub(_bus, _appLog, [new Adapter()], []);
        _log = new ModLog(Path.Combine(_dir, ModLog.FileName), _time);
        _service = new ModerationService(new ChatModerationService([_twitch, _youtube], []), _log, _hub,
            [new FakeProtected()], [_quota], [_roles], () => _settings, _appLog, _time);
    }

    public void Dispose()
    {
        _service.Dispose();
        _hub.Dispose();
        _log.Dispose();
        _appLog.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static readonly ModActor Sam = ModActor.Member("member-1", "Sam", "device-1");

    private static ModerationCommand Ban(string user = "u1", string platform = "Twitch", string? reason = "spam") =>
        new(platform, ModerationAction.Ban) { TargetUserId = user, TargetName = "Bob", Reason = reason };

    [Fact]
    public async Task Streamer_bans_are_sent_and_logged()
    {
        var result = await _service.ExecuteAsync(Ban(), ModActor.Streamer);

        Assert.True(result.Ok);
        Assert.Equal("Banned Bob", result.Message);
        Assert.Equal(["Ban u1 spam"], _twitch.Calls);
        var row = Assert.Single(_log.Recent());
        Assert.Equal(("ban", ModActorKind.Streamer, "u1", "spam", ModResult.Ok), (row.Action, row.ActorKind, row.TargetUserId, row.Reason, row.Result));
    }

    [Fact]
    public async Task Members_cannot_act_against_the_broadcaster_mods_vips_portal_members_or_bots_but_the_streamer_can()
    {
        var protectedTargets = new[]
        {
            Ban() with { TargetRoles = ChatRoles.Broadcaster },
            Ban() with { TargetRoles = ChatRoles.Moderator },
            Ban() with { TargetRoles = ChatRoles.Vip },
            Ban("bot-1"),
            Ban("mod-by-lookup"),
        };
        foreach (var command in protectedTargets)
        {
            var refused = await _service.ExecuteAsync(command, Sam);
            Assert.Equal(ModResult.Skipped, refused.Result);
            Assert.Contains("Only the streamer", refused.Message);
        }
        Assert.Empty(_twitch.Calls);
        Assert.All(_log.Recent(), r => Assert.Equal(ModResult.Skipped, r.Result));

        Assert.True((await _service.ExecuteAsync(Ban("bot-1"), ModActor.Streamer)).Ok);
        // An unknown moderator lookup (null) does not protect.
        Assert.True((await _service.ExecuteAsync(Ban("unknown"), Sam)).Ok);
    }

    [Fact]
    public async Task Members_get_ten_bans_and_thirty_timeouts_per_ten_minutes()
    {
        for (var i = 0; i < 10; i++) Assert.True((await _service.ExecuteAsync(Ban("u" + i), Sam)).Ok);
        var eleventh = await _service.ExecuteAsync(Ban("u11"), Sam);
        Assert.Equal(ModResult.Skipped, eleventh.Result);
        Assert.Contains("10 bans", eleventh.Message);
        // Another member and the streamer are not limited by Sam's count.
        Assert.True((await _service.ExecuteAsync(Ban("u12"), ModActor.Member("member-2", "Alex"))).Ok);
        Assert.True((await _service.ExecuteAsync(Ban("u13"), ModActor.Streamer)).Ok);

        for (var i = 0; i < 30; i++)
            Assert.True((await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Timeout) { TargetUserId = "t" + i }, Sam)).Ok);
        Assert.Equal(ModResult.Skipped,
            (await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Timeout) { TargetUserId = "t31" }, Sam)).Result);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True((await _service.ExecuteAsync(Ban("u14"), Sam)).Ok);
    }

    [Fact]
    public async Task Members_reasons_carry_their_name_on_twitch_and_kick_trimmed_to_the_limit()
    {
        _twitch.MaxReason = 20;
        await _service.ExecuteAsync(Ban(reason: "spam links again and again"), Sam);
        Assert.Equal("Ban u1 [mod: Sam] spam link", _twitch.Calls[^1]);
        Assert.Equal("[mod: Sam] spam link", _log.Recent()[0].Reason);

        await _service.ExecuteAsync(Ban("u2", reason: null), Sam);
        Assert.Equal("Ban u2 [mod: Sam]", _twitch.Calls[^1]);

        await _service.ExecuteAsync(Ban("u3", "YouTube"), Sam);
        Assert.Equal("Ban u3 spam", _youtube.Calls[^1]);   // YouTube keeps no reason; no prefix
        await _service.ExecuteAsync(Ban("u4"), ModActor.Streamer);
        Assert.Equal("Ban u4 spam", _twitch.Calls[^1]);
    }

    [Fact]
    public async Task Youtube_actions_stop_at_the_quota_reserve()
    {
        _quota.Left = 2_040;
        var result = await _service.ExecuteAsync(Ban("u1", "YouTube"), ModActor.Streamer);
        Assert.Equal(ModResult.Skipped, result.Result);
        Assert.Contains("quota", result.Message);
        Assert.Empty(_youtube.Calls);
        Assert.Equal(ModResult.Skipped, _log.Recent()[0].Result);

        _quota.Left = 2_050;
        Assert.True((await _service.ExecuteAsync(Ban("u1", "YouTube"), ModActor.Streamer)).Ok);
    }

    [Fact]
    public async Task Unban_sends_the_stored_ban_id_and_marks_the_ban_undone()
    {
        _youtube.NextRef = "BAN-ID-7";
        var ban = await _service.ExecuteAsync(Ban("u1", "YouTube") with { ChannelId = "chat-2" }, ModActor.Streamer);
        Assert.Equal("BAN-ID-7", ban.Entry!.PlatformRef);

        var unban = await _service.ExecuteAsync(new ModerationCommand("YouTube", ModerationAction.Unban) { TargetUserId = "u1" }, Sam);

        Assert.True(unban.Ok);
        Assert.Equal("Unban u1 ref=BAN-ID-7 chat=chat-2", _youtube.Calls[^1]);
        Assert.Equal(ban.Entry.Id, unban.Entry!.Undoes);
        Assert.Equal(unban.Entry.Id, _log.Get(ban.Entry.Id)!.UndoneBy);
    }

    [Fact]
    public async Task Timeouts_report_and_log_the_duration_the_platform_applies()
    {
        _twitch.Unit = TimeSpan.FromMinutes(1);
        var (applied, note) = _service.PreviewDuration("Twitch", TimeSpan.FromSeconds(90));
        Assert.Equal((TimeSpan.FromMinutes(2), "Twitch: 2 min, rounded up from 1 min 30 s"), (applied, note));

        var result = await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Timeout)
        {
            TargetUserId = "u1", TargetName = "Bob", Duration = TimeSpan.FromSeconds(90),
        }, ModActor.Streamer);
        Assert.Equal("Timed out Bob for 2 min (asked for 1 min 30 s)", result.Message);
        Assert.Equal((90, 120), (_log.Recent()[0].DurationRequestedSeconds, _log.Recent()[0].DurationAppliedSeconds));
    }

    [Fact]
    public async Task Website_and_unavailable_tools_are_not_sent_and_say_why()
    {
        _twitch.Website = "https://example.test/mod";
        var web = await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.SlowMode), ModActor.Streamer);
        Assert.Equal((ModResult.Skipped, "https://example.test/mod"), (web.Result, web.WebsiteUrl));

        var none = await _service.ExecuteAsync(new ModerationCommand("Rumble", ModerationAction.Ban), ModActor.Streamer);
        Assert.Equal(ModResult.Skipped, none.Result);
        Assert.Contains("cannot moderate Rumble", none.Message);
        Assert.Empty(_log.Recent());
    }

    [Fact]
    public async Task Platform_errors_are_logged_as_failed()
    {
        _twitch.Throw = true;
        var result = await _service.ExecuteAsync(Ban(), ModActor.Streamer);
        Assert.Equal((ModResult.Failed, "Twitch said no"), (result.Result, result.Message));
        Assert.Equal(("Twitch said no", ModResult.Failed), (_log.Recent()[0].Error, _log.Recent()[0].Result));
    }

    [Fact]
    public async Task Soft_warnings_can_be_turned_off()
    {
        _youtube.WarnState = ModerationToolState.Closest;
        Assert.Equal(ModerationToolState.Closest, _service.Capability("YouTube", ModerationAction.Warn).State);
        _settings.SoftWarnings = false;
        Assert.Equal(ModerationToolState.NotAvailable, _service.Capability("YouTube", ModerationAction.Warn).State);
        Assert.Equal(ModResult.Skipped, (await _service.ExecuteAsync(new ModerationCommand("YouTube", ModerationAction.Warn), ModActor.Streamer)).Result);
    }

    [Fact]
    public async Task Chat_hub_reports_deletions_bans_and_timeouts_and_outside_actions_are_logged_once()
    {
        var notices = new List<ChatModerationNotice>();
        _hub.ModerationSeen += notices.Add;
        _bus.Publish(new BotEvent("alpha.chatMessage", new Dictionary<string, object?> { ["id"] = "m1", ["user"] = "Bob", ["message"] = "rude" }));
        var message = _hub.RecentMessages()[0];

        _bus.Publish(new BotEvent(ChatHub.MessageDeletedEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Alpha", ["messageId"] = "m1", ["userId"] = "u1", ["user"] = "Bob", ["moderator"] = "WebMod",
        }));
        _bus.Publish(new BotEvent(ChatHub.UserTimedOutEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Alpha", ["userId"] = "u1", ["user"] = "Bob", ["duration"] = 600, ["reason"] = "calm down",
        }));
        _bus.Publish(new BotEvent(ChatHub.UserBannedEvent, new Dictionary<string, object?> { ["platform"] = "Alpha", ["userId"] = "u1" }));
        _bus.Publish(new BotEvent(ChatHub.UserBannedEvent, new Dictionary<string, object?> { ["userId"] = "no platform" }));

        Assert.Equal([ChatModerationKind.MessageDeleted, ChatModerationKind.UserTimedOut, ChatModerationKind.UserBanned], notices.Select(n => n.Kind));
        Assert.Equal((600, "calm down", "WebMod"), (notices[1].DurationSeconds, notices[1].Reason, notices[0].Moderator));
        Assert.True(_hub.IsDeleted(message));
        Assert.Equal(3, _hub.RecentModeration().Count);

        var rows = _log.Recent().Reverse().ToArray();
        Assert.Equal(["delete", "timeout", "ban"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(ModActorKind.External, r.ActorKind));
        Assert.Equal(("WebMod", "rude"), (rows[0].ActorName, rows[0].MessageText));
        Assert.Equal("Someone on Alpha", rows[1].ActorName);

        // Cuebot's own ban, echoed back by the platform, is not logged a second time.
        await _service.ExecuteAsync(Ban("u9"), ModActor.Streamer);
        _bus.Publish(new BotEvent(ChatHub.UserBannedEvent, new Dictionary<string, object?> { ["platform"] = "Twitch", ["userId"] = "u9" }));
        Assert.Single(_log.ForTarget("Twitch", "u9"));
    }

    [Fact]
    public void Capabilities_round_up_to_the_platform_unit_and_stay_within_limits()
    {
        var kick = new ModerationCapability(ModerationAction.Timeout, ModerationToolState.Closest)
        {
            DurationUnit = TimeSpan.FromMinutes(1), MinDuration = TimeSpan.FromMinutes(1), MaxDuration = TimeSpan.FromDays(7),
        };
        Assert.Equal(TimeSpan.FromMinutes(10), kick.Apply(TimeSpan.FromSeconds(570)));
        Assert.Equal(TimeSpan.FromMinutes(1), kick.Apply(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromDays(7), kick.Apply(TimeSpan.FromDays(30)));
        Assert.True(kick.CanRun);
        Assert.False(ModerationCapability.Website(ModerationAction.Clear, "u", "n").CanRun);
        // A moderator written before capabilities offers its three flags only.
        Assert.Equal(ModerationToolState.Works, ModerationCapability.FromLegacy(ModerationAction.Ban, ChatModerationActions.Ban).State);
        Assert.Equal(ModerationToolState.NotAvailable, ModerationCapability.FromLegacy(ModerationAction.Unban, ChatModerationActions.Ban).State);
        Assert.Equal(["30 s", "9 min 30 s", "2 h", "7 days"],
            new[] { 30, 570, 7200, 604800 }.Select(s => ModerationService.FormatDuration(TimeSpan.FromSeconds(s))));
    }

    private sealed class Adapter : IChatEventAdapter
    {
        public string Platform => "Alpha";

        public ChatMessage? ToChatMessage(BotEvent e) => e.Type == "alpha.chatMessage"
            ? ChatMessage.Create("Alpha", e.Args.GetValueOrDefault("id") as string, (string)e.Args["user"]!, "u1", (string)e.Args["message"]!, e.Time)
            : null;

        public FeedItem? ToFeedItem(BotEvent e) => null;
    }

    private sealed class FakeModerator(string platform) : IChatModerator
    {
        public List<string> Calls { get; } = [];
        public int QuotaCost { get; init; }
        public int? MaxReason { get; set; }
        public TimeSpan Unit { get; set; } = TimeSpan.FromSeconds(1);
        public string? Website { get; set; }
        public string? NextRef { get; set; }
        public bool Throw { get; set; }
        public ModerationToolState WarnState { get; set; } = ModerationToolState.Works;
        public string Platform => platform;
        public ChatModerationActions Available => ChatModerationActions.Ban;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;

        public ModerationCapability Capability(ModerationAction action) => action switch
        {
            ModerationAction.SlowMode when Website is not null => ModerationCapability.Website(action, Website, "On the site."),
            ModerationAction.Warn => new ModerationCapability(action, WarnState) { QuotaCost = QuotaCost },
            _ => ModerationCapability.Works(action) with { QuotaCost = QuotaCost, MaxReasonLength = MaxReason, DurationUnit = Unit },
        };

        public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException($"{platform} said no");
            Calls.Add(r.Action switch
            {
                ModerationAction.Unban => $"Unban {r.UserId} ref={r.PlatformRef} chat={r.ChannelId}",
                _ => $"{r.Action} {r.UserId} {r.Reason}".TrimEnd(),
            });
            var applied = r.Duration is { } d ? Capability(r.Action).Apply(d) : (TimeSpan?)null;
            return Task.FromResult(ModerationOutcome.Done(applied, NextRef));
        }
    }

    private sealed class FakeQuota : IPlatformQuota
    {
        public int Left { get; set; } = 10_000;
        public string Platform => "YouTube";
        public int Remaining => Left;
        public int Reserve => 2_000;
        public DateTimeOffset ResetsAt => DateTimeOffset.UnixEpoch;
        public string? Refuse(int units) => Remaining - units < Reserve ? "Not enough YouTube quota" : null;
    }

    private sealed class FakeProtected : IProtectedAccounts
    {
        public string? WhyProtected(string platform, string userId, string userName) =>
            userId == "bot-1" ? "This is your bot account. Only the streamer can do this." : null;
    }

    private sealed class FakeRoles : IChannelRoles
    {
        public string Platform => "Twitch";
        public Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default) => Task.FromResult(userId == "mod-by-lookup");

        public Task<bool?> CheckModeratorAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult<bool?>(userId == "mod-by-lookup" ? true : userId == "unknown" ? null : false);
    }
}
