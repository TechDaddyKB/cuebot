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

/// <summary>Feeds ChatHub's chat from "test.chat" events, on the platform named in the event.</summary>
internal sealed class TestChatAdapter(string platform) : IChatEventAdapter
{
    public string Platform => platform;

    public ChatMessage? ToChatMessage(BotEvent e) => e.Type == "test.chat" && (string)e.Args["platform"]! == platform
        ? new ChatMessage(platform, (string)e.Args["id"]!, (string)e.Args["user"]!, (string)e.Args["userId"]!, (string)e.Args["text"]!, DateTimeOffset.UtcNow)
        {
            Roles = (ChatRoles)(int)e.Args["roles"]!,
            CanDelete = (bool)e.Args["canDelete"]!,
        }
        : null;

    public FeedItem? ToFeedItem(BotEvent e) => null;
}

/// <summary>A platform moderator that records what it was asked. Kick-like platforms count whole minutes up to 7 days.</summary>
internal sealed class RecordingModerator(string platform, bool wholeMinutes = false) : IChatModerator
{
    public readonly List<ModerationRequest> Requests = [];

    public string Platform => platform;
    public ChatModerationActions Available => ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban;
    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;

    public ModerationCapability Capability(ModerationAction action) => action switch
    {
        ModerationAction.Timeout when wholeMinutes => new ModerationCapability(action, ModerationToolState.Closest)
        {
            Note = "Kick counts whole minutes.", DurationUnit = TimeSpan.FromMinutes(1), MinDuration = TimeSpan.FromMinutes(1),
            MaxDuration = TimeSpan.FromDays(7),
        },
        ModerationAction.Clear when wholeMinutes => ModerationCapability.Website(action, "https://kick.com/dashboard", "Kick has no API for it."),
        ModerationAction.Warn when wholeMinutes => ModerationCapability.Website(action, "javascript:alert(1)", "Not a real link."),
        ModerationAction.Pin or ModerationAction.Unpin => ModerationCapability.NotAvailable(action, "No pins."),
        _ when ModerationCapability.IsQueueTool(action) => ModerationCapability.NotAvailable(action, "Not here."),
        _ => ModerationCapability.Works(action),
    };

    public Task<ModerationOutcome> ExecuteAsync(ModerationRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        var capability = Capability(request.Action);
        return Task.FromResult(ModerationOutcome.Done(request.Duration is { } d ? capability.Apply(d) : null));
    }
}

public sealed class PortalChatTests : IDisposable
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

    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ChatHub _hub;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cuebot-portal-chat-{Guid.NewGuid():N}.db");
    private readonly ModLog _modLog;
    private readonly RecordingModerator _twitch = new("Twitch");
    private readonly RecordingModerator _kick = new("Kick", wholeMinutes: true);
    private readonly ModerationSettings _settings = new() { MemberBansPerWindow = 2, MemberTimeoutsPerWindow = 30 };
    private readonly ModerationService _moderation;
    private readonly PortalDirectory _dir = new(new PortalSettings { Enabled = true }, () => { });
    private readonly PortalChat _chat;
    private readonly PortalPush _push;

    public PortalChatTests()
    {
        _bus = new EventBus(_log);
        _hub = new ChatHub(_bus, _log, [new TestChatAdapter("Twitch"), new TestChatAdapter("Kick")], []);
        _modLog = new ModLog(_dbPath);
        _moderation = new ModerationService(new ChatModerationService([_twitch, _kick], []), _modLog, _hub,
            [new PortalProtectedAccounts(_dir)], settings: () => _settings, appLog: _log);
        _chat = new PortalChat(_dir, _hub, _moderation, _log);
        _push = new PortalPush(_dir, limits: new PortalPushLimits { FlushInterval = Timeout.InfiniteTimeSpan });
        _chat.Attach(_push);
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

    private static PortalRole Standard => DefaultRoles.Create().Single(r => r.Id == DefaultRoles.StandardModId);

    private (PortalMember Member, PortalDevice Device) Join(Guid roleId, IdentityProvider provider = IdentityProvider.Google, string providerId = "")
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _dir.RedeemInvite(_dir.CreateInvite(roleId).Secret,
            new ProviderIdentity(provider, providerId.Length > 0 ? providerId : Guid.NewGuid().ToString(), "m", "Sam"),
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

    private void Say(string id, string user = "Bob", string userId = "u1", string text = "hello", ChatRoles roles = ChatRoles.None,
        string platform = "Twitch", bool canDelete = true) =>
        _bus.Publish(new BotEvent("test.chat", new Dictionary<string, object?>
        {
            ["platform"] = platform, ["id"] = id, ["user"] = user, ["userId"] = userId, ["text"] = text, ["roles"] = (int)roles,
            ["canDelete"] = canDelete,
        }));

    private static bool Ok(JsonObject answer) => (bool)answer["ok"]!;

    // ---------------------------------------------------------------- permissions and defaults

    [Fact]
    public void Default_roles_follow_the_plan()
    {
        var roles = DefaultRoles.Create().ToDictionary(r => r.Id);
        var standard = roles[DefaultRoles.StandardModId].Permissions;
        Assert.Subset(standard.ToHashSet(), DefaultRoles.StandardChat.ToHashSet());
        foreach (var no in new[] { PortalPermission.ChatModes, PortalPermission.BlockedTerms, PortalPermission.NotesDelete, PortalPermission.ConfirmLinks })
            Assert.DoesNotContain(no, standard);
        foreach (var yes in new[] { PortalPermission.ChatBan, PortalPermission.LinkedBans, PortalPermission.ViewModLog })
            Assert.Contains(yes, standard);
        foreach (var id in new[] { DefaultRoles.LeadModId, DefaultRoles.TechLeadId })
            Assert.Subset(roles[id].Permissions.ToHashSet(), DefaultRoles.LeadChat.ToHashSet());
    }

    [Fact]
    public void Roles_saved_before_chat_get_chat_rights_on_load()
    {
        var saves = 0;
        var oldStandard = new PortalRole { Id = DefaultRoles.StandardModId, Name = "Standard Mod", Permissions = [PortalPermission.RunActions] };
        var oldLead = new PortalRole { Id = DefaultRoles.LeadModId, Name = "Lead Mod", Permissions = [PortalPermission.RunActions] };
        var critical = new PortalRole { Name = "Show runner", Permissions = [PortalPermission.ShowCritical] };
        var sound = new PortalRole { Name = "Sound tech", Permissions = [PortalPermission.UploadSounds] };
        var settings = new PortalSettings { Version = 1, Roles = [oldStandard, oldLead, critical, sound] };

        _ = new PortalDirectory(settings, () => saves++);

        Assert.Equal(PortalSettings.CurrentVersion, settings.Version);
        Assert.Equal(1, saves);
        Assert.Equal([PortalPermission.RunActions, .. DefaultRoles.StandardChat], oldStandard.Permissions);
        Assert.Subset(oldLead.Permissions.ToHashSet(), DefaultRoles.LeadChat.ToHashSet());
        Assert.Subset(critical.Permissions.ToHashSet(), DefaultRoles.LeadChat.ToHashSet());
        Assert.Equal([PortalPermission.UploadSounds, PortalPermission.ViewChat, PortalPermission.ViewModLog], sound.Permissions);

        // Loading again changes nothing (a streamer who took a right away keeps it taken away).
        sound.Permissions.Remove(PortalPermission.ViewChat);
        _ = new PortalDirectory(settings, () => saves++);
        Assert.Equal(1, saves);
        Assert.DoesNotContain(PortalPermission.ViewChat, sound.Permissions);
    }

    [Fact]
    public void A_new_portal_starts_at_the_current_version()
    {
        var settings = new PortalSettings();
        _ = new PortalDirectory(settings, () => { });
        Assert.Equal(PortalSettings.CurrentVersion, settings.Version);
        Assert.Contains(PortalPermission.ViewChat, settings.Roles.Single(r => r.Id == DefaultRoles.StandardModId).Permissions);
    }

    [Fact]
    public void Sets_the_moderation_permissions_hook()
    {
        Assert.IsType<PortalModerationPermissions>(_moderation.Permissions);
        var hook = _moderation.Permissions!;
        var sam = Custom(PortalPermission.ChatDeleteTimeout);
        var actor = ModActor.Member(sam.Id.ToString(), "Sam");
        Assert.Null(hook.Refuse(actor, new ModerationCommand("Twitch", ModerationAction.Timeout)));
        Assert.Contains("ban", hook.Refuse(actor, new ModerationCommand("Twitch", ModerationAction.Ban)));
        Assert.Contains("linked", hook.Refuse(actor, new ModerationCommand("Twitch", ModerationAction.Timeout) { ViaLinkId = "l1" }));
        Assert.Null(hook.Refuse(ModActor.Automation("Rule"), new ModerationCommand("Twitch", ModerationAction.Ban)));
        Assert.NotNull(hook.Refuse(ModActor.Member(Guid.NewGuid().ToString(), "Gone"), new ModerationCommand("Twitch", ModerationAction.Timeout)));
    }

    // ---------------------------------------------------------------- reading ops by role

    [Theory]
    [InlineData("chat.history")]
    [InlineData("chat.capabilities")]
    [InlineData("chat.user")]
    [InlineData("chat.log")]
    [InlineData("chat.act")]
    [InlineData("chat.queues")]
    public async Task Every_op_is_refused_without_the_right(string op)
    {
        var nobody = Custom(PortalPermission.UploadSounds);
        var answer = await Call(nobody, op, new { platform = "Twitch", userId = "u1", action = "Ban" });
        Assert.False(Ok(answer));
        Assert.Contains("role", (string)answer["error"]!);
        Assert.Empty(_twitch.Requests);
        Assert.Empty(_modLog.Recent());
    }

    [Fact]
    public async Task History_has_recent_messages_with_deleted_flags_and_platform_filter()
    {
        Say("m1", text: "<b>hi</b>");
        Say("m2", platform: "Kick", userId: "k1");
        _bus.Publish(new BotEvent(ChatHub.MessageDeletedEvent, new Dictionary<string, object?> { ["platform"] = "Twitch", ["messageId"] = "m1" }));
        var sam = Member(DefaultRoles.StandardModId);

        var all = await Call(sam, "chat.history");
        Assert.True(Ok(all));
        Assert.Equal(["Twitch", "Kick"], all["platforms"]!.AsArray().Select(p => (string)p!));
        var lines = all["messages"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.True((bool)lines[0]!["deleted"]!);
        Assert.Equal("<b>hi</b>", (string)lines[0]!["text"]!);   // text, as sent: the page renders it as text
        Assert.Equal("deleted", (string)all["moderation"]![0]!["kind"]!);

        var kick = await Call(sam, "chat.history", new { platform = "kick" });
        Assert.Equal("m2", (string)kick["messages"]![0]!["id"]!);
        Assert.Single(kick["messages"]!.AsArray());
    }

    [Fact]
    public async Task Capabilities_show_state_role_and_applied_timeouts_per_platform()
    {
        var sam = Member(DefaultRoles.StandardModId);
        var answer = await Call(sam, "chat.capabilities");
        Assert.True(Ok(answer));
        var kick = answer["platforms"]!.AsArray().Single(p => (string)p!["platform"]! == "Kick")!;
        var tools = kick["tools"]!.AsArray().ToDictionary(t => (string)t!["action"]!, t => t!);
        Assert.Equal("Closest", (string)tools["Timeout"]["state"]!);
        Assert.Equal(60, (int)tools["Timeout"]["unitSeconds"]!);
        Assert.True((bool)tools["Ban"]["allowed"]!);
        Assert.False((bool)tools["SlowMode"]["allowed"]!);   // a Standard Mod has no chat modes
        Assert.Equal("OnWebsite", (string)tools["Clear"]["state"]!);
        Assert.Equal("https://kick.com/dashboard", (string)tools["Clear"]["websiteUrl"]!);
        Assert.Null(tools["Warn"]["websiteUrl"]);   // only https links reach the page
        var twoWeeks = kick["timeouts"]!.AsArray().Single(t => (int)t!["requested"]! == 604800)!;
        Assert.Equal(604800, (int)twoWeeks["applied"]!);
        Assert.Equal(6, kick["timeouts"]!.AsArray().Count);
    }

    [Fact]
    public async Task User_card_has_messages_roles_and_log_rows_only_with_the_log_right()
    {
        Say("m1", roles: ChatRoles.Subscriber);
        Say("m2");
        var lead = Member(DefaultRoles.LeadModId);
        Assert.True(Ok(await Call(lead, "chat.act", new { action = "Timeout", platform = "Twitch", messageId = "m2", durationSeconds = 60 })));

        var card = await Call(lead, "chat.user", new { platform = "Twitch", userId = "u1" });
        Assert.Equal("Bob", (string)card["name"]!);
        Assert.Equal(["Subscriber"], card["roles"]!.AsArray().Select(r => (string)r!));
        Assert.Equal(2, card["messages"]!.AsArray().Count);
        Assert.Single(card["log"]!.AsArray());
        Assert.Equal("timeout", (string)card["inForce"]!["action"]!);

        var viewer = Custom(PortalPermission.ViewChat);
        var limited = await Call(viewer, "chat.user", new { platform = "Twitch", userId = "u1" });
        Assert.True(Ok(limited));
        Assert.Null(limited["log"]);
    }

    [Fact]
    public async Task Log_pages_by_platform_and_target_and_hides_device_and_member_ids()
    {
        Say("m1");
        Say("m2", platform: "Kick", userId: "k1", user: "Kim");
        var lead = Member(DefaultRoles.LeadModId);
        await Call(lead, "chat.act", new { action = "Delete", platform = "Twitch", messageId = "m1" });
        await Call(lead, "chat.act", new { action = "Timeout", platform = "Twitch", userId = "u1", durationSeconds = 30 });
        await Call(lead, "chat.act", new { action = "Delete", platform = "Kick", messageId = "m2" });

        var first = await Call(lead, "chat.log", new { limit = 2 });
        Assert.Equal(2, first["rows"]!.AsArray().Count);
        Assert.True((bool)first["more"]!);
        var second = await Call(lead, "chat.log", new { limit = 2, offset = 2 });
        Assert.Single(second["rows"]!.AsArray());
        Assert.False((bool)second["more"]!);

        var kick = await Call(lead, "chat.log", new { platform = "Kick" });
        Assert.Equal("Kim", (string)kick["rows"]![0]!["targetName"]!);
        var bob = await Call(lead, "chat.log", new { platform = "Twitch", userId = "u1" });
        Assert.Equal(2, bob["rows"]!.AsArray().Count);
        Assert.False(Ok(await Call(lead, "chat.log", new { userId = "u1" })));

        var row = first["rows"]![0]!.AsObject();
        Assert.Equal("Member", (string)row["actorKind"]!);
        Assert.False(row.ContainsKey("actorDeviceId"));
        Assert.False(row.ContainsKey("actorMemberId"));
        Assert.False(row.ContainsKey("hash"));
    }

    // ---------------------------------------------------------------- acting by role

    [Theory]
    [InlineData("Delete", true, true)]
    [InlineData("Timeout", true, true)]
    [InlineData("Warn", true, true)]
    [InlineData("Ban", true, true)]
    [InlineData("Unban", true, true)]
    [InlineData("SlowMode", false, true)]
    [InlineData("Clear", false, true)]
    public async Task Act_follows_the_role(string action, bool standardMay, bool leadMay)
    {
        Say("m1");
        foreach (var (roleId, may) in new[] { (DefaultRoles.StandardModId, standardMay), (DefaultRoles.LeadModId, leadMay) })
        {
            var member = Member(roleId);
            var before = _twitch.Requests.Count;
            var answer = await Call(member, "chat.act", new { action, platform = "Twitch", messageId = "m1", userId = "u1", durationSeconds = 60 });
            Assert.Equal(may, Ok(answer));
            Assert.Equal(may ? before + 1 : before, _twitch.Requests.Count);
            if (!may)
            {
                Assert.Equal("Skipped", (string)answer["result"]!);
                Assert.Contains("role does not include", (string)answer["error"]!);
            }
        }
    }

    [Fact]
    public async Task Act_logs_the_member_and_device_and_applies_at_once()
    {
        Say("m1", text: "buy followers");
        var (sam, device) = Join(DefaultRoles.StandardModId);
        var request = new JsonObject
        {
            ["action"] = "delete", ["platform"] = "Twitch", ["messageId"] = "m1", ["text"] = "rewritten",
            [PortalSignInHandler.DeviceKey] = device.Id.ToString(),
        };
        var answer = await _chat.HandleAsync(sam, _dir.RoleOf(sam), "chat.act", request);
        Assert.True(Ok(answer), (string?)answer["error"]);
        Assert.Equal("Ok", (string)answer["result"]!);
        var entry = _modLog.Recent().Single();
        Assert.Equal(ModActorKind.Member, entry.ActorKind);
        Assert.Equal(sam.Id.ToString(), entry.ActorMemberId);
        Assert.Equal(device.Id.ToString(), entry.ActorDeviceId);
        Assert.Equal("buy followers", entry.MessageText);   // from Cuebot's chat, not from the device
        Assert.Equal("m1", _twitch.Requests.Single().MessageId);
    }

    [Fact]
    public async Task Kick_timeouts_report_the_duration_applied()
    {
        Say("k", platform: "Kick", userId: "k1");
        var sam = Member(DefaultRoles.StandardModId);
        var answer = await Call(sam, "chat.act", new { action = "Timeout", platform = "Kick", messageId = "k", durationSeconds = 570, reason = "spam" });
        Assert.True(Ok(answer));
        Assert.Equal(600, (int)answer["appliedSeconds"]!);
        Assert.Equal("[mod: Sam] spam", _kick.Requests.Single().Reason);
    }

    [Fact]
    public async Task Bad_requests_are_explained()
    {
        Say("m1", canDelete: false);
        var sam = Member(DefaultRoles.StandardModId);
        Assert.Contains("no id", (string)(await Call(sam, "chat.act", new { action = "Delete", platform = "Twitch", messageId = "m1" }))["error"]!);
        Assert.Contains("no longer", (string)(await Call(sam, "chat.act", new { action = "Delete", platform = "Twitch", messageId = "zz" }))["error"]!);
        Assert.Contains("Pick", (string)(await Call(sam, "chat.act", new { action = "Delete", platform = "Twitch" }))["error"]!);
        Assert.Contains("Pick", (string)(await Call(sam, "chat.act", new { action = "Ban", platform = "Twitch" }))["error"]!);
        Assert.Contains("tool", (string)(await Call(sam, "chat.act", new { action = "Dance", platform = "Twitch" }))["error"]!);
        Assert.Contains("14 days", (string)(await Call(sam, "chat.act", new { action = "Timeout", platform = "Twitch", userId = "u1", durationSeconds = 9_999_999 }))["error"]!);
        Assert.Contains("500", (string)(await Call(sam, "chat.act", new { action = "Ban", platform = "Twitch", userId = "u1", reason = new string('x', 501) }))["error"]!);
        Assert.Contains("held message first", (string)(await Call(sam, "chat.act", new { action = "ApproveHeld", platform = "Twitch", messageId = "m1" }))["error"]!);
        Assert.Empty(_twitch.Requests);
    }

    [Fact]
    public async Task Website_tools_return_the_link_instead_of_acting()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var answer = await Call(lead, "chat.act", new { action = "Clear", platform = "Kick" });
        Assert.False(Ok(answer));
        Assert.Equal("https://kick.com/dashboard", (string)answer["websiteUrl"]!);
        Assert.Empty(_kick.Requests);
    }

    // ---------------------------------------------------------------- refusals from ModerationService

    [Fact]
    public async Task Rate_limit_refusals_reach_the_mod_and_the_log()
    {
        var sam = Member(DefaultRoles.StandardModId);
        for (var i = 0; i < 2; i++)
            Assert.True(Ok(await Call(sam, "chat.act", new { action = "Ban", platform = "Twitch", userId = $"x{i}", user = $"Spammer {i}" })));
        var third = await Call(sam, "chat.act", new { action = "Ban", platform = "Twitch", userId = "x3", user = "Spammer 3" });
        Assert.False(Ok(third));
        Assert.Equal("Skipped", (string)third["result"]!);
        Assert.Contains("2 bans", (string)third["error"]!);
        Assert.Equal("Skipped", (string)third["row"]!["result"]!);
        Assert.Equal(2, _twitch.Requests.Count);

        // Limits are per member: another mod is not held up.
        var kim = Member(DefaultRoles.StandardModId);
        Assert.True(Ok(await Call(kim, "chat.act", new { action = "Ban", platform = "Twitch", userId = "x3" })));
    }

    [Fact]
    public async Task Protected_targets_are_refused_even_when_the_device_hides_their_roles()
    {
        Say("mod1", user: "Mia", userId: "mia", roles: ChatRoles.Moderator);
        Say("vip1", user: "Vic", userId: "vic", roles: ChatRoles.Vip);
        var lead = Member(DefaultRoles.LeadModId);

        var byMessage = await Call(lead, "chat.act", new { action = "Timeout", platform = "Twitch", messageId = "mod1" });
        Assert.Contains("is a moderator", (string)byMessage["error"]!);
        // No message id and no roles from the device: Cuebot still knows Vic is a VIP from the chat.
        var byUser = await Call(lead, "chat.act", new { action = "Ban", platform = "Twitch", userId = "vic", user = "not vic" });
        Assert.Contains("is a VIP", (string)byUser["error"]!);

        // A portal member's Twitch account.
        var (other, _) = Join(DefaultRoles.StandardModId, IdentityProvider.Twitch, "tw-42");
        var member = await Call(lead, "chat.act", new { action = "Ban", platform = "Twitch", userId = "tw-42" });
        Assert.Contains("member of your mod portal", (string)member["error"]!);
        Assert.Contains(other.Name, (string)member["error"]!);

        Assert.Empty(_twitch.Requests);
        Assert.All(_modLog.Recent(), r => Assert.Equal(ModResult.Skipped, r.Result));
    }

    [Fact]
    public async Task A_suspended_member_cannot_act()
    {
        Say("m1");
        var sam = Member(DefaultRoles.StandardModId);
        _dir.SetSuspended(sam.Id, true);
        var answer = await Call(sam, "chat.act", new { action = "Delete", platform = "Twitch", messageId = "m1" });
        Assert.False(Ok(answer));
        Assert.Contains("paused", (string)answer["error"]!);
        Assert.Empty(_twitch.Requests);
    }

    // ---------------------------------------------------------------- push topics

    private FakeLink Link((PortalMember Member, PortalDevice Device) m) =>
        new(new PortalLinkContext(1, new byte[32]) { MemberId = m.Member.Id, DeviceId = m.Device.Id });

    private JsonObject Subscribe(FakeLink link, string topic, object? parameters = null)
    {
        var bytes = _push.TryHandle(link, JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", topic, @params = parameters ?? new { } }), out var done);
        done?.Invoke();
        return (JsonObject)JsonNode.Parse(bytes!)!;
    }

    [Fact]
    public void Topics_need_their_rights()
    {
        var standard = Link(Join(DefaultRoles.StandardModId));
        Assert.True(Ok(Subscribe(standard, PortalChat.ChatTopic)));
        Assert.True(Ok(Subscribe(standard, PortalChat.LogTopic)));

        var chatOnly = Link(Join(_dir.SaveRole(new PortalRole { Name = "Chat only", Permissions = [PortalPermission.ViewChat] }).Id));
        Assert.True(Ok(Subscribe(chatOnly, PortalChat.ChatTopic)));
        Assert.False(Ok(Subscribe(chatOnly, PortalChat.LogTopic)));

        var none = Link(Join(_dir.SaveRole(new PortalRole { Name = "Sounds", Permissions = [PortalPermission.UploadSounds] }).Id));
        var refused = Subscribe(none, PortalChat.ChatTopic);
        Assert.False(Ok(refused));
        Assert.Contains("role", (string)refused["error"]!);

        Assert.False(Ok(Subscribe(standard, PortalChat.ChatTopic, new { platform = 5 })));
    }

    [Fact]
    public async Task Pushes_messages_deletions_and_log_rows_filtered_by_platform()
    {
        var sam = Join(DefaultRoles.StandardModId);
        var all = Link(sam);
        var kickOnly = Link(Join(DefaultRoles.StandardModId));
        Subscribe(all, PortalChat.ChatTopic);
        Subscribe(all, PortalChat.LogTopic);
        Subscribe(kickOnly, PortalChat.ChatTopic, new { platform = "Kick" });

        Say("m1", text: "<script>x</script>");
        Say("k1", platform: "Kick", userId: "k");
        await Call(sam.Member, "chat.act", new { action = "Delete", platform = "Twitch", messageId = "m1" });
        _bus.Publish(new BotEvent(ChatHub.MessageDeletedEvent, new Dictionary<string, object?> { ["platform"] = "Twitch", ["messageId"] = "m1" }));
        await _push.FlushAsync();

        var events = all.Sent.Where(f => (string)f["push"]! == PortalChat.ChatTopic).SelectMany(f => f["events"]!.AsArray()).ToList();
        Assert.Equal(["message", "message", "moderation"], events.Select(e => (string)e!["type"]!));
        Assert.Equal("<script>x</script>", (string)events[0]!["message"]!["text"]!);
        Assert.Equal("deleted", (string)events[2]!["notice"]!["kind"]!);
        var rows = all.Sent.Where(f => (string)f["push"]! == PortalChat.LogTopic).SelectMany(f => f["events"]!.AsArray()).ToList();
        Assert.Equal("delete", (string)rows.Single()!["row"]!["action"]!);

        var kick = kickOnly.Sent.SelectMany(f => f["events"]!.AsArray()).ToList();
        Assert.Equal("k1", (string)kick.Single()!["message"]!["id"]!);
    }
}
