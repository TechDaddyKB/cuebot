using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Users;

/// <summary>What a link operation did, in words for the person who clicked.</summary>
public sealed record IdentityLinkResult(bool Ok, string Message)
{
    /// <summary>The person after the change (null when it was dissolved or nothing changed).</summary>
    public IdentityPerson? Person { get; init; }

    /// <summary>
    /// Set when an account already belongs to another person: show both and ask whether to merge the two people or move
    /// the account (<see cref="LinkConflictChoice"/>). Nothing was changed.
    /// </summary>
    public IdentityPerson? Conflict { get; init; }

    public bool NeedsChoice => Conflict is not null && !Ok;

    /// <summary>The account memberships added, changed or removed.</summary>
    public IReadOnlyList<string> LinkIds { get; init; } = [];

    public static IdentityLinkResult Refused(string why) => new(false, why);
}

/// <summary>What to do when the other account already belongs to a person.</summary>
public enum LinkConflictChoice
{
    /// <summary>Change nothing; return the conflict so the UI can show both histories.</summary>
    Ask,

    /// <summary>Join the two people into one (every account of the other person becomes a suggestion).</summary>
    Merge,

    /// <summary>Take the account out of its person and link it here.</summary>
    Move,
}

/// <summary>A one-time <c>!link</c> code a mod started for an account.</summary>
public sealed record LinkVerification(string Code, ViewerRef Account, DateTimeOffset ExpiresAt, string StartedBy)
{
    /// <summary>What the mod tells the viewer (privately, not in public chat).</summary>
    public string Instructions =>
        $"Send this privately to {Account.UserName} on {Account.Platform} (a whisper or DM, not public chat): " +
        $"\"To link your accounts, type !link {Code} in chat from your other account within {IdentityLinks.CodeMinutes} minutes.\"";
}

public enum LinkVerifyOutcome { Verified, Unknown, Expired, SameAccount, Refused, RateLimited, Disabled }

public sealed record LinkVerifyResult(LinkVerifyOutcome Outcome, string Message)
{
    public IdentityPerson? Person { get; init; }
}

/// <summary>Raised after identity data changed: what changed and the memberships involved.</summary>
public sealed record IdentityLinkChange(string Kind, string? PersonId, IReadOnlyList<string> LinkIds)
{
    public const string Proposed = "proposed", Confirmed = "confirmed", Verified = "verified", Rejected = "rejected",
        Unlinked = "unlinked", Notes = "notes", Erased = "erased", Retention = "retention";
}

/// <summary>Looks up an account's current name on its platform (YouTube's 30-day refresh). Optional, per platform.</summary>
public interface IAccountLookup
{
    string Platform { get; }

    /// <summary>The current name; null when the account no longer exists. Throws when the platform cannot be asked now.</summary>
    Task<string?> CurrentNameAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// A store whose data about a viewer spans linked accounts. "Forget this viewer" (<see cref="ViewerData"/>) asks it for
/// the accounts that belong to the same person, exports what it holds and erases it.
/// </summary>
public interface IPersonDataStore
{
    string Name { get; }

    /// <summary>The account itself and the accounts confirmed or verified as the same person.</summary>
    IReadOnlyList<ViewerRef> LinkedAccounts(string platform, string userId, string userName);

    JsonObject Export(IReadOnlyList<ViewerRef> accounts);

    /// <summary>Erases what it holds about the accounts; returns how many records changed.</summary>
    int Erase(IReadOnlyList<ViewerRef> accounts);
}

/// <summary>
/// Identity links (docs/UNIFIED_MOD_CHAT_PLAN.md, phase 4): people made of platform accounts, with how sure the team is
/// (suggested, confirmed by a second person, or verified by the viewer with <c>!link CODE</c>), the evidence, rejected
/// suggestions, and mod notes that follow links. The rules of section 7: a link never includes the broadcaster, a
/// platform moderator or VIP, a portal member or a bot account; an account belongs to one person (adding it to another
/// asks to merge or move); more than four accounts or two on one platform need the streamer; the proposer never
/// confirms their own suggestion (decision 5). Every link, confirmation, rejection and unlink is a mod log row. Same
/// names only ever give a hint on the user card; nothing is matched on behaviour. Everything sits behind the streamer's
/// <see cref="ModerationSettings.IdentityLinksEnabled"/> setting, off by default until the privacy review (decision 12).
/// Stored in the mod log database (schema version 2). Thread-safe.
/// </summary>
public sealed class IdentityLinks : IPersonDataStore, IDisposable
{
    public const int CodeMinutes = 10;
    public const int MaxAccounts = 4;
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;
    private const int MaxFailedPerAccount = 5;
    private const int MaxFailedOverall = 30;
    private const int MaxCodesPerActorPerHour = 10;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(10);

    private readonly ModLog _log;
    private readonly Func<ModerationSettings> _settings;
    private readonly IReadOnlyList<IProtectedAccounts> _protected;
    private readonly IReadOnlyList<IChannelRoles> _roles;
    private readonly IReadOnlyList<IAccountLookup> _lookups;
    private readonly UserRegistry? _users;
    private readonly ChatHub? _hub;
    private readonly AppLog? _appLog;
    private readonly TimeProvider _time;
    private readonly object _codeGate = new();
    private readonly Dictionary<string, LinkVerification> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModActor> _codeActors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _starts = new(StringComparer.Ordinal);

    public IdentityLinks(ModLog log, Func<ModerationSettings>? settings = null, IEnumerable<IProtectedAccounts>? protectedAccounts = null,
        IEnumerable<IChannelRoles>? roles = null, UserRegistry? users = null, ChatHub? hub = null, IEnumerable<IAccountLookup>? lookups = null,
        AppLog? appLog = null, TimeProvider? time = null)
    {
        _log = log;
        var defaults = new ModerationSettings();
        _settings = settings ?? (() => defaults);
        _protected = protectedAccounts?.ToArray() ?? [];
        _roles = roles?.ToArray() ?? [];
        _lookups = lookups?.ToArray() ?? [];
        _users = users;
        _hub = hub;
        _appLog = appLog;
        _time = time ?? TimeProvider.System;
        if (_hub is not null) _hub.MessageReceived += OnChatMessage;
    }

    /// <summary>The streamer's setting; while false every operation is refused and nothing is shown.</summary>
    public bool Enabled => _settings().IdentityLinksEnabled;

    /// <summary>Set by <see cref="ModerationService"/>: the portal's permission hook, asked before every member operation.</summary>
    public Func<IModerationPermissions?> PermissionsSource { get; set; } = () => null;

    /// <summary>Raised after people, links or notes changed (on the caller's thread).</summary>
    public event Action<IdentityLinkChange>? Changed;

    string IPersonDataStore.Name => "identity links, notes and mod log";

    public ModLog Log => _log;

    // ---------------------------------------------------------------- reading

    /// <summary>The person this account belongs to; null when it is in none (or links are off).</summary>
    public IdentityPerson? PersonOf(string platform, string userId) =>
        Enabled && userId.Length > 0 ? _log.PersonByAccount(LinkPlatforms.Canon(platform), userId) : null;

    public IdentityPerson? Person(string personId) => Enabled ? _log.PersonById(personId) : null;

    /// <summary>Every person (for the streamer's review); empty while links are off.</summary>
    public IReadOnlyList<IdentityPerson> People() => Enabled ? _log.AllPersons() : [];

    /// <summary>The person holding this membership.</summary>
    public IdentityPerson? PersonOfLink(string linkId) => Enabled ? _log.Identity(t => t.PersonByLink(linkId)) : null;

    /// <summary>How sure the link between two accounts of one person is: the weaker of their two memberships.</summary>
    public static LinkStatus Between(IdentityAccount a, IdentityAccount b) => (LinkStatus)Math.Min((int)a.Status, (int)b.Status);

    /// <summary>"verified 2026-10-02", "suggested by Sam, not confirmed", "confirmed by Kris and Sam".</summary>
    public static string Describe(IdentityAccount account, LinkStatus status)
    {
        var date = account.StatusAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return status switch
        {
            LinkStatus.Verified => $"verified {date}",
            LinkStatus.Confirmed => account.ConfirmedBy.Count > 0
                ? $"confirmed by {string.Join(" and ", new[] { account.AddedBy }.Concat(account.ConfirmedBy.Select(c => c.By)).Distinct())}"
                : $"confirmed {date}",
            LinkStatus.Suggested => $"suggested by {account.AddedBy}, not confirmed" + (account.NeedsStreamer ? " (needs the streamer)" : ""),
            _ => "rejected",
        };
    }

    public static bool SamePlatform(string a, string b) => string.Equals(LinkPlatforms.Canon(a), LinkPlatforms.Canon(b), StringComparison.Ordinal);

    // ---------------------------------------------------------------- proposing

    /// <summary>
    /// A mod asserts that <paramref name="from"/> and <paramref name="to"/> are one person (section 7.1): the new
    /// membership is <see cref="LinkStatus.Suggested"/> until a second person confirms it. When <paramref name="to"/>
    /// (or <paramref name="from"/>) already belongs to another person, nothing changes unless
    /// <paramref name="choice"/> says to merge or move.
    /// </summary>
    public async Task<IdentityLinkResult> ProposeAsync(ViewerRef from, ViewerRef to, ModActor actor, string note = "",
        IReadOnlyList<LinkEvidenceInput>? evidence = null, LinkConflictChoice choice = LinkConflictChoice.Ask,
        ChatRoles fromRoles = ChatRoles.None, ChatRoles toRoles = ChatRoles.None, CancellationToken ct = default)
    {
        if (Refusal(actor, LinkOperation.Propose) is { } refused) return IdentityLinkResult.Refused(refused);
        from = Clean(from);
        to = Clean(to);
        if (from.UserId.Length == 0 || to.UserId.Length == 0) return IdentityLinkResult.Refused("Both accounts need a platform user id.");
        if (SamePlatform(from.Platform, to.Platform) && from.UserId == to.UserId) return IdentityLinkResult.Refused("That is the same account.");
        foreach (var (account, roles) in new[] { (from, fromRoles), (to, toRoles) })
            if (await WhyCannotLinkAsync(account, roles, ct).ConfigureAwait(false) is { } why)
                return IdentityLinkResult.Refused(why);
        if (actor.Kind != ModActorKind.Streamer && _log.IsRejected(from.Platform, from.UserId, to.Platform, to.UserId))
            return IdentityLinkResult.Refused("This link was marked wrong before. Only the streamer can suggest it again.");

        var now = _time.GetUtcNow();
        var proposal = NewId();
        var key = ActorKey(actor);
        var logged = new List<(string Action, IdentityAccount Account)>();
        var result = _log.Identity(t =>
        {
            var pf = t.PersonByAccount(from.Platform, from.UserId);
            var pt = t.PersonByAccount(to.Platform, to.UserId);
            if (pf is not null && pf.Id == pt?.Id) return IdentityLinkResult.Refused($"{to.UserName} is already linked to {from.UserName}.");
            if (pt is not null && choice == LinkConflictChoice.Ask)
                return new IdentityLinkResult(false, $"{to.UserName} on {to.Platform} already belongs to another person. Merge the two, or move the account?")
                {
                    Person = pf,
                    Conflict = pt,
                };

            IdentityAccount Fresh(ViewerRef v, string person) => new()
            {
                LinkId = NewId(), PersonId = person, Platform = v.Platform, UserId = v.UserId, NameAtLink = v.UserName, AddedAt = now,
                AddedBy = actor.Name, AddedByKey = key, ProposalId = proposal, Status = LinkStatus.Suggested, StatusAt = now, LastRefreshed = now,
            };

            string personId;
            var added = new List<IdentityAccount>();
            if (pf is null && (pt is null || choice == LinkConflictChoice.Move))
            {
                // A new person: both accounts suggested together (a moved account leaves its old person).
                personId = NewId();
                t.InsertPerson(new IdentityPerson { Id = personId, Note = note.Trim(), CreatedAt = now, CreatedBy = actor.Name, LastReviewed = now });
                var a = Fresh(from, personId);
                t.InsertAccount(a);
                added.Add(a);
                if (pt?.Account(to.Platform, to.UserId) is { } moving)
                {
                    var moved = moving with { PersonId = personId, ProposalId = proposal, Status = LinkStatus.Suggested, StatusAt = now, AddedBy = actor.Name, AddedByKey = key };
                    t.UpdateAccount(moved);
                    added.Add(moved);
                    logged.Add(("link-move", moved));
                    DissolveIfAlone(t, pt.Id);
                }
                else
                {
                    var b = Fresh(to, personId);
                    t.InsertAccount(b);
                    added.Add(b);
                }
            }
            else if (pf is null)
            {
                // Merge with "to"'s person: the asking account joins it.
                personId = pt!.Id;
                var a = Fresh(from, personId);
                t.InsertAccount(a);
                added.Add(a);
            }
            else
            {
                personId = pf.Id;
                if (pt is null)
                {
                    var b = Fresh(to, personId);
                    t.InsertAccount(b);
                    added.Add(b);
                }
                else if (choice == LinkConflictChoice.Move)
                {
                    var moved = pt.Account(to.Platform, to.UserId)! with
                    {
                        PersonId = personId, ProposalId = proposal, Status = LinkStatus.Suggested, StatusAt = now, AddedBy = actor.Name, AddedByKey = key,
                    };
                    t.UpdateAccount(moved);
                    added.Add(moved);
                    logged.Add(("link-move", moved));
                    DissolveIfAlone(t, pt.Id);
                }
                else
                {
                    // Merge: every account of the other person joins as a suggestion of this proposal.
                    foreach (var acc in pt.Accounts)
                    {
                        var merged = acc with { PersonId = personId, ProposalId = proposal, Status = LinkStatus.Suggested, StatusAt = now, AddedBy = actor.Name, AddedByKey = key };
                        t.UpdateAccount(merged);
                        added.Add(merged);
                        logged.Add(("link-merge", merged));
                    }
                    t.MoveEvidence(pt.Id, personId);
                    t.DeletePerson(pt.Id);
                }
            }

            var person = t.PersonById(personId)!;
            if (NeedsStreamer(person))
                foreach (var acc in person.Accounts.Where(a => a.ProposalId == proposal))
                    t.UpdateAccount(acc with { NeedsStreamer = true });
            var anchor = added.FirstOrDefault(a => a.Is(to.Platform, to.UserId)) ?? added.FirstOrDefault();
            foreach (var e in evidence ?? [])
                t.InsertEvidence(new LinkEvidence
                {
                    Id = NewId(), PersonId = personId, LinkId = anchor?.LinkId, Kind = LinkEvidenceKinds.All.Contains(e.Kind) ? e.Kind : LinkEvidenceKinds.ModNote,
                    Quote = Trim(e.Quote, 500), MessageIds = e.MessageIds.Take(20).ToArray(), AddedBy = actor.Name, At = now,
                });
            if (note.Trim().Length > 0)
            {
                t.InsertEvidence(new LinkEvidence { Id = NewId(), PersonId = personId, LinkId = anchor?.LinkId, Kind = LinkEvidenceKinds.ModNote, Quote = Trim(note, 500), AddedBy = actor.Name, At = now });
                if (person.Note.Length == 0) t.UpdatePerson(personId, Trim(note, 500), person.LastReviewed);
            }
            person = t.PersonById(personId)!;
            foreach (var acc in added.Where(a => logged.All(l => l.Account.LinkId != a.LinkId)))
                logged.Add(("link", person.AccountByLink(acc.LinkId) ?? acc));
            var needs = person.Accounts.Any(a => a.ProposalId == proposal && a.NeedsStreamer);
            return new IdentityLinkResult(true, $"Suggested: {from.UserName} on {from.Platform} and {to.UserName} on {to.Platform} are one person. " +
                                               (needs ? "Only the streamer can confirm it (more than four accounts, or two on one platform)."
                                                   : "Someone else has to confirm it."))
            {
                Person = person,
                LinkIds = added.Select(a => a.LinkId).ToArray(),
            };
        });
        if (!result.Ok) return result;
        foreach (var (action, account) in logged)
            LogRow(action, account, actor, account.NeedsStreamer ? "suggested (needs the streamer)" : "suggested");
        _appLog?.Info("Identity links", $"{actor.Name} suggested a link ({result.LinkIds.Count} account(s))");
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Proposed, result.Person?.Id, result.LinkIds));
        return result;
    }

    /// <summary>
    /// A second person confirms a suggestion: every account of the same proposal becomes <see cref="LinkStatus.Confirmed"/>.
    /// The proposer cannot confirm their own (decision 5); one that needs the streamer is confirmed by the streamer, or
    /// by a member when the streamer proposed it.
    /// </summary>
    public IdentityLinkResult Confirm(string linkId, ModActor actor)
    {
        if (!Enabled) return IdentityLinkResult.Refused(OffMessage);
        var key = ActorKey(actor);
        if (_log.Identity(t => t.PersonByLink(linkId))?.AccountByLink(linkId) is { Status: LinkStatus.Suggested } mine && mine.AddedByKey == key)
            return IdentityLinkResult.Refused("You suggested this link, so someone else has to confirm it: the streamer or another mod allowed to confirm links.");
        if (Refusal(actor, LinkOperation.Confirm) is { } refused) return IdentityLinkResult.Refused(refused);
        var now = _time.GetUtcNow();
        var changed = new List<IdentityAccount>();
        var result = _log.Identity(t =>
        {
            if (FindLink(t, linkId) is not { } found) return IdentityLinkResult.Refused("That link no longer exists.");
            var (person, account) = found;
            if (account.Status != LinkStatus.Suggested) return IdentityLinkResult.Refused($"That link is already {account.Status.ToString().ToLowerInvariant()}.");
            if (account.AddedByKey == key)
                return IdentityLinkResult.Refused("You suggested this link, so someone else has to confirm it: the streamer or another mod allowed to confirm links.");
            if (account.NeedsStreamer && actor.Kind != ModActorKind.Streamer && account.AddedByKey != StreamerKey)
                return IdentityLinkResult.Refused("This person has more than four accounts or two on one platform: only the streamer can confirm it.");
            foreach (var acc in person.Accounts.Where(a => a.ProposalId == account.ProposalId && a.Status == LinkStatus.Suggested))
            {
                var confirmed = acc with { Status = LinkStatus.Confirmed, StatusAt = now };
                t.UpdateAccount(confirmed);
                t.InsertConfirmation(acc.LinkId, new LinkConfirmation(actor.Name, actor.MemberId, now));
                changed.Add(confirmed);
            }
            t.UpdatePerson(person.Id, person.Note, now);
            return new IdentityLinkResult(true, "Link confirmed.") { Person = t.PersonById(person.Id), LinkIds = changed.Select(c => c.LinkId).ToArray() };
        });
        if (!result.Ok) return result;
        foreach (var acc in changed) LogRow("link-confirm", acc, actor, "confirmed");
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Confirmed, result.Person?.Id, result.LinkIds));
        return result;
    }

    /// <summary>
    /// Marks a suggestion wrong: its accounts leave the person and the pairs are remembered as rejected (only the
    /// account ids), so the same suggestion is not made again. The proposer may withdraw their own.
    /// </summary>
    public IdentityLinkResult Reject(string linkId, ModActor actor, string reason = "")
    {
        if (!Enabled) return IdentityLinkResult.Refused(OffMessage);
        var key = ActorKey(actor);
        var account0 = _log.Identity(t => t.PersonByLink(linkId))?.AccountByLink(linkId);
        if (account0 is null) return IdentityLinkResult.Refused("That link no longer exists.");
        if (account0.AddedByKey != key && Refusal(actor, LinkOperation.Reject) is { } refused) return IdentityLinkResult.Refused(refused);
        if (account0.Status != LinkStatus.Suggested) return IdentityLinkResult.Refused("Only a suggestion can be rejected; unlink a confirmed link instead.");
        var removed = new List<IdentityAccount>();
        var result = _log.Identity(t =>
        {
            var (person, account) = FindLink(t, linkId)!.Value;
            var leaving = person.Accounts.Where(a => a.ProposalId == account.ProposalId && a.Status == LinkStatus.Suggested).ToList();
            RemoveAccounts(t, person, leaving, markWrong: true);
            removed.AddRange(leaving);
            return new IdentityLinkResult(true, "Suggestion rejected; it will not be suggested again.")
            {
                Person = t.PersonById(person.Id),
                LinkIds = leaving.Select(a => a.LinkId).ToArray(),
            };
        });
        foreach (var acc in removed) LogRow("link-reject", acc, actor, Reason("rejected", reason));
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Rejected, result.Person?.Id, result.LinkIds));
        return result;
    }

    /// <summary>
    /// Takes one account out of its person, or dissolves the person (<paramref name="dissolve"/>). Marked
    /// <paramref name="wrong"/>, the pairs are remembered as rejected. Reversing the actions made through the link is
    /// <see cref="ModerationService.UnlinkAsync"/>'s job, which calls this.
    /// </summary>
    public IdentityLinkResult Unlink(string linkId, ModActor actor, bool dissolve = false, bool wrong = false, string reason = "")
    {
        if (!Enabled) return IdentityLinkResult.Refused(OffMessage);
        var key = ActorKey(actor);
        var person0 = _log.Identity(t => t.PersonByLink(linkId));
        if (person0 is null) return IdentityLinkResult.Refused("That link no longer exists.");
        var ownSuggestion = person0.AccountByLink(linkId) is { Status: LinkStatus.Suggested } own && own.AddedByKey == key && !dissolve;
        if (!ownSuggestion && Refusal(actor, LinkOperation.Unlink) is { } refused) return IdentityLinkResult.Refused(refused);
        var removed = new List<IdentityAccount>();
        var result = _log.Identity(t =>
        {
            if (FindLink(t, linkId) is not { } found) return IdentityLinkResult.Refused("That link no longer exists.");
            var (person, account) = found;
            var leaving = dissolve ? person.Accounts.ToList() : [account];
            RemoveAccounts(t, person, leaving, wrong);
            removed.AddRange(leaving);
            return new IdentityLinkResult(true, dissolve ? "The accounts are no longer linked." : $"{account.Name} on {account.Platform} is no longer linked.")
            {
                Person = t.PersonById(person.Id),
                LinkIds = leaving.Select(a => a.LinkId).ToArray(),
            };
        });
        if (!result.Ok) return result;
        foreach (var acc in removed) LogRow("unlink", acc, actor, Reason(wrong ? "marked wrong" : "no longer needed", reason));
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Unlinked, person0.Id, result.LinkIds));
        return result;
    }

    // ---------------------------------------------------------------- !link verification

    /// <summary>
    /// Starts a viewer verification for <paramref name="account"/> (section 7.1): a one-time code, valid
    /// <see cref="CodeMinutes"/> minutes, that the viewer types as <c>!link CODE</c> from their other account. The code
    /// is shown to the mod only; they send it to the viewer privately. Null with the reason when refused.
    /// </summary>
    public async Task<(LinkVerification? Verification, string Message)> StartVerificationAsync(ViewerRef account, ModActor actor,
        ChatRoles roles = ChatRoles.None, CancellationToken ct = default)
    {
        if (Refusal(actor, LinkOperation.Propose) is { } refused) return (null, refused);
        account = Clean(account);
        if (account.UserId.Length == 0) return (null, "The account needs a platform user id.");
        if (await WhyCannotLinkAsync(account, roles, ct).ConfigureAwait(false) is { } why) return (null, why);
        var now = _time.GetUtcNow();
        lock (_codeGate)
        {
            ExpireCodes(now);
            var key = ActorKey(actor);
            if (!_starts.TryGetValue(key, out var starts)) _starts[key] = starts = [];
            starts.RemoveAll(s => s <= now - TimeSpan.FromHours(1));
            if (starts.Count >= MaxCodesPerActorPerHour)
                return (null, $"You started {MaxCodesPerActorPerHour} verifications in the last hour. Wait a little.");
            starts.Add(now);
            // One open code per account: a new one replaces the old.
            foreach (var old in _codes.Where(c => c.Value.Account.Platform == account.Platform && c.Value.Account.UserId == account.UserId).Select(c => c.Key).ToArray())
            {
                _codes.Remove(old);
                _codeActors.Remove(old);
            }
            string code;
            do code = string.Concat(Enumerable.Range(0, CodeLength).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]));
            while (_codes.ContainsKey(code));
            var verification = new LinkVerification(code, account, now.AddMinutes(CodeMinutes), actor.Name);
            _codes[code] = verification;
            _codeActors[code] = actor;
            return (verification, verification.Instructions);
        }
    }

    /// <summary>Codes still open (for the UI).</summary>
    public IReadOnlyList<LinkVerification> OpenVerifications
    {
        get
        {
            lock (_codeGate)
            {
                ExpireCodes(_time.GetUtcNow());
                return _codes.Values.ToArray();
            }
        }
    }

    /// <summary>
    /// A viewer typed <c>!link CODE</c>: a code that is open, from a different account, links the two as
    /// <see cref="LinkStatus.Verified"/>. Each code works once. Unknown codes count against the account (and against
    /// everyone) so codes cannot be guessed; after too many the account is ignored for a while.
    /// </summary>
    public async Task<LinkVerifyResult> RedeemAsync(ViewerRef redeemer, string code, ChatRoles roles = ChatRoles.None, string messageId = "",
        CancellationToken ct = default)
    {
        if (!Enabled) return new LinkVerifyResult(LinkVerifyOutcome.Disabled, OffMessage);
        redeemer = Clean(redeemer);
        code = code.Trim().ToUpperInvariant();
        var now = _time.GetUtcNow();
        LinkVerification? verification;
        ModActor starter;
        lock (_codeGate)
        {
            var accountKey = redeemer.Platform + ":" + redeemer.UserId;
            if (Failures(accountKey, now) >= MaxFailedPerAccount || Failures("*", now) >= MaxFailedOverall)
                return new LinkVerifyResult(LinkVerifyOutcome.RateLimited, "Too many wrong codes. Try again later.");
            if (!_codes.TryGetValue(code, out verification))
            {
                Fail(accountKey, now);
                return new LinkVerifyResult(LinkVerifyOutcome.Unknown, "That code is not known.");
            }
            starter = _codeActors[code];
            if (verification.ExpiresAt <= now)
            {
                _codes.Remove(code);
                _codeActors.Remove(code);
                Fail(accountKey, now);
                return new LinkVerifyResult(LinkVerifyOutcome.Expired, "That code has expired.");
            }
            if (SamePlatform(verification.Account.Platform, redeemer.Platform) && verification.Account.UserId == redeemer.UserId)
                return new LinkVerifyResult(LinkVerifyOutcome.SameAccount, "Type the code from your other account.");
            // Single use: from here on the code is spent, whatever happens.
            _codes.Remove(code);
            _codeActors.Remove(code);
        }
        if (await WhyCannotLinkAsync(redeemer, roles, ct).ConfigureAwait(false) is { } why)
            return new LinkVerifyResult(LinkVerifyOutcome.Refused, why);

        var a = verification.Account;
        var changed = new List<(string Action, IdentityAccount Account)>();
        var person = _log.Identity(t =>
        {
            var pa = t.PersonByAccount(a.Platform, a.UserId);
            var pb = t.PersonByAccount(redeemer.Platform, redeemer.UserId);
            var proposal = NewId();
            IdentityAccount Fresh(ViewerRef v, string personId) => new()
            {
                LinkId = NewId(), PersonId = personId, Platform = v.Platform, UserId = v.UserId, NameAtLink = v.UserName, AddedAt = now,
                AddedBy = starter.Name, AddedByKey = "viewer", ProposalId = proposal, Status = LinkStatus.Verified, StatusAt = now, LastRefreshed = now,
            };
            string personId;
            if (pa is not null && pa.Id == pb?.Id)
            {
                personId = pa.Id;
                var b = pb.Account(redeemer.Platform, redeemer.UserId)! with { Status = LinkStatus.Verified, StatusAt = now, NeedsStreamer = false };
                t.UpdateAccount(b);
                changed.Add(("link-verify", b));
                if (pa.Accounts.Count == 2)
                {
                    var first = pa.Account(a.Platform, a.UserId)! with { Status = LinkStatus.Verified, StatusAt = now, NeedsStreamer = false };
                    t.UpdateAccount(first);
                    changed.Add(("link-verify", first));
                }
            }
            else
            {
                if (pa is null)
                {
                    personId = NewId();
                    t.InsertPerson(new IdentityPerson { Id = personId, CreatedAt = now, CreatedBy = starter.Name, LastReviewed = now });
                    var first = Fresh(a, personId);
                    t.InsertAccount(first);
                    changed.Add(("link-verify", first));
                }
                else personId = pa.Id;
                if (pb?.Account(redeemer.Platform, redeemer.UserId) is { } moving)
                {
                    var moved = moving with { PersonId = personId, ProposalId = proposal, Status = LinkStatus.Verified, StatusAt = now, AddedBy = starter.Name, AddedByKey = "viewer" };
                    t.UpdateAccount(moved);
                    changed.Add(("link-verify", moved));
                    DissolveIfAlone(t, pb.Id);
                }
                else
                {
                    var b = Fresh(redeemer, personId);
                    t.InsertAccount(b);
                    changed.Add(("link-verify", b));
                }
            }
            var p = t.PersonById(personId)!;
            if (NeedsStreamer(p))
                foreach (var (_, acc) in changed.Where(c => c.Account.PersonId == personId && c.Account.Is(redeemer.Platform, redeemer.UserId)).ToArray())
                {
                    // Over the limits: the proof stands, but only the streamer turns it into a link that is used.
                    var held = p.AccountByLink(acc.LinkId)! with { Status = LinkStatus.Suggested, NeedsStreamer = true, AddedByKey = "viewer" };
                    t.UpdateAccount(held);
                }
            t.InsertEvidence(new LinkEvidence
            {
                Id = NewId(), PersonId = personId, LinkId = p.Account(redeemer.Platform, redeemer.UserId)?.LinkId, Kind = LinkEvidenceKinds.VerifiedCode,
                MessageIds = messageId.Length > 0 ? [messageId] : [], AddedBy = starter.Name, At = now,
            });
            t.UpdatePerson(personId, p.Note, now);
            return t.PersonById(personId)!;
        });
        foreach (var (action, acc) in changed) LogRow(action, person.AccountByLink(acc.LinkId) ?? acc, starter, "verified with !link");
        _appLog?.Info("Identity links", "A viewer verified two accounts with !link");
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Verified, person.Id, changed.Select(c => c.Account.LinkId).ToArray()));
        return new LinkVerifyResult(LinkVerifyOutcome.Verified, "Verified.") { Person = person };
    }

    /// <summary>Hears <c>!link CODE</c> in chat; answers only a code that worked, so codes cannot be probed.</summary>
    private void OnChatMessage(ChatMessage message)
    {
        TouchLinked(message);
        if (!Enabled) return;
        var text = message.Text.Trim();
        if (!text.StartsWith("!link ", StringComparison.OrdinalIgnoreCase)) return;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[1].Length != CodeLength) return;
        _ = HandleAsync();

        async Task HandleAsync()
        {
            try
            {
                var result = await RedeemAsync(new ViewerRef(message.Platform, message.UserId, message.User), parts[1], message.Roles, message.Id)
                    .ConfigureAwait(false);
                if (result.Outcome == LinkVerifyOutcome.Verified && _hub is not null)
                    await _hub.SendAsync(message.Platform, $"@{message.User} thanks, that's confirmed.").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _appLog?.Warn("Identity links", $"!link failed: {ex.Message}");
            }
        }
    }

    private readonly object _touchGate = new();
    private HashSet<string>? _linkedKeys;
    private readonly Dictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);

    /// <summary>
    /// A linked account seen in chat: its name is kept current and its data counts as refreshed (YouTube's 30-day rule).
    /// At most once an hour per account; accounts in no link cost a set lookup.
    /// </summary>
    private void TouchLinked(ChatMessage message)
    {
        if (message.UserId.Length == 0) return;
        try
        {
            var key = LinkPlatforms.Canon(message.Platform) + ":" + message.UserId;
            var now = _time.GetUtcNow();
            lock (_touchGate)
            {
                _linkedKeys ??= _log.AccountsWhere(null, null, null, null).Select(a => LinkPlatforms.Canon(a.Platform) + ":" + a.UserId)
                    .ToHashSet(StringComparer.Ordinal);
                if (!_linkedKeys.Contains(key)) return;
                if (_touched.TryGetValue(key, out var last) && now - last < TimeSpan.FromHours(1)) return;
                _touched[key] = now;
            }
            _log.TouchAccount(LinkPlatforms.Canon(message.Platform), message.UserId, message.User, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Identity links", $"Could not refresh a linked account: {ex.Message}");
        }
    }

    private void OnChanged(IdentityLinkChange change)
    {
        lock (_touchGate) _linkedKeys = null;
        Changed?.Invoke(change);
    }

    // ---------------------------------------------------------------- notes

    /// <summary>Notes on this account and on every account linked to it, newest first (empty while links are off).</summary>
    public IReadOnlyList<ModNote> NotesFor(string platform, string userId, ModActor? reader = null)
    {
        if (!Enabled || userId.Length == 0) return [];
        if (reader is not null && Refusal(reader, LinkOperation.ReadNotes) is not null) return [];
        platform = LinkPlatforms.Canon(platform);
        var person = _log.PersonByAccount(platform, userId);
        var accounts = person?.Accounts.Select(a => (a.Platform, a.UserId)).ToList() ?? [];
        if (!accounts.Contains((platform, userId))) accounts.Add((platform, userId));
        return _log.NotesFor(accounts).Select(n => n with { PersonId = person?.Id }).ToArray();
    }

    /// <summary>Adds a note on one account. Notes are free text: the UI warns not to record health, beliefs or similar.</summary>
    public (ModNote? Note, string Message) AddNote(ViewerRef account, string text, ModActor actor)
    {
        if (Refusal(actor, LinkOperation.AddNote) is { } refused) return (null, refused);
        account = Clean(account);
        text = text.Trim();
        if (text.Length == 0) return (null, "Write something first.");
        if (account.UserId.Length == 0) return (null, "The account needs a platform user id.");
        var now = _time.GetUtcNow();
        var note = new ModNote
        {
            Id = NewId(), Platform = account.Platform, UserId = account.UserId, Text = Trim(text, 2000), Author = actor.Name,
            AuthorMemberId = ActorKey(actor), CreatedAt = now, EditedAt = now,
        };
        _log.InsertNote(note);
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Notes, null, []));
        return (note, "Note saved.");
    }

    /// <summary>Changes a note: its author, or someone allowed to delete others' notes.</summary>
    public (bool Ok, string Message) EditNote(string noteId, string text, ModActor actor)
    {
        if (!Enabled) return (false, OffMessage);
        if (_log.Note(noteId) is not { } note) return (false, "That note no longer exists.");
        if (note.AuthorMemberId != ActorKey(actor) && Refusal(actor, LinkOperation.DeleteNote) is { } refused) return (false, refused);
        if (Refusal(actor, LinkOperation.AddNote) is { } cannot) return (false, cannot);
        text = text.Trim();
        if (text.Length == 0) return DeleteNote(noteId, actor);
        _log.UpdateNote(noteId, Trim(text, 2000), _time.GetUtcNow());
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Notes, null, []));
        return (true, "Note saved.");
    }

    public (bool Ok, string Message) DeleteNote(string noteId, ModActor actor)
    {
        if (!Enabled) return (false, OffMessage);
        if (_log.Note(noteId) is not { } note) return (false, "That note no longer exists.");
        if (note.AuthorMemberId != ActorKey(actor) && Refusal(actor, LinkOperation.DeleteNote) is { } refused) return (false, refused);
        _log.DeleteNotes(noteId);
        OnChanged(new IdentityLinkChange(IdentityLinkChange.Notes, null, []));
        return (true, "Note deleted.");
    }

    // ---------------------------------------------------------------- hints

    /// <summary>
    /// Accounts on other platforms with the same name, from the viewers Cuebot has seen: only ever a hint on the user
    /// card (section 7.1). Leaves out accounts already in this person, rejected pairs and protected accounts. Nothing is
    /// matched on behaviour (writing style, timing): that would be profiling.
    /// </summary>
    public IReadOnlyList<ViewerRef> SameNameHints(string platform, string userId, string name)
    {
        if (!Enabled || _users is null || name.Trim().Length == 0) return [];
        platform = LinkPlatforms.Canon(platform);
        var person = _log.PersonByAccount(platform, userId);
        var hints = new List<ViewerRef>();
        var platforms = new[] { "Twitch", "YouTube", "Kick", "Rumble" }
            .Concat(_users.All().Select(u => LinkPlatforms.Canon(u.Platform))).Distinct().Where(p => p != platform);
        foreach (var other in platforms)
        {
            if (_users.FindByName(other, name) is not { } found || found.UserId.Length == 0 || found.IsBotAccount) continue;
            if (found.Roles.HasFlag(ChatRoles.Broadcaster) || found.Roles.HasFlag(ChatRoles.Moderator) || found.Roles.HasFlag(ChatRoles.Vip)) continue;
            var canon = LinkPlatforms.Canon(found.Platform);
            if (person?.Account(canon, found.UserId) is not null) continue;
            if (_log.IsRejected(platform, userId, canon, found.UserId)) continue;
            if (_protected.Any(p => p.WhyProtected(canon, found.UserId, found.UserName) is not null)) continue;
            hints.Add(new ViewerRef(canon, found.UserId, found.DisplayName.Length > 0 ? found.DisplayName : found.UserName));
        }
        return hints;
    }

    // ---------------------------------------------------------------- retention

    /// <summary>
    /// Applies the identity retention (section 9.2, decision 8): suggestions nobody confirmed go after
    /// <see cref="IdentityRetention.UnconfirmedLinkDays"/>; rejected pairs <see cref="IdentityRetention.LinkMonthsAfterEnd"/>
    /// months after they were rejected; notes <see cref="IdentityRetention.NoteMonthsAfterEdit"/> months after their last
    /// edit; notes on YouTube accounts that are in no link and not seen for 30 days. Returns how many records went.
    /// Runs whether links are on or off: stored data keeps its limits.
    /// </summary>
    public int ApplyRetention(DateTimeOffset now)
    {
        var r = _settings().LinkRetention;
        var removed = 0;
        var stale = _log.AccountsWhere(LinkStatus.Suggested, now.AddDays(-r.UnconfirmedLinkDays), null, null);
        if (stale.Count > 0) removed += RemoveSilently(stale);
        removed += _log.DeleteRejections(now.AddMonths(-r.LinkMonthsAfterEnd));
        removed += _log.DeleteNotes(editedBefore: now.AddMonths(-r.NoteMonthsAfterEdit));
        if (_users is not null)
        {
            // YouTube's 30-day rule for notes on accounts in no link: kept while the viewer is seen in chat.
            var cutoff = now.AddDays(-IdentityRetention.YouTubeRefreshDays);
            var linked = _log.AccountsWhere(null, null, null, "YouTube").Select(a => a.UserId).ToHashSet(StringComparer.Ordinal);
            var seen = _users.All().Where(u => LinkPlatforms.IsYouTube(u.Platform) && u.LastSeen >= cutoff).Select(u => u.UserId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var note in _log.NotesForPlatform("YouTube").Where(n => !linked.Contains(n.UserId) && !seen.Contains(n.UserId)))
                removed += _log.DeleteNotes(note.Id);
        }
        if (removed > 0)
        {
            _appLog?.Info("Identity links", $"Removed {removed} old link suggestion(s), rejection(s) or note(s)");
            OnChanged(new IdentityLinkChange(IdentityLinkChange.Retention, null, []));
        }
        return removed;
    }

    /// <summary>
    /// YouTube's 30-day rule for links (section 9.3): a YouTube account in a link whose data is older than 25 days is read
    /// again from YouTube (<see cref="IAccountLookup"/>, 1 quota unit), which refreshes its name. One that is gone, or that
    /// could not be refreshed within 30 days, is dropped from its person. Seeing the account in chat also refreshes it.
    /// </summary>
    public async Task<int> RefreshYouTubeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var lookup = _lookups.FirstOrDefault(l => LinkPlatforms.IsYouTube(l.Platform));
        var due = _log.AccountsWhere(null, null, now.AddDays(-IdentityRetention.YouTubeRefreshStartDays), "YouTube");
        var dropped = new List<IdentityAccount>();
        var refreshed = 0;
        foreach (var account in due)
        {
            string? name = null;
            var known = false;
            if (lookup is not null)
            {
                try
                {
                    name = await lookup.CurrentNameAsync(account.UserId, ct).ConfigureAwait(false);
                    known = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _appLog?.Warn("Identity links", $"Could not refresh a linked YouTube account: {ex.Message}");
                }
            }
            if (known && name is not null)
            {
                _log.TouchAccount(account.Platform, account.UserId, name, now);
                refreshed++;
            }
            else if (known || account.LastRefreshed < now.AddDays(-IdentityRetention.YouTubeRefreshDays))
                dropped.Add(account);
        }
        if (dropped.Count > 0)
        {
            RemoveSilently(dropped);
            _appLog?.Info("Identity links", $"Dropped {dropped.Count} linked YouTube account(s) that could not be refreshed within 30 days");
        }
        return refreshed;
    }

    // ---------------------------------------------------------------- forget this viewer

    /// <summary>The account itself and the accounts confirmed or verified as the same person (suggestions are not followed).</summary>
    public IReadOnlyList<ViewerRef> LinkedAccounts(string platform, string userId, string userName)
    {
        var canon = LinkPlatforms.Canon(platform);
        var self = new ViewerRef(canon, userId, userName);
        if (userId.Length == 0) return [self];
        var person = _log.PersonByAccount(canon, userId);
        if (person?.Account(canon, userId) is not { } me) return [self];
        return [self, .. person.Accounts.Where(a => a.LinkId != me.LinkId && Between(me, a) >= LinkStatus.Confirmed).Select(a => a.Ref)];
    }

    /// <summary>Everything held about these accounts: the person and its links, notes and mod log rows (a viewer's access request).</summary>
    public JsonObject Export(IReadOnlyList<ViewerRef> accounts)
    {
        var people = new JsonArray();
        var notes = new JsonArray();
        var rows = new JsonArray();
        var seenPeople = new HashSet<string>();
        foreach (var a in accounts)
        {
            var platform = LinkPlatforms.Canon(a.Platform);
            if (a.UserId.Length == 0) continue;
            if (_log.PersonByAccount(platform, a.UserId) is { } p && seenPeople.Add(p.Id))
                people.Add(new JsonObject
                {
                    ["note"] = p.Note,
                    ["createdAt"] = p.CreatedAt.ToString("O"),
                    ["accounts"] = new JsonArray(p.Accounts.Where(x => accounts.Any(v => SamePlatform(v.Platform, x.Platform) && v.UserId == x.UserId))
                        .Select(x => (JsonNode)new JsonObject
                        {
                            ["platform"] = x.Platform, ["userId"] = x.UserId, ["nameAtLink"] = x.NameAtLink, ["currentName"] = x.CurrentName,
                            ["status"] = x.Status.ToString().ToLowerInvariant(), ["since"] = x.StatusAt.ToString("O"), ["addedBy"] = x.AddedBy,
                        }).ToArray()),
                    ["evidence"] = new JsonArray(p.Evidence.Select(e => (JsonNode)new JsonObject
                    {
                        ["kind"] = e.Kind, ["quote"] = e.Quote, ["at"] = e.At.ToString("O"), ["addedBy"] = e.AddedBy,
                    }).ToArray()),
                });
            foreach (var n in _log.NotesFor([(platform, a.UserId)]))
                notes.Add(new JsonObject { ["platform"] = n.Platform, ["userId"] = n.UserId, ["text"] = n.Text, ["author"] = n.Author, ["editedAt"] = n.EditedAt.ToString("O") });
            foreach (var e in _log.ForTarget(platform, a.UserId, 10_000))
                rows.Add(ApiRow(e));
        }
        return new JsonObject { ["people"] = people, ["notes"] = notes, ["modLog"] = rows };
    }

    /// <summary>
    /// Erases what is held about these accounts (section 9.2): links (the person goes when fewer than two accounts are
    /// left), rejected pairs, notes, and in the mod log the names and message text; an active ban keeps its row and the
    /// platform user id. Works whether links are on or off. Not logged as unlinks, which would record the link again.
    /// </summary>
    public int Erase(IReadOnlyList<ViewerRef> accounts)
    {
        var changed = 0;
        foreach (var a in accounts.Where(a => a.UserId.Length > 0))
        {
            var platform = LinkPlatforms.Canon(a.Platform);
            changed += _log.DeleteNotes(platform: platform, userId: a.UserId);
            changed += _log.DeleteRejections(DateTimeOffset.MaxValue, platform, a.UserId);
            if (_log.PersonByAccount(platform, a.UserId) is { } person && person.Account(platform, a.UserId) is { } account)
            {
                _log.Identity(t =>
                {
                    RemoveAccounts(t, person, [account], markWrong: false);
                    return 0;
                });
                changed++;
            }
            changed += _log.EraseAccount(platform, a.UserId);
        }
        if (changed > 0) OnChanged(new IdentityLinkChange(IdentityLinkChange.Erased, null, []));
        return changed;
    }

    private ITimer? _timer;

    /// <summary>Starts the retention and YouTube refresh job: shortly after start, then every six hours.</summary>
    public void Start() =>
        _timer ??= _time.CreateTimer(_ => _ = RunRetentionAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));

    /// <summary>One pass of <see cref="ApplyRetention"/> and <see cref="RefreshYouTubeAsync"/>; never throws.</summary>
    public async Task RunRetentionAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _time.GetUtcNow();
            ApplyRetention(now);
            await RefreshYouTubeAsync(now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Identity links", $"Retention failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_hub is not null) _hub.MessageReceived -= OnChatMessage;
    }

    // ---------------------------------------------------------------- rules and helpers

    private const string OffMessage = "Identity links are turned off.";
    private const string StreamerKey = "streamer";

    /// <summary>"streamer", "member:&lt;id&gt;", or the kind and name for automation.</summary>
    public static string ActorKey(ModActor actor) => actor.Kind switch
    {
        ModActorKind.Streamer => StreamerKey,
        ModActorKind.Member => "member:" + (actor.MemberId ?? actor.Name),
        _ => actor.Kind.ToString().ToLowerInvariant() + ":" + actor.Name,
    };

    /// <summary>Links off, or the permission hook refuses: the reason; otherwise null.</summary>
    public string? Refusal(ModActor actor, LinkOperation operation)
    {
        if (!Enabled) return OffMessage;
        if (actor.Kind == ModActorKind.Streamer) return null;
        var permissions = PermissionsSource();
        return permissions is not null ? permissions.RefuseLink(actor, operation) : LinkPermissionDefaults.Refuse(actor, operation);
    }

    /// <summary>
    /// The accounts a link may never include (section 7.2), whoever asks: the broadcaster, platform moderators and VIPs
    /// (from chat roles, the viewer list and the platform's moderator lookup), bot accounts, and whatever the platform
    /// modules and the portal protect (bot accounts, portal members).
    /// </summary>
    public async Task<string?> WhyCannotLinkAsync(ViewerRef account, ChatRoles roles = ChatRoles.None, CancellationToken ct = default)
    {
        var name = account.UserName.Length > 0 ? account.UserName : "This account";
        var known = _users?.Get(account.Platform, account.UserId)
                    ?? _users?.All().FirstOrDefault(u => SamePlatform(u.Platform, account.Platform) && u.UserId == account.UserId);
        var all = roles | (known?.Roles ?? ChatRoles.None);
        if (all.HasFlag(ChatRoles.Broadcaster)) return $"{name} is the broadcaster and can never be linked.";
        if (all.HasFlag(ChatRoles.Moderator)) return $"{name} is a moderator and can never be linked.";
        if (all.HasFlag(ChatRoles.Vip)) return $"{name} is a VIP and can never be linked.";
        if (known?.IsBotAccount == true) return $"{name} is a bot account and can never be linked.";
        foreach (var p in _protected)
            if (p.WhyProtected(account.Platform, account.UserId, account.UserName) is { } why)
                return $"{name} can never be linked: {why}";
        if (_roles.FirstOrDefault(r => SamePlatform(r.Platform, account.Platform)) is { } lookup)
        {
            try
            {
                if (await lookup.CheckModeratorAsync(account.UserId, ct).ConfigureAwait(false) == true)
                    return $"{name} is a moderator and can never be linked.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _appLog?.Warn("Identity links", $"Could not check whether {name} moderates {account.Platform}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>More than four accounts, or two on one platform: the streamer decides.</summary>
    private static bool NeedsStreamer(IdentityPerson person) =>
        person.Accounts.Count > MaxAccounts || person.Accounts.GroupBy(a => a.Platform, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);

    private static (IdentityPerson Person, IdentityAccount Account)? FindLink(ModLog.IdentityTx t, string linkId) =>
        t.PersonByLink(linkId) is { } person && person.AccountByLink(linkId) is { } account ? (person, account) : null;

    /// <summary>Takes accounts out of a person; remembers the pairs when marked wrong; dissolves a person left with one account.</summary>
    private void RemoveAccounts(ModLog.IdentityTx t, IdentityPerson person, IReadOnlyList<IdentityAccount> leaving, bool markWrong)
    {
        var now = _time.GetUtcNow();
        var staying = person.Accounts.Where(a => leaving.All(l => l.LinkId != a.LinkId)).ToList();
        if (markWrong)
        {
            // Pairs between each leaving account and each staying one; when everything goes, among the leaving ones.
            var pairs = staying.Count > 0
                ? leaving.SelectMany(l => staying.Select(o => (l, o)))
                : leaving.SelectMany((l, i) => leaving.Skip(i + 1).Select(o => (l, o)));
            foreach (var (l, o) in pairs) t.InsertRejection(new LinkRejection(l.Platform, l.UserId, o.Platform, o.UserId, now));
        }
        foreach (var l in leaving) t.DeleteAccount(l.LinkId);
        DissolveIfAlone(t, person.Id);
    }

    private static void DissolveIfAlone(ModLog.IdentityTx t, string personId)
    {
        if (t.PersonById(personId) is { } p && p.Accounts.Count < 2) t.DeletePerson(personId);
    }

    /// <summary>Removes accounts without a mod log row (retention, the YouTube rule).</summary>
    private int RemoveSilently(IReadOnlyList<IdentityAccount> accounts)
    {
        var removed = 0;
        foreach (var group in accounts.GroupBy(a => a.PersonId))
            removed += _log.Identity(t =>
            {
                if (t.PersonById(group.Key) is not { } person) return 0;
                var leaving = person.Accounts.Where(a => group.Any(g => g.LinkId == a.LinkId)).ToList();
                RemoveAccounts(t, person, leaving, markWrong: false);
                return leaving.Count;
            });
        lock (_touchGate) _linkedKeys = null;
        return removed;
    }

    private void LogRow(string action, IdentityAccount account, ModActor actor, string reason)
    {
        try
        {
            _log.Append(new ModLogEntry
            {
                ActorKind = actor.Kind,
                ActorMemberId = actor.MemberId,
                ActorName = actor.Name,
                ActorDeviceId = actor.DeviceId,
                Platform = account.Platform,
                Action = action,
                TargetUserId = account.UserId,
                TargetName = account.Name,
                Reason = reason,
                ViaLinkId = account.LinkId,
                Result = ModResult.Ok,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _appLog?.Warn("Identity links", $"Could not write the mod log: {ex.Message}");
        }
    }

    private int Failures(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var list)) return 0;
        list.RemoveAll(t => t <= now - FailWindow);
        return list.Count;
    }

    private void Fail(string key, DateTimeOffset now)
    {
        foreach (var k in new[] { key, "*" })
        {
            if (!_failures.TryGetValue(k, out var list)) _failures[k] = list = [];
            list.Add(now);
        }
        if (_failures.Count > 5000) _failures.Clear();
    }

    private void ExpireCodes(DateTimeOffset now)
    {
        foreach (var code in _codes.Where(c => c.Value.ExpiresAt <= now).Select(c => c.Key).ToArray())
        {
            _codes.Remove(code);
            _codeActors.Remove(code);
        }
    }

    private static ViewerRef Clean(ViewerRef v) => new(LinkPlatforms.Canon(v.Platform), v.UserId.Trim(), v.UserName.Trim().TrimStart('@'));

    private static string Reason(string what, string reason) => reason.Trim().Length > 0 ? $"{what}: {Trim(reason, 300)}" : what;

    private static string Trim(string text, int max) => text.Trim().Length > max ? text.Trim()[..max] : text.Trim();

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    internal static JsonObject ApiRow(ModLogEntry e) => new()
    {
        ["at"] = e.At.ToString("O"),
        ["platform"] = e.Platform,
        ["action"] = e.Action,
        ["by"] = e.ActorName,
        ["name"] = e.TargetName,
        ["message"] = e.MessageText,
        ["reason"] = e.Reason,
        ["result"] = e.Result.ToString().ToLowerInvariant(),
        ["durationSeconds"] = e.DurationAppliedSeconds,
        ["lifted"] = e.UndoneAt?.ToString("O"),
    };
}
