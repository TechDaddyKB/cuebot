using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.YouTube.Api;

namespace Cuebot.YouTube;

/// <summary>YouTube functions available to user scripts (counterparts of Streamer.bot's YouTube C# methods).</summary>
internal sealed class YouTubeScriptFunctions(YouTubeService yt) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("youtube.sendMessage", "Send a chat message (useBot, fallback, broadcastId optional)", ["message", "useBot", "fallback", "broadcastId"],
            async (_, a, ct) =>
            {
                await yt.SendChatMessageAsync(a.GetString("message"), a.GetBool("useBot"), a.GetBool("fallback", true), Opt(a, "broadcastId"), ct)
                    .ConfigureAwait(false);
                return null;
            }),
        new("youtube.sendMessageToLatestMonitored", "Send a chat message to the latest monitored broadcast", ["message", "useBot", "fallback"],
            async (_, a, ct) =>
            {
                await yt.SendChatMessageAsync(a.GetString("message"), a.GetBool("useBot"), a.GetBool("fallback", true), null, ct).ConfigureAwait(false);
                return null;
            }),
        new("youtube.getBroadcaster", "The broadcaster account: {userName, userId, customUrl, profileImageUrl, isSignedIn}", [],
            (_, _, _) => Task.FromResult<object?>(Account(yt.Settings.Broadcaster))),
        new("youtube.getBot", "The bot account: {userName, userId, customUrl, profileImageUrl, isSignedIn}", [],
            (_, _, _) => Task.FromResult<object?>(Account(yt.Settings.Bot))),
        new("youtube.getLatestMonitoredBroadcast", "The latest monitored broadcast, or null", [],
            (_, _, _) => Task.FromResult<object?>(yt.LatestBroadcast is { } b ? Broadcast(b) : null)),
        new("youtube.getMonitoredBroadcasts", "All monitored broadcasts", [],
            (_, _, _) => Task.FromResult<object?>(yt.MonitoredBroadcasts.Select(Broadcast).ToArray())),
        new("youtube.setTitle", "Set a broadcast's title", ["title", "broadcastId"],
            (_, a, ct) => Update(new VideoChange(Title: a.GetString("title")), a, ct)),
        new("youtube.setDescription", "Set a broadcast's description", ["description", "broadcastId"],
            (_, a, ct) => Update(new VideoChange(Description: a.GetString("description")), a, ct)),
        new("youtube.setMetadata", "Set title and/or description", ["title", "description", "broadcastId"],
            (_, a, ct) => Update(new VideoChange(Title: Opt(a, "title"), Description: a.GetElement("description") is null ? null : a.GetString("description")), a, ct)),
        new("youtube.setCategory", "Set a broadcast's category id", ["categoryId", "broadcastId"],
            (_, a, ct) => Update(new VideoChange(CategoryId: a.GetString("categoryId")), a, ct)),
        new("youtube.setPrivacy", "Set privacy: public, unlisted or private", ["privacy", "broadcastId"],
            (_, a, ct) => Update(new VideoChange(PrivacyStatus: a.GetString("privacy").Trim().ToLowerInvariant()), a, ct)),
        new("youtube.addTags", "Add tags (array or comma-separated)", ["tags", "broadcastId"],
            (_, a, ct) => Tags(a, current => current.Concat(TagList(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ct)),
        new("youtube.removeTags", "Remove tags (array or comma-separated)", ["tags", "broadcastId"],
            (_, a, ct) => Tags(a, current => current.Where(t => !TagList(a).Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray(), ct)),
        new("youtube.clearTags", "Remove all tags", ["broadcastId"],
            (_, a, ct) => Tags(a, _ => [], ct)),
        new("youtube.banUser", "Ban a user (channel id, @handle or recent chatter name)", ["user", "broadcastId"],
            async (_, a, ct) => { await yt.BanUserAsync(a.GetString("user"), Opt(a, "broadcastId"), ct).ConfigureAwait(false); return null; }),
        new("youtube.timeoutUser", "Time a user out for duration seconds", ["user", "duration", "broadcastId"],
            async (_, a, ct) => { await yt.TimeoutUserAsync(a.GetString("user"), Seconds(a), Opt(a, "broadcastId"), ct).ConfigureAwait(false); return null; }),
        new("youtube.unbanUser", "Lift a ban Cuebot placed this session", ["user"],
            async (_, a, ct) => { await yt.UnbanUserAsync(a.GetString("user"), ct).ConfigureAwait(false); return null; }),
        new("youtube.deleteMessage", "Delete a chat message by id", ["messageId"],
            async (_, a, ct) => { await yt.DeleteMessageAsync(a.GetString("messageId"), ct).ConfigureAwait(false); return null; }),
        new("youtube.addModerator", "Make a user a chat moderator", ["user", "broadcastId"],
            async (_, a, ct) => { await yt.AddModeratorAsync(a.GetString("user"), Opt(a, "broadcastId"), ct).ConfigureAwait(false); return null; }),
        new("youtube.removeModerator", "Remove a chat moderator", ["user", "broadcastId"],
            async (_, a, ct) => { await yt.RemoveModeratorAsync(a.GetString("user"), Opt(a, "broadcastId"), ct).ConfigureAwait(false); return null; }),
        new("youtube.createPoll", "Start a poll; returns its message id", ["question", "options", "broadcastId"],
            async (_, a, ct) => await yt.CreatePollAsync(a.GetString("question"), List(a, "options"), Opt(a, "broadcastId"), ct).ConfigureAwait(false)),
        new("youtube.endPoll", "End a poll (the active one by default)", ["pollMessageId"],
            async (_, a, ct) => { await yt.EndPollAsync(Opt(a, "pollMessageId"), ct).ConfigureAwait(false); return null; }),
        new("youtube.getVideoInfo", "Video details (latest broadcast by default)", ["videoId"],
            async (_, a, ct) => YouTubeEvents.VideoArgs("", await yt.GetVideoAsync(Opt(a, "videoId"), ct).ConfigureAwait(false))),
        new("youtube.getChannelInfo", "Channel details by channel id, @handle or recent chatter name", ["user"],
            async (_, a, ct) => YouTubeEvents.ChannelArgs("", await yt.GetChannelAsync(a.GetString("user"), ct).ConfigureAwait(false))),
        new("youtube.getQuota", "Estimated quota units used today and when it resets", [],
            (_, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["used"] = yt.QuotaUsedToday,
                ["dailyQuota"] = yt.Settings.DailyQuota,
                ["exhausted"] = yt.QuotaExhaustedUntil is not null,
                ["resetsAt"] = yt.QuotaResetsAt.ToString("O"),
            })),
    ];

    private async Task<object?> Update(VideoChange change, JsonElement a, CancellationToken ct)
    {
        await yt.UpdateBroadcastAsync(change, Opt(a, "broadcastId"), ct).ConfigureAwait(false);
        return null;
    }

    private async Task<object?> Tags(JsonElement a, Func<IReadOnlyList<string>, IReadOnlyList<string>> update, CancellationToken ct)
    {
        var video = await yt.UpdateTagsAsync(update, Opt(a, "broadcastId"), ct).ConfigureAwait(false);
        return video.Tags;
    }

    private static string[] TagList(JsonElement a) => List(a, "tags");

    /// <summary>A JSON array of strings, or a comma/newline-separated string.</summary>
    private static string[] List(JsonElement a, string name) =>
        a.GetElement(name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
            : a.GetString(name).Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? Opt(JsonElement a, string name) => a.GetString(name).Trim() is { Length: > 0 } v ? v : null;

    private static int Seconds(JsonElement a) =>
        int.TryParse(a.GetString("duration", "300"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 300;

    private static Dictionary<string, object?> Account(YouTubeAccount account) => new()
    {
        ["userName"] = account.ChannelTitle,
        ["userId"] = account.ChannelId,
        ["customUrl"] = account.CustomUrl,
        ["profileImageUrl"] = account.ProfileImageUrl,
        ["isSignedIn"] = account.IsSignedIn,
    };

    private static Dictionary<string, object?> Broadcast(YouTubeBroadcast b) => new()
    {
        ["id"] = b.Id,
        ["title"] = b.Title,
        ["description"] = b.Description,
        ["privacy"] = b.PrivacyStatus,
        ["status"] = b.LifeCycleStatus,
        ["liveChatId"] = b.LiveChatId,
        ["url"] = b.Url,
        ["actualStart"] = b.ActualStart?.ToString("O"),
        ["actualEnd"] = b.ActualEnd?.ToString("O"),
        ["scheduledStart"] = b.ScheduledStart?.ToString("O"),
        ["channelId"] = b.ChannelId,
        ["thumbnailUrl"] = b.ThumbnailUrl,
    };
}
