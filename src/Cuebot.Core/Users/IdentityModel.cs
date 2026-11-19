using Cuebot.Core.Variables;

namespace Cuebot.Core.Users;

/// <summary>
/// How sure the team is that an account belongs to a person (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 7.1). Ordered from
/// weakest to strongest; <see cref="Rejected"/> is not a membership, it is remembered so the same wrong suggestion is not
/// made again.
/// </summary>
public enum LinkStatus
{
    /// <summary>A mod asserted it; a second person has not confirmed it. Never used by default in a linked action.</summary>
    Suggested,

    /// <summary>Asserted by one person and confirmed by another (the streamer, or a member allowed to confirm links).</summary>
    Confirmed,

    /// <summary>The viewer proved control of both accounts with <c>!link CODE</c>.</summary>
    Verified,

    /// <summary>Marked wrong: only the two account ids are kept.</summary>
    Rejected,
}

/// <summary>What a link operation is, for <see cref="Chat.Moderation.IModerationPermissions.RefuseLink"/>.</summary>
public enum LinkOperation
{
    /// <summary>See linked accounts on a user card.</summary>
    View,

    /// <summary>Assert "same person as..." (makes a suggestion) or start a <c>!link</c> verification.</summary>
    Propose,

    /// <summary>Confirm someone else's suggestion.</summary>
    Confirm,

    /// <summary>Mark a suggestion wrong.</summary>
    Reject,

    /// <summary>Take an account out of a person, or dissolve it.</summary>
    Unlink,

    ReadNotes,
    AddNote,

    /// <summary>Delete or edit a note someone else wrote.</summary>
    DeleteNote,
}

/// <summary>Kinds of evidence stored with a link (section 6.2).</summary>
public static class LinkEvidenceKinds
{
    public const string ViewerSaid = "viewer-said";
    public const string VerifiedCode = "verified-code";
    public const string ModNote = "mod-note";
    public const string SameName = "same-name";

    public static readonly IReadOnlyList<string> All = [ViewerSaid, VerifiedCode, ModNote, SameName];
}

/// <summary>Evidence for a proposal: what supports it (a quote of the viewer saying so, the message ids).</summary>
public sealed record LinkEvidenceInput(string Kind, string Quote = "")
{
    public IReadOnlyList<string> MessageIds { get; init; } = [];
}

/// <summary>A member (or the streamer) who confirmed a link.</summary>
public sealed record LinkConfirmation(string By, string? MemberId, DateTimeOffset At);

/// <summary>One piece of evidence stored with a person.</summary>
public sealed record LinkEvidence
{
    public string Id { get; init; } = "";
    public string PersonId { get; init; } = "";

    /// <summary>The account membership it supports, when it is about one.</summary>
    public string? LinkId { get; init; }

    public string Kind { get; init; } = LinkEvidenceKinds.ModNote;
    public string Quote { get; init; } = "";
    public IReadOnlyList<string> MessageIds { get; init; } = [];
    public string AddedBy { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// One platform account in a person: the "link" a linked action goes through. <see cref="LinkId"/> is what mod log rows
/// made because of it carry as <c>viaLinkId</c>.
/// </summary>
public sealed record IdentityAccount
{
    public string LinkId { get; init; } = "";
    public string PersonId { get; init; } = "";

    /// <summary>"Twitch", "YouTube", "Kick", "Rumble".</summary>
    public string Platform { get; init; } = "";

    /// <summary>The platform's stable user id.</summary>
    public string UserId { get; init; } = "";

    public string NameAtLink { get; init; } = "";

    /// <summary>The name last seen in chat, when it changed since the link was made.</summary>
    public string CurrentName { get; init; } = "";

    public DateTimeOffset AddedAt { get; init; }
    public string AddedBy { get; init; } = "";

    /// <summary>
    /// Who added it, as a stable key ("streamer", "member:&lt;id&gt;", "viewer"): the proposer, who may not confirm it.
    /// </summary>
    public string AddedByKey { get; init; } = "";

    /// <summary>Accounts added by one proposal share it; confirming confirms all of them.</summary>
    public string ProposalId { get; init; } = "";

    public LinkStatus Status { get; init; }

    /// <summary>When it became <see cref="Status"/>.</summary>
    public DateTimeOffset StatusAt { get; init; }

    public IReadOnlyList<LinkConfirmation> ConfirmedBy { get; init; } = [];

    /// <summary>When the account's data was last refreshed (seen in chat or read from the platform): YouTube's 30-day rule.</summary>
    public DateTimeOffset LastRefreshed { get; init; }

    /// <summary>More than four accounts or two on one platform: only the streamer can confirm it.</summary>
    public bool NeedsStreamer { get; init; }

    public string Name => CurrentName.Length > 0 ? CurrentName : NameAtLink;

    public ViewerRef Ref => new(Platform, UserId, Name);

    public bool Is(string platform, string userId) => IdentityLinks.SamePlatform(Platform, platform) && UserId == userId;
}

/// <summary>Two or more platform accounts that the team believes are one person (section 6.2).</summary>
public sealed record IdentityPerson
{
    /// <summary>Random; never shown to viewers, never sent to a platform.</summary>
    public string Id { get; init; } = "";

    public string Note { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";

    /// <summary>For the yearly review prompt and the YouTube 30-day refresh.</summary>
    public DateTimeOffset LastReviewed { get; init; }

    public IReadOnlyList<IdentityAccount> Accounts { get; init; } = [];
    public IReadOnlyList<LinkEvidence> Evidence { get; init; } = [];

    public IdentityAccount? Account(string platform, string userId) => Accounts.FirstOrDefault(a => a.Is(platform, userId));

    public IdentityAccount? AccountByLink(string linkId) => Accounts.FirstOrDefault(a => a.LinkId == linkId);
}

/// <summary>A note about a viewer (section 6.3): on a person (follows every linked account) or on one account.</summary>
public sealed record ModNote
{
    public string Id { get; init; } = "";

    /// <summary>Set when the note is about the person (all linked accounts); null for a note on one account.</summary>
    public string? PersonId { get; init; }

    public string Platform { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Text { get; init; } = "";
    public string Author { get; init; } = "";
    public string? AuthorMemberId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset EditedAt { get; init; }
}

/// <summary>A suggestion marked wrong: only the two account ids and when.</summary>
public sealed record LinkRejection(string PlatformA, string UserIdA, string PlatformB, string UserIdB, DateTimeOffset At);

/// <summary>Canonical platform names for identity links ("twitch", "tw" → "Twitch").</summary>
public static class LinkPlatforms
{
    /// <summary>"Twitch", "YouTube", "Kick", "Rumble"; other names keep their first letter upper case.</summary>
    public static string Canon(string platform) =>
        AccountLinks.DisplayName(AccountLinks.PlatformOf(platform) ?? UserVariables.NormalizePlatform(platform));

    /// <summary>True for YouTube accounts, whose data must be refreshed every 30 days.</summary>
    public static bool IsYouTube(string platform) => string.Equals(Canon(platform), "YouTube", StringComparison.Ordinal);
}
