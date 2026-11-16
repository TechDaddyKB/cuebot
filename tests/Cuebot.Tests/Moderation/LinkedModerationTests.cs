using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Users;
using static Cuebot.Tests.Moderation.LinkHarness;

namespace Cuebot.Tests.Moderation;

/// <summary>
/// Linked bans and timeouts (phase 5): the preview for every mix of capability, link status and quota; execution with
/// partial failure and manual rows; the second confirmation; rate limits; incident undo (with a failed unban); undoing a
/// member's last hour; unlinking with reversal; later arrivals; automatic mirroring on and off; streamer notices.
/// </summary>
public sealed class LinkedModerationTests : IDisposable
{
    private readonly LinkHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ModerationService Service => _h.Service;

    private static ModerationCommand Ban(ViewerRef v) =>
        new(v.Platform, ModerationAction.Ban) { TargetUserId = v.UserId, TargetName = v.UserName };

    private static ModerationCommand Timeout(ViewerRef v, TimeSpan duration) =>
        new(v.Platform, ModerationAction.Timeout) { TargetUserId = v.UserId, TargetName = v.UserName, Duration = duration };

    // ---------------------------------------------------------------- preview

    public enum Cap { Works, Website, Missing }

    public enum Quota { Plenty, Short }

    public static TheoryData<ModerationAction, Cap, LinkStatus, Quota, bool, bool, string> PreviewCases()
    {
        // action, YouTube capability, link status, quota, ticked, can tick, what will happen (start) + notes fragment
        var data = new TheoryData<ModerationAction, Cap, LinkStatus, Quota, bool, bool, string>();
        foreach (var action in new[] { ModerationAction.Ban, ModerationAction.Timeout })
            foreach (var cap in Enum.GetValues<Cap>())
                foreach (var status in new[] { LinkStatus.Suggested, LinkStatus.Confirmed, LinkStatus.Verified })
                    foreach (var quota in Enum.GetValues<Quota>())
                    {
                        bool ticked, canTick;
                        string expect;
                        if (cap == Cap.Website) (ticked, canTick, expect) = (false, false, "By hand: open YouTube|No API");
                        else if (cap == Cap.Missing) (ticked, canTick, expect) = (false, false, "Not available|cannot do this now");
                        else if (quota == Quota.Short) (ticked, canTick, expect) = (false, false, "quota units|Not enough YouTube quota");
                        else if (status == LinkStatus.Suggested)
                            (ticked, canTick, expect) = (false, action == ModerationAction.Timeout, "quota units|Unticked: not confirmed");
                        else (ticked, canTick, expect) = (true, true, "quota units|");
                        data.Add(action, cap, status, quota, ticked, canTick, expect);
                    }
        return data;
    }

    [Theory]
    [MemberData(nameof(PreviewCases))]
    public async Task Preview_rows_follow_capability_link_status_and_quota(ModerationAction action, Cap cap, LinkStatus status, Quota quota,
        bool ticked, bool canTick, string expect)
    {
        await _h.LinkAsync(Tw(), Yt(), status);
        if (cap == Cap.Website) _h.YouTube.Website = "https://studio.youtube.com/";
        if (cap == Cap.Missing) _h.YouTube.Missing.Add(action);
        _h.Quota.Left = quota == Quota.Plenty ? 9_000 : 2_100;
        var command = action == ModerationAction.Ban ? Ban(Tw()) : Timeout(Tw(), TimeSpan.FromMinutes(10));

        var preview = await Service.BuildPreviewAsync(command, Sam);

        Assert.True(preview.IsLinked);
        Assert.Equal(("origin", true, true, "(the one clicked)"), (preview.Origin.RowId, preview.Origin.Ticked, preview.Origin.CanTick, preview.Origin.LinkText));
        var row = Assert.Single(preview.Rows, r => !r.IsOrigin);
        Assert.Equal(("YouTube", "Rowdy Guy", status), (row.Platform, row.Name, row.Status));
        Assert.Equal((ticked, canTick), (row.Ticked, row.CanTick));
        var parts = expect.Split('|');
        if (parts[0] == "quota units")
        {
            Assert.StartsWith(action == ModerationAction.Ban ? "Ban" : "Timeout 10 min", row.WillHappen);
            Assert.Contains("200 quota units", row.WillHappen);
            Assert.Equal(quota == Quota.Plenty ? 9_000 : 2_100, row.QuotaLeft);
        }
        else Assert.StartsWith(parts[0], row.WillHappen);
        if (parts[1].Length > 0) Assert.Contains(parts[1], row.Notes);
        Assert.Contains(status switch { LinkStatus.Verified => "verified 2026-11-16", LinkStatus.Confirmed => "confirmed by Sam and Alex", _ => "suggested by Sam" },
            row.LinkText);
        Assert.Empty(_h.YouTube.Calls);   // a dry run
    }

    [Fact]
    public async Task Suggested_links_may_be_ticked_for_a_ban_only_by_the_streamer()
    {
        await _h.LinkAsync(Tw(), Yt(), LinkStatus.Suggested);
        var row = (await Service.BuildPreviewAsync(Ban(Tw()), ModActor.Streamer)).Rows[1];
        Assert.Equal((false, true), (row.Ticked, row.CanTick));
    }

    [Fact]
    public async Task Timeouts_show_the_duration_each_platform_really_applies()
    {
        await _h.LinkAsync(Tw(), Kk());
        var preview = await Service.BuildPreviewAsync(Timeout(Tw(), TimeSpan.FromSeconds(570)), Sam);
        Assert.Equal("Timeout 9 min 30 s", preview.Rows[0].WillHappen);
        Assert.Equal("Timeout 10 min", preview.Rows[1].WillHappen);
        Assert.Contains("rounded up from 9 min 30 s", preview.Rows[1].Notes);
    }

    [Fact]
    public async Task Rumble_is_a_by_hand_row_and_already_banned_accounts_start_unticked()
    {
        await _h.LinkAsync(Tw(), Rb());
        await _h.LinkAsync(Tw(), Kk());
        await Service.ExecuteAsync(Ban(Kk()) with { Reason = "earlier" }, ModActor.Streamer);
        _h.Kick.Calls.Clear();

        var preview = await Service.BuildPreviewAsync(Ban(Tw()), Sam);

        var rumble = preview.Rows.Single(r => r.Platform == "Rumble");
        Assert.Equal((true, false, "By hand: open Rumble", "https://rumble.com/chat/popup"), (rumble.ByHand, rumble.CanTick, rumble.WillHappen, rumble.WebsiteUrl));
        var kick = preview.Rows.Single(r => r.Platform == "Kick");
        Assert.Equal((false, true, true), (kick.Ticked, kick.CanTick, kick.AlreadyActioned));
        Assert.Contains("Already banned", kick.Notes);
    }

    [Fact]
    public async Task Without_links_or_with_the_setting_off_the_preview_has_one_row()
    {
        Assert.False((await Service.BuildPreviewAsync(Ban(Tw()), Sam)).IsLinked);
        await _h.LinkAsync(Tw(), Yt());
        _h.Settings.IdentityLinksEnabled = false;
        var preview = await Service.BuildPreviewAsync(Ban(Tw()), Sam);
        Assert.Single(preview.Rows);
        var linkedTick = await Service.ExecuteLinkedAsync(new LinkedExecution(preview, ["origin"], "spam"), Sam);
        Assert.Null(linkedTick.Refused);
        Assert.Equal(["Ban tw1 [mod: Sam] spam"], _h.Twitch.Calls);
        Assert.Empty(_h.YouTube.Calls);
    }

    // ---------------------------------------------------------------- execute

    [Fact]
    public async Task A_linked_ban_is_one_incident_with_independent_calls_partial_failure_and_manual_rows()
    {
        await _h.LinkAsync(Tw(), Yt());
        await _h.LinkAsync(Tw(), Kk(), LinkStatus.Verified);
        await _h.LinkAsync(Tw(), Rb());
        _h.Kick.Failing.Add(ModerationAction.Ban);
        var preview = await Service.BuildPreviewAsync(Ban(Tw()), Sam);

        Assert.Equal("A reason is required.", (await Service.ExecuteLinkedAsync(new LinkedExecution(preview, preview.DefaultTicked, "  "), Sam)).Refused);
        var ask = await Service.ExecuteLinkedAsync(new LinkedExecution(preview, preview.DefaultTicked, "ban evasion"), Sam);
        Assert.True(ask.NeedsConfirmation);
        Assert.Equal("Ban 3 accounts on 3 platforms?", ask.ConfirmText);
        Assert.Empty(_h.Twitch.Calls);

        var result = await Service.ExecuteLinkedAsync(new LinkedExecution(preview, preview.DefaultTicked, "ban evasion") { Confirmed = true }, Sam);

        Assert.True(result.Partial);
        Assert.Equal(["Ban tw1 [mod: Sam] ban evasion"], _h.Twitch.Calls);
        Assert.Equal(["Ban UCyt1 ban evasion"], _h.YouTube.Calls);   // no "[mod: ...]" on YouTube, which keeps no reason
        var byPlatform = result.Rows.ToDictionary(r => r.Row.Platform);
        Assert.Equal(ModResult.Ok, byPlatform["Twitch"].Result);
        Assert.Equal(ModResult.Ok, byPlatform["YouTube"].Result);
        Assert.Equal(ModResult.Failed, byPlatform["Kick"].Result);
        Assert.Contains("Kick said no", byPlatform["Kick"].Message);
        Assert.True(byPlatform["Rumble"].AwaitingManual);
        Assert.Equal("https://rumble.com/chat/popup", byPlatform["Rumble"].WebsiteUrl);
        Assert.Equal("Banned on Twitch, YouTube; failed on Kick; by hand: Rumble.", result.Summary);

        var rows = Service.IncidentRows(result.IncidentId);
        Assert.Equal(3, rows.Count);
        Assert.Null(rows.Single(r => r.Platform == "Twitch").ViaLinkId);
        Assert.NotNull(rows.Single(r => r.Platform == "YouTube").ViaLinkId);
        Assert.Equal("YTBAN-1", rows.Single(r => r.Platform == "YouTube").PlatformRef);

        var manual = Service.RecordManual(result.IncidentId, preview, byPlatform["Rumble"].Row.RowId, "ban evasion", Sam);
        Assert.Equal((ModResult.Manual, result.IncidentId, "ban"), (manual!.Result, manual.IncidentId, manual.Action));

        _h.Kick.Failing.Clear();
        var retry = await Service.RetryRowAsync(result.IncidentId, preview, byPlatform["Kick"].Row.RowId, "ban evasion", Sam);
        Assert.Equal(ModResult.Ok, retry.Result);
        Assert.Equal(5, Service.IncidentRows(result.IncidentId).Count);

        var notice = Assert.Single(_h.Notices);
        Assert.Equal("Sam made a linked ban", notice.Title);
        Assert.Equal(result.IncidentId, notice.IncidentId);
    }

    [Fact]
    public async Task Reasons_are_trimmed_per_platform_after_the_mod_prefix()
    {
        await _h.LinkAsync(Tw(), Kk());
        var preview = await Service.BuildPreviewAsync(Timeout(Tw(), TimeSpan.FromMinutes(5)), Sam);
        await Service.ExecuteLinkedAsync(new LinkedExecution(preview, preview.DefaultTicked, new string('x', 200)), Sam);
        var kick = _h.Log.Recent().Single(r => r.Platform == "Kick" && r.Action == "timeout");
        Assert.Equal(100, kick.Reason.Length);
        Assert.StartsWith("[mod: Sam] xxx", kick.Reason);
        Assert.Equal(211, _h.Log.Recent().Single(r => r.Platform == "Twitch" && r.Action == "timeout").Reason.Length);
    }

    [Fact]
    public async Task Members_may_start_five_linked_bans_an_hour()
    {
        for (var i = 0; i < 6; i++) await _h.LinkAsync(Tw("tw" + i, "a" + i), Kk("k" + i, "b" + i));
        for (var i = 0; i < 5; i++)
        {
            var p = await Service.BuildPreviewAsync(Ban(Tw("tw" + i, "a" + i)), Sam);
            Assert.Null((await Service.ExecuteLinkedAsync(new LinkedExecution(p, p.DefaultTicked, "spam") { Confirmed = true }, Sam)).Refused);
        }
        var sixth = await Service.BuildPreviewAsync(Ban(Tw("tw5", "a5")), Sam);
        Assert.Contains("5 linked bans", (await Service.ExecuteLinkedAsync(new LinkedExecution(sixth, sixth.DefaultTicked, "spam") { Confirmed = true }, Sam)).Refused);
        // Others and the streamer are not limited by Sam's count; Sam is again after an hour.
        Assert.Null((await Service.ExecuteLinkedAsync(new LinkedExecution(sixth, sixth.DefaultTicked, "spam") { Confirmed = true }, ModActor.Streamer)).Refused);
        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.LinkAsync(Tw("tw9", "z"), Kk("k9", "z"));
        var later = await Service.BuildPreviewAsync(Ban(Tw("tw9", "z")), Sam);
        Assert.Null((await Service.ExecuteLinkedAsync(new LinkedExecution(later, later.DefaultTicked, "spam") { Confirmed = true }, Sam)).Refused);
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public async Task Undo_lifts_every_row_that_worked_with_the_youtube_ban_id_and_reports_a_failed_unban()
    {
        await _h.LinkAsync(Tw(), Yt());
        await _h.LinkAsync(Tw(), Kk());
        await _h.LinkAsync(Tw(), Rb());
        var preview = await Service.BuildPreviewAsync(Ban(Tw()), Sam);
        var done = await Service.ExecuteLinkedAsync(new LinkedExecution(preview, preview.DefaultTicked, "evasion") { Confirmed = true }, Sam);
        Service.RecordManual(done.IncidentId, preview, preview.Rows.Single(r => r.ByHand).RowId, "evasion", Sam);
        Assert.True(Service.CanUndoIncident(done.IncidentId));
        _h.Kick.Failing.Add(ModerationAction.Unban);

        var undo = await Service.UndoIncidentAsync(done.IncidentId, Alex);

        Assert.Contains("Unban UCyt1 ref=YTBAN-1", _h.YouTube.Calls);
        Assert.Contains("Unban tw1 ref=", _h.Twitch.Calls);
        var kick = undo.Rows.Single(r => r.Row.Platform == "Kick");
        Assert.Equal(ModResult.Failed, kick.Result);
        var rumble = undo.Rows.Single(r => r.Row.Platform == "Rumble");
        Assert.Contains("by hand", rumble.Message);
        Assert.Equal("Lifted 2 of 4. Some need another try or doing by hand.", undo.Summary);
        Assert.Null(_h.Log.FindInForce("Twitch", "tw1"));
        Assert.NotNull(_h.Log.FindInForce("Kick", "k1"));   // the failed unban leaves it in force
        Assert.True(Service.CanUndoIncident(done.IncidentId));
        var lifts = Service.IncidentRows(undo.UndoIncidentId);
        Assert.All(lifts, r => Assert.Equal("unban", r.Action));
        Assert.Contains(_h.Notices, n => n.Title == "Alex undid an incident");

        _h.Kick.Failing.Clear();
        var again = await Service.UndoIncidentAsync(done.IncidentId, ModActor.Streamer);
        Assert.Equal(ModResult.Ok, again.Rows.Single(r => r.Row.Platform == "Kick").Result);
    }

    [Fact]
    public async Task Deleted_messages_and_warnings_cannot_be_undone()
    {
        var incident = "incident-1";
        await Service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Delete) { TargetUserId = "tw1", MessageId = "m1", IncidentId = incident }, ModActor.Streamer);
        await Service.ExecuteAsync(new ModerationCommand("Twitch", ModerationAction.Warn) { TargetUserId = "tw1", Reason = "be nice", IncidentId = incident }, ModActor.Streamer);
        var undo = await Service.UndoIncidentAsync(incident, ModActor.Streamer);
        Assert.Empty(undo.Rows);
        Assert.Equal(["Deleted messages cannot be restored on any platform.", "Warnings cannot be withdrawn."], undo.CannotUndo);
        Assert.False(Service.CanUndoIncident(incident));
    }

    [Fact]
    public async Task The_streamer_undoes_everything_a_member_did_in_the_last_hour()
    {
        await Service.ExecuteAsync(Ban(Kk("k-old", "old")), Sam);
        _h.Time.Advance(TimeSpan.FromMinutes(90));
        await Service.ExecuteAsync(Ban(Tw("tw7", "x")), Sam);
        await Service.ExecuteAsync(Timeout(Kk("k7", "y"), TimeSpan.FromHours(1)), Sam);
        await Service.ExecuteAsync(Ban(Tw("tw8", "z")), Alex);
        var suggestion = await _h.Links.ProposeAsync(Tw("tw10", "p"), Yt("UC10", "q"), Sam);

        Assert.NotNull((await Service.UndoMemberRecentAsync("member-sam", Sam)).Refused);
        var undo = await Service.UndoMemberRecentAsync("member-sam", ModActor.Streamer);

        Assert.Equal(2, undo.Rows.Count(r => r.Result == ModResult.Ok));
        Assert.Contains("Withdrew 1 link suggestion", undo.Summary);
        Assert.Null(_h.Links.PersonOf("Twitch", "tw10"));
        Assert.Null(_h.Log.FindInForce("Twitch", "tw7"));
        Assert.Null(_h.Log.FindInForce("Kick", "k7"));
        Assert.NotNull(_h.Log.FindInForce("Twitch", "tw8"));   // Alex's
        Assert.NotNull(_h.Log.FindInForce("Kick", "k-old"));   // older than an hour
        _ = suggestion;
    }

    [Fact]
    public async Task Unlinking_a_wrong_link_offers_to_reverse_what_it_caused()
    {
        var person = await _h.LinkAsync(Tw(), Yt());
        var preview = await Service.BuildPreviewAsync(Ban(Tw()), ModActor.Streamer);
        await Service.ExecuteLinkedAsync(new LinkedExecution(preview, preview.DefaultTicked, "evasion") { Confirmed = true }, ModActor.Streamer);
        var link = person.Account("YouTube", "UCyt1")!.LinkId;

        var wrong = Service.PreviewUnlink(link, dissolve: false, wrong: true)!;
        Assert.True(wrong.ReverseByDefault);
        Assert.Equal("Also reverse the 1 action that was applied because of this link?", wrong.Question);
        Assert.False(Service.PreviewUnlink(link, false, wrong: false)!.ReverseByDefault);

        var (unlink, reversed) = await Service.UnlinkAsync(link, ModActor.Streamer, dissolve: false, wrong: true, "different people",
            wrong.InForce.Select(r => r.Id).ToArray());

        Assert.True(unlink.Ok);
        Assert.Equal(ModResult.Ok, Assert.Single(reversed!.Rows).Result);
        Assert.Null(_h.Log.FindInForce("YouTube", "UCyt1"));
        Assert.NotNull(_h.Log.FindInForce("Twitch", "tw1"));   // the account clicked keeps its own ban
        Assert.Single(_h.Log.Rejections());
    }

    // ---------------------------------------------------------------- later arrivals and mirroring

    [Fact]
    public async Task A_newly_confirmed_account_of_a_banned_person_raises_a_prompt()
    {
        await _h.LinkAsync(Tw(), Yt());
        await Service.ExecuteAsync(Ban(Tw()) with { Reason = "raid" }, ModActor.Streamer);
        await Service.ExecuteAsync(Ban(Yt()) with { Reason = "raid" }, ModActor.Streamer);

        await _h.LinkAsync(Tw(), Kk());
        await Service.LastMirror;

        var prompt = Assert.Single(Service.LaterArrivals);
        Assert.Equal("rowdyguy on Kick is linked to a person banned on Twitch and YouTube. Apply here too?", prompt.Text);
        Assert.Empty(_h.Kick.Calls);
        Assert.Contains(_h.Notices, n => n.Message == prompt.Text);

        var applied = await Service.ApplyLaterArrivalAsync(prompt.Id, Sam, "same person, banned elsewhere");
        Assert.True(applied.Ok, applied.Message);
        Assert.Equal(["Ban k1 [mod: Sam] same person, banned elsewhere"], _h.Kick.Calls);
        Assert.NotNull(applied.Entry!.ViaLinkId);
        Assert.Empty(Service.LaterArrivals);
    }

    [Fact]
    public async Task Suggestions_raise_no_prompt()
    {
        await _h.LinkAsync(Tw(), Yt());
        await Service.ExecuteAsync(Ban(Tw()) with { Reason = "raid" }, ModActor.Streamer);
        await _h.LinkAsync(Tw(), Kk(), LinkStatus.Suggested);
        await Service.LastMirror;
        Assert.Empty(Service.LaterArrivals);
    }

    [Fact]
    public async Task Mirroring_off_leaves_other_platforms_alone()
    {
        await _h.LinkAsync(Tw(), Yt());
        await Service.ExecuteAsync(Ban(Tw()) with { Reason = "raid" }, Sam);
        Service.RecordExternal(new ExternalModerationNotice("Kick", "ban", _h.Time.GetUtcNow()) { TargetUserId = "k1", Moderator = "kickmod" });
        await Service.LastMirror;
        Assert.Empty(_h.YouTube.Calls);
    }

    [Fact]
    public async Task Mirroring_on_applies_bans_from_any_source_to_verified_and_confirmed_links_only()
    {
        _h.Settings.AutomaticMirroring = true;
        await _h.LinkAsync(Tw(), Yt());
        await _h.LinkAsync(Tw(), Kk(), LinkStatus.Verified);
        await _h.LinkAsync(Tw(), Rb());                                     // by hand: skipped
        await _h.LinkAsync(Tw(), Tw("tw-alt", "alt"), LinkStatus.Suggested); // never through a suggestion

        var source = await Service.ExecuteAsync(Ban(Tw()) with { Reason = "raid" }, Sam);

        Assert.Equal(["Ban UCyt1 Mirrored from Twitch: [mod: Sam] raid"], _h.YouTube.Calls);
        Assert.Equal(["Ban k1 Mirrored from Twitch: [mod: Sam] raid"], _h.Kick.Calls);
        Assert.Equal(["Ban tw1 [mod: Sam] raid"], _h.Twitch.Calls);
        var incident = Service.IncidentRows(source.Entry!.IncidentId);
        Assert.Equal(3, incident.Count);
        Assert.All(incident.Skip(1), r => Assert.Equal((ModActorKind.Automation, "Automatic mirroring"), (r.ActorKind, r.ActorName)));
        Assert.Contains(_h.Notices, n => n.Title == "Automatic mirroring applied a ban" && n.IncidentId == source.Entry.IncidentId);

        // Undo as one incident.
        var undo = await Service.UndoIncidentAsync(source.Entry.IncidentId, ModActor.Streamer);
        Assert.Equal(3, undo.Rows.Count(r => r.Result == ModResult.Ok));
    }

    [Fact]
    public async Task Mirroring_on_follows_a_ban_made_on_the_website()
    {
        _h.Settings.AutomaticMirroring = true;
        await _h.LinkAsync(Kk(), Yt());
        Service.RecordExternal(new ExternalModerationNotice("Kick", "ban", _h.Time.GetUtcNow()) { TargetUserId = "k1", TargetName = "rowdyguy", Moderator = "kickmod" });
        await Service.LastMirror;
        Assert.Equal(["Ban UCyt1 Mirrored from Kick"], _h.YouTube.Calls);
        var external = _h.Log.Recent().Single(r => r.Platform == "Kick" && r.Action == "ban");
        Assert.Equal(external.IncidentId, _h.Log.Recent().Single(r => r.Platform == "YouTube" && r.Action == "ban").IncidentId);
    }

    [Fact]
    public async Task Mirroring_on_applies_a_later_arrival_at_once()
    {
        _h.Settings.AutomaticMirroring = true;
        await _h.LinkAsync(Tw(), Yt());
        _h.Settings.AutomaticMirroring = false;
        await Service.ExecuteAsync(Ban(Tw()) with { Reason = "raid" }, ModActor.Streamer);
        _h.Settings.AutomaticMirroring = true;
        _h.YouTube.Calls.Clear();

        await _h.LinkAsync(Yt(), Kk(), LinkStatus.Verified);
        await Service.LastMirror;

        Assert.Equal(["Ban k1 Mirrored from Twitch: raid"], _h.Kick.Calls);
        Assert.Empty(Service.LaterArrivals);
    }
}
