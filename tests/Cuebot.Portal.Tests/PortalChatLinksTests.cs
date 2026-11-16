using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;
using Cuebot.Portal.Chat;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Tests;

/// <summary>A moderator for the link tests: fails bans on demand, or has no API (by hand on the website).</summary>
internal sealed class LinkTestModerator(string platform, string? website = null) : IChatModerator
{
    public readonly List<ModerationRequest> Requests = [];
    public bool Fail { get; set; }
    public string Platform => platform;
    public ChatModerationActions Available => ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban;
    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;

    public ModerationCapability Capability(ModerationAction action) =>
        website is not null ? ModerationCapability.Website(action, website, $"{platform} has no moderation API.")
        : ModerationCapability.IsQueueTool(action) || action is ModerationAction.Pin or ModerationAction.Unpin ? ModerationCapability.NotAvailable(action, "No.")
        : ModerationCapability.Works(action);

    public Task<ModerationOutcome> ExecuteAsync(ModerationRequest request, CancellationToken ct = default)
    {
        if (Fail && request.Action == ModerationAction.Ban) return Task.FromResult(ModerationOutcome.Failed($"{platform} said no"));
        lock (Requests) Requests.Add(request);
        return Task.FromResult(ModerationOutcome.Done(request.Duration));
    }
}

/// <summary>Identity links, notes and linked bans in the mod portal (PortalChat.Links.cs), per op and role.</summary>
public sealed class PortalChatLinksTests : IDisposable
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
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cuebot-portal-links-{Guid.NewGuid():N}.db");
    private readonly ModLog _modLog;
    private readonly LinkTestModerator _twitch = new("Twitch");
    private readonly LinkTestModerator _kick = new("Kick");
    private readonly LinkTestModerator _rumble = new("Rumble", "https://rumble.com/account/moderation");
    private readonly ModerationSettings _settings = new() { IdentityLinksEnabled = true };
    private readonly IdentityLinks _links;
    private readonly ModerationService _moderation;
    private readonly PortalDirectory _dir = new(new PortalSettings { Enabled = true }, () => { });
    private readonly PortalChat _chat;
    private readonly PortalPush _push;

    public PortalChatLinksTests()
    {
        _bus = new EventBus(_log);
        _hub = new ChatHub(_bus, _log, [new TestChatAdapter("Twitch"), new TestChatAdapter("Kick"), new TestChatAdapter("Rumble")], []);
        _modLog = new ModLog(_dbPath);
        var protectedAccounts = new PortalProtectedAccounts(_dir);
        _links = new IdentityLinks(_modLog, () => _settings, [protectedAccounts], hub: _hub, appLog: _log);
        _moderation = new ModerationService(new ChatModerationService([_twitch, _kick, _rumble], []), _modLog, _hub, [protectedAccounts],
            settings: () => _settings, appLog: _log)
        {
            Links = _links,
        };
        _chat = new PortalChat(_dir, _hub, _moderation, _log);
        _push = new PortalPush(_dir, limits: new PortalPushLimits { FlushInterval = Timeout.InfiniteTimeSpan });
        _chat.Attach(_push);
        Say("t1", "Rowdy", "u1", "I'm rowdyguy on Kick btw");
        Say("k1", "rowdyguy", "k1", "hey it's me from twitch", platform: "Kick");
        Say("r1", "RowdyG", "r1", "hi", platform: "Rumble");
        Say("t2", "Innocent", "u2", "hello");
        Say("t3", "ModMia", "mia", "hi all", ChatRoles.Moderator);
    }

    public void Dispose()
    {
        _chat.Dispose();
        _push.Dispose();
        _moderation.Dispose();
        _links.Dispose();
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

    private PortalMember Standard() => Join(DefaultRoles.StandardModId).Member;
    private PortalMember Lead() => Join(DefaultRoles.LeadModId).Member;

    private PortalMember Custom(params PortalPermission[] permissions) =>
        Join(_dir.SaveRole(new PortalRole { Name = "Custom " + Guid.NewGuid(), Permissions = [.. permissions] }).Id).Member;

    private Task<JsonObject> Call(PortalMember member, string op, object? body = null)
    {
        var request = body is null ? new JsonObject() : (JsonObject)JsonSerializer.SerializeToNode(body)!;
        return _chat.HandleAsync(member, _dir.RoleOf(member), op, request);
    }

    private void Say(string id, string user, string userId, string text = "hello", ChatRoles roles = ChatRoles.None, string platform = "Twitch") =>
        _bus.Publish(new BotEvent("test.chat", new Dictionary<string, object?>
        {
            ["platform"] = platform, ["id"] = id, ["user"] = user, ["userId"] = userId, ["text"] = text, ["roles"] = (int)roles, ["canDelete"] = true,
        }));

    private static bool Ok(JsonObject answer) => (bool)answer["ok"]!;
    private static string Error(JsonObject answer) => (string?)answer["error"] ?? "";

    /// <summary>Sam (standard) suggests, Alex (lead) confirms.</summary>
    private async Task<string> LinkAsync(PortalMember proposer, PortalMember confirmer, string platform, string userId, string otherPlatform, string otherUserId)
    {
        var proposed = await Call(proposer, "chat.link.propose", new { platform, userId, otherPlatform, otherUserId, choice = "Merge" });
        Assert.True(Ok(proposed), Error(proposed));
        var linkId = _links.PersonOf(otherPlatform, otherUserId)!.Account(otherPlatform, otherUserId)!.LinkId;
        var confirmed = await Call(confirmer, "chat.link.confirm", new { linkId });
        Assert.True(Ok(confirmed), Error(confirmed));
        return linkId;
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public void Link_operations_follow_the_role()
    {
        var standard = DefaultRoles.Create().Single(r => r.Id == DefaultRoles.StandardModId);
        var lead = DefaultRoles.Create().Single(r => r.Id == DefaultRoles.LeadModId);
        var none = new PortalRole { Name = "Chat only", Permissions = [PortalPermission.ViewChat] };
        foreach (var op in new[] { LinkOperation.View, LinkOperation.Propose, LinkOperation.ReadNotes, LinkOperation.AddNote })
            Assert.Null(PortalModerationPermissions.RefuseLink(standard, op));
        foreach (var op in new[] { LinkOperation.Confirm, LinkOperation.Reject, LinkOperation.Unlink, LinkOperation.DeleteNote })
        {
            Assert.NotNull(PortalModerationPermissions.RefuseLink(standard, op));
            Assert.Null(PortalModerationPermissions.RefuseLink(lead, op));
        }
        foreach (var op in Enum.GetValues<LinkOperation>())
            Assert.NotNull(PortalModerationPermissions.RefuseLink(none, op));
        Assert.True(PortalModerationPermissions.CanViewLinks(new PortalRole { Permissions = [PortalPermission.NotesRead] }));

        var hook = new PortalModerationPermissions(_dir);
        Assert.Null(hook.RefuseLink(ModActor.Streamer, LinkOperation.Confirm));
        Assert.NotNull(hook.RefuseLink(ModActor.Automation("x"), LinkOperation.View));
        var sam = Standard();
        Assert.Null(hook.RefuseLink(ModActor.Member(sam.Id.ToString(), "Sam"), LinkOperation.Propose));
        _dir.SetSuspended(sam.Id, true, "test");
        Assert.Contains("paused", hook.RefuseLink(ModActor.Member(sam.Id.ToString(), "Sam"), LinkOperation.Propose));
    }

    [Fact]
    public async Task Every_link_op_is_refused_while_links_are_off()
    {
        _settings.IdentityLinksEnabled = false;
        var lead = Lead();
        var caps = await Call(lead, "chat.capabilities");
        Assert.False((bool)caps["links"]!["enabled"]!);
        foreach (var op in new[] { "chat.person", "chat.link.propose", "chat.link.confirm", "chat.link.reject", "chat.link.unlinkPreview",
                     "chat.link.unlink", "chat.link.verify", "chat.note.add", "chat.note.edit", "chat.note.delete", "chat.linked.preview",
                     "chat.linked.execute", "chat.linked.manual", "chat.linked.retry", "chat.incident.undo", "chat.laterArrivals" })
        {
            Assert.True(PortalChat.Handles(op), op);
            var answer = await Call(lead, op, new { platform = "Twitch", userId = "u1", action = "Ban" });
            Assert.False(Ok(answer), op);
            Assert.Contains("turned off", Error(answer));
        }
        Assert.Empty(_modLog.Recent(10));

        _settings.IdentityLinksEnabled = true;
        caps = await Call(lead, "chat.capabilities");
        Assert.True((bool)caps["links"]!["enabled"]!);
        Assert.True((bool)caps["links"]!["rights"]!["confirm"]!);
    }

    [Fact]
    public async Task A_role_without_link_rights_sees_nothing()
    {
        var chatOnly = Custom(PortalPermission.ViewChat, PortalPermission.ViewModLog, PortalPermission.ChatBan);
        var person = await Call(chatOnly, "chat.person", new { platform = "Twitch", userId = "u1" });
        Assert.Contains("linked accounts", Error(person));
        Assert.Contains("suggesting", Error(await Call(chatOnly, "chat.link.propose",
            new { platform = "Twitch", userId = "u1", otherPlatform = "Kick", otherUserId = "k1" })));
        Assert.Contains("suggesting", Error(await Call(chatOnly, "chat.link.verify", new { platform = "Twitch", userId = "u1" })));
        Assert.Contains("writing notes", Error(await Call(chatOnly, "chat.note.add", new { platform = "Twitch", userId = "u1", text = "x" })));
        Assert.False(Ok(await Call(chatOnly, "chat.laterArrivals")));
        var noChat = Custom(PortalPermission.UploadSounds);
        Assert.Contains("role", Error(await Call(noChat, "chat.person", new { platform = "Twitch", userId = "u1" })));
    }

    // ---------------------------------------------------------------- links

    [Fact]
    public async Task Propose_reads_accounts_and_evidence_from_cuebots_chat()
    {
        var sam = Standard();
        var proposed = await Call(sam, "chat.link.propose", new
        {
            platform = "Twitch", userId = "u1", user = "Somebody Else", otherPlatform = "Kick", otherUserId = "k1", note = "Said so in chat",
            messageIds = new[] { "t1", "k1" },
        });
        Assert.True(Ok(proposed), Error(proposed));

        var card = await Call(sam, "chat.person", new { platform = "Kick", userId = "k1" });
        Assert.True(Ok(card), Error(card));
        var accounts = card["person"]!["accounts"]!.AsArray();
        Assert.Equal(["rowdyguy", "Rowdy"], accounts.Select(a => (string)a!["name"]!));
        Assert.True((bool)accounts[0]!["isThis"]!);
        Assert.All(accounts, a => Assert.Equal("Suggested", (string)a!["status"]!));
        Assert.True((bool)accounts[1]!["mine"]!);
        Assert.Contains("not confirmed", (string)accounts[1]!["linkText"]!);
        var quotes = card["person"]!["evidence"]!.AsArray().Select(e => (string)e!["quote"]!).ToList();
        Assert.Contains("Twitch, Rowdy: I'm rowdyguy on Kick btw", quotes);
        Assert.Contains("Said so in chat", quotes);
        Assert.DoesNotContain(card.ToJsonString(), "personId");

        // Unknown accounts, foreign messages and protected accounts are refused.
        Assert.Contains("not seen", Error(await Call(sam, "chat.link.propose", new { platform = "Twitch", userId = "u1", otherPlatform = "Kick", otherUserId = "ghost" })));
        Assert.Contains("not from these two", Error(await Call(sam, "chat.link.propose",
            new { platform = "Twitch", userId = "u2", otherPlatform = "Rumble", otherUserId = "r1", messageIds = new[] { "t1" } })));
        Assert.Contains("moderator", Error(await Call(sam, "chat.link.propose", new { platform = "Twitch", userId = "mia", otherPlatform = "Rumble", otherUserId = "r1" })));
    }

    [Fact]
    public async Task Merge_or_move_is_asked_when_an_account_already_belongs_to_someone()
    {
        var sam = Standard();
        Assert.True(Ok(await Call(sam, "chat.link.propose", new { platform = "Twitch", userId = "u1", otherPlatform = "Kick", otherUserId = "k1" })));
        var asked = await Call(sam, "chat.link.propose", new { platform = "Rumble", userId = "r1", otherPlatform = "Kick", otherUserId = "k1" });
        Assert.False(Ok(asked));
        Assert.True((bool)asked["needsChoice"]!);
        Assert.Equal(2, asked["conflict"]!.AsArray().Count);
        Assert.True(Ok(await Call(sam, "chat.link.propose", new { platform = "Rumble", userId = "r1", otherPlatform = "Kick", otherUserId = "k1", choice = "Merge" })));
        Assert.Equal(3, _links.PersonOf("Kick", "k1")!.Accounts.Count);
    }

    [Fact]
    public async Task Nobody_confirms_their_own_suggestion()
    {
        var sam = Standard();
        var alex = Lead();
        var bea = Lead();
        Assert.True(Ok(await Call(alex, "chat.link.propose", new { platform = "Twitch", userId = "u1", otherPlatform = "Kick", otherUserId = "k1" })));
        var linkId = _links.PersonOf("Kick", "k1")!.Account("Kick", "k1")!.LinkId;

        var own = await Call(alex, "chat.link.confirm", new { linkId });
        Assert.False(Ok(own));
        Assert.Contains("You suggested this link", Error(own));
        Assert.Contains("confirming links", Error(await Call(sam, "chat.link.confirm", new { linkId })));
        var confirmed = await Call(bea, "chat.link.confirm", new { linkId });
        Assert.True(Ok(confirmed), Error(confirmed));
        Assert.Equal(LinkStatus.Confirmed, _links.PersonOf("Kick", "k1")!.Account("Kick", "k1")!.Status);
    }

    [Fact]
    public async Task Reject_and_unlink_need_confirm_links_and_unlink_lists_what_to_reverse()
    {
        var sam = Standard();
        var alex = Lead();
        Assert.True(Ok(await Call(sam, "chat.link.propose", new { platform = "Twitch", userId = "u2", otherPlatform = "Rumble", otherUserId = "r1" })));
        var suggestion = _links.PersonOf("Rumble", "r1")!.Account("Rumble", "r1")!.LinkId;
        Assert.Contains("rejecting", Error(await Call(Standard(), "chat.link.reject", new { linkId = suggestion })));
        Assert.True(Ok(await Call(alex, "chat.link.reject", new { linkId = suggestion, reason = "different people" })));
        Assert.Null(_links.PersonOf("Rumble", "r1"));

        var linkId = await LinkAsync(sam, alex, "Twitch", "u1", "Kick", "k1");
        Assert.True(Ok(await Call(alex, "chat.act", new { action = "Ban", platform = "Kick", userId = "k1", reason = "evading" })));
        // The ban above was not through the link; one that was is listed for reversal.
        var preview = await Call(sam, "chat.linked.preview", new { action = "Timeout", platform = "Twitch", userId = "u1", durationSeconds = 600 });
        var done = await Call(sam, "chat.linked.execute", new { previewId = (string)preview["previewId"]!, ticked = new[] { "origin", linkId }, reason = "spam" });
        Assert.True(Ok(done), Error(done));

        Assert.Contains("unlinking", Error(await Call(sam, "chat.link.unlinkPreview", new { linkId, wrong = true })));
        var what = await Call(alex, "chat.link.unlinkPreview", new { linkId, wrong = true });
        Assert.True(Ok(what), Error(what));
        Assert.True((bool)what["reverseByDefault"]!);
        var inForce = what["inForce"]!.AsArray();
        Assert.Equal("timeout", (string)inForce.Single()!["action"]!);

        var noBan = Custom(PortalPermission.ViewChat, PortalPermission.ConfirmLinks);
        Assert.Contains("lifting bans", Error(await Call(noBan, "chat.link.unlink",
            new { linkId, wrong = true, reverse = new[] { (string)inForce[0]!["id"]! } })));
        var unlinked = await Call(alex, "chat.link.unlink", new { linkId, wrong = true, reason = "wrong", reverse = new[] { (string)inForce[0]!["id"]! } });
        Assert.True(Ok(unlinked), Error(unlinked));
        Assert.Contains("Lifted 1 of 1", (string)unlinked["reversed"]!["summary"]!);
        Assert.Null(_links.PersonOf("Kick", "k1"));
    }

    [Fact]
    public async Task Verify_gives_the_mod_a_one_time_code()
    {
        var sam = Standard();
        var answer = await Call(sam, "chat.link.verify", new { platform = "Twitch", userId = "u1" });
        Assert.True(Ok(answer), Error(answer));
        var code = (string)answer["code"]!;
        Assert.Equal(6, code.Length);
        Assert.Equal(10, (int)answer["minutes"]!);
        Assert.Contains(code, (string)answer["instructions"]!);
        Assert.Contains("moderator", Error(await Call(sam, "chat.link.verify", new { platform = "Twitch", userId = "mia" })));

        Say("k9", "rowdyguy", "k1", $"!link {code}", platform: "Kick");
        Assert.Equal(LinkStatus.Verified, _links.PersonOf("Twitch", "u1")?.Account("Kick", "k1")?.Status);
    }

    // ---------------------------------------------------------------- notes

    [Fact]
    public async Task Notes_follow_the_notes_rights()
    {
        var sam = Standard();
        var alex = Lead();
        var added = await Call(alex, "chat.note.add", new { platform = "Twitch", userId = "u1", text = "Warned twice about spam" });
        Assert.True(Ok(added), Error(added));
        var noteId = (string)added["note"]!["id"]!;
        var mine = await Call(sam, "chat.note.add", new { platform = "Twitch", userId = "u1", text = "Seems fine now" });
        Assert.True(Ok(mine));

        var card = await Call(sam, "chat.person", new { platform = "Twitch", userId = "u1" });
        Assert.Equal(2, card["notes"]!.AsArray().Count);
        Assert.Contains("deleting other people's notes", Error(await Call(sam, "chat.note.delete", new { noteId })));
        Assert.Contains("deleting other people's notes", Error(await Call(sam, "chat.note.edit", new { noteId, text = "changed" })));
        Assert.True(Ok(await Call(sam, "chat.note.edit", new { noteId = (string)mine["note"]!["id"]!, text = "Edited" })));
        Assert.Contains("under", Error(await Call(sam, "chat.note.edit", new { noteId = (string)mine["note"]!["id"]!, text = new string('x', 2001) })));
        Assert.True(Ok(await Call(alex, "chat.note.delete", new { noteId })));

        var noRead = Custom(PortalPermission.ViewChat, PortalPermission.ProposeLinks);
        var noNotes = await Call(noRead, "chat.person", new { platform = "Twitch", userId = "u1" });
        Assert.True(Ok(noNotes));
        Assert.Null(noNotes["notes"]);
    }

    // ---------------------------------------------------------------- linked bans

    [Fact]
    public async Task Linked_ban_runs_each_platform_with_retry_by_hand_and_undo()
    {
        var sam = Standard();
        var alex = Lead();
        var kickLink = await LinkAsync(sam, alex, "Twitch", "u1", "Kick", "k1");
        var rumbleLink = await LinkAsync(sam, alex, "Twitch", "u1", "Rumble", "r1");
        _kick.Fail = true;

        var preview = await Call(sam, "chat.linked.preview", new { action = "Ban", platform = "Twitch", messageId = "t1", user = "Fake name" });
        Assert.True(Ok(preview), Error(preview));
        Assert.True((bool)preview["linked"]!);
        var rows = preview["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Rowdy", (string)rows[0]!["name"]!);
        var rumble = rows.Single(r => (string)r!["rowId"]! == rumbleLink)!;
        Assert.True((bool)rumble["byHand"]!);
        Assert.Equal("https://rumble.com/account/moderation", (string)rumble["websiteUrl"]!);
        Assert.Equal(["origin", kickLink], preview["defaultTicked"]!.AsArray().Select(t => (string)t!));
        var previewId = (string)preview["previewId"]!;
        string[] ticked = ["origin", kickLink];

        Assert.Contains("reason", Error(await Call(sam, "chat.linked.execute", new { previewId, ticked })));
        var ask = await Call(sam, "chat.linked.execute", new { previewId, ticked, reason = "ban evasion" });
        Assert.True((bool)ask["needsConfirmation"]!);
        Assert.Equal("Ban 2 accounts on 2 platforms?", (string)ask["confirmText"]!);
        Assert.Empty(_twitch.Requests);
        Assert.Contains("expired", Error(await Call(alex, "chat.linked.execute", new { previewId, ticked, reason = "x", confirmed = true })));

        var done = await Call(sam, "chat.linked.execute", new { previewId, ticked, reason = "ban evasion", confirmed = true });
        Assert.True(Ok(done), Error(done));
        Assert.True((bool)done["partial"]!);
        var incidentId = (string)done["incidentId"]!;
        var results = done["rows"]!.AsArray().ToDictionary(r => (string)r!["rowId"]!);
        Assert.Equal("Ok", (string)results["origin"]!["result"]!);
        Assert.Equal("Failed", (string)results[kickLink]!["result"]!);
        Assert.True((bool)results[kickLink]!["canRetry"]!);
        Assert.True((bool)results[rumbleLink]!["awaitingManual"]!);
        Assert.Contains("expired", Error(await Call(sam, "chat.linked.execute", new { previewId, ticked, reason = "again", confirmed = true })));

        // Retry and "I did it" are only for this mod's own incident, and only for those rows.
        Assert.Contains("no longer has", Error(await Call(alex, "chat.linked.retry", new { incidentId, rowId = kickLink })));
        Assert.Contains("did not fail", Error(await Call(sam, "chat.linked.retry", new { incidentId, rowId = "origin" })));
        _kick.Fail = false;
        var retried = await Call(sam, "chat.linked.retry", new { incidentId, rowId = kickLink });
        Assert.True(Ok(retried), Error(retried));
        Assert.Single(_kick.Requests);
        Assert.Contains("not waiting", Error(await Call(sam, "chat.linked.manual", new { incidentId, rowId = kickLink })));
        var manual = await Call(sam, "chat.linked.manual", new { incidentId, rowId = rumbleLink });
        Assert.True(Ok(manual), Error(manual));
        Assert.Equal("Manual", (string)manual["row"]!["result"]!);
        Assert.True((bool)manual["row"]!["linked"]!);
        Assert.False(Ok(await Call(sam, "chat.linked.manual", new { incidentId, rowId = rumbleLink })));

        // Undo incident lifts what Cuebot did; the by-hand ban is lifted by hand.
        Assert.Contains("lifting", Error(await Call(Custom(PortalPermission.ViewChat, PortalPermission.ProposeLinks), "chat.incident.undo", new { incidentId })));
        var undone = await Call(alex, "chat.incident.undo", new { incidentId });
        Assert.True(Ok(undone), Error(undone));
        var lifted = undone["rows"]!.AsArray();
        Assert.Equal(2, lifted.Count(r => (string)r!["result"]! == "Ok"));
        Assert.Contains(lifted, r => (string)r!["platform"]! == "Rumble" && (string)r!["websiteUrl"]! == "https://rumble.com/account/moderation");
        Assert.Contains(_twitch.Requests, r => r.Action == ModerationAction.Unban);
        Assert.Contains(_kick.Requests, r => r.Action == ModerationAction.Unban);
        // Only the by-hand ban is still in force: undoing again says to lift it by hand, and sends nothing.
        var again = await Call(alex, "chat.incident.undo", new { incidentId });
        Assert.Equal(["Rumble"], again["rows"]!.AsArray().Select(r => (string)r!["platform"]!));
        Assert.Single(_kick.Requests, r => r.Action == ModerationAction.Unban);
    }

    [Fact]
    public async Task Suggested_links_and_missing_rights_never_reach_a_platform()
    {
        var sam = Standard();
        var alex = Lead();
        await LinkAsync(sam, alex, "Twitch", "u1", "Kick", "k1");
        Assert.True(Ok(await Call(sam, "chat.link.propose", new { platform = "Twitch", userId = "u1", otherPlatform = "Rumble", otherUserId = "r1", choice = "Merge" })));
        var suggested = _links.PersonOf("Rumble", "r1")!.Account("Rumble", "r1")!.LinkId;

        // The by-hand row through a suggestion cannot be ticked for a member's ban, even when the device says so.
        var preview = await Call(sam, "chat.linked.preview", new { action = "Ban", platform = "Twitch", userId = "u1" });
        Assert.DoesNotContain(suggested, preview["defaultTicked"]!.AsArray().Select(t => (string)t!));
        var done = await Call(sam, "chat.linked.execute",
            new { previewId = (string)preview["previewId"]!, ticked = new[] { "origin", suggested }, reason = "spam" });
        Assert.True(Ok(done), Error(done));
        Assert.Equal(["origin"], done["rows"]!.AsArray().Select(r => (string)r!["rowId"]!));

        // Without the linked right only the account clicked is shown.
        var noLinked = Custom(PortalPermission.ViewChat, PortalPermission.ChatDeleteTimeout, PortalPermission.ProposeLinks);
        var single = await Call(noLinked, "chat.linked.preview", new { action = "Timeout", platform = "Twitch", userId = "u1", durationSeconds = 60 });
        Assert.True(Ok(single), Error(single));
        Assert.False((bool)single["linked"]!);
        Assert.Single(single["rows"]!.AsArray());
        Assert.Contains("Your role does not include timeouts", (string)single["linkedNote"]!);
        Assert.Contains("role", Error(await Call(noLinked, "chat.linked.preview", new { action = "Ban", platform = "Twitch", userId = "u1" })));
        Assert.Empty(_kick.Requests);
    }

    [Fact]
    public async Task Later_arrivals_can_be_listed_applied_and_dismissed()
    {
        var sam = Standard();
        var alex = Lead();
        Assert.True(Ok(await Call(alex, "chat.act", new { action = "Ban", platform = "Twitch", userId = "u1", reason = "spam" })));
        await LinkAsync(sam, alex, "Twitch", "u1", "Kick", "k1");
        for (var i = 0; i < 100 && _moderation.LaterArrivals.Count == 0; i++) await Task.Delay(20);

        var list = await Call(sam, "chat.laterArrivals");
        var item = list["items"]!.AsArray().Single()!;
        Assert.Equal("k1", (string)item["userId"]!);
        Assert.Equal(["Twitch"], item["bannedOn"]!.AsArray().Select(b => (string)b!));
        var card = await Call(sam, "chat.person", new { platform = "Kick", userId = "k1" });
        Assert.Single(card["laterArrivals"]!.AsArray());

        var noLinked = Custom(PortalPermission.ViewChat, PortalPermission.ChatBan, PortalPermission.ProposeLinks);
        Assert.Contains("linked", Error(await Call(noLinked, "chat.laterArrivals", new { mode = "apply", id = (string)item["id"]!, reason = "evading" })));
        Assert.Contains("reason", Error(await Call(sam, "chat.laterArrivals", new { mode = "apply", id = (string)item["id"]! })));
        var applied = await Call(sam, "chat.laterArrivals", new { mode = "apply", id = (string)item["id"]!, reason = "evading" });
        Assert.True(Ok(applied), Error(applied));
        Assert.Contains(_kick.Requests, r => r.Action == ModerationAction.Ban);
        Assert.Empty(applied["items"]!.AsArray());
        Assert.Contains("already", Error(await Call(sam, "chat.laterArrivals", new { mode = "dismiss", id = (string)item["id"]! })));
    }

    // ---------------------------------------------------------------- push and the log

    private FakeLink Link(PortalMember member) =>
        new(new PortalLinkContext(1, new byte[32]) { MemberId = member.Id, DeviceId = _dir.Member(member.Id)!.Devices[0].Id });

    private void Subscribe(FakeLink link, string topic)
    {
        var bytes = _push.TryHandle(link, JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", topic, @params = new { } }), out var done);
        done?.Invoke();
        Assert.True((bool)JsonNode.Parse(bytes!)!["ok"]!);
    }

    [Fact]
    public async Task Link_changes_and_link_rows_reach_only_roles_that_see_links()
    {
        var sam = Standard();
        var alex = Lead();
        var logOnly = Custom(PortalPermission.ViewChat, PortalPermission.ViewModLog);
        var samLink = Link(sam);
        var otherLink = Link(logOnly);
        foreach (var l in new[] { samLink, otherLink })
        {
            Subscribe(l, PortalChat.LinksTopic);
            Subscribe(l, PortalChat.LogTopic);
        }

        await LinkAsync(sam, alex, "Twitch", "u1", "Kick", "k1");
        await _push.FlushAsync();

        var links = samLink.Sent.Where(f => (string)f["push"]! == PortalChat.LinksTopic).SelectMany(f => f["events"]!.AsArray()).ToList();
        Assert.Equal(["proposed", "confirmed"], links.Where(e => (string)e!["type"]! == "changed").Select(e => (string)e!["kind"]!));
        var samRows = samLink.Sent.Where(f => (string)f["push"]! == PortalChat.LogTopic).SelectMany(f => f["events"]!.AsArray()).ToList();
        Assert.Contains(samRows, e => (string)e!["row"]!["action"]! == "link-confirm");
        Assert.DoesNotContain(otherLink.Sent, f => (string)f["push"]! == PortalChat.LinksTopic);
        Assert.DoesNotContain(otherLink.Sent, f => (string)f["push"]! == PortalChat.LogTopic);

        Assert.Empty((await Call(logOnly, "chat.log"))["rows"]!.AsArray());
        Assert.NotEmpty((await Call(sam, "chat.log"))["rows"]!.AsArray());
        _settings.IdentityLinksEnabled = false;
        Assert.Empty((await Call(sam, "chat.log"))["rows"]!.AsArray());
    }
}
