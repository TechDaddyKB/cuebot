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
    int ActiveSubscribers)
{
    /// <summary>Active gifted subscriptions (part of <see cref="ActiveSubscribers"/> since Kick's 2026-08-11 change).</summary>
    public int ActiveGiftedSubscribers { get; init; }

    /// <summary>Subscriptions that were cancelled but have not run out yet.</summary>
    public int CanceledSubscribers { get; init; }

    public string BannerPicture { get; init; } = "";
}

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

/// <summary>A live stream from GET /public/v1/users/livestreams or /public/v2/livestreams.</summary>
public sealed record KickLivestream(
    long BroadcasterUserId,
    string UserName,
    string Slug,
    string ProfilePicture,
    string Title,
    KickCategory? Category,
    int ViewerCount,
    DateTimeOffset? StartedAt,
    string Language,
    bool IsMature,
    IReadOnlyList<string> Tags,
    string Thumbnail);

/// <summary>One place on the KICKs leaderboard.</summary>
public sealed record KickLeaderboardEntry(int Rank, long UserId, string UserName, long GiftedAmount);

/// <summary>GET /public/v1/kicks/leaderboard: the top gifters this week, this month and of all time.</summary>
public sealed record KickLeaderboard(IReadOnlyList<KickLeaderboardEntry> Week, IReadOnlyList<KickLeaderboardEntry> Month,
    IReadOnlyList<KickLeaderboardEntry> Lifetime);

/// <summary>One ad break from the ad status.</summary>
public sealed record KickAdBreakItem(string Id, string AdBreakId, int DurationSeconds, string Status, string Source, DateTimeOffset? CreatedAt);

/// <summary>GET /public/v1/ads/ad-break-status.</summary>
public sealed record KickAdStatus(bool OptedIn, bool AdsBlocked, bool AutoAdsActive, int RemainingAdBreaks, int MaxAdBreaks, int LimitPeriodSeconds,
    IReadOnlyList<KickAdBreakItem> AdBreaks);

/// <summary>Result of POST /public/v1/ads/ad-break.</summary>
public sealed record KickAdBreak(string Id, string AdBreakId, int RemainingAdBreaks);

/// <summary>
/// The broadcaster's ingest address and stream key (<c>streamkey:read</c>). A class, not a record, so the key never
/// shows up in ToString() output or logs.
/// </summary>
public sealed class KickStreamCredentials(string url, string key)
{
    public string Url { get; } = url;
    public string Key { get; } = key;
    public override string ToString() => $"Kick ingest {Url} (key hidden)";
}
