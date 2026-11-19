namespace Cuebot.Core.Chat.Moderation;

/// <summary>The moderation tools of the combined chat (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 4).</summary>
public enum ModerationAction
{
    Delete,
    Timeout,
    Ban,

    /// <summary>Lifts a ban or a timeout.</summary>
    Unban,
    Warn,

    /// <summary>Clears the whole chat.</summary>
    Clear,
    SlowMode,
    FollowersOnly,
    SubscribersOnly,
    EmoteOnly,
    Pin,
    Unpin,

    // Phase 3 ("Twitch depth"): the tools of a platform's moderation queues (<see cref="IModerationQueues"/>). A platform
    // whose moderator does not implement that interface does not have them.

    /// <summary>Turns Shield Mode on (<see cref="ModerationRequest.Enabled"/>) or off.</summary>
    ShieldMode,

    /// <summary>Lets a message the platform held (AutoMod) into chat; <see cref="ModerationRequest.MessageId"/> is the held message.</summary>
    ApproveHeld,

    /// <summary>Keeps a held message out of chat.</summary>
    DenyHeld,

    /// <summary>Marks a chatter as suspicious: their messages are flagged for moderators.</summary>
    MonitorUser,

    /// <summary>Marks a chatter as restricted: only moderators see their messages.</summary>
    RestrictUser,

    /// <summary>Removes a chatter's monitored or restricted status.</summary>
    ClearSuspicious,

    /// <summary>Approves an unban request (<see cref="ModerationRequest.ItemId"/>), which lifts the ban.</summary>
    ApproveUnbanRequest,

    /// <summary>Denies an unban request.</summary>
    DenyUnbanRequest,

    /// <summary>Adds <see cref="ModerationRequest.Term"/> to the platform's blocked terms.</summary>
    AddBlockedTerm,

    /// <summary>Removes the blocked term <see cref="ModerationRequest.ItemId"/>.</summary>
    RemoveBlockedTerm,
}

/// <summary>What a tool does on one platform.</summary>
public enum ModerationToolState
{
    /// <summary>The platform's API does it.</summary>
    Works,

    /// <summary>The API does something similar; <see cref="ModerationCapability.Note"/> names the difference.</summary>
    Closest,

    /// <summary>No API: the button opens the platform's own page (<see cref="ModerationCapability.WebsiteUrl"/>).</summary>
    OnWebsite,

    /// <summary>The platform has no such feature, or Cuebot cannot use it now; <see cref="ModerationCapability.Note"/> says why.</summary>
    NotAvailable,
}

/// <summary>
/// One tool on one platform: whether it works, its limits (durations, reason length), what it costs (YouTube quota
/// units) and where to do it by hand. Built by each <see cref="IChatModerator"/>; the UI shows it before a click.
/// </summary>
public sealed record ModerationCapability(ModerationAction Action, ModerationToolState State)
{
    /// <summary>One plain line: the difference for <see cref="ModerationToolState.Closest"/>, the reason when not available.</summary>
    public string Note { get; init; } = "";

    public TimeSpan? MinDuration { get; init; }
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>The platform's unit: durations are rounded up to a whole number of these (Kick: one minute).</summary>
    public TimeSpan DurationUnit { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest reason the platform stores; null when it stores none or has no limit.</summary>
    public int? MaxReasonLength { get; init; }

    /// <summary>YouTube quota units one call costs; 0 elsewhere.</summary>
    public int QuotaCost { get; init; }

    /// <summary>The platform's own page for doing this by hand, when there is one.</summary>
    public string? WebsiteUrl { get; init; }

    /// <summary>True when Cuebot can do it itself (works, or the closest equivalent).</summary>
    public bool CanRun => State is ModerationToolState.Works or ModerationToolState.Closest;

    /// <summary>The duration the platform will really apply: rounded up to its unit and kept within its limits.</summary>
    public TimeSpan Apply(TimeSpan requested)
    {
        var unit = DurationUnit > TimeSpan.Zero ? DurationUnit : TimeSpan.FromSeconds(1);
        var units = Math.Ceiling(Math.Max(requested.Ticks, 1) / (double)unit.Ticks);
        var applied = TimeSpan.FromTicks((long)(units * unit.Ticks));
        if (MinDuration is { } min && applied < min) applied = min;
        if (MaxDuration is { } max && applied > max) applied = max;
        return applied;
    }

    public static ModerationCapability Works(ModerationAction action) => new(action, ModerationToolState.Works);

    public static ModerationCapability NotAvailable(ModerationAction action, string why) =>
        new(action, ModerationToolState.NotAvailable) { Note = why };

    public static ModerationCapability Website(ModerationAction action, string? url, string note) =>
        new(action, ModerationToolState.OnWebsite) { WebsiteUrl = url, Note = note };

    /// <summary>All the tools, in toolbar order.</summary>
    public static IReadOnlyList<ModerationAction> AllActions { get; } = Enum.GetValues<ModerationAction>();

    /// <summary>
    /// The tools of a platform's moderation queues (Shield Mode, held messages, suspicious users, unban requests,
    /// blocked terms). Only a moderator that implements <see cref="IModerationQueues"/> offers them.
    /// </summary>
    public static bool IsQueueTool(ModerationAction action) => action >= ModerationAction.ShieldMode;

    /// <summary>What a moderator written before capabilities existed offers: its three flags, nothing else.</summary>
    public static ModerationCapability FromLegacy(ModerationAction action, ChatModerationActions available)
    {
        var flag = action switch
        {
            ModerationAction.Delete => ChatModerationActions.DeleteMessage,
            ModerationAction.Timeout => ChatModerationActions.Timeout,
            ModerationAction.Ban => ChatModerationActions.Ban,
            _ => ChatModerationActions.None,
        };
        if (flag == ChatModerationActions.None) return NotAvailable(action, "Cuebot cannot do this on this platform.");
        return available.HasFlag(flag) ? Works(action) : NotAvailable(action, "Not available right now: connect and sign in first.");
    }

    /// <summary>Lower-case name used in the mod log ("ban", "slow-mode"...).</summary>
    public static string LogName(ModerationAction action) => action switch
    {
        ModerationAction.SlowMode or ModerationAction.FollowersOnly or ModerationAction.SubscribersOnly or ModerationAction.EmoteOnly => "mode",
        ModerationAction.ShieldMode => "shield",
        ModerationAction.ApproveHeld => "held-approve",
        ModerationAction.DenyHeld => "held-deny",
        ModerationAction.MonitorUser => "suspicious-monitor",
        ModerationAction.RestrictUser => "suspicious-restrict",
        ModerationAction.ClearSuspicious => "suspicious-clear",
        ModerationAction.ApproveUnbanRequest => "unban-request-approve",
        ModerationAction.DenyUnbanRequest => "unban-request-deny",
        ModerationAction.AddBlockedTerm => "term-add",
        ModerationAction.RemoveBlockedTerm => "term-remove",
        _ => action.ToString().ToLowerInvariant(),
    };

    /// <summary>Plain name for buttons and messages.</summary>
    public static string Label(ModerationAction action) => action switch
    {
        ModerationAction.Delete => "Delete message",
        ModerationAction.SlowMode => "Slow mode",
        ModerationAction.FollowersOnly => "Followers-only",
        ModerationAction.SubscribersOnly => "Subscribers-only",
        ModerationAction.EmoteOnly => "Emote-only",
        ModerationAction.Clear => "Clear chat",
        ModerationAction.ShieldMode => "Shield Mode",
        ModerationAction.ApproveHeld => "Allow held message",
        ModerationAction.DenyHeld => "Deny held message",
        ModerationAction.MonitorUser => "Monitor",
        ModerationAction.RestrictUser => "Restrict",
        ModerationAction.ClearSuspicious => "Not suspicious",
        ModerationAction.ApproveUnbanRequest => "Approve unban request",
        ModerationAction.DenyUnbanRequest => "Deny unban request",
        ModerationAction.AddBlockedTerm => "Block term",
        ModerationAction.RemoveBlockedTerm => "Unblock term",
        _ => action.ToString(),
    };

    /// <summary>Actions about one person (they need a target user).</summary>
    public static bool TargetsUser(ModerationAction action) =>
        action is ModerationAction.Timeout or ModerationAction.Ban or ModerationAction.Unban or ModerationAction.Warn
            or ModerationAction.MonitorUser or ModerationAction.RestrictUser or ModerationAction.ClearSuspicious;
}

/// <summary>One request to a platform moderator.</summary>
public sealed record ModerationRequest(ModerationAction Action)
{
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string MessageId { get; init; } = "";

    /// <summary>The chat the message came from, where a platform has several (the YouTube live chat id).</summary>
    public string? ChannelId { get; init; }

    public TimeSpan? Duration { get; init; }
    public string? Reason { get; init; }

    /// <summary>Chat modes and pins: turn on (true) or off (false).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Chat modes: slow mode seconds between messages, or followers-only minutes a viewer must have followed. Null uses
    /// the platform's default.
    /// </summary>
    public int? ModeValue { get; init; }

    /// <summary>What the platform returned when the action being undone was made (the YouTube ban id, needed to unban).</summary>
    public string? PlatformRef { get; init; }

    /// <summary>The queue item acted on: an unban request id, a blocked term id.</summary>
    public string ItemId { get; init; } = "";

    /// <summary>The blocked term to add (or, for the log, the one removed).</summary>
    public string Term { get; init; } = "";
}

/// <summary>What a platform call did.</summary>
public sealed record ModerationOutcome(bool Ok, string? Error = null)
{
    /// <summary>The duration that was really applied (Kick rounds up to whole minutes).</summary>
    public TimeSpan? AppliedDuration { get; init; }

    /// <summary>The platform's id for the action (the YouTube ban id); stored in the mod log so it can be undone.</summary>
    public string? PlatformRef { get; init; }

    public static ModerationOutcome Done(TimeSpan? applied = null, string? platformRef = null) =>
        new(true) { AppliedDuration = applied, PlatformRef = platformRef };

    public static ModerationOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// A quota a platform charges per call (YouTube's daily units). <see cref="ModerationService"/> asks before every
/// action that costs some.
/// </summary>
public interface IPlatformQuota
{
    string Platform { get; }

    /// <summary>Units left today.</summary>
    int Remaining { get; }

    /// <summary>Units moderation must leave for everything else (reading chat).</summary>
    int Reserve { get; }

    /// <summary>When the count starts again.</summary>
    DateTimeOffset ResetsAt { get; }

    /// <summary>Null when <paramref name="units"/> may be spent on moderation; otherwise why not, in plain words.</summary>
    string? Refuse(int units);
}

/// <summary>
/// Accounts that only the streamer may act against: the bot accounts (from each platform module), portal members (from
/// the portal). Implementations must be quick; they are asked before every action on a person.
/// </summary>
public interface IProtectedAccounts
{
    /// <summary>Null when the account is not protected; otherwise why it is ("This is your bot account").</summary>
    string? WhyProtected(string platform, string userId, string userName);
}
