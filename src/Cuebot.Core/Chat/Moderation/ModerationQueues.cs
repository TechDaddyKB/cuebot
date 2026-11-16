using Cuebot.Core.Commands;

namespace Cuebot.Core.Chat.Moderation;

/// <summary>
/// A platform's moderation queues and channel safety state (docs/UNIFIED_MOD_CHAT_PLAN.md, phase 3): messages its
/// filter held for review, Shield Mode, suspicious chatters, unban requests and blocked terms. Implemented by the
/// platform's <see cref="IChatModerator"/> (Twitch); <see cref="ModerationService.QueuesFor"/> finds it. Reading goes
/// through this interface; every change goes through <see cref="ModerationService.ExecuteAsync"/> with the queue tools
/// (<see cref="ModerationCapability.IsQueueTool"/>), so it is permission-checked and logged.
/// </summary>
public interface IModerationQueues
{
    /// <summary>Same name as the moderator's <see cref="IChatModerator.Platform"/>.</summary>
    string Platform { get; }

    /// <summary>Held messages waiting for a decision, oldest first, plus recently decided ones (<see cref="HeldChatMessage.Status"/>).</summary>
    IReadOnlyList<HeldChatMessage> HeldMessages { get; }

    /// <summary>Shield Mode as last seen (an event or <see cref="RefreshAsync"/>); null when not known yet.</summary>
    ShieldModeState? ShieldMode { get; }

    /// <summary>A chatter's suspicious status as seen in platform events this session; null when not flagged.</summary>
    SuspiciousUserInfo? SuspiciousUser(string userId);

    /// <summary>Re-reads what the platform can tell (Shield Mode); errors are swallowed and leave the old state.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Unban requests waiting for an answer (the platform's list; throws on platform errors).</summary>
    Task<IReadOnlyList<UnbanRequestInfo>> GetUnbanRequestsAsync(CancellationToken ct = default);

    /// <summary>The channel's blocked terms (the platform's list; throws on platform errors).</summary>
    Task<IReadOnlyList<BlockedTermInfo>> GetBlockedTermsAsync(CancellationToken ct = default);

    /// <summary>Raised (on a background thread) when a queue or state changed: a message held or decided, Shield Mode...</summary>
    event Action<ModerationQueueChange>? Changed;
}

/// <summary>What changed in <see cref="IModerationQueues"/>.</summary>
public enum ModerationQueueKind
{
    HeldMessages,
    ShieldMode,
    SuspiciousUsers,
    UnbanRequests,
    BlockedTerms,
}

/// <summary>One change: the queue, and the item when there is one (held message id, request id, user id).</summary>
public sealed record ModerationQueueChange(string Platform, ModerationQueueKind Kind, string ItemId = "");

/// <summary>Where a held message stands.</summary>
public enum HeldMessageStatus
{
    Pending,
    Approved,
    Denied,

    /// <summary>Nobody decided in time; the platform dropped it.</summary>
    Expired,
}

/// <summary>A chat message the platform's filter (Twitch AutoMod, a blocked term) held for a moderator to allow or deny.</summary>
public sealed record HeldChatMessage(string Platform, string Id, DateTimeOffset HeldAt)
{
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";

    /// <summary>The login name when it differs from <see cref="UserName"/>.</summary>
    public string UserLogin { get; init; } = "";
    public string Text { get; init; } = "";

    /// <summary>Why it was held, in plain words ("AutoMod: aggression, level 3", "Blocked term").</summary>
    public string Reason { get; init; } = "";

    public HeldMessageStatus Status { get; init; } = HeldMessageStatus.Pending;

    /// <summary>The moderator who decided, when the platform says.</summary>
    public string DecidedBy { get; init; } = "";

    public bool IsPending => Status == HeldMessageStatus.Pending;
}

/// <summary>Shield Mode: on or off, who changed it last and since when.</summary>
public sealed record ShieldModeState(bool IsActive)
{
    public string ModeratorName { get; init; } = "";
    public DateTimeOffset? Since { get; init; }
}

/// <summary>How the platform treats a suspicious chatter.</summary>
public enum SuspiciousLevel
{
    None,

    /// <summary>Their messages appear, flagged for moderators.</summary>
    Monitored,

    /// <summary>Only moderators see their messages.</summary>
    Restricted,
}

/// <summary>A chatter the platform or a moderator flagged.</summary>
public sealed record SuspiciousUserInfo(string Platform, string UserId, SuspiciousLevel Level)
{
    public string UserName { get; init; } = "";

    /// <summary>Why, in plain words ("possible ban evader", "banned in 2 shared channels"), when the platform says.</summary>
    public string Why { get; init; } = "";

    public string ModeratorName { get; init; } = "";
}

/// <summary>Where an unban request stands.</summary>
public enum UnbanRequestStatus
{
    Pending,
    Approved,
    Denied,

    /// <summary>Withdrawn by the viewer, or closed because the ban was lifted another way.</summary>
    Closed,
}

/// <summary>A banned viewer asking to be unbanned.</summary>
public sealed record UnbanRequestInfo(string Platform, string Id, string UserId, DateTimeOffset CreatedAt)
{
    public string UserName { get; init; } = "";
    public string UserLogin { get; init; } = "";

    /// <summary>What the viewer wrote.</summary>
    public string Text { get; init; } = "";

    public UnbanRequestStatus Status { get; init; } = UnbanRequestStatus.Pending;
    public string ResolvedBy { get; init; } = "";
    public string ResolutionText { get; init; } = "";
}

/// <summary>A word or phrase the platform keeps out of chat.</summary>
public sealed record BlockedTermInfo(string Platform, string Id, string Text);

/// <summary>
/// A moderation action a platform reported in full (Twitch <c>channel.moderate</c>): published as
/// <see cref="ChatHub.ModerationActionEvent"/> and recorded by <see cref="ModerationService"/> in the mod log as
/// <see cref="ModActorKind.External"/> unless it is Cuebot's own action echoed back.
/// </summary>
public sealed record ExternalModerationNotice(string Platform, string Action, DateTimeOffset Time)
{
    /// <summary>The moderator's display name; empty when the platform does not say.</summary>
    public string Moderator { get; init; } = "";
    public string ModeratorId { get; init; } = "";
    public string TargetUserId { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string MessageText { get; init; } = "";
    public int? DurationSeconds { get; init; }

    /// <summary>The reason, or for chat modes and terms what changed ("Slow mode on (30)", the term).</summary>
    public string Reason { get; init; } = "";

    /// <summary>The channel the action happened in when it is not the streamer's own (another channel of a shared chat).</summary>
    public string? ChannelId { get; init; }

    internal static ExternalModerationNotice? From(Events.BotEvent e)
    {
        var a = e.Args;
        var platform = a.GetString("platform");
        var action = a.GetString("action");
        if (platform.Length == 0 || action.Length == 0) return null;
        var duration = int.TryParse(a.GetString("duration"), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : (int?)null;
        return new ExternalModerationNotice(platform, action, e.Time)
        {
            Moderator = a.GetString("moderator"),
            ModeratorId = a.GetString("moderatorUserId"),
            TargetUserId = a.GetString("userId"),
            TargetName = a.GetString("user") is { Length: > 0 } user ? user : a.GetString("userName"),
            MessageId = a.GetString("messageId"),
            MessageText = a.GetString("message"),
            DurationSeconds = duration,
            Reason = a.GetString("reason"),
            ChannelId = a.GetString("channelId") is { Length: > 0 } channel ? channel : null,
        };
    }
}
