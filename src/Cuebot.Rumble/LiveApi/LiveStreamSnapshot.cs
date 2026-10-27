namespace Cuebot.Rumble.LiveApi;

/// <summary>
/// One response of Rumble's Live Stream API: the account or channel, its recent followers, subscribers and gifted
/// subscriptions, and its live streams with recent chat. The stream key Rumble includes is deliberately not kept.
/// </summary>
internal sealed record LiveStreamSnapshot(
    DateTimeOffset ServerTime,
    string UserName,
    string? ChannelName,
    int FollowerCount,
    int SubscriberCount,
    IReadOnlyList<ApiFollower> RecentFollowers,
    IReadOnlyList<ApiSubscriber> RecentSubscribers,
    IReadOnlyList<ApiGiftedSub> RecentGiftedSubs,
    IReadOnlyList<ApiLivestream> Livestreams)
{
    /// <summary>Name shown for the channel: the channel's name when the key belongs to a channel, else the user's.</summary>
    public string DisplayName => ChannelName is { Length: > 0 } channel ? channel : UserName;

    /// <summary>The stream that is live right now, if any (the first one when Rumble lists several).</summary>
    public ApiLivestream? LiveStream => Livestreams.FirstOrDefault(s => s.IsLive);
}

internal sealed record ApiFollower(string UserName, DateTimeOffset FollowedOn);

internal sealed record ApiSubscriber(string UserName, long AmountCents, DateTimeOffset SubscribedOn);

/// <summary>A gift of one or more subscriptions. Rumble gives these no id or timestamp.</summary>
internal sealed record ApiGiftedSub(string PurchasedBy, int TotalGifts, int RemainingGifts, string GiftType, long VideoId);

/// <summary>A live stream. <see cref="Id"/> is base 36, as on rumble.com; the chat uses the same number in base 10.</summary>
internal sealed record ApiLivestream(
    string Id,
    string Title,
    bool IsLive,
    DateTimeOffset CreatedOn,
    int WatchingNow,
    IReadOnlyList<ApiChatMessage> RecentMessages,
    IReadOnlyList<ApiRant> RecentRants);

/// <summary>A chat message as the Live Stream API reports it: no message or user id.</summary>
internal sealed record ApiChatMessage(string UserName, string Text, DateTimeOffset CreatedOn, IReadOnlyList<string> Badges);

internal sealed record ApiRant(string UserName, string Text, DateTimeOffset CreatedOn, long AmountCents, IReadOnlyList<string> Badges);
