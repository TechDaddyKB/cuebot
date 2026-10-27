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
