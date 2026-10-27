using System.Text.Json;
using Cuebot.YouTube.Auth;

namespace Cuebot.YouTube.Api;

/// <summary>What to change on a video; null members stay as they are.</summary>
public sealed record VideoChange(
    string? Title = null,
    string? Description = null,
    string? CategoryId = null,
    IReadOnlyList<string>? Tags = null,
    string? PrivacyStatus = null);

/// <summary>Typed YouTube Data API v3 calls used by the module (each documents its quota cost in <see cref="QuotaCosts"/>).</summary>
internal sealed class YouTubeApi(YouTubeApiClient client)
{
    private const int MaxChatResults = 2000;

    public QuotaTracker Quota => client.Quota;

    public async Task<YouTubeChannel?> GetMyChannelAsync(AccountTokens account, CancellationToken ct)
    {
        var body = await Get(account, "channels", [Q("part", "snippet,statistics"), Q("mine", "true")], QuotaCosts.ChannelsList, ct)
            .ConfigureAwait(false);
        return body.Items().Select(YouTubeChannel.Parse).FirstOrDefault();
    }

    public async Task<YouTubeChannel?> GetChannelAsync(AccountTokens account, string channelId, CancellationToken ct)
    {
        var body = await Get(account, "channels", [Q("part", "snippet,statistics"), Q("id", channelId)], QuotaCosts.ChannelsList, ct)
            .ConfigureAwait(false);
        return body.Items().Select(YouTubeChannel.Parse).FirstOrDefault();
    }

    /// <summary>Looks a channel up by its @handle.</summary>
    public async Task<YouTubeChannel?> GetChannelByHandleAsync(AccountTokens account, string handle, CancellationToken ct)
    {
        var body = await Get(account, "channels", [Q("part", "snippet,statistics"), Q("forHandle", handle.StartsWith('@') ? handle : "@" + handle)],
            QuotaCosts.ChannelsList, ct).ConfigureAwait(false);
        return body.Items().Select(YouTubeChannel.Parse).FirstOrDefault();
    }

    /// <summary>The signed-in channel's broadcasts with a status of "active" or "upcoming".</summary>
    public async Task<IReadOnlyList<YouTubeBroadcast>> ListBroadcastsAsync(AccountTokens account, string status, CancellationToken ct)
    {
        var body = await Get(account, "liveBroadcasts",
            [Q("part", "id,snippet,status"), Q("broadcastStatus", status), Q("broadcastType", "all"), Q("maxResults", "50")],
            QuotaCosts.LiveBroadcastsList, ct).ConfigureAwait(false);
        return body.Items().Select(YouTubeBroadcast.Parse).ToArray();
    }

    public async Task<IReadOnlyList<YouTubeVideo>> GetVideosAsync(AccountTokens account, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var body = await Get(account, "videos",
            [Q("part", "snippet,status,statistics,liveStreamingDetails"), Q("id", string.Join(',', ids))], QuotaCosts.VideosList, ct)
            .ConfigureAwait(false);
        return body.Items().Select(YouTubeVideo.Parse).ToArray();
    }

    public async Task<ChatPage> ListChatMessagesAsync(AccountTokens account, string liveChatId, string? pageToken, CancellationToken ct)
    {
        var body = await Get(account, "liveChat/messages",
            [Q("liveChatId", liveChatId), Q("part", "id,snippet,authorDetails"), Q("maxResults", MaxChatResults.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             Q("pageToken", string.IsNullOrEmpty(pageToken) ? null : pageToken)],
            QuotaCosts.LiveChatMessagesList, ct).ConfigureAwait(false);
        return ChatPage.Parse(body);
    }

    /// <summary>Posts a text message; returns the new message id.</summary>
    public async Task<string> InsertChatMessageAsync(AccountTokens account, string liveChatId, string text, CancellationToken ct)
    {
        var body = await client.SendAsync(account, HttpMethod.Post, "liveChat/messages", [Q("part", "snippet")], new
        {
            snippet = new { liveChatId, type = "textMessageEvent", textMessageDetails = new { messageText = text } },
        }, QuotaCosts.LiveChatMessagesInsert, ct).ConfigureAwait(false);
        return body.Str("id");
    }

    public Task DeleteChatMessageAsync(AccountTokens account, string messageId, CancellationToken ct) =>
        client.SendAsync(account, HttpMethod.Delete, "liveChat/messages", [Q("id", messageId)], null, QuotaCosts.LiveChatMessagesDelete, ct);

    /// <summary>Bans a channel from the chat, permanently or (with a duration) temporarily; returns the ban id needed to lift it.</summary>
    public async Task<string> BanAsync(AccountTokens account, string liveChatId, string channelId, int? durationSeconds, CancellationToken ct)
    {
        object snippet = durationSeconds is { } seconds
            ? new { liveChatId, type = "temporary", banDurationSeconds = seconds, bannedUserDetails = new { channelId } }
            : new { liveChatId, type = "permanent", bannedUserDetails = new { channelId } };
        var body = await client.SendAsync(account, HttpMethod.Post, "liveChat/bans", [Q("part", "snippet")], new { snippet },
            QuotaCosts.LiveChatBansInsert, ct).ConfigureAwait(false);
        return body.Str("id");
    }

    public Task UnbanAsync(AccountTokens account, string banId, CancellationToken ct) =>
        client.SendAsync(account, HttpMethod.Delete, "liveChat/bans", [Q("id", banId)], null, QuotaCosts.LiveChatBansDelete, ct);

    public async Task<string> AddModeratorAsync(AccountTokens account, string liveChatId, string channelId, CancellationToken ct)
    {
        var body = await client.SendAsync(account, HttpMethod.Post, "liveChat/moderators", [Q("part", "snippet")], new
        {
            snippet = new { liveChatId, moderatorDetails = new { channelId } },
        }, QuotaCosts.LiveChatModeratorsInsert, ct).ConfigureAwait(false);
        return body.Str("id");
    }

    /// <summary>Moderator resource ids by moderator channel id.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ListModeratorsAsync(AccountTokens account, string liveChatId, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var body = await Get(account, "liveChat/moderators",
                [Q("liveChatId", liveChatId), Q("part", "snippet"), Q("maxResults", "50"), Q("pageToken", page)],
                QuotaCosts.LiveChatModeratorsList, ct).ConfigureAwait(false);
            foreach (var item in body.Items())
                result[item.At("snippet", "moderatorDetails").Str("channelId")] = item.Str("id");
            page = body.Str("nextPageToken") is { Length: > 0 } next ? next : null;
        } while (page is not null);
        return result;
    }

    public Task RemoveModeratorAsync(AccountTokens account, string moderatorId, CancellationToken ct) =>
        client.SendAsync(account, HttpMethod.Delete, "liveChat/moderators", [Q("id", moderatorId)], null, QuotaCosts.LiveChatModeratorsDelete, ct);

    /// <summary>
    /// Starts a chat poll; returns the poll's message id. YouTube allows 2–4 options.
    /// </summary>
    public async Task<string> CreatePollAsync(AccountTokens account, string liveChatId, string question, IReadOnlyList<string> options,
        CancellationToken ct)
    {
        var body = await client.SendAsync(account, HttpMethod.Post, "liveChat/messages", [Q("part", "snippet")], new
        {
            snippet = new
            {
                liveChatId,
                type = "pollEvent",
                pollDetails = new { metadata = new { questionText = question, options = options.Select(o => new { optionText = o }).ToArray() } },
            },
        }, QuotaCosts.LiveChatMessagesInsert, ct).ConfigureAwait(false);
        return body.Str("id");
    }

    /// <summary>Closes a poll (liveChatMessages.transition to "closed").</summary>
    public Task EndPollAsync(AccountTokens account, string pollMessageId, CancellationToken ct) =>
        client.SendAsync(account, HttpMethod.Post, "liveChat/messages/transition",
            [Q("id", pollMessageId), Q("status", "closed"), Q("part", "snippet")], null, QuotaCosts.LiveChatMessagesTransition, ct);

    public async Task<IReadOnlyList<YouTubeSubscriber>> ListRecentSubscribersAsync(AccountTokens account, CancellationToken ct)
    {
        var body = await Get(account, "subscriptions",
            [Q("part", "snippet,subscriberSnippet"), Q("myRecentSubscribers", "true"), Q("maxResults", "50")],
            QuotaCosts.SubscriptionsList, ct).ConfigureAwait(false);
        return body.Items().Select(i =>
        {
            var sub = i.Prop("subscriberSnippet");
            return new YouTubeSubscriber(i.Str("id"), sub.Str("channelId"), sub.Str("title"), sub.Thumbnail(), i.Prop("snippet").Time("publishedAt"));
        }).ToArray();
    }

    /// <summary>
    /// Updates a video's title, description, category, tags and/or privacy. YouTube replaces the whole snippet/status
    /// part, so the current values are read first (1 unit) and sent back with the changes (50 units).
    /// </summary>
    public async Task<YouTubeVideo> UpdateVideoAsync(AccountTokens account, string videoId, VideoChange change, CancellationToken ct)
    {
        var current = await Get(account, "videos", [Q("part", "snippet,status"), Q("id", videoId)], QuotaCosts.VideosList, ct)
            .ConfigureAwait(false);
        var item = current.Items().FirstOrDefault();
        if (!item.Exists()) throw new YouTubeApiException($"YouTube video {videoId} was not found.", 404, "videoNotFound");

        var parts = new List<string> { "snippet" };
        var body = new Dictionary<string, object?> { ["id"] = videoId, ["snippet"] = BuildSnippet(item.Prop("snippet"), change) };
        if (change.PrivacyStatus is { } privacy)
        {
            parts.Add("status");
            body["status"] = BuildStatus(item.Prop("status"), privacy);
        }
        var updated = await client.SendAsync(account, HttpMethod.Put, "videos", [Q("part", string.Join(',', parts))], body,
            QuotaCosts.VideosUpdate, ct).ConfigureAwait(false);
        return YouTubeVideo.Parse(updated);
    }

    internal static Dictionary<string, object?> BuildSnippet(JsonElement snippet, VideoChange change)
    {
        var result = new Dictionary<string, object?>
        {
            ["title"] = change.Title ?? snippet.Str("title"),
            ["description"] = change.Description ?? snippet.Str("description"),
            ["categoryId"] = change.CategoryId ?? snippet.Str("categoryId"),
            ["tags"] = change.Tags ?? snippet.Strings("tags"),
        };
        foreach (var key in (string[])["defaultLanguage", "defaultAudioLanguage"])
            if (snippet.Str(key) is { Length: > 0 } value) result[key] = value;
        return result;
    }

    internal static Dictionary<string, object?> BuildStatus(JsonElement status, string privacy)
    {
        var result = new Dictionary<string, object?> { ["privacyStatus"] = privacy };
        foreach (var key in (string[])["license", "publishAt"])
            if (status.Str(key) is { Length: > 0 } value) result[key] = value;
        foreach (var key in (string[])["embeddable", "publicStatsViewable", "selfDeclaredMadeForKids"])
            if (status.Prop(key).ValueKind is JsonValueKind.True or JsonValueKind.False) result[key] = status.Bool(key);
        return result;
    }

    private Task<JsonElement> Get(AccountTokens account, string resource, IEnumerable<KeyValuePair<string, string?>> query, int cost,
        CancellationToken ct) =>
        client.SendAsync(account, HttpMethod.Get, resource, query, null, cost, ct);

    private static KeyValuePair<string, string?> Q(string key, string? value) => new(key, value);
}
