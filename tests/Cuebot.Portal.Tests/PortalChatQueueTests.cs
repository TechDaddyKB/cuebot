using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Portal.Chat;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Tests;

/// <summary>A Twitch-like moderator with moderation queues, whose lists the tests fill in.</summary>
internal sealed class QueueTwitch : IChatModerator, IModerationQueues
{
    public readonly List<ModerationRequest> Requests = [];
    public readonly List<HeldChatMessage> Held = [];
    public readonly Dictionary<string, SuspiciousUserInfo> Flagged = [];
    public readonly List<UnbanRequestInfo> Unbans = [];
    public readonly List<BlockedTermInfo> Terms = [];
    public ShieldModeState? Shield { get; set; }
    public int ListCalls;
    public int Refreshes;
    public Exception? ListError { get; set; }

    public string Platform => "Twitch";
    public ChatModerationActions Available => ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban;
    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;
    public ModerationCapability Capability(ModerationAction action) => ModerationCapability.Works(action);

    public Task<ModerationOutcome> ExecuteAsync(ModerationRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(ModerationOutcome.Done(request.Duration));
    }

    public IReadOnlyList<HeldChatMessage> HeldMessages => Held;
    public ShieldModeState? ShieldMode => Shield;
    public SuspiciousUserInfo? SuspiciousUser(string userId) => Flagged.GetValueOrDefault(userId);

    public Task RefreshAsync(CancellationToken ct = default)
    {
        Refreshes++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UnbanRequestInfo>> GetUnbanRequestsAsync(CancellationToken ct = default)
    {
        ListCalls++;
        return ListError is { } e ? Task.FromException<IReadOnlyList<UnbanRequestInfo>>(e) : Task.FromResult<IReadOnlyList<UnbanRequestInfo>>([.. Unbans]);
    }

    public Task<IReadOnlyList<BlockedTermInfo>> GetBlockedTermsAsync(CancellationToken ct = default)
    {
        ListCalls++;
        return ListError is { } e ? Task.FromException<IReadOnlyList<BlockedTermInfo>>(e) : Task.FromResult<IReadOnlyList<BlockedTermInfo>>([.. Terms]);
    }

    public event Action<ModerationQueueChange>? Changed;
    public void Raise(ModerationQueueKind kind, string itemId = "") => Changed?.Invoke(new ModerationQueueChange("Twitch", kind, itemId));
}

/// <summary>The Twitch moderation queues in the mod portal (phase 3): chat.queues, the queue tools in chat.act, and the "modqueue" topic.</summary>
public sealed class PortalChatQueueTests : IDisposable
{
    private sealed class FakeLink(PortalLinkContext context) : IPortalPushLink
    {
        public readonly List<JsonObject> Sent = [];
        public PortalLinkContext? Context { get; } = context;
        public bool IsOpen => true;

        public Task SendAsync(byte[] plaintext, CancellationToken ct)
        {
            lock (Sent) Sent.Add((JsonObject)JsonNode.Parse(plaintext)!);
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ChatHub _hub;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cuebot-portal-queues-{Guid.NewGuid():N}.db");
    private readonly ModLog _modLog;
    private readonly QueueTwitch _twitch = new();
    private readonly RecordingModerator _kick = new("Kick", wholeMinutes: true);
    private readonly ModerationService _moderation;
    private readonly PortalDirectory _dir = new(new PortalSettings { Enabled = true }, () => { });
    private readonly PortalChat _chat;
    private readonly PortalPush _push;

    public PortalChatQueueTests()
    {
        _bus = new EventBus(_log);
        _hub = new ChatHub(_bus, _log, [new TestChatAdapter("Twitch"), new TestChatAdapter("Kick")], []);
        _modLog = new ModLog(_dbPath);
        _moderation = new ModerationService(new ChatModerationService([_twitch, _kick], []), _modLog, _hub,
            [new PortalProtectedAccounts(_dir)], appLog: _log);
        _chat = new PortalChat(_dir, _hub, _moderation, _log);
        _push = new PortalPush(_dir, limits: new PortalPushLimits { FlushInterval = Timeout.InfiniteTimeSpan });
        _chat.Attach(_push);

        _twitch.Held.Add(new HeldChatMessage("Twitch", "h1", T0) { UserId = "u1", UserName = "Bob", Text = "<b>held</b>", Reason = "AutoMod: aggression, level 3" });
        _twitch.Held.Add(new HeldChatMessage("Twitch", "h0", T0.AddMinutes(-5)) { UserId = "u2", UserName = "Ann", Text = "old", Status = HeldMessageStatus.Denied, DecidedBy = "Mia" });
        _twitch.Unbans.Add(new UnbanRequestInfo("Twitch", "r1", "u3", T0) { UserName = "Cal", Text = "sorry" });
        _twitch.Terms.Add(new BlockedTermInfo("Twitch", "t1", "badword"));
        _twitch.Shield = new ShieldModeState(true) { ModeratorName = "Mia", Since = T0 };
    }

    public void Dispose()
    {
        _chat.Dispose();
        _push.Dispose();
        _moderation.Dispose();
        _hub.Dispose();
        _modLog.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        _log.Dispose();
    }

    private (PortalMember Member, PortalDevice Device) Join(Guid roleId)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _dir.RedeemInvite(_dir.CreateInvite(roleId).Secret,
            new ProviderIdentity(IdentityProvider.Google, Guid.NewGuid().ToString(), "m", "Sam"),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone");
    }

    private PortalMember Member(Guid roleId) => Join(roleId).Member;

    private PortalMember Custom(params PortalPermission[] permissions) =>
        Member(_dir.SaveRole(new PortalRole { Name = "Custom", Permissions = [.. permissions] }).Id);

    private Task<JsonObject> Call(PortalMember member, string op, object? body = null)
    {
        var request = body is null ? new JsonObject() : (JsonObject)JsonSerializer.SerializeToNode(body)!;
        return _chat.HandleAsync(member, _dir.RoleOf(member), op, request);
    }

    private static bool Ok(JsonObject answer) => (bool)answer["ok"]!;

    private static string Err(JsonObject answer) => (string?)answer["error"] ?? "";

    /// <summary>A request for each queue tool that would work for a role that has the right.</summary>
    private static object RequestFor(string action) => action switch
    {
        "ShieldMode" => new { action, platform = "Twitch", enabled = false },
        "ApproveHeld" or "DenyHeld" => new { action, platform = "Twitch", itemId = "h1" },
        "MonitorUser" or "RestrictUser" or "ClearSuspicious" => new { action, platform = "Twitch", userId = "u9", user = "Spam" },
        "ApproveUnbanRequest" or "DenyUnbanRequest" => new { action, platform = "Twitch", itemId = "r1", reason = "ok" },
        "AddBlockedTerm" => new { action, platform = "Twitch", term = "newword" },
        _ => new { action, platform = "Twitch", itemId = "t1" },
    };

    // ---------------------------------------------------------------- permissions

    [Theory]
    [InlineData(ModerationAction.ShieldMode, PortalPermission.ChatModes)]
    [InlineData(ModerationAction.ApproveHeld, PortalPermission.ChatDeleteTimeout)]
    [InlineData(ModerationAction.DenyHeld, PortalPermission.ChatDeleteTimeout)]
    [InlineData(ModerationAction.MonitorUser, PortalPermission.ChatDeleteTimeout)]
    [InlineData(ModerationAction.RestrictUser, PortalPermission.ChatBan)]
    [InlineData(ModerationAction.ClearSuspicious, PortalPermission.ChatBan)]
    [InlineData(ModerationAction.ApproveUnbanRequest, PortalPermission.ChatBan)]
    [InlineData(ModerationAction.DenyUnbanRequest, PortalPermission.ChatBan)]
    [InlineData(ModerationAction.AddBlockedTerm, PortalPermission.BlockedTerms)]
    [InlineData(ModerationAction.RemoveBlockedTerm, PortalPermission.BlockedTerms)]
    public void Each_queue_tool_needs_its_right(ModerationAction action, PortalPermission needs) =>
        Assert.Equal(needs, PortalModerationPermissions.For(action));

    [Theory]
    [InlineData("ShieldMode", false, true)]
    [InlineData("ApproveHeld", true, true)]
    [InlineData("DenyHeld", true, true)]
    [InlineData("MonitorUser", true, true)]
    [InlineData("RestrictUser", true, true)]
    [InlineData("ClearSuspicious", true, true)]
    [InlineData("ApproveUnbanRequest", true, true)]
    [InlineData("DenyUnbanRequest", true, true)]
    [InlineData("AddBlockedTerm", false, true)]
    [InlineData("RemoveBlockedTerm", false, true)]
    public async Task Queue_tools_follow_the_role(string action, bool standardMay, bool leadMay)
    {
        foreach (var (roleId, may) in new[]
                 {
                     (DefaultRoles.StandardModId, standardMay), (DefaultRoles.LeadModId, leadMay), (DefaultRoles.TechLeadId, leadMay),
                 })
        {
            // Each attempt on a fresh item: an allowed held message or request is decided only once.
            _twitch.Held.RemoveAll(h => h.Id == "h1");
            _twitch.Held.Add(new HeldChatMessage("Twitch", "h1", T0) { UserId = "u1", UserName = "Bob", Text = "held" });
            var before = _twitch.Requests.Count;
            var answer = await Call(Member(roleId), "chat.act", RequestFor(action));
            Assert.True(may == Ok(answer), $"{action} for {roleId}: {Err(answer)}");
            Assert.Equal(may ? before + 1 : before, _twitch.Requests.Count);
            if (!may)
            {
                Assert.Equal("Skipped", (string)answer["result"]!);
                Assert.Contains("role does not include", Err(answer));
            }
        }
    }

    [Fact]
    public async Task Delete_and_timeout_alone_allows_held_messages_and_monitoring_only()
    {
        var sam = Custom(PortalPermission.ViewChat, PortalPermission.ChatDeleteTimeout);
        Assert.True(Ok(await Call(sam, "chat.act", RequestFor("ApproveHeld"))));
        Assert.True(Ok(await Call(sam, "chat.act", RequestFor("MonitorUser"))));
        foreach (var action in new[] { "RestrictUser", "ClearSuspicious", "ApproveUnbanRequest", "RemoveBlockedTerm", "ShieldMode" })
            Assert.Contains("role does not include", Err(await Call(sam, "chat.act", RequestFor(action))));
        Assert.Equal(2, _twitch.Requests.Count);
        // Refused item tools do not make Cuebot ask the platform for its lists first.
        Assert.Equal(0, _twitch.ListCalls);
        Assert.Equal(5, _modLog.Recent().Count(r => r.Result == ModResult.Skipped));
    }

    [Fact]
    public async Task Capabilities_say_which_queue_tools_the_role_has()
    {
        var answer = await Call(Member(DefaultRoles.StandardModId), "chat.capabilities", new { platform = "Twitch" });
        var tools = answer["platforms"]![0]!["tools"]!.AsArray().ToDictionary(t => (string)t!["action"]!, t => (bool)t!["allowed"]!);
        Assert.True(tools["ApproveHeld"]);
        Assert.True(tools["RestrictUser"]);
        Assert.False(tools["ShieldMode"]);
        Assert.False(tools["AddBlockedTerm"]);
    }

    // ---------------------------------------------------------------- acting on queue items

    [Fact]
    public async Task Held_messages_are_decided_with_Cuebots_copy_of_the_message()
    {
        var sam = Member(DefaultRoles.StandardModId);
        var answer = await Call(sam, "chat.act", new { action = "ApproveHeld", platform = "Twitch", itemId = "h1", text = "rewritten", userId = "u2" });
        Assert.True(Ok(answer), Err(answer));
        var request = _twitch.Requests.Single();
        Assert.Equal(ModerationAction.ApproveHeld, request.Action);
        Assert.Equal("h1", request.MessageId);
        Assert.Equal("u1", request.UserId);
        var row = _modLog.Recent().Single();
        Assert.Equal("held-approve", row.Action);
        Assert.Equal("<b>held</b>", row.MessageText);
        Assert.Equal("Bob", row.TargetName);
        Assert.Equal("Allowed Bob's held message", (string)answer["message"]!);
    }

    [Fact]
    public async Task Held_messages_already_decided_or_gone_are_explained()
    {
        var sam = Member(DefaultRoles.StandardModId);
        Assert.Contains("already denied", Err(await Call(sam, "chat.act", new { action = "DenyHeld", platform = "Twitch", itemId = "h0" })));
        Assert.Contains("no longer waiting", Err(await Call(sam, "chat.act", new { action = "DenyHeld", platform = "Twitch", itemId = "zz" })));
        Assert.Contains("held message first", Err(await Call(sam, "chat.act", new { action = "DenyHeld", platform = "Twitch" })));
        Assert.Empty(_twitch.Requests);
    }

    [Fact]
    public async Task Unban_requests_carry_the_answer_to_the_viewer()
    {
        var sam = Member(DefaultRoles.StandardModId);
        var answer = await Call(sam, "chat.act", new { action = "DenyUnbanRequest", platform = "Twitch", itemId = "r1", reason = "  Not this time.  " });
        Assert.True(Ok(answer), Err(answer));
        var request = _twitch.Requests.Single();
        Assert.Equal("r1", request.ItemId);
        Assert.Equal("u3", request.UserId);
        Assert.Equal("Not this time.", request.Reason);   // no "[mod: Sam]" prefix: the viewer reads it
        Assert.Equal("sorry", _modLog.Recent().Single().MessageText);

        Assert.Contains("no longer open", Err(await Call(sam, "chat.act", new { action = "ApproveUnbanRequest", platform = "Twitch", itemId = "r9" })));
        Assert.Contains("500", Err(await Call(sam, "chat.act", new { action = "ApproveUnbanRequest", platform = "Twitch", itemId = "r1", reason = new string('x', 501) })));
    }

    [Fact]
    public async Task Platform_errors_while_finding_an_item_are_explained()
    {
        _twitch.ListError = new HttpRequestException("503 from Twitch");
        var answer = await Call(Member(DefaultRoles.StandardModId), "chat.act", new { action = "ApproveUnbanRequest", platform = "Twitch", itemId = "r1" });
        Assert.False(Ok(answer));
        Assert.Contains("503 from Twitch", Err(answer));
        Assert.Empty(_twitch.Requests);
    }

    [Fact]
    public async Task Blocked_terms_are_added_and_removed()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var added = await Call(lead, "chat.act", new { action = "AddBlockedTerm", platform = "Twitch", term = "  spam link  " });
        Assert.True(Ok(added), Err(added));
        Assert.Equal("spam link", _twitch.Requests.Single().Term);

        var removed = await Call(lead, "chat.act", new { action = "RemoveBlockedTerm", platform = "Twitch", itemId = "t1", term = "other" });
        Assert.True(Ok(removed), Err(removed));
        Assert.Equal("t1", _twitch.Requests[1].ItemId);
        Assert.Equal("badword", _twitch.Requests[1].Term);   // the platform's text, not the device's
        Assert.Equal("Unblocked \"badword\" on Twitch", (string)removed["message"]!);

        Assert.Contains("Type the word", Err(await Call(lead, "chat.act", new { action = "AddBlockedTerm", platform = "Twitch", term = "   " })));
        Assert.Contains("500", Err(await Call(lead, "chat.act", new { action = "AddBlockedTerm", platform = "Twitch", term = new string('x', 501) })));
        Assert.Contains("no longer blocked", Err(await Call(lead, "chat.act", new { action = "RemoveBlockedTerm", platform = "Twitch", itemId = "t9" })));
        Assert.Equal(2, _twitch.Requests.Count);
    }

    [Fact]
    public async Task Queue_tools_on_a_platform_without_queues_are_not_available()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var answer = await Call(lead, "chat.act", new { action = "ApproveHeld", platform = "Kick", itemId = "h1" });
        Assert.False(Ok(answer));
        Assert.Equal("Skipped", (string)answer["result"]!);
        Assert.Empty(_kick.Requests);
    }

    [Fact]
    public async Task Suspicious_chatters_are_flagged_through_the_user_tools()
    {
        var lead = Member(DefaultRoles.LeadModId);
        Assert.True(Ok(await Call(lead, "chat.act", new { action = "RestrictUser", platform = "Twitch", userId = "u9", user = "Spam" })));
        Assert.Equal(("u9", ModerationAction.RestrictUser), (_twitch.Requests.Single().UserId, _twitch.Requests.Single().Action));
    }

    // ---------------------------------------------------------------- chat.queues

    [Fact]
    public async Task Queues_show_held_messages_and_shield_by_role()
    {
        var standard = await Call(Member(DefaultRoles.StandardModId), "chat.queues");
        Assert.True(Ok(standard), Err(standard));
        var twitch = standard["platforms"]!.AsArray().Single()!;   // Kick has no queues
        Assert.Equal("Twitch", (string)twitch["platform"]!);
        Assert.True((bool)twitch["shield"]!["active"]!);
        Assert.Equal("Mia", (string)twitch["shield"]!["moderator"]!);
        var held = twitch["held"]!.AsArray();
        Assert.Equal(["h1", "h0"], held.Select(h => (string)h!["id"]!));   // pending first, then decided
        Assert.Equal("<b>held</b>", (string)held[0]!["text"]!);
        Assert.Equal("Denied", (string)held[1]!["status"]!);
        Assert.Equal(1, (int)twitch["pendingHeld"]!);
        Assert.Null(twitch["unbanRequests"]);
        Assert.Null(twitch["blockedTerms"]);
        Assert.False((bool)standard["rights"]!["blockedTerms"]!);
        Assert.False((bool)standard["rights"]!["shield"]!);
        Assert.Equal(0, _twitch.ListCalls);

        // Seeing the chat only: Shield Mode, but no held messages.
        var viewer = await Call(Custom(PortalPermission.ViewChat), "chat.queues");
        var viewerTwitch = viewer["platforms"]![0]!;
        Assert.NotNull(viewerTwitch["shield"]);
        Assert.Null(viewerTwitch["held"]);

        Assert.False(Ok(await Call(Custom(PortalPermission.UploadSounds), "chat.queues")));
    }

    [Fact]
    public async Task Unban_requests_and_blocked_terms_only_on_request_and_with_the_right()
    {
        var standard = Member(DefaultRoles.StandardModId);
        Assert.Contains("role", Err(await Call(standard, "chat.queues", new { blockedTerms = true })));
        var unban = await Call(standard, "chat.queues", new { unbanRequests = true });
        Assert.Equal("sorry", (string)unban["platforms"]![0]!["unbanRequests"]![0]!["text"]!);

        var lead = await Call(Member(DefaultRoles.LeadModId), "chat.queues", new { unbanRequests = true, blockedTerms = true, refresh = true });
        var twitch = lead["platforms"]![0]!;
        Assert.Equal("badword", (string)twitch["blockedTerms"]![0]!["text"]!);
        Assert.Equal("r1", (string)twitch["unbanRequests"]![0]!["id"]!);
        Assert.Equal(1, _twitch.Refreshes);

        _twitch.ListError = new HttpRequestException("401");
        var failed = await Call(Member(DefaultRoles.LeadModId), "chat.queues", new { blockedTerms = true });
        Assert.True(Ok(failed));
        Assert.Contains("401", (string)failed["platforms"]![0]!["blockedTermsError"]!);
    }

    [Fact]
    public async Task Queues_without_shield_state_say_null_and_filter_by_platform()
    {
        _twitch.Shield = null;
        var answer = await Call(Member(DefaultRoles.StandardModId), "chat.queues", new { platform = "kick" });
        Assert.Empty(answer["platforms"]!.AsArray());
        var twitch = (await Call(Member(DefaultRoles.StandardModId), "chat.queues", new { platform = "twitch" }))["platforms"]![0]!.AsObject();
        Assert.True(twitch.ContainsKey("shield"));
        Assert.Null(twitch["shield"]);
    }

    [Fact]
    public async Task User_card_shows_a_flagged_chatter_to_roles_that_can_act()
    {
        _twitch.Flagged["u1"] = new SuspiciousUserInfo("Twitch", "u1", SuspiciousLevel.Restricted) { Why = "possible ban evader" };
        var card = await Call(Member(DefaultRoles.StandardModId), "chat.user", new { platform = "Twitch", userId = "u1" });
        Assert.Equal("Restricted", (string)card["suspicious"]!["level"]!);
        Assert.Equal("possible ban evader", (string)card["suspicious"]!["why"]!);
        Assert.True((bool)card["suspiciousKnown"]!);

        var viewer = await Call(Custom(PortalPermission.ViewChat), "chat.user", new { platform = "Twitch", userId = "u1" });
        Assert.Null(viewer["suspicious"]);
        Assert.Null(viewer["suspiciousKnown"]);
        var kick = await Call(Member(DefaultRoles.StandardModId), "chat.user", new { platform = "Kick", userId = "u1" });
        Assert.Null(kick["suspiciousKnown"]);
    }

    // ---------------------------------------------------------------- push

    private FakeLink Link((PortalMember Member, PortalDevice Device) m) =>
        new(new PortalLinkContext(1, new byte[32]) { MemberId = m.Member.Id, DeviceId = m.Device.Id });

    private JsonObject Subscribe(FakeLink link, string topic, object? parameters = null)
    {
        var bytes = _push.TryHandle(link, JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", topic, @params = parameters ?? new { } }), out var done);
        done?.Invoke();
        return (JsonObject)JsonNode.Parse(bytes!)!;
    }

    private static List<JsonNode> Events(FakeLink link) =>
        [.. link.Sent.Where(f => (string)f["push"]! == PortalChat.QueueTopic).SelectMany(f => f["events"]!.AsArray()).Select(e => e!)];

    [Fact]
    public async Task Queue_changes_reach_only_roles_that_may_see_them()
    {
        var standard = Link(Join(DefaultRoles.StandardModId));
        var lead = Link(Join(DefaultRoles.LeadModId));
        var viewer = Link(Join(_dir.SaveRole(new PortalRole { Name = "Viewer", Permissions = [PortalPermission.ViewChat] }).Id));
        var kickOnly = Link(Join(DefaultRoles.LeadModId));
        foreach (var link in new[] { standard, lead, viewer }) Assert.True(Ok(Subscribe(link, PortalChat.QueueTopic)));
        Subscribe(kickOnly, PortalChat.QueueTopic, new { platform = "Kick" });
        Assert.False(Ok(Subscribe(Link(Join(_dir.SaveRole(new PortalRole { Name = "Sounds", Permissions = [PortalPermission.UploadSounds] }).Id)),
            PortalChat.QueueTopic)));

        _twitch.Held.Add(new HeldChatMessage("Twitch", "h2", T0.AddMinutes(1)) { UserId = "u5", UserName = "Eve", Text = "<script>x</script>" });
        _twitch.Raise(ModerationQueueKind.HeldMessages, "h2");
        _twitch.Shield = new ShieldModeState(false);
        _twitch.Raise(ModerationQueueKind.ShieldMode);
        _twitch.Flagged["u5"] = new SuspiciousUserInfo("Twitch", "u5", SuspiciousLevel.Monitored);
        _twitch.Raise(ModerationQueueKind.SuspiciousUsers, "u5");
        _twitch.Raise(ModerationQueueKind.UnbanRequests, "r2");
        _twitch.Raise(ModerationQueueKind.BlockedTerms);
        await _push.FlushAsync();

        Assert.Equal(["held", "shield", "suspicious", "unbanRequests"], Events(standard).Select(e => (string)e["type"]!));
        Assert.Equal(["held", "shield", "suspicious", "unbanRequests", "blockedTerms"], Events(lead).Select(e => (string)e["type"]!));
        Assert.Equal(["shield"], Events(viewer).Select(e => (string)e["type"]!));
        Assert.Empty(Events(kickOnly));

        var held = Events(standard)[0];
        Assert.Equal("<script>x</script>", (string)held["item"]!["text"]!);
        Assert.Equal(2, (int)held["pendingHeld"]!);
        Assert.False((bool)Events(viewer)[0]["shield"]!["active"]!);
        Assert.Equal("Monitored", (string)Events(standard)[2]["suspicious"]!["level"]!);
    }

    [Fact]
    public async Task A_member_who_loses_the_right_stops_getting_held_messages()
    {
        var role = _dir.SaveRole(new PortalRole { Name = "Mods", Permissions = [PortalPermission.ViewChat, PortalPermission.ChatDeleteTimeout] });
        var link = Link(Join(role.Id));
        Subscribe(link, PortalChat.QueueTopic);
        role.Permissions.Remove(PortalPermission.ChatDeleteTimeout);
        _dir.SaveRole(role);
        _twitch.Raise(ModerationQueueKind.HeldMessages, "h1");
        _twitch.Raise(ModerationQueueKind.ShieldMode);
        await _push.FlushAsync();
        Assert.Equal(["shield"], Events(link).Select(e => (string)e["type"]!));
    }
}
