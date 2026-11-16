using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.Moderation;

/// <summary>
/// Phase 3 in ModerationService: actions a platform reports in full (channel.moderate) and their de-duplication
/// against Cuebot's own rows, and the queue tools (held messages, Shield Mode, suspicious users, unban requests, terms).
/// </summary>
public sealed class ModerationQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-moderation-queues-").FullName;
    private readonly ManualClock _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly AppLog _appLog = new();
    private readonly EventBus _bus;
    private readonly ChatHub _hub;
    private readonly ModLog _log;
    private readonly QueueModerator _twitch = new();
    private readonly LegacyModerator _kick = new();
    private readonly ModerationService _service;

    public ModerationQueueTests()
    {
        _bus = new EventBus(_appLog);
        _hub = new ChatHub(_bus, _appLog, [], []);
        _log = new ModLog(Path.Combine(_dir, ModLog.FileName), _time);
        _service = new ModerationService(new ChatModerationService([_twitch, _kick], []), _log, _hub, time: _time);
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

    private void Report(string action, string user = "u1", string moderator = "Mod", string reason = "", string messageId = "", bool legacy = false)
    {
        if (legacy)
        {
            _bus.Publish(new BotEvent(ChatHub.UserBannedEvent, new Dictionary<string, object?>
            {
                ["platform"] = "Twitch", ["userId"] = user, ["user"] = "Bob", ["moderator"] = moderator, ["modLogged"] = true,
            }));
            return;
        }
        _bus.Publish(new BotEvent(ChatHub.ModerationActionEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["action"] = action, ["moderator"] = moderator, ["userId"] = user, ["user"] = user.Length > 0 ? "Bob" : "",
            ["reason"] = reason, ["messageId"] = messageId,
        }));
    }

    private static ModerationCommand Ban(string user = "u1") => new("Twitch", ModerationAction.Ban) { TargetUserId = user, TargetName = "Bob" };

    [Fact]
    public async Task Cuebots_own_action_echoed_back_is_not_logged_twice_but_a_second_real_one_is()
    {
        await _service.ExecuteAsync(Ban(), ModActor.Streamer);
        Report("ban", moderator: "Streamer");
        Assert.Single(_log.Recent());

        // Within the echo window, another ban of the same person is a real outside action: the own row is used up.
        Report("ban", moderator: "OtherMod");
        Assert.Equal(["OtherMod", "Streamer"], _log.Recent().Select(r => r.ActorName));
        Assert.Equal(ModActorKind.External, _log.Recent()[0].ActorKind);
    }

    [Fact]
    public async Task An_echo_that_arrives_before_the_platform_answers_is_matched_to_the_pending_command()
    {
        _twitch.BeforeAnswer = () => Report("timeout", moderator: "Streamer");
        await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Timeout)
        {
            TargetUserId = "u1", TargetName = "Bob", Duration = TimeSpan.FromMinutes(1),
        }, ModActor.Streamer);
        var row = Assert.Single(_log.Recent());
        Assert.Equal(ModActorKind.Streamer, row.ActorKind);

        // The row already absorbed its echo: a later one is a real action.
        _twitch.BeforeAnswer = null;
        Report("timeout", moderator: "Mod");
        Assert.Equal(2, _log.Recent().Count);
    }

    [Fact]
    public async Task Echo_matching_tells_chat_modes_and_terms_apart_and_expires()
    {
        await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.SlowMode) { ModeValue = 30 }, ModActor.Streamer);
        Report("mode", user: "", reason: "Emote-only on");
        Report("mode", user: "", reason: "Slow mode on (30)");
        Assert.Equal(["Emote-only on", "Slow mode on (30)"], _log.Recent().Select(r => r.Reason));

        await _service.ExecuteAsync(ModerationCommand.BlockTerm("Twitch", "spoiler"), ModActor.Streamer);
        Report("term-add", user: "", reason: "other");
        Report("term-add", user: "", reason: "Spoiler");
        Assert.Equal(["other", "spoiler"], _log.Recent().Take(2).Select(r => r.Reason));

        await _service.ExecuteAsync(Ban("u9"), ModActor.Streamer);
        _time.Advance(TimeSpan.FromMinutes(3));
        Report("ban", user: "u9", moderator: "Streamer");
        Assert.Equal(ModActorKind.External, _log.Recent()[0].ActorKind);
    }

    [Fact]
    public async Task Older_notices_are_skipped_when_the_platform_reports_in_full_and_an_outside_unban_lifts_the_ban()
    {
        Report("ban", legacy: true);
        Assert.Empty(_log.Recent());

        Report("ban", moderator: "WebMod", reason: "spam");
        var ban = Assert.Single(_log.Recent());
        Assert.Equal(("WebMod", "spam"), (ban.ActorName, ban.Reason));

        Report("unban-request-approve", moderator: "OtherMod", reason: "ok");
        var rows = _log.Recent();
        Assert.Equal(rows[0].Id, _log.Get(ban.Id)!.UndoneBy);
        Assert.Null(_log.FindInForce("Twitch", "u1"));

        // The streamer approves an unban request from Cuebot: the ban Cuebot made is marked lifted too.
        await _service.ExecuteAsync(Ban("u2"), ModActor.Streamer);
        var own = _log.Recent()[0];
        var result = await _service.ExecuteAsync(ModerationCommand.ForUnbanRequest(
            new UnbanRequestInfo("Twitch", "req-1", "u2", _time.GetUtcNow()) { UserName = "Bob", Text = "sorry" }, approve: true, "welcome back"),
            ModActor.Streamer);
        Assert.Equal("Approved Bob's unban request", result.Message);
        Assert.Equal(("unban-request-approve", "welcome back", "sorry"), (result.Entry!.Action, result.Entry.Reason, result.Entry.MessageText));
        Assert.Equal(result.Entry.Id, _log.Get(own.Id)!.UndoneBy);
        Assert.Equal("ApproveUnbanRequest u2 req-1 welcome back", _twitch.Calls[^1]);
        // Twitch's unban that follows is the same action.
        Report("unban", user: "u2", moderator: "Streamer");
        Assert.Equal(result.Entry.Id, _log.Recent()[0].Id);
    }

    [Fact]
    public async Task Queue_tools_are_logged_and_only_offered_by_platforms_with_queues()
    {
        Assert.Same(_twitch, _service.QueuesFor("twitch"));
        Assert.Null(_service.QueuesFor("Kick"));
        // Kick's moderator predates the queue tools and answers "works" for anything: the service knows better.
        Assert.All(ModerationCapability.AllActions.Where(ModerationCapability.IsQueueTool),
            a => Assert.Equal(ModerationToolState.NotAvailable, _service.Capability("Kick", a).State));
        Assert.Equal(ModerationToolState.Works, _service.Capability("Twitch", ModerationAction.ShieldMode).State);

        var held = new HeldChatMessage("Twitch", "held-1", _time.GetUtcNow()) { UserId = "u1", UserName = "Bob", Text = "bad words" };
        _twitch.Held.Add(held);
        Assert.Equal([held], _service.PendingHeldMessages());
        var changes = new List<ModerationQueueChange>();
        _service.QueuesChanged += changes.Add;
        _twitch.Raise(new ModerationQueueChange("Twitch", ModerationQueueKind.HeldMessages, "held-1"));
        Assert.Single(changes);

        var allowed = await _service.ExecuteAsync(ModerationCommand.ForHeld(held, allow: true), ModActor.Streamer);
        Assert.Equal("Allowed Bob's held message", allowed.Message);
        Assert.Equal(("held-approve", "held-1", "bad words"), (allowed.Entry!.Action, allowed.Entry.MessageId, allowed.Entry.MessageText));

        var shield = await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.ShieldMode) { Enabled = true }, ModActor.Streamer);
        Assert.Equal(("Turned Shield Mode on on Twitch", "shield", "Shield Mode on"), (shield.Message, shield.Entry!.Action, shield.Entry.Reason));

        var term = await _service.ExecuteAsync(ModerationCommand.UnblockTerm(new BlockedTermInfo("Twitch", "t-1", "spoiler")), ModActor.Streamer);
        Assert.Equal(("Unblocked \"spoiler\" on Twitch", "term-remove", "spoiler"), (term.Message, term.Entry!.Action, term.Entry.Reason));
        Assert.Equal("RemoveBlockedTerm  t-1", _twitch.Calls[^1]);

        var kick = await _service.ExecuteAsync(new ModerationCommand("Kick", ModerationAction.ShieldMode), ModActor.Streamer);
        Assert.Equal(ModResult.Skipped, kick.Result);
        Assert.Empty(_kick.Calls);
    }

    [Fact]
    public async Task Members_cannot_restrict_protected_people_but_may_clear_them()
    {
        var sam = ModActor.Member("m-1", "Sam");
        var restrict = await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.RestrictUser)
        {
            TargetUserId = "u1", TargetName = "Vip", TargetRoles = ChatRoles.Vip,
        }, sam);
        Assert.Equal(ModResult.Skipped, restrict.Result);
        Assert.Contains("VIP", restrict.Message);

        var clear = await _service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.ClearSuspicious)
        {
            TargetUserId = "u1", TargetName = "Vip", TargetRoles = ChatRoles.Vip,
        }, sam);
        Assert.True(clear.Ok);
        Assert.Equal("Vip is no longer marked suspicious", clear.Message);
    }

    /// <summary>A Twitch-like moderator with queues.</summary>
    private sealed class QueueModerator : IChatModerator, IModerationQueues
    {
        public List<string> Calls { get; } = [];
        public List<HeldChatMessage> Held { get; } = [];
        public Action? BeforeAnswer { get; set; }
        public string Platform => "Twitch";
        public ChatModerationActions Available => ChatModerationActions.Ban | ChatModerationActions.Timeout | ChatModerationActions.DeleteMessage;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public ModerationCapability Capability(ModerationAction action) => ModerationCapability.Works(action);

        public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
        {
            BeforeAnswer?.Invoke();
            Calls.Add($"{r.Action} {r.UserId} {r.ItemId} {r.Reason}".TrimEnd());
            return Task.FromResult(ModerationOutcome.Done(r.Duration));
        }

        public IReadOnlyList<HeldChatMessage> HeldMessages => Held;
        public ShieldModeState? ShieldMode => null;
        public SuspiciousUserInfo? SuspiciousUser(string userId) => null;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UnbanRequestInfo>> GetUnbanRequestsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UnbanRequestInfo>>([]);
        public Task<IReadOnlyList<BlockedTermInfo>> GetBlockedTermsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BlockedTermInfo>>([]);
        public event Action<ModerationQueueChange>? Changed;
        public void Raise(ModerationQueueChange change) => Changed?.Invoke(change);
    }

    /// <summary>A moderator written before the queue tools: its default says "works" for everything.</summary>
    private sealed class LegacyModerator : IChatModerator
    {
        public List<string> Calls { get; } = [];
        public string Platform => "Kick";
        public ChatModerationActions Available => ChatModerationActions.Ban;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public ModerationCapability Capability(ModerationAction action) => ModerationCapability.Works(action);

        public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
        {
            Calls.Add(r.Action.ToString());
            return Task.FromResult(ModerationOutcome.Done());
        }
    }
}
