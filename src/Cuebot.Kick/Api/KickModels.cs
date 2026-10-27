namespace Cuebot.Kick.Api;

/// <summary>A user from GET /public/v1/users.</summary>
public sealed record KickUser(long UserId, string Name, string ProfilePicture);

public sealed record KickCategory(long Id, string Name, string Thumbnail);

/// <summary>A channel from GET /public/v1/channels, with its current stream.</summary>
public sealed record KickChannel(
    long BroadcasterUserId,
    string Slug,
    string Title,
    KickCategory? Category,
    string Description,
    bool IsLive,
    int ViewerCount,
    DateTimeOffset? StartTime,
    string Language,
    bool IsMature,
    IReadOnlyList<string> Tags,
    int ActiveSubscribers);

/// <summary>Result of POST /public/v1/chat.</summary>
public sealed record KickChatSendResult(string MessageId, bool IsSent);

/// <summary>An event subscription (webhook) from /public/v1/events/subscriptions.</summary>
public sealed record KickEventSubscription(string Id, string Event, int Version, string Method);

/// <summary>Internal ids of a channel needed by the real-time connection, from kick.com's website API.</summary>
public sealed record KickChannelIds(long ChannelId, long ChatroomId, long UserId, string Slug);

/// <summary>A channel reward from GET /public/v1/channels/rewards.</summary>
public sealed record KickReward(
    string Id,
    string Title,
    string Description,
    int Cost,
    string BackgroundColor,
    bool IsEnabled,
    bool IsPaused,
    bool IsUserInputRequired,
    bool SkipsRequestQueue);

/// <summary>Fields to create or change on a reward; null leaves a field as it is (or at Kick's default when creating).</summary>
public sealed record KickRewardUpdate
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int? Cost { get; init; }
    public string? BackgroundColor { get; init; }
    public bool? IsEnabled { get; init; }

    /// <summary>Only on update (Kick creates rewards unpaused).</summary>
    public bool? IsPaused { get; init; }

    public bool? IsUserInputRequired { get; init; }
    public bool? SkipsRequestQueue { get; init; }
}

/// <summary>A redemption from GET /public/v1/channels/rewards/redemptions.</summary>
public sealed record KickRedemption(string Id, string RewardId, string RewardTitle, long RedeemerUserId, string Status, string UserInput,
    DateTimeOffset? RedeemedAt);

/// <summary>A redemption Kick could not accept or reject (reason UNKNOWN, NOT_PENDING, NOT_FOUND or NOT_OWNED).</summary>
public sealed record KickRedemptionFailure(string Id, string Reason);
