using System.Text.Json;

namespace Cuebot.YouTube.Api;

/// <summary>A live broadcast (liveBroadcasts resource) plus, while monitored, its latest video statistics.</summary>
public sealed record YouTubeBroadcast(
    string Id,
    string Title,
    string Description,
    string LiveChatId,
    string LifeCycleStatus,
    string PrivacyStatus,
    string ChannelId,
    string ThumbnailUrl,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd)
{
    public string Url => $"https://www.youtube.com/watch?v={Id}";

    internal static YouTubeBroadcast Parse(JsonElement item)
    {
        var snippet = item.Prop("snippet");
        var status = item.Prop("status");
        return new YouTubeBroadcast(item.Str("id"), snippet.Str("title"), snippet.Str("description"), snippet.Str("liveChatId"),
            status.Str("lifeCycleStatus"), status.Str("privacyStatus"), snippet.Str("channelId"), snippet.Thumbnail(),
            snippet.Time("scheduledStartTime"), snippet.Time("actualStartTime"), snippet.Time("actualEndTime"));
    }
}

/// <summary>Viewer and engagement numbers of a live video (videos.list statistics + liveStreamingDetails).</summary>
public sealed record BroadcastStatistics(long? ConcurrentViewers, long ViewCount, long LikeCount, long CommentCount);

/// <summary>A channel (channels resource).</summary>
public sealed record YouTubeChannel(
    string Id,
    string Title,
    string Description,
    string CustomUrl,
    string ThumbnailUrl,
    long SubscriberCount,
    bool HiddenSubscriberCount,
    long VideoCount,
    long ViewCount,
    DateTimeOffset? PublishedAt)
{
    public string Url => CustomUrl.Length > 0 ? $"https://www.youtube.com/{CustomUrl}" : $"https://www.youtube.com/channel/{Id}";

    internal static YouTubeChannel Parse(JsonElement item)
    {
        var snippet = item.Prop("snippet");
        var stats = item.Prop("statistics");
        return new YouTubeChannel(item.Str("id"), snippet.Str("title"), snippet.Str("description"), snippet.Str("customUrl"),
            snippet.Thumbnail(), stats.Long("subscriberCount"), stats.Bool("hiddenSubscriberCount"), stats.Long("videoCount"),
            stats.Long("viewCount"), snippet.Time("publishedAt"));
    }
}

/// <summary>A video (videos resource), including live details for broadcasts.</summary>
public sealed record YouTubeVideo(
    string Id,
    string Title,
    string Description,
    string ChannelId,
    string ChannelTitle,
    string CategoryId,
    IReadOnlyList<string> Tags,
    string PrivacyStatus,
    string LiveBroadcastContent,
    string ActiveLiveChatId,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    BroadcastStatistics Statistics,
    string ThumbnailUrl)
{
    public string Url => $"https://www.youtube.com/watch?v={Id}";

    internal static YouTubeVideo Parse(JsonElement item)
    {
        var snippet = item.Prop("snippet");
        var live = item.Prop("liveStreamingDetails");
        var stats = item.Prop("statistics");
        return new YouTubeVideo(item.Str("id"), snippet.Str("title"), snippet.Str("description"), snippet.Str("channelId"),
            snippet.Str("channelTitle"), snippet.Str("categoryId"), snippet.Strings("tags"), item.At("status").Str("privacyStatus"),
            snippet.Str("liveBroadcastContent"), live.Str("activeLiveChatId"), live.Time("scheduledStartTime"),
            live.Time("actualStartTime"), live.Time("actualEndTime"),
            new BroadcastStatistics(live.NullableLong("concurrentViewers"), stats.Long("viewCount"), stats.Long("likeCount"),
                stats.Long("commentCount")),
            snippet.Thumbnail());
    }
}

/// <summary>One page of liveChatMessages.list.</summary>
internal sealed record ChatPage(IReadOnlyList<JsonElement> Items, string NextPageToken, TimeSpan PollingInterval, DateTimeOffset? OfflineAt)
{
    internal static ChatPage Parse(JsonElement body) =>
        new(body.Items().Select(i => i.Clone()).ToArray(), body.Str("nextPageToken"),
            TimeSpan.FromMilliseconds(Math.Max(0, body.Long("pollingIntervalMillis"))), body.Time("offlineAt"));
}

/// <summary>A recent subscriber (subscriptions.list myRecentSubscribers).</summary>
public sealed record YouTubeSubscriber(string SubscriptionId, string ChannelId, string Title, string ThumbnailUrl, DateTimeOffset? SubscribedAt);
