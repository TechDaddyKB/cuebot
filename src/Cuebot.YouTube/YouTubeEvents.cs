using Cuebot.Core.Events;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Monitoring;

namespace Cuebot.YouTube;

/// <summary>
/// Builds the module's non-chat <see cref="BotEvent"/>s (chat items are mapped by <c>ChatItemMapper</c>).
/// Argument names follow the other platform modules (<c>user</c>, <c>userName</c>, <c>userId</c>...).
/// </summary>
internal static class YouTubeEvents
{
    public static BotEvent Broadcast(BroadcastChange change, YouTubeAccount broadcaster)
    {
        var type = change.Kind switch
        {
            BroadcastChangeKind.Added => YouTubeEventTypes.BroadcastAdded,
            BroadcastChangeKind.Removed => YouTubeEventTypes.BroadcastRemoved,
            BroadcastChangeKind.MonitoringStarted => YouTubeEventTypes.BroadcastMonitoringStarted,
            BroadcastChangeKind.Started => YouTubeEventTypes.BroadcastStarted,
            BroadcastChangeKind.Updated => YouTubeEventTypes.BroadcastUpdated,
            BroadcastChangeKind.Ended => YouTubeEventTypes.BroadcastEnded,
            _ => YouTubeEventTypes.BroadcastMonitoringEnded,
        };
        var args = BroadcastArgs(change.Broadcast, broadcaster);
        if (change.Previous is { } old)
        {
            args["previousTitle"] = old.Title;
            args["previousDescription"] = old.Description;
            args["previousPrivacy"] = old.PrivacyStatus;
        }
        return new BotEvent(type, args);
    }

    public static Dictionary<string, object?> BroadcastArgs(YouTubeBroadcast b, YouTubeAccount broadcaster) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["platform"] = ChatAdapterPlatform,
        ["broadcastId"] = b.Id,
        ["broadcastTitle"] = b.Title,
        ["broadcastDescription"] = b.Description,
        ["broadcastPrivacy"] = b.PrivacyStatus,
        ["broadcastStatus"] = b.LifeCycleStatus,
        ["broadcastUrl"] = b.Url,
        ["broadcastThumbnail"] = b.ThumbnailUrl,
        ["broadcastScheduledStart"] = b.ScheduledStart?.ToString("O") ?? "",
        ["broadcastActualStart"] = b.ActualStart?.ToString("O") ?? "",
        ["broadcastActualEnd"] = b.ActualEnd?.ToString("O") ?? "",
        ["liveChatId"] = b.LiveChatId,
        ["title"] = b.Title,
        ["user"] = broadcaster.ChannelTitle,
        ["userName"] = broadcaster.ChannelTitle,
        ["userId"] = b.ChannelId.Length > 0 ? b.ChannelId : broadcaster.ChannelId,
    };

    public static BotEvent Statistics(YouTubeBroadcast b, BroadcastStatistics s, YouTubeAccount broadcaster)
    {
        var args = BroadcastArgs(b, broadcaster);
        args["concurrentViewers"] = s.ConcurrentViewers ?? 0;
        args["viewerCount"] = s.ConcurrentViewers ?? 0;
        args["viewCount"] = s.ViewCount;
        args["likeCount"] = s.LikeCount;
        args["commentCount"] = s.CommentCount;
        return new BotEvent(YouTubeEventTypes.BroadcastStatisticsUpdated, args);
    }

    public static BotEvent NewSubscriber(YouTubeSubscriber s) =>
        new(YouTubeEventTypes.NewSubscriber, new Dictionary<string, object?>
        {
            ["platform"] = ChatAdapterPlatform,
            ["user"] = s.Title,
            ["userName"] = s.Title,
            ["userId"] = s.ChannelId,
            ["userProfileUrl"] = s.ThumbnailUrl,
            ["channelUrl"] = $"https://www.youtube.com/channel/{s.ChannelId}",
            ["subscribedAt"] = s.SubscribedAt?.ToString("O") ?? "",
        });

    public static BotEvent QuotaExceeded(DateTimeOffset resumesAt, int usedToday) =>
        new(YouTubeEventTypes.QuotaExceeded, new Dictionary<string, object?>
        {
            ["platform"] = ChatAdapterPlatform,
            ["resumesAt"] = resumesAt.ToString("O"),
            ["quotaUsed"] = usedToday,
        });

    /// <summary>Arguments describing an account (Add Broadcaster Information, getBroadcaster/getBot).</summary>
    public static Dictionary<string, object?> AccountArgs(string prefix, YouTubeAccount account) => new(StringComparer.OrdinalIgnoreCase)
    {
        [prefix + "User"] = account.ChannelTitle,
        [prefix + "UserName"] = account.ChannelTitle,
        [prefix + "UserId"] = account.ChannelId,
        [prefix + "CustomUrl"] = account.CustomUrl,
        [prefix + "ProfileImageUrl"] = account.ProfileImageUrl,
        [prefix + "IsSignedIn"] = account.IsSignedIn,
    };

    public static Dictionary<string, object?> ChannelArgs(string prefix, YouTubeChannel c) => new(StringComparer.OrdinalIgnoreCase)
    {
        [prefix + "Id"] = c.Id,
        [prefix + "Title"] = c.Title,
        [prefix + "Description"] = c.Description,
        [prefix + "CustomUrl"] = c.CustomUrl,
        [prefix + "Url"] = c.Url,
        [prefix + "ProfileImageUrl"] = c.ThumbnailUrl,
        [prefix + "SubscriberCount"] = c.SubscriberCount,
        [prefix + "SubscriberCountHidden"] = c.HiddenSubscriberCount,
        [prefix + "VideoCount"] = c.VideoCount,
        [prefix + "ViewCount"] = c.ViewCount,
        [prefix + "CreatedAt"] = c.PublishedAt?.ToString("O") ?? "",
    };

    public static Dictionary<string, object?> VideoArgs(string prefix, YouTubeVideo v) => new(StringComparer.OrdinalIgnoreCase)
    {
        [prefix + "Id"] = v.Id,
        [prefix + "Title"] = v.Title,
        [prefix + "Description"] = v.Description,
        [prefix + "Url"] = v.Url,
        [prefix + "ChannelId"] = v.ChannelId,
        [prefix + "ChannelTitle"] = v.ChannelTitle,
        [prefix + "CategoryId"] = v.CategoryId,
        [prefix + "Tags"] = string.Join(", ", v.Tags),
        [prefix + "Privacy"] = v.PrivacyStatus,
        [prefix + "LiveStatus"] = v.LiveBroadcastContent,
        [prefix + "Thumbnail"] = v.ThumbnailUrl,
        [prefix + "ConcurrentViewers"] = v.Statistics.ConcurrentViewers ?? 0,
        [prefix + "ViewCount"] = v.Statistics.ViewCount,
        [prefix + "LikeCount"] = v.Statistics.LikeCount,
        [prefix + "CommentCount"] = v.Statistics.CommentCount,
        [prefix + "ActualStart"] = v.ActualStart?.ToString("O") ?? "",
        [prefix + "ActualEnd"] = v.ActualEnd?.ToString("O") ?? "",
    };

    private const string ChatAdapterPlatform = YouTubeChatAdapter.PlatformName;
}
