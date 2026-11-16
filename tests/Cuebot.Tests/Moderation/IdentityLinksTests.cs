using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Users;
using Microsoft.Data.Sqlite;
using static Cuebot.Tests.Moderation.LinkHarness;

namespace Cuebot.Tests.Moderation;

/// <summary>
/// Identity links (phase 4): the setting, protected accounts, two-person confirmation, merges and moves, limits,
/// rejections, unlinking, <c>!link</c> verification, notes, hints, retention, the YouTube refresh, erasure and export,
/// and the mod log's schema migration from version 1.
/// </summary>
public sealed class IdentityLinksTests : IDisposable
{
    private readonly LinkHarness _h = new();

    public void Dispose() => _h.Dispose();

    private IdentityLinks Links => _h.Links;

    // ---------------------------------------------------------------- the setting

    [Fact]
    public async Task Everything_is_off_until_the_streamer_turns_identity_links_on()
    {
        Assert.False(new ModerationSettings().IdentityLinksEnabled);
        Assert.False(new ModerationSettings().AutomaticMirroring);
        _h.Settings.IdentityLinksEnabled = false;

        Assert.False((await Links.ProposeAsync(Tw(), Yt(), ModActor.Streamer)).Ok);
        Assert.Null((await Links.StartVerificationAsync(Tw(), ModActor.Streamer)).Verification);
        Assert.Null(Links.AddNote(Tw(), "note", ModActor.Streamer).Note);
        Assert.Empty(Links.SameNameHints("Twitch", "tw1", "rowdy_guy"));
        Assert.Equal(LinkVerifyOutcome.Disabled, (await Links.RedeemAsync(Yt(), "ABCDEF")).Outcome);
        Assert.Empty(_h.Log.Recent());
    }

    [Fact]
    public async Task Links_made_while_on_are_hidden_while_off_and_back_when_on()
    {
        await _h.LinkAsync(Tw(), Yt());
        _h.Settings.IdentityLinksEnabled = false;
        Assert.Null(Links.PersonOf("Twitch", "tw1"));
        Assert.Empty(Links.People());
        _h.Settings.IdentityLinksEnabled = true;
        Assert.Equal(2, Links.PersonOf("Twitch", "tw1")!.Accounts.Count);
    }

    // ---------------------------------------------------------------- rules

    [Theory]
    [InlineData("broadcaster")]
    [InlineData("moderator")]
    [InlineData("vip")]
    [InlineData("bot-registry")]
    [InlineData("bot-1")]
    [InlineData("portal-member")]
    [InlineData("mod-by-lookup")]
    [InlineData("moderator-registry")]
    public async Task Protected_accounts_can_never_be_linked_even_by_the_streamer(string who)
    {
        var target = Tw(who.StartsWith("bot-1") || who is "portal-member" or "mod-by-lookup" ? who : "tw-" + who);
        var roles = who switch
        {
            "broadcaster" => ChatRoles.Broadcaster,
            "moderator" => ChatRoles.Moderator,
            "vip" => ChatRoles.Vip,
            _ => ChatRoles.None,
        };
        if (who == "bot-registry") _h.Seen(target, bot: true);
        if (who == "moderator-registry") _h.Seen(target, moderator: true);

        var result = await Links.ProposeAsync(Yt(), target, ModActor.Streamer, toRoles: roles);

        Assert.False(result.Ok);
        Assert.Contains("never be linked", result.Message);
        Assert.Null((await Links.StartVerificationAsync(target, ModActor.Streamer, roles)).Verification);
    }

    [Fact]
    public async Task A_suggestion_needs_a_second_person_and_the_proposer_cannot_confirm_it()
    {
        var proposed = await Links.ProposeAsync(Tw(), Yt(), Sam, "said so in chat",
            [new LinkEvidenceInput(LinkEvidenceKinds.ViewerSaid, "I'm rowdy_guy on Twitch") { MessageIds = ["m1"] }]);
        Assert.True(proposed.Ok, proposed.Message);
        Assert.All(proposed.Person!.Accounts, a => Assert.Equal(LinkStatus.Suggested, a.Status));
        Assert.Contains(proposed.Person.Evidence, e => e.Kind == LinkEvidenceKinds.ViewerSaid && e.MessageIds.SequenceEqual(["m1"]));

        var self = Links.Confirm(proposed.LinkIds[0], Sam);
        Assert.False(self.Ok);
        Assert.Contains("someone else", self.Message);
        // A member without the "Confirm links" permission cannot either.
        Assert.False(Links.Confirm(proposed.LinkIds[0], ModActor.Member("member-pat", "Pat")).Ok);

        var confirmed = Links.Confirm(proposed.LinkIds[0], Alex);
        Assert.True(confirmed.Ok, confirmed.Message);
        Assert.All(confirmed.Person!.Accounts, a => Assert.Equal(LinkStatus.Confirmed, a.Status));
        Assert.Equal("confirmed by Sam and Alex", IdentityLinks.Describe(confirmed.Person.Accounts[1], LinkStatus.Confirmed));
        Assert.False(Links.Confirm(proposed.LinkIds[0], ModActor.Streamer).Ok);   // already confirmed

        var rows = _h.Log.Recent().Reverse().Select(r => (r.Action, r.ActorName, r.TargetUserId)).ToArray();
        Assert.Equal([("link", "Sam", "tw1"), ("link", "Sam", "UCyt1"), ("link-confirm", "Alex", "tw1"), ("link-confirm", "Alex", "UCyt1")], rows);
        Assert.Null(_h.Log.FindBrokenRow());
    }

    [Fact]
    public async Task The_streamer_confirms_for_a_one_mod_team_but_not_their_own_suggestion()
    {
        var byMod = await Links.ProposeAsync(Tw(), Yt(), Sam);
        Assert.True(Links.Confirm(byMod.LinkIds[0], ModActor.Streamer).Ok);

        var byStreamer = await Links.ProposeAsync(Kk(), Rb(), ModActor.Streamer);
        Assert.False(Links.Confirm(byStreamer.LinkIds[0], ModActor.Streamer).Ok);
        Assert.True(Links.Confirm(byStreamer.LinkIds[0], Alex).Ok);
    }

    [Fact]
    public async Task An_account_already_in_a_person_asks_to_merge_or_move()
    {
        var first = await _h.LinkAsync(Tw(), Yt());
        var second = await _h.LinkAsync(Kk(), Rb());

        var ask = await Links.ProposeAsync(Tw(), Kk(), Sam);
        Assert.True(ask.NeedsChoice);
        Assert.Equal(second.Id, ask.Conflict!.Id);
        Assert.Equal(2, Links.PersonOf("Twitch", "tw1")!.Accounts.Count);   // nothing changed

        var moved = await Links.ProposeAsync(Tw(), Kk(), Sam, choice: LinkConflictChoice.Move);
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(3, moved.Person!.Accounts.Count);
        Assert.Equal(LinkStatus.Suggested, moved.Person.Account("Kick", "k1")!.Status);
        Assert.Null(Links.PersonOf("Rumble", "r1"));   // left alone, so the old person is gone
        Assert.Contains(_h.Log.Recent(), r => r.Action == "link-move");

        await _h.LinkAsync(Rb(), new ViewerRef("Kick", "k2", "other"));
        var merged = await Links.ProposeAsync(Yt(), Rb(), Sam, choice: LinkConflictChoice.Merge);
        Assert.True(merged.Ok, merged.Message);
        Assert.Equal(first.Id, merged.Person!.Id);
        Assert.Equal(5, merged.Person.Accounts.Count);
        Assert.Equal(LinkStatus.Confirmed, merged.Person.Account("Twitch", "tw1")!.Status);
        Assert.Equal(LinkStatus.Suggested, merged.Person.Account("Rumble", "r1")!.Status);
    }

    [Fact]
    public async Task More_than_four_accounts_or_two_on_one_platform_need_the_streamer()
    {
        var alt = await Links.ProposeAsync(Tw(), Tw("tw2", "rowdy_alt"), Sam);
        Assert.True(alt.Ok);
        Assert.Contains("Only the streamer", alt.Message);
        Assert.All(alt.Person!.Accounts, a => Assert.True(a.NeedsStreamer));
        Assert.Contains("only the streamer", Links.Confirm(alt.LinkIds[0], Alex).Message);
        Assert.True(Links.Confirm(alt.LinkIds[0], ModActor.Streamer).Ok);

        var p = await _h.LinkAsync(Yt(), Kk());
        await _h.LinkAsync(Yt(), Rb());
        await _h.LinkAsync(Yt(), new ViewerRef("Trovo", "t1", "rowdy"));
        var fifth = await Links.ProposeAsync(Yt(), new ViewerRef("TikTok", "x1", "rowdy"), Sam);
        Assert.True(fifth.Person!.Account("TikTok", "x1")!.NeedsStreamer);
        Assert.False(fifth.Person.Account("Kick", "k1")!.NeedsStreamer);
        Assert.False(Links.Confirm(fifth.LinkIds[0], Alex).Ok);
        Assert.True(Links.Confirm(fifth.LinkIds[0], ModActor.Streamer).Ok);
        Assert.Equal(p.Id, Links.PersonOf("TikTok", "x1")!.Id);
    }

    [Fact]
    public async Task A_rejected_suggestion_is_remembered_and_not_made_again()
    {
        var proposed = await Links.ProposeAsync(Tw(), Yt(), Sam);
        Assert.False(Links.Reject(proposed.LinkIds[0], ModActor.Member("member-pat", "Pat")).Ok);
        var rejected = Links.Reject(proposed.LinkIds[0], Alex, "different people");
        Assert.True(rejected.Ok, rejected.Message);
        Assert.Null(Links.PersonOf("Twitch", "tw1"));
        Assert.Single(_h.Log.Rejections());
        Assert.Equal(2, _h.Log.Recent().Count(r => r.Action == "link-reject"));

        var again = await Links.ProposeAsync(Yt(), Tw(), Sam);
        Assert.False(again.Ok);
        Assert.Contains("marked wrong", again.Message);
        Assert.True((await Links.ProposeAsync(Yt(), Tw(), ModActor.Streamer)).Ok);
    }

    [Fact]
    public async Task Unlink_removes_one_account_or_dissolves_and_marks_wrong_pairs()
    {
        var person = await _h.LinkAsync(Tw(), Yt());
        await _h.LinkAsync(Tw(), Kk());
        person = Links.Person(person.Id)!;

        Assert.False(Links.Unlink(person.Account("Kick", "k1")!.LinkId, Sam).Ok);   // needs the permission
        var one = Links.Unlink(person.Account("Kick", "k1")!.LinkId, Alex, wrong: true);
        Assert.True(one.Ok, one.Message);
        Assert.Equal(2, Links.Person(person.Id)!.Accounts.Count);
        Assert.Equal(2, _h.Log.Rejections().Count);   // Kick with Twitch, Kick with YouTube

        var all = Links.Unlink(person.Account("Twitch", "tw1")!.LinkId, ModActor.Streamer, dissolve: true);
        Assert.True(all.Ok);
        Assert.Null(Links.Person(person.Id));
        Assert.Equal(3, _h.Log.Recent().Count(r => r.Action == "unlink"));
        Assert.Contains(_h.Log.Recent(), r => r.Action == "unlink" && r.Reason == "marked wrong");
    }

    [Fact]
    public async Task A_proposer_may_withdraw_their_own_suggestion()
    {
        var proposed = await Links.ProposeAsync(Tw(), Yt(), Sam);
        Assert.True(Links.Unlink(proposed.LinkIds[1], Sam).Ok);
        Assert.Null(Links.PersonOf("Twitch", "tw1"));
    }

    // ---------------------------------------------------------------- !link verification

    [Fact]
    public async Task A_viewer_verifies_with_a_one_time_code_from_the_other_account()
    {
        var (verification, instructions) = await Links.StartVerificationAsync(Tw(), Sam);
        Assert.NotNull(verification);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", verification!.Code);
        Assert.Contains("privately", instructions);
        Assert.Equal(_h.Time.GetUtcNow().AddMinutes(10), verification.ExpiresAt);

        Assert.Equal(LinkVerifyOutcome.SameAccount, (await Links.RedeemAsync(Tw(), verification.Code)).Outcome);   // not spent
        var result = await Links.RedeemAsync(Yt(), verification.Code.ToLowerInvariant(), messageId: "msg-9");
        Assert.Equal(LinkVerifyOutcome.Verified, result.Outcome);
        Assert.All(result.Person!.Accounts, a => Assert.Equal(LinkStatus.Verified, a.Status));
        Assert.Contains(result.Person.Evidence, e => e.Kind == LinkEvidenceKinds.VerifiedCode && e.MessageIds.Contains("msg-9"));
        Assert.Equal(2, _h.Log.Recent().Count(r => r.Action == "link-verify"));

        // Single use: the same code again is unknown.
        Assert.Equal(LinkVerifyOutcome.Unknown, (await Links.RedeemAsync(Kk(), verification.Code)).Outcome);
    }

    [Fact]
    public async Task Codes_expire_after_ten_minutes()
    {
        var (verification, _) = await Links.StartVerificationAsync(Tw(), Sam);
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(LinkVerifyOutcome.Expired, (await Links.RedeemAsync(Yt(), verification!.Code)).Outcome);
        Assert.Null(Links.PersonOf("Twitch", "tw1"));
        Assert.Empty(Links.OpenVerifications);
    }

    [Fact]
    public async Task Wrong_codes_are_rate_limited_per_account()
    {
        var (verification, _) = await Links.StartVerificationAsync(Tw(), Sam);
        for (var i = 0; i < 5; i++) Assert.Equal(LinkVerifyOutcome.Unknown, (await Links.RedeemAsync(Yt(), "ZZZZZ" + i)).Outcome);
        // Even the right code is ignored from this account for a while...
        Assert.Equal(LinkVerifyOutcome.RateLimited, (await Links.RedeemAsync(Yt(), verification!.Code)).Outcome);
        // ...but still works from another account, and the limit lifts after ten minutes.
        _h.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(LinkVerifyOutcome.Verified, (await Links.RedeemAsync(Kk(), verification.Code)).Outcome);
    }

    [Fact]
    public async Task A_protected_account_cannot_redeem_and_spends_the_code()
    {
        var (verification, _) = await Links.StartVerificationAsync(Tw(), Sam);
        Assert.Equal(LinkVerifyOutcome.Refused, (await Links.RedeemAsync(new ViewerRef("Kick", "bot-1", "bot"), verification!.Code)).Outcome);
        Assert.Equal(LinkVerifyOutcome.Unknown, (await Links.RedeemAsync(Yt(), verification.Code)).Outcome);
    }

    [Fact]
    public async Task Link_in_chat_answers_only_a_code_that_worked_and_only_while_on()
    {
        var (verification, _) = await Links.StartVerificationAsync(Tw(), Sam);
        Chat("YouTube", "UCyt1", "Rowdy Guy", "!link WRONG1");
        await WaitFor(() => false, 200);
        Assert.Empty(_h.Sent);

        Chat("YouTube", "UCyt1", "Rowdy Guy", "!link " + verification!.Code);
        await WaitFor(() => _h.Sent.Count > 0);
        Assert.Equal(("YouTube", "@Rowdy Guy thanks, that's confirmed."), Assert.Single(_h.Sent));
        Assert.Equal(LinkStatus.Verified, Links.PersonOf("YouTube", "UCyt1")!.Accounts[0].Status);

        _h.Settings.IdentityLinksEnabled = false;
        Chat("Kick", "k1", "rowdyguy", "!link " + verification.Code);
        await WaitFor(() => false, 200);
        Assert.Single(_h.Sent);
    }

    private void Chat(string platform, string userId, string user, string text) =>
        _h.Bus.Publish(new BotEvent($"{platform.ToLowerInvariant()}.chatMessage",
            new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString("N"), ["user"] = user, ["userId"] = userId, ["message"] = text }));

    private static async Task WaitFor(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
    }

    // ---------------------------------------------------------------- notes and hints

    [Fact]
    public async Task Notes_follow_links_and_only_their_author_or_a_lead_edits_them()
    {
        var (note, _) = Links.AddNote(Tw(), "Spammed links twice", Sam);
        Assert.NotNull(note);
        Assert.Empty(Links.NotesFor("YouTube", "UCyt1"));
        await _h.LinkAsync(Tw(), Yt());
        Assert.Equal("Spammed links twice", Assert.Single(Links.NotesFor("YouTube", "UCyt1")).Text);

        Assert.False(Links.EditNote(note!.Id, "changed", ModActor.Member("member-pat", "Pat")).Ok);
        Assert.True(Links.EditNote(note.Id, "Spammed links three times", Sam).Ok);
        Assert.False(Links.DeleteNote(note.Id, ModActor.Member("member-pat", "Pat")).Ok);
        Assert.True(Links.DeleteNote(note.Id, Alex).Ok);
        Assert.Empty(Links.NotesFor("Twitch", "tw1"));
    }

    [Fact]
    public async Task Same_names_are_only_a_hint()
    {
        _h.Seen(Tw("tw1", "Rowdy"));
        _h.Seen(Kk("k1", "rowdy"));
        _h.Seen(new ViewerRef("YouTube", "bot-1", "rowdy"));   // protected: never hinted

        var hints = Links.SameNameHints("Twitch", "tw1", "Rowdy");

        Assert.Equal([new ViewerRef("Kick", "k1", "rowdy")], hints);
        Assert.Null(Links.PersonOf("Kick", "k1"));
        await _h.LinkAsync(Tw("tw1", "Rowdy"), Kk("k1", "rowdy"));
        Assert.Empty(Links.SameNameHints("Twitch", "tw1", "Rowdy"));
    }

    // ---------------------------------------------------------------- retention and the YouTube rule

    [Fact]
    public async Task Retention_drops_old_suggestions_rejections_and_notes_but_keeps_active_links()
    {
        await _h.LinkAsync(Tw(), Yt());                                   // confirmed: stays
        await _h.LinkAsync(Kk(), Rb(), LinkStatus.Suggested);             // never confirmed: 60 days
        var rejected = await Links.ProposeAsync(Kk("k9", "x"), Rb("r9", "y"), Sam);
        Links.Reject(rejected.LinkIds[0], Alex);
        Links.AddNote(Tw(), "old note", Sam);

        _h.Time.Advance(TimeSpan.FromDays(59));
        Links.ApplyRetention(_h.Time.GetUtcNow());
        Assert.NotNull(Links.PersonOf("Kick", "k1"));

        _h.Time.Advance(TimeSpan.FromDays(2));
        Links.ApplyRetention(_h.Time.GetUtcNow());
        Assert.Null(Links.PersonOf("Kick", "k1"));
        Assert.NotNull(Links.PersonOf("Twitch", "tw1"));
        Assert.Single(_h.Log.Rejections());
        Assert.Single(Links.NotesFor("Twitch", "tw1"));

        _h.Time.Advance(TimeSpan.FromDays(24 * 31));
        Links.ApplyRetention(_h.Time.GetUtcNow());
        Assert.Empty(_h.Log.Rejections());
        Assert.Empty(Links.NotesFor("Twitch", "tw1"));
        Assert.NotNull(Links.PersonOf("Twitch", "tw1"));
    }

    [Fact]
    public async Task Link_rows_in_the_mod_log_keep_the_person_while_linked_and_24_months_after()
    {
        _h.Settings.TimeoutDays = 180;
        var person = await _h.LinkAsync(Tw(), Kk());
        _h.Time.Advance(TimeSpan.FromDays(400));
        _h.Service.ApplyRetention(_h.Time.GetUtcNow());
        Assert.All(_h.Log.Recent().Where(r => r.Action.StartsWith("link")), r => Assert.False(r.Redacted));

        Links.Unlink(person.Accounts[0].LinkId, ModActor.Streamer, dissolve: true);
        _h.Time.Advance(TimeSpan.FromDays(23 * 30));
        _h.Service.ApplyRetention(_h.Time.GetUtcNow());
        Assert.All(_h.Log.Recent(), r => Assert.False(r.Redacted));

        _h.Time.Advance(TimeSpan.FromDays(60));
        _h.Service.ApplyRetention(_h.Time.GetUtcNow());
        Assert.All(_h.Log.Recent(), r => Assert.True(r.Redacted && r.TargetUserId == ""));
        Assert.Null(_h.Log.FindBrokenRow());
    }

    [Fact]
    public async Task Youtube_links_are_refreshed_within_30_days_or_dropped()
    {
        await _h.LinkAsync(Tw(), Yt());
        await _h.LinkAsync(Kk(), Yt("UCgone", "Gone"));
        await _h.LinkAsync(Rb(), Yt("UCdown", "Down"));
        _h.Lookup.Names["UCyt1"] = "Rowdy Renamed";

        _h.Time.Advance(TimeSpan.FromDays(20));
        await Links.RefreshYouTubeAsync(_h.Time.GetUtcNow());
        Assert.Equal(0, _h.Lookup.Calls);   // not due yet

        _h.Time.Advance(TimeSpan.FromDays(6));
        await Links.RefreshYouTubeAsync(_h.Time.GetUtcNow());
        Assert.Equal(3, _h.Lookup.Calls);
        Assert.Equal("Rowdy Renamed", Links.PersonOf("Twitch", "tw1")!.Account("YouTube", "UCyt1")!.Name);
        Assert.Null(Links.PersonOf("Kick", "k1"));   // YouTube says these channels are gone: dropped (and the person with them)
        Assert.Null(Links.PersonOf("Rumble", "r1"));

        _h.Time.Advance(TimeSpan.FromDays(10));
        await Links.RefreshYouTubeAsync(_h.Time.GetUtcNow());
        Assert.Equal(3, _h.Lookup.Calls);   // refreshed 10 days ago: not due
    }

    [Fact]
    public async Task A_youtube_lookup_that_fails_is_retried_until_30_days_then_the_account_is_dropped()
    {
        await _h.LinkAsync(Tw(), Yt());
        _h.Lookup.Throw = true;
        _h.Time.Advance(TimeSpan.FromDays(26));
        await Links.RefreshYouTubeAsync(_h.Time.GetUtcNow());
        Assert.NotNull(Links.PersonOf("YouTube", "UCyt1"));

        // Seen in chat: that refreshes it too.
        _h.Bus.Publish(new BotEvent("youtube.chatMessage", new Dictionary<string, object?>
            { ["id"] = "m1", ["user"] = "Rowdy", ["userId"] = "UCyt1", ["message"] = "hello" }));
        _h.Time.Advance(TimeSpan.FromDays(5));
        await Links.RefreshYouTubeAsync(_h.Time.GetUtcNow());
        Assert.Equal("Rowdy", Links.PersonOf("YouTube", "UCyt1")!.Account("YouTube", "UCyt1")!.CurrentName);

        _h.Time.Advance(TimeSpan.FromDays(31));
        await Links.RefreshYouTubeAsync(_h.Time.GetUtcNow());
        Assert.Null(Links.PersonOf("Twitch", "tw1"));
    }

    // ---------------------------------------------------------------- forget this viewer

    [Fact]
    public async Task Forget_finds_the_person_by_any_account_exports_and_erases_but_keeps_active_bans()
    {
        await _h.LinkAsync(Tw(), Yt());
        await _h.LinkAsync(Tw(), Kk(), LinkStatus.Suggested);   // a guess: not followed, not erased
        _h.Seen(Tw());
        _h.Seen(Kk());
        Links.AddNote(Yt(), "warned about spam", Sam);
        await _h.Service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Ban) { TargetUserId = "tw1", TargetName = "rowdy_guy", Reason = "spam" },
            ModActor.Streamer);
        await _h.Service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Delete)
            { TargetUserId = "tw1", TargetName = "rowdy_guy", MessageId = "m1", MessageText = "buy followers" }, ModActor.Streamer);
        using var viewers = new ViewerData([_h.Users], _h.Users, _h.AppLog, _h.Time, [Links]);

        var accounts = viewers.AccountsOf("YouTube", "UCyt1", "Rowdy Guy");
        Assert.Equal(["YouTube:UCyt1", "Twitch:tw1"], accounts.Select(a => $"{a.Platform}:{a.UserId}"));

        var export = viewers.Export("YouTube", "UCyt1", "Rowdy Guy").ToJsonString();
        Assert.Contains("warned about spam", export);
        Assert.Contains("buy followers", export);
        Assert.Contains("\"status\":\"confirmed\"", export);
        Assert.DoesNotContain("k1", export);

        var forgot = viewers.Forget("YouTube", "UCyt1", "Rowdy Guy");
        Assert.True(forgot.Total > 0);
        Assert.Null(_h.Log.PersonByAccount("Twitch", "tw1"));
        Assert.Empty(_h.Log.NotesFor([("YouTube", "UCyt1")]));
        Assert.Null(_h.Users.Get("Twitch", "tw1"));
        Assert.NotNull(_h.Users.Get("Kick", "k1"));
        var ban = _h.Log.Recent().Single(r => r.Action == "ban");
        Assert.Equal(("tw1", "", "spam", true), (ban.TargetUserId, ban.TargetName, ban.Reason, ban.Redacted));
        Assert.True(ban.IsInForce(_h.Time.GetUtcNow()));
        var delete = _h.Log.Recent().Single(r => r.Action == "delete");
        Assert.Equal(("", "", ""), (delete.TargetUserId, delete.TargetName, delete.MessageText));
        Assert.All(_h.Log.Recent().Where(r => r.Action.StartsWith("link") && r.Platform != "Kick"), r => Assert.Equal("", r.TargetName));
        Assert.Null(_h.Log.FindBrokenRow());
    }

    // ---------------------------------------------------------------- schema migration

    [Fact]
    public void A_version_1_mod_log_is_upgraded_in_place_and_keeps_its_rows()
    {
        var path = Path.Combine(_h.Dir, "v1.db");
        using (var log = new ModLog(path, _h.Time))
        {
            log.Append(new ModLogEntry { ActorKind = ModActorKind.Streamer, ActorName = "Streamer", Platform = "Twitch", Action = "ban", TargetUserId = "u1", TargetName = "Bob", Result = ModResult.Ok });
            log.Append(new ModLogEntry { ActorKind = ModActorKind.Streamer, ActorName = "Streamer", Platform = "Kick", Action = "timeout", TargetUserId = "u2", TargetName = "Ann", DurationAppliedSeconds = 60, Result = ModResult.Ok });
        }
        // Turn it back into what phase 1 wrote: no identity tables, user_version 1.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE identity_persons; DROP TABLE identity_accounts; DROP TABLE identity_confirmations; DROP TABLE identity_evidence; " +
                              "DROP TABLE identity_rejections; DROP TABLE mod_notes; DROP INDEX ix_mod_actions_incident; DROP INDEX ix_mod_actions_via_link; " +
                              "PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }

        using (var upgraded = new ModLog(path, _h.Time))
        {
            Assert.Equal(2, upgraded.Recent().Count);
            Assert.Null(upgraded.FindBrokenRow());
            var links = new IdentityLinks(upgraded, () => _h.Settings, time: _h.Time);
            Assert.True(links.ProposeAsync(Tw(), Yt(), ModActor.Streamer).GetAwaiter().GetResult().Ok);
            Assert.True(links.AddNote(Tw(), "n", ModActor.Streamer).Note is not null);
            Assert.Equal(4, upgraded.Recent().Count);
        }
        using var reopened = new ModLog(path, _h.Time);
        Assert.NotNull(reopened.PersonByAccount("Twitch", "tw1"));
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal((long)ModLog.SchemaVersion, version.ExecuteScalar());
    }
}
