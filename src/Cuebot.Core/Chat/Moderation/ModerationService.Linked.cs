using System.Globalization;
using System.Security.Cryptography;
using Cuebot.Core.Users;

namespace Cuebot.Core.Chat.Moderation;

/// <summary>One account in a linked ban or timeout preview (docs/UNIFIED_MOD_CHAT_PLAN.md, section 7.4).</summary>
public sealed record LinkedPreviewRow
{
    public string RowId { get; init; } = "";
    public string Platform { get; init; } = "";
    public string UserId { get; init; } = "";

    /// <summary>The current name (kept current from chat), else the name when the link was made.</summary>
    public string Name { get; init; } = "";

    /// <summary>The account the mod clicked.</summary>
    public bool IsOrigin { get; init; }

    /// <summary>The membership this row goes through (null for the account clicked); logged as <c>viaLinkId</c>.</summary>
    public string? LinkId { get; init; }

    /// <summary>How sure the link to the clicked account is (null for the account clicked).</summary>
    public LinkStatus? Status { get; init; }

    /// <summary>"(the one clicked)", "verified 2026-10-02", "suggested by Sam, not confirmed".</summary>
    public string LinkText { get; init; } = "";

    public ModerationToolState State { get; init; }

    /// <summary>"Ban", "Timeout 10 min", "By hand: open Rumble", "Not available".</summary>
    public string WillHappen { get; init; } = "";

    /// <summary>For timeouts: what the platform will really apply.</summary>
    public TimeSpan? AppliedDuration { get; init; }

    /// <summary>YouTube quota units the call costs; 0 elsewhere.</summary>
    public int QuotaCost { get; init; }

    /// <summary>Units left today on that platform, when it charges quota.</summary>
    public int? QuotaLeft { get; init; }

    /// <summary>Why the box starts unticked, or what the mod should know: "Unticked: not confirmed", "No API", "Already banned".</summary>
    public string Notes { get; init; } = "";

    /// <summary>The box starts ticked: the account clicked, and verified or confirmed links that can run.</summary>
    public bool Ticked { get; init; }

    /// <summary>The mod may tick it (it can run and nothing forbids it).</summary>
    public bool CanTick { get; init; }

    /// <summary>No API: done by hand on the platform's website, then recorded with "I did it".</summary>
    public bool ByHand => State == ModerationToolState.OnWebsite;

    public string? WebsiteUrl { get; init; }

    /// <summary>The account is already banned (or, for a timeout, timed out) according to the mod log.</summary>
    public bool AlreadyActioned { get; init; }
}

/// <summary>The dry run of a linked ban or timeout: nothing has been sent to any platform.</summary>
public sealed record LinkedPreview
{
    public string Id { get; init; } = "";
    public ModerationCommand Command { get; init; } = new("", ModerationAction.Ban);
    public string? PersonId { get; init; }
    public IReadOnlyList<LinkedPreviewRow> Rows { get; init; } = [];
    public DateTimeOffset At { get; init; }

    /// <summary>More than the account clicked: the dialog is a linked action.</summary>
    public bool IsLinked => Rows.Count > 1;

    public LinkedPreviewRow Origin => Rows[0];

    public IReadOnlyList<string> DefaultTicked => Rows.Where(r => r.Ticked).Select(r => r.RowId).ToArray();

    /// <summary>A permanent ban touching more than one platform asks a second time (section 7.4, step 4).</summary>
    public bool NeedsSecondConfirmation(IEnumerable<string> ticked) =>
        Command.Action == ModerationAction.Ban && Selected(ticked).Select(r => r.Platform).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    /// <summary>"Ban 3 accounts on 3 platforms?"</summary>
    public string SecondConfirmationText(IEnumerable<string> ticked)
    {
        var rows = Selected(ticked).ToArray();
        var platforms = rows.Select(r => r.Platform).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"Ban {rows.Length} account{(rows.Length == 1 ? "" : "s")} on {platforms} platform{(platforms == 1 ? "" : "s")}?";
    }

    internal IEnumerable<LinkedPreviewRow> Selected(IEnumerable<string> ticked)
    {
        var set = ticked.ToHashSet(StringComparer.Ordinal);
        return Rows.Where(r => r.CanTick && set.Contains(r.RowId));
    }
}

/// <summary>What the mod chose in the preview: the ticked rows and the reason (required).</summary>
public sealed record LinkedExecution(LinkedPreview Preview, IReadOnlyCollection<string> Ticked, string Reason)
{
    /// <summary>The second confirmation was given (needed for a permanent ban on more than one platform).</summary>
    public bool Confirmed { get; init; }
}

/// <summary>How one row of a linked action ended.</summary>
public sealed record LinkedRowResult(LinkedPreviewRow Row, ModResult Result, string Message)
{
    public ModLogEntry? Entry { get; init; }

    /// <summary>By hand: the page to open, then "I did it" (<see cref="ModerationService.RecordManual"/>).</summary>
    public string? WebsiteUrl { get; init; }

    /// <summary>A by-hand row nobody has confirmed doing yet.</summary>
    public bool AwaitingManual { get; init; }
}

/// <summary>One linked decision: an incident with a result per account. A failure on one platform does not undo the others.</summary>
public sealed record LinkedIncidentResult(string IncidentId, IReadOnlyList<LinkedRowResult> Rows, string Summary)
{
    /// <summary>Nothing was sent: the second confirmation is needed first (<see cref="ConfirmText"/>).</summary>
    public bool NeedsConfirmation { get; init; }

    public string ConfirmText { get; init; } = "";

    /// <summary>Refused before anything was sent (no reason, rate limit, links off).</summary>
    public string? Refused { get; init; }

    public bool Partial => Rows.Any(r => r.Result == ModResult.Ok) && Rows.Any(r => r.Result is ModResult.Failed or ModResult.Skipped);
}

/// <summary>How undoing one row went.</summary>
public sealed record UndoRowResult(ModLogEntry Row, ModResult Result, string Message)
{
    public string? WebsiteUrl { get; init; }
}

/// <summary>Undoing an incident (or a member's last hour): one result per row that was in force, plus what cannot be undone.</summary>
public sealed record IncidentUndoResult(string UndoIncidentId, IReadOnlyList<UndoRowResult> Rows, string Summary)
{
    public string? Refused { get; init; }

    /// <summary>Plain notes: "Deleted messages cannot be restored", "Warnings cannot be withdrawn".</summary>
    public IReadOnlyList<string> CannotUndo { get; init; } = [];
}

/// <summary>What unlinking would reverse: rows made through the link that are still in force.</summary>
public sealed record UnlinkPreview(string LinkId, bool Dissolve, IReadOnlyList<IdentityAccount> Leaving, IReadOnlyList<ModLogEntry> InForce)
{
    /// <summary>Reversing is ticked by default when the link is being marked wrong (section 7.3).</summary>
    public bool ReverseByDefault { get; init; }

    /// <summary>"Also reverse the 2 actions that were applied because of this link?"</summary>
    public string Question => InForce.Count == 0 ? ""
        : $"Also reverse the {InForce.Count} action{(InForce.Count == 1 ? "" : "s")} that {(InForce.Count == 1 ? "was" : "were")} applied because of this link?";
}

/// <summary>"This person is banned on Twitch and Kick. Apply here too?" for an account newly linked to a banned person.</summary>
public sealed record LaterArrivalPrompt(string Id, string PersonId, string LinkId, ViewerRef Account, IReadOnlyList<string> BannedOn, DateTimeOffset At)
{
    public string Text => $"{Account.UserName} on {Account.Platform} is linked to a person banned on {Join(BannedOn)}. Apply here too?";

    internal static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}

/// <summary>Something the streamer should hear about: linked actions by mods, automatic mirroring, later arrivals.</summary>
public sealed record ModerationStreamerNotice(string Title, string Message)
{
    public string? IncidentId { get; init; }
}

public sealed partial class ModerationService
{
    private static readonly TimeSpan UndoToastWindow = TimeSpan.FromHours(24);
    private IdentityLinks? _links;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _linkedRate = new(StringComparer.Ordinal);
    private readonly List<LaterArrivalPrompt> _later = [];

    /// <summary>
    /// Identity links (phase 4). Setting it shares this service's portal permissions with it and starts listening for
    /// newly linked accounts (later arrivals, automatic mirroring).
    /// </summary>
    public IdentityLinks? Links
    {
        get => _links;
        set
        {
            if (_links is not null) _links.Changed -= OnLinksChanged;
            _links = value;
            if (value is null) return;
            value.PermissionsSource = () => Permissions;
            value.Changed += OnLinksChanged;
        }
    }

    /// <summary>True when identity links are on and present: linked previews, notes and the link tools are offered.</summary>
    public bool LinksEnabled => _links?.Enabled == true;

    /// <summary>Linked actions by others, automatic mirroring and later arrivals, for the streamer (a desktop notification).</summary>
    public event Action<ModerationStreamerNotice>? StreamerNotice;

    /// <summary>Raised when <see cref="LaterArrivals"/> changes.</summary>
    public event Action? LaterArrivalsChanged;

    /// <summary>The last automatic mirroring or later-arrival check started in the background (tests wait for it).</summary>
    internal Task LastMirror { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// The dry run of a ban or timeout (section 7.4, step 2): one row for the account clicked and one for each account
    /// linked to it, with the link's status and date, what will happen there (the duration the platform will really
    /// apply, the YouTube quota it costs), whether it is done by hand, and whether its box starts ticked (verified and
    /// confirmed links yes, suggestions no). Names and ban state come from what Cuebot already knows (chat, the mod log);
    /// nothing is sent to a platform. Without identity links (or with the setting off) the preview has one row.
    /// </summary>
    public async Task<LinkedPreview> BuildPreviewAsync(ModerationCommand command, ModActor actor, CancellationToken ct = default)
    {
        if (command.Action is not (ModerationAction.Ban or ModerationAction.Timeout))
            throw new ArgumentException("Only bans and timeouts have a linked preview.", nameof(command));
        var spent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<LinkedPreviewRow>
        {
            await PreviewRowAsync(command, actor, command.Platform, command.TargetUserId, command.TargetName, null, null, "(the one clicked)",
                spent, ct).ConfigureAwait(false),
        };
        IdentityPerson? person = null;
        if (LinksEnabled && command.TargetUserId.Length > 0)
        {
            person = _links!.PersonOf(command.Platform, command.TargetUserId);
            if (person?.Account(LinkPlatforms.Canon(command.Platform), command.TargetUserId) is { } me)
                foreach (var other in person.Accounts.Where(a => a.LinkId != me.LinkId)
                             .OrderByDescending(a => IdentityLinks.Between(me, a)).ThenBy(a => a.Platform, StringComparer.OrdinalIgnoreCase))
                {
                    var status = IdentityLinks.Between(me, other);
                    rows.Add(await PreviewRowAsync(command, actor, PlatformName(other.Platform), other.UserId, other.Name, other.LinkId, status,
                        IdentityLinks.Describe(other, status), spent, ct).ConfigureAwait(false));
                }
        }
        return new LinkedPreview { Id = NewId(), Command = command, PersonId = person?.Id, Rows = rows, At = _time.GetUtcNow() };
    }

    private async Task<LinkedPreviewRow> PreviewRowAsync(ModerationCommand command, ModActor actor, string platform, string userId, string name,
        string? linkId, LinkStatus? status, string linkText, Dictionary<string, int> spent, CancellationToken ct)
    {
        var origin = linkId is null;
        var action = command.Action;
        var capability = Capability(platform, action);
        var notes = new List<string>();
        var row = new LinkedPreviewRow
        {
            RowId = origin ? "origin" : linkId!,
            Platform = platform,
            UserId = userId,
            Name = name,
            IsOrigin = origin,
            LinkId = linkId,
            Status = status,
            LinkText = linkText,
            State = capability.State,
            WebsiteUrl = capability.WebsiteUrl,
        };
        if (!capability.CanRun)
        {
            var byHand = capability.State == ModerationToolState.OnWebsite;
            return row with
            {
                WillHappen = byHand ? $"By hand: open {platform}" : "Not available",
                Notes = byHand ? "No API" : capability.Note,
            };
        }

        TimeSpan? applied = null;
        if (action == ModerationAction.Timeout)
        {
            var (a, note) = PreviewDuration(platform, command.Duration ?? TimeSpan.FromMinutes(10));
            applied = a;
            if (note.Length > 0) notes.Add(note);
        }
        var willHappen = action == ModerationAction.Ban ? "Ban" : $"Timeout {FormatDuration(applied!.Value)}";
        var canTick = true;
        var ticked = origin || status >= LinkStatus.Confirmed;
        if (!origin && status == LinkStatus.Suggested)
        {
            notes.Add("Unticked: not confirmed");
            // Section 8: a linked permanent ban needs a confirmed or verified link; only the streamer may go further.
            if (action == ModerationAction.Ban && actor.Kind != ModActorKind.Streamer)
            {
                canTick = false;
                notes.Add("A linked ban needs a confirmed or verified link");
            }
        }

        int? left = null;
        if (capability.QuotaCost > 0 && QuotaFor(platform) is { } quota)
        {
            left = quota.Remaining;
            var units = spent.GetValueOrDefault(platform) + capability.QuotaCost;
            willHappen += string.Format(CultureInfo.InvariantCulture, ", {0:N0} quota units ({1:N0} left today)", capability.QuotaCost, quota.Remaining);
            if (quota.Refuse(units) is { } noQuota)
            {
                ticked = false;
                canTick = false;
                notes.Add(noQuota);
            }
            else if (ticked) spent[platform] = units;
        }

        var already = false;
        if (Log?.FindInForce(platform, userId) is { } inForce && userId.Length > 0)
        {
            already = inForce.Action == "ban" || action == ModerationAction.Timeout;
            if (already)
            {
                notes.Add(inForce.Action == "ban" ? "Already banned" : "Already timed out");
                if (!origin) ticked = false;
            }
        }

        if (actor.Kind != ModActorKind.Streamer && !origin
            && await WhyProtectedAsync(command with { Platform = platform, TargetUserId = userId, TargetName = name, TargetRoles = ChatRoles.None }, ct)
                .ConfigureAwait(false) is { } protectedWhy)
        {
            ticked = false;
            canTick = false;
            notes.Add(protectedWhy);
        }

        return row with
        {
            WillHappen = willHappen,
            AppliedDuration = applied,
            QuotaCost = capability.QuotaCost,
            QuotaLeft = left,
            Notes = string.Join(". ", notes),
            Ticked = ticked && canTick,
            CanTick = canTick,
            AlreadyActioned = already,
        };
    }

    // ---------------------------------------------------------------- execute

    /// <summary>
    /// Carries out a previewed linked ban or timeout (section 7.4, steps 3 to 6): a reason is required; a permanent ban
    /// on more than one platform needs <see cref="LinkedExecution.Confirmed"/>; members may start
    /// <see cref="ModerationSettings.MemberLinkedBansPerHour"/> linked bans an hour. The ticked rows form one incident;
    /// each platform call is independent and logged (through the link, with <c>viaLinkId</c>), so a failure on one
    /// platform leaves the others done. Rows without an API come back as "by hand" with the website and wait for
    /// <see cref="RecordManual"/>. Linked actions by anyone but the streamer notify the streamer.
    /// </summary>
    public async Task<LinkedIncidentResult> ExecuteLinkedAsync(LinkedExecution request, ModActor actor, CancellationToken ct = default)
    {
        var preview = request.Preview;
        var reason = request.Reason.Trim();
        if (reason.Length == 0) return Refuse("A reason is required.");
        var selected = preview.Selected(request.Ticked).ToList();
        var linked = selected.Any(r => !r.IsOrigin);
        var byHand = preview.Rows.Where(r => r.ByHand && (r.IsOrigin || r.Status >= LinkStatus.Confirmed || request.Ticked.Contains(r.RowId))).ToList();
        if (selected.Count == 0 && byHand.Count == 0) return Refuse("Nothing is ticked.");
        if (linked && !LinksEnabled) return Refuse("Identity links are turned off.");
        if (preview.NeedsSecondConfirmation(request.Ticked) && !request.Confirmed)
            return new LinkedIncidentResult("", [], "Confirm first.") { NeedsConfirmation = true, ConfirmText = preview.SecondConfirmationText(request.Ticked) };
        if (linked && preview.Command.Action == ModerationAction.Ban && actor.Kind == ModActorKind.Member && LinkedRateLimited(actor) is { } limited)
            return Refuse(limited);

        var incident = NewId();
        var results = new List<LinkedRowResult>();
        foreach (var row in selected)
        {
            var result = await ExecuteCoreAsync(RowCommand(preview.Command, row, incident, reason), actor, mirror: false, ct).ConfigureAwait(false);
            results.Add(new LinkedRowResult(row, result.Result, result.Message) { Entry = result.Entry, WebsiteUrl = result.WebsiteUrl });
        }
        foreach (var row in byHand.Where(r => selected.All(s => s.RowId != r.RowId)))
            results.Add(new LinkedRowResult(row, ModResult.Skipped, $"By hand: open {row.Platform}, then press \"I did it\".")
            {
                WebsiteUrl = row.WebsiteUrl,
                AwaitingManual = true,
            });

        var summary = Summary(preview.Command.Action, results);
        if (linked && actor.Kind != ModActorKind.Streamer)
            StreamerNotice?.Invoke(new ModerationStreamerNotice($"{actor.Name} made a linked {Verb(preview.Command.Action)}", summary) { IncidentId = incident });
        return new LinkedIncidentResult(incident, results, summary);

        static LinkedIncidentResult Refuse(string why) => new("", [], why) { Refused = why };
    }

    /// <summary>A mod did a by-hand row on the website: records it in the incident as <see cref="ModResult.Manual"/>.</summary>
    public ModLogEntry? RecordManual(string incidentId, LinkedPreview preview, string rowId, string reason, ModActor actor)
    {
        if (preview.Rows.FirstOrDefault(r => r.RowId == rowId) is not { } row || Log is null) return null;
        var command = RowCommand(preview.Command, row, incidentId, reason.Trim());
        return Log.Append(Row(command, actor, reason.Trim(), ModResult.Manual, "") with
        {
            DurationRequestedSeconds = Seconds(command.Action == ModerationAction.Timeout ? command.Duration : null),
        });
    }

    /// <summary>Tries one row of an incident again (a failed platform call).</summary>
    public async Task<LinkedRowResult> RetryRowAsync(string incidentId, LinkedPreview preview, string rowId, string reason, ModActor actor,
        CancellationToken ct = default)
    {
        var row = preview.Rows.First(r => r.RowId == rowId);
        var result = await ExecuteCoreAsync(RowCommand(preview.Command, row, incidentId, reason.Trim()), actor, mirror: false, ct).ConfigureAwait(false);
        return new LinkedRowResult(row, result.Result, result.Message) { Entry = result.Entry, WebsiteUrl = result.WebsiteUrl };
    }

    private static ModerationCommand RowCommand(ModerationCommand command, LinkedPreviewRow row, string incident, string reason) =>
        row.IsOrigin
            ? command with { IncidentId = incident, Reason = reason, ViaLinkId = null, MessageId = "", MessageText = "" }
            : new ModerationCommand(row.Platform, command.Action)
            {
                TargetUserId = row.UserId,
                TargetName = row.Name,
                Duration = command.Duration,
                Reason = reason,
                IncidentId = incident,
                ViaLinkId = row.LinkId,
            };

    private string? LinkedRateLimited(ModActor actor)
    {
        var limit = _settings().MemberLinkedBansPerHour;
        if (limit <= 0) return null;
        var now = _time.GetUtcNow();
        var key = actor.MemberId ?? actor.Name;
        lock (_linkedRate)
        {
            if (!_linkedRate.TryGetValue(key, out var times)) _linkedRate[key] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && times.Peek() <= now - TimeSpan.FromHours(1)) times.Dequeue();
            if (times.Count >= limit) return $"{actor.Name} has made {limit} linked bans in the last hour. Wait a little, or ask the streamer.";
            times.Enqueue(now);
            return null;
        }
    }

    private static string Summary(ModerationAction action, IReadOnlyList<LinkedRowResult> rows)
    {
        string Names(IEnumerable<LinkedRowResult> r) => string.Join(", ", r.Select(x => x.Row.Platform).Distinct(StringComparer.OrdinalIgnoreCase));
        var parts = new List<string>();
        var done = rows.Where(r => r.Result == ModResult.Ok).ToArray();
        if (done.Length > 0) parts.Add($"{(action == ModerationAction.Ban ? "Banned" : "Timed out")} on {Names(done)}");
        var failed = rows.Where(r => r.Result == ModResult.Failed).ToArray();
        if (failed.Length > 0) parts.Add($"failed on {Names(failed)}");
        var skipped = rows.Where(r => r.Result == ModResult.Skipped && !r.AwaitingManual).ToArray();
        if (skipped.Length > 0) parts.Add($"not sent on {Names(skipped)}");
        var hand = rows.Where(r => r.AwaitingManual).ToArray();
        if (hand.Length > 0) parts.Add($"by hand: {Names(hand)}");
        return parts.Count == 0 ? "Nothing was done." : string.Join("; ", parts) + ".";
    }

    private static string Verb(ModerationAction action) => action == ModerationAction.Ban ? "ban" : "timeout";

    // ---------------------------------------------------------------- undo

    /// <summary>The rows of an incident, oldest first.</summary>
    public IReadOnlyList<ModLogEntry> IncidentRows(string incidentId) => Log?.ForIncident(incidentId) ?? [];

    /// <summary>True when some ban or timeout of the incident is still in force.</summary>
    public bool CanUndoIncident(string incidentId)
    {
        var now = _time.GetUtcNow();
        return IncidentRows(incidentId).Any(r => r.Action is "ban" or "timeout" && r.Result is ModResult.Ok or ModResult.Manual && r.UndoneBy is null
                                                 && (r.Action == "ban" || r.At.AddSeconds(r.DurationAppliedSeconds ?? r.DurationRequestedSeconds ?? 0) > now));
    }

    /// <summary>The "Undo" on the toast after a linked action is offered for 24 hours; the mod log offers it at any time.</summary>
    public bool UndoStillOffered(DateTimeOffset madeAt) => _time.GetUtcNow() - madeAt < UndoToastWindow;

    /// <summary>
    /// Undoes an incident (section 7.4, step 7): lifts every ban and timeout of it that worked and is still in force,
    /// using the stored YouTube ban id; each lift is its own logged row (and costs YouTube quota). A lift that fails
    /// leaves that row in force and is reported. Deleted messages cannot be restored and warnings cannot be withdrawn;
    /// bans made by hand must be lifted by hand.
    /// </summary>
    public async Task<IncidentUndoResult> UndoIncidentAsync(string incidentId, ModActor actor, CancellationToken ct = default)
    {
        var rows = IncidentRows(incidentId);
        if (rows.Count == 0) return new IncidentUndoResult("", [], "That incident is not in the mod log.") { Refused = "That incident is not in the mod log." };
        var result = await UndoRowsAsync(rows, actor, $"Undo of incident {incidentId[..Math.Min(8, incidentId.Length)]}", ct).ConfigureAwait(false);
        if (actor.Kind != ModActorKind.Streamer && result.Rows.Count > 0)
            StreamerNotice?.Invoke(new ModerationStreamerNotice($"{actor.Name} undid an incident", result.Summary) { IncidentId = result.UndoIncidentId });
        return result;
    }

    /// <summary>
    /// "Undo everything this member did in the last hour" (section 8), for the streamer: lifts the member's bans and
    /// timeouts still in force and withdraws the link suggestions they made in that hour.
    /// </summary>
    public async Task<IncidentUndoResult> UndoMemberRecentAsync(string memberId, ModActor actor, TimeSpan? window = null, CancellationToken ct = default)
    {
        if (actor.Kind != ModActorKind.Streamer)
            return new IncidentUndoResult("", [], "Only the streamer can do this.") { Refused = "Only the streamer can do this." };
        if (Log is null) return new IncidentUndoResult("", [], "There is no mod log.") { Refused = "There is no mod log." };
        var since = _time.GetUtcNow() - (window ?? TimeSpan.FromHours(1));
        var rows = Log.ByMemberSince(memberId, since);
        var result = await UndoRowsAsync(rows.Reverse().ToArray(), actor, "Undo of everything a mod did in the last hour", ct).ConfigureAwait(false);
        var withdrawn = 0;
        if (_links is { Enabled: true } links)
            foreach (var link in rows.Where(r => r.Action == "link" && r.ViaLinkId is not null).Select(r => r.ViaLinkId!).Distinct())
                if (links.PersonOfLink(link)?.AccountByLink(link) is { Status: LinkStatus.Suggested }
                    && links.Unlink(link, actor, reason: "withdrawn with the mod's last hour").Ok)
                    withdrawn++;
        return withdrawn == 0 ? result : result with { Summary = $"{result.Summary} Withdrew {withdrawn} link suggestion(s)." };
    }

    private async Task<IncidentUndoResult> UndoRowsAsync(IReadOnlyList<ModLogEntry> rows, ModActor actor, string reason, CancellationToken ct)
    {
        var undoIncident = NewId();
        var now = _time.GetUtcNow();
        var results = new List<UndoRowResult>();
        var cannot = new List<string>();
        foreach (var row in rows)
        {
            if (row.UndoneBy is not null) continue;
            switch (row.Action)
            {
                case "delete" when row.Result == ModResult.Ok:
                    if (!cannot.Contains(DeletedNote)) cannot.Add(DeletedNote);
                    continue;
                case "warn" when row.Result == ModResult.Ok:
                    if (!cannot.Contains(WarnNote)) cannot.Add(WarnNote);
                    continue;
                case "ban" or "timeout" when row.Result == ModResult.Manual:
                    var page = Capability(row.Platform, ModerationAction.Unban).WebsiteUrl ?? Capability(row.Platform, ModerationAction.Ban).WebsiteUrl;
                    results.Add(new UndoRowResult(row, ModResult.Skipped, $"Lift it by hand on {row.Platform}.") { WebsiteUrl = page });
                    continue;
                case "ban" or "timeout" when row.IsInForce(now) && row.TargetUserId.Length > 0:
                    var lift = await ExecuteCoreAsync(new ModerationCommand(row.Platform, ModerationAction.Unban)
                    {
                        TargetUserId = row.TargetUserId,
                        TargetName = row.TargetName,
                        ChannelId = row.ChannelId,
                        IncidentId = undoIncident,
                        ViaLinkId = row.ViaLinkId,
                        UndoesRowId = row.Id,
                        Reason = reason,
                    }, actor, mirror: false, ct).ConfigureAwait(false);
                    results.Add(new UndoRowResult(row, lift.Result, lift.Ok ? $"{row.Platform}: lifted" : $"{row.Platform}: {lift.Message}")
                    {
                        WebsiteUrl = lift.WebsiteUrl,
                    });
                    continue;
            }
        }
        var ok = results.Count(r => r.Result == ModResult.Ok);
        var failed = results.Count(r => r.Result != ModResult.Ok);
        var summary = results.Count == 0 ? "Nothing in force to undo." : $"Lifted {ok} of {results.Count}." + (failed > 0 ? " Some need another try or doing by hand." : "");
        if (cannot.Count > 0) summary += " " + string.Join(" ", cannot);
        return new IncidentUndoResult(undoIncident, results, summary) { CannotUndo = cannot };
    }

    private const string DeletedNote = "Deleted messages cannot be restored on any platform.";
    private const string WarnNote = "Warnings cannot be withdrawn.";

    // ---------------------------------------------------------------- unlink

    /// <summary>What unlinking <paramref name="linkId"/> (or dissolving its person) would reverse; null when the link is gone.</summary>
    public UnlinkPreview? PreviewUnlink(string linkId, bool dissolve, bool wrong)
    {
        if (_links?.PersonOfLink(linkId) is not { } person || person.AccountByLink(linkId) is not { } account) return null;
        var leaving = dissolve ? person.Accounts : [account];
        var inForce = Log?.InForceVia(leaving.Select(a => a.LinkId)) ?? [];
        return new UnlinkPreview(linkId, dissolve, leaving, inForce) { ReverseByDefault = wrong };
    }

    /// <summary>
    /// Unlinks (section 7.3) and reverses the chosen actions that were applied through the link and are still in force.
    /// The unlink happens first, so a refused unlink reverses nothing.
    /// </summary>
    public async Task<(IdentityLinkResult Unlink, IncidentUndoResult? Reversed)> UnlinkAsync(string linkId, ModActor actor, bool dissolve, bool wrong,
        string reason, IReadOnlyCollection<string> reverseRowIds, CancellationToken ct = default)
    {
        if (_links is null) return (IdentityLinkResult.Refused("Identity links are not available."), null);
        var preview = PreviewUnlink(linkId, dissolve, wrong);
        var unlink = _links.Unlink(linkId, actor, dissolve, wrong, reason);
        if (!unlink.Ok || preview is null) return (unlink, null);
        var rows = preview.InForce.Where(r => reverseRowIds.Contains(r.Id)).Reverse().ToArray();
        if (rows.Length == 0) return (unlink, null);
        var reversed = await UndoRowsAsync(rows, actor, wrong ? "The link was wrong" : "Unlinked", ct).ConfigureAwait(false);
        return (unlink, reversed);
    }

    // ---------------------------------------------------------------- later arrivals and mirroring

    /// <summary>Accounts newly linked to a banned person, waiting for a mod's yes (oldest first).</summary>
    public IReadOnlyList<LaterArrivalPrompt> LaterArrivals
    {
        get { lock (_later) return _later.ToArray(); }
    }

    /// <summary>Applies the ban to the newly linked account ("Apply here too?"), through the link, as its own incident.</summary>
    public async Task<ModerationResult> ApplyLaterArrivalAsync(string promptId, ModActor actor, string reason, CancellationToken ct = default)
    {
        LaterArrivalPrompt? prompt;
        lock (_later) prompt = _later.FirstOrDefault(p => p.Id == promptId);
        if (prompt is null) return new ModerationResult(ModResult.Skipped, "That was already answered.");
        if (!LinksEnabled) return new ModerationResult(ModResult.Skipped, "Identity links are turned off.");
        if (reason.Trim().Length == 0) return new ModerationResult(ModResult.Skipped, "A reason is required.");
        DismissLaterArrival(promptId);
        return await ExecuteCoreAsync(new ModerationCommand(prompt.Account.Platform, ModerationAction.Ban)
        {
            TargetUserId = prompt.Account.UserId,
            TargetName = prompt.Account.UserName,
            ViaLinkId = prompt.LinkId,
            Reason = reason.Trim(),
        }, actor, mirror: false, ct).ConfigureAwait(false);
    }

    public bool DismissLaterArrival(string promptId)
    {
        bool removed;
        lock (_later) removed = _later.RemoveAll(p => p.Id == promptId) > 0;
        if (removed) LaterArrivalsChanged?.Invoke();
        return removed;
    }

    private void OnLinksChanged(IdentityLinkChange change)
    {
        if (change.Kind is not (IdentityLinkChange.Confirmed or IdentityLinkChange.Verified) || change.LinkIds.Count == 0) return;
        LastMirror = Task.Run(() => CheckLaterArrivalsAsync(change.LinkIds));
    }

    /// <summary>
    /// An account just became a confirmed or verified part of a person banned elsewhere: with automatic mirroring the ban
    /// is applied at once (logged, notified, undoable); otherwise a prompt waits for a mod.
    /// </summary>
    private async Task CheckLaterArrivalsAsync(IReadOnlyList<string> linkIds)
    {
        try
        {
            if (_links is not { Enabled: true } links || Log is not { } log) return;
            foreach (var linkId in linkIds)
            {
                if (links.PersonOfLink(linkId) is not { } person || person.AccountByLink(linkId) is not { } account) continue;
                if (log.FindInForce(account.Platform, account.UserId) is { Action: "ban" }) continue;
                var banned = person.Accounts
                    .Where(o => o.LinkId != account.LinkId && IdentityLinks.Between(account, o) >= LinkStatus.Confirmed
                                && log.FindInForce(o.Platform, o.UserId) is { Action: "ban" })
                    .ToArray();
                if (banned.Length == 0) continue;
                var platforms = banned.Select(b => PlatformName(b.Platform)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
                if (_settings().AutomaticMirroring)
                {
                    var source = log.FindInForce(banned[0].Platform, banned[0].UserId)!;
                    await MirrorToAsync(source, [account], CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                var prompt = new LaterArrivalPrompt(NewId(), person.Id, linkId, new ViewerRef(PlatformName(account.Platform), account.UserId, account.Name),
                    platforms, _time.GetUtcNow());
                lock (_later)
                {
                    _later.RemoveAll(p => p.LinkId == linkId);
                    _later.Add(prompt);
                }
                LaterArrivalsChanged?.Invoke();
                StreamerNotice?.Invoke(new ModerationStreamerNotice("A banned person has another account", prompt.Text));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Moderation", $"Could not check a newly linked account: {ex.Message}");
        }
    }

    /// <summary>
    /// Automatic mirroring (decision 6): a ban that worked, from any source, goes to the person's verified and confirmed
    /// links that are not banned yet, without a preview, in the same incident (so undoing it lifts all of them). Never
    /// through suggested links; never from a row that itself came through a link.
    /// </summary>
    private async Task MirrorAsync(ModLogEntry source, CancellationToken ct)
    {
        try
        {
            if (!_settings().AutomaticMirroring || _links is not { Enabled: true } links || Log is null) return;
            if (source.Action != "ban" || source.Result != ModResult.Ok || source.ViaLinkId is not null || source.TargetUserId.Length == 0) return;
            if (links.PersonOf(source.Platform, source.TargetUserId) is not { } person
                || person.Account(LinkPlatforms.Canon(source.Platform), source.TargetUserId) is not { } me) return;
            var targets = person.Accounts.Where(o => o.LinkId != me.LinkId && IdentityLinks.Between(me, o) >= LinkStatus.Confirmed).ToArray();
            await MirrorToAsync(source, targets, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _appLog?.Warn("Moderation", $"Automatic mirroring failed: {ex.Message}");
        }
    }

    private async Task MirrorToAsync(ModLogEntry source, IReadOnlyList<IdentityAccount> targets, CancellationToken ct)
    {
        var actor = ModActor.Automation("Automatic mirroring");
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var target in targets)
        {
            var platform = PlatformName(target.Platform);
            if (Log!.FindInForce(platform, target.UserId) is { Action: "ban" }) continue;
            if (!Capability(platform, ModerationAction.Ban).CanRun) continue;
            var result = await ExecuteCoreAsync(new ModerationCommand(platform, ModerationAction.Ban)
            {
                TargetUserId = target.UserId,
                TargetName = target.Name,
                IncidentId = source.IncidentId,
                ViaLinkId = target.LinkId,
                Reason = $"Mirrored from {source.Platform}" + (source.Reason.Length > 0 ? ": " + source.Reason : ""),
            }, actor, mirror: false, ct).ConfigureAwait(false);
            (result.Ok ? done : failed).Add(platform);
        }
        if (done.Count + failed.Count == 0) return;
        var text = (done.Count > 0 ? $"Banned on {LaterArrivalPrompt.Join(done)}" : "")
                   + (failed.Count > 0 ? (done.Count > 0 ? "; " : "") + $"failed on {LaterArrivalPrompt.Join(failed)}" : "")
                   + $" (mirrored from a ban on {source.Platform}). You can undo it from the mod log.";
        StreamerNotice?.Invoke(new ModerationStreamerNotice("Automatic mirroring applied a ban", text) { IncidentId = source.IncidentId });
    }

    /// <summary>The platform name as the moderators know it ("Twitch"), for an identity account's canonical platform.</summary>
    private string PlatformName(string platform) =>
        _moderators.ModeratorFor(platform)?.Platform ?? LinkPlatforms.Canon(platform);

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
}
