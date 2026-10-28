using Cuebot.Core.Actions;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Triggers;

namespace Cuebot.YouTube.SubActions;

/// <summary>A YouTube sub-action defined by its parameters and a body.</summary>
internal sealed class YouTubeSubAction(
    string typeId,
    string displayName,
    IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => YouTubeTriggers.Category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        await run(context, parameters, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>The YouTube sub-actions (Send Message, moderation, broadcast title/description/privacy, info, polls).</summary>
internal static class YouTubeSubActions
{
    private static readonly ParameterDescriptor BroadcastId =
        new("broadcastId", "Broadcast id") { Help = "Leave empty for the latest monitored broadcast" };

    private static readonly ParameterDescriptor User =
        new("user", "User") { DefaultValue = "%userId%", Help = "Channel id, @handle, or the display name of a recent chatter" };

    /// <summary>Number of handlers <see cref="Create"/> returns (building them never touches the service).</summary>
    public static int Count { get; } = Create(null!).Count();

    public static IEnumerable<ISubActionHandler> Create(YouTubeService yt) =>
    [
        new YouTubeSubAction("youtube.sendMessage", "Send message to channel",
            [
                new("message", "Message", ParameterKind.Multiline) { Help = "Messages over 200 characters are split" },
                new("useBot", "Send as the bot account", ParameterKind.Boolean) { DefaultValue = "false" },
                new("fallback", "Fall back to the broadcaster", ParameterKind.Boolean) { DefaultValue = "true" },
                BroadcastId,
            ],
            (_, p, ct) => yt.SendChatMessageAsync(p["message"], p.GetBool("useBot"), p.GetBool("fallback", true), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.deleteMessage", "Delete message",
            [new("messageId", "Message id") { DefaultValue = "%messageId%" }],
            (_, p, ct) => yt.DeleteMessageAsync(Clean(p["messageId"]), ct)),

        new YouTubeSubAction("youtube.banUser", "Ban user (bannedUser, bannedUserName, bannedUserId, banResult)", [User, BroadcastId],
            async (c, p, ct) =>
            {
                await yt.BanUserAsync(Clean(p["user"]), Optional(p["broadcastId"]), ct).ConfigureAwait(false);
                await WriteBannedAsync(yt, c, Clean(p["user"]), "banResult", ct).ConfigureAwait(false);
            }),

        new YouTubeSubAction("youtube.timeoutUser", "Timeout user (bannedUser, bannedUserName, bannedUserId, timeoutResult)",
            [User, new("duration", "Duration (seconds)", ParameterKind.Number) { DefaultValue = "300" }, BroadcastId],
            async (c, p, ct) =>
            {
                await yt.TimeoutUserAsync(Clean(p["user"]), p.GetInt("duration", 300), Optional(p["broadcastId"]), ct).ConfigureAwait(false);
                await WriteBannedAsync(yt, c, Clean(p["user"]), "timeoutResult", ct).ConfigureAwait(false);
            }),

        new YouTubeSubAction("youtube.unbanUser", "Unban user", [User],
            (_, p, ct) => yt.UnbanUserAsync(Clean(p["user"]), ct)),

        new YouTubeSubAction("youtube.addModerator", "Add moderator", [User, BroadcastId],
            (_, p, ct) => yt.AddModeratorAsync(Clean(p["user"]), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.removeModerator", "Remove moderator", [User, BroadcastId],
            (_, p, ct) => yt.RemoveModeratorAsync(Clean(p["user"]), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.setTitle", "Set title",
            [new("title", "Title"), BroadcastId],
            (_, p, ct) => yt.UpdateBroadcastAsync(new VideoChange(Title: Required(p["title"], "title")), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.setDescription", "Set description",
            [new("description", "Description", ParameterKind.Multiline), BroadcastId],
            (_, p, ct) => yt.UpdateBroadcastAsync(new VideoChange(Description: p["description"]), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.setTitleAndDescription", "Set title and description",
            [new("title", "Title"), new("description", "Description", ParameterKind.Multiline), BroadcastId],
            (_, p, ct) => yt.UpdateBroadcastAsync(new VideoChange(Title: Required(p["title"], "title"), Description: p["description"]),
                Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.setPrivacy", "Set privacy",
            [new("privacy", "Privacy", ParameterKind.Choice) { DefaultValue = "public", Choices = ["public", "unlisted", "private"] }, BroadcastId],
            (_, p, ct) => yt.UpdateBroadcastAsync(new VideoChange(PrivacyStatus: p["privacy"].Trim().ToLowerInvariant()), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.setCategory", "Set category",
            [new("categoryId", "Category id") { Help = "YouTube video category id, e.g. 20 = Gaming, 22 = People & Blogs" }, BroadcastId],
            (_, p, ct) => yt.UpdateBroadcastAsync(new VideoChange(CategoryId: Required(p["categoryId"], "category id")), Optional(p["broadcastId"]), ct)),

        new YouTubeSubAction("youtube.getVideoInfo", "Get video info",
            [new("videoId", "Video id") { Help = "Leave empty for the latest monitored broadcast. Adds %video...% arguments" }],
            async (c, p, ct) => Merge(c, YouTubeEvents.VideoArgs("video", await yt.GetVideoAsync(Optional(p["videoId"]), ct).ConfigureAwait(false)))),

        new YouTubeSubAction("youtube.getChannelInfo", "Get user (channel) info",
            [new("user", "User") { DefaultValue = "%userId%", Help = "Adds %channel...% arguments" }],
            async (c, p, ct) => Merge(c, YouTubeEvents.ChannelArgs("channel", await yt.GetChannelAsync(Clean(p["user"]), ct).ConfigureAwait(false)))),

        new YouTubeSubAction("youtube.addBroadcasterInfo", "Add broadcaster information", [],
            (c, _, _) =>
            {
                Merge(c, YouTubeEvents.AccountArgs("broadcast", yt.Settings.Broadcaster));
                Merge(c, YouTubeEvents.AccountArgs("bot", yt.Settings.Bot));
                return Task.CompletedTask;
            }),

        new YouTubeSubAction("youtube.getBroadcast", "Get latest monitored broadcast", [],
            (c, _, _) =>
            {
                var latest = yt.LatestBroadcast;
                c.Args["broadcastIsLive"] = latest is not null;
                if (latest is null) return Task.CompletedTask;
                var args = YouTubeEvents.BroadcastArgs(latest, yt.Settings.Broadcaster);
                args.Remove("user");
                args.Remove("userName");
                args.Remove("userId");
                args.Remove("title");
                Merge(c, args);
                if (yt.StatisticsOf(latest.Id) is { } s)
                {
                    c.Args["concurrentViewers"] = s.ConcurrentViewers ?? 0;
                    c.Args["likeCount"] = s.LikeCount;
                    c.Args["viewCount"] = s.ViewCount;
                }
                return Task.CompletedTask;
            }),

        new YouTubeSubAction("youtube.createPoll", "Create poll",
            [
                new("question", "Question"),
                new("options", "Options (one per line, 2–4)", ParameterKind.Multiline),
                BroadcastId,
            ],
            async (c, p, ct) =>
            {
                var options = p["options"].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                c.Args["pollMessageId"] = await yt.CreatePollAsync(p["question"], options, Optional(p["broadcastId"]), ct).ConfigureAwait(false);
            }),

        new YouTubeSubAction("youtube.endPoll", "End poll",
            [new("pollMessageId", "Poll message id") { Help = "Leave empty for the active poll" }],
            (_, p, ct) => yt.EndPollAsync(Optional(Clean(p["pollMessageId"])), ct)),
    ];

    /// <summary>
    /// Streamer.bot's Ban / Timeout User outputs: bannedUser, bannedUserName, bannedUserId, bannedUserType and the
    /// result flag (true; a failure stops the action instead). The user was just resolved, so this is a cache hit.
    /// </summary>
    private static async Task WriteBannedAsync(YouTubeService yt, ActionRunContext c, string user, string result, CancellationToken ct)
    {
        var (channelId, name) = await yt.ResolveUserAsync(user, ct).ConfigureAwait(false);
        c.Args["bannedUser"] = name;
        c.Args["bannedUserName"] = name;
        c.Args["bannedUserId"] = channelId;
        c.Args["bannedUserType"] = "youtube";
        c.Args[result] = true;
    }

    /// <summary>A parameter whose %variable% did not resolve (argument missing) counts as empty.</summary>
    internal static string Clean(string value)
    {
        var v = value.Trim();
        return v.Length > 2 && v[0] == '%' && v[^1] == '%' && v.IndexOf('%', 1) == v.Length - 1 ? "" : v;
    }

    private static string? Optional(string value) => Clean(value) is { Length: > 0 } v ? v : null;

    private static string Required(string value, string what) =>
        value.Trim().Length > 0 ? value.Trim() : throw new ArgumentException($"No {what} given.");

    private static void Merge(ActionRunContext context, IReadOnlyDictionary<string, object?> args)
    {
        foreach (var (key, value) in args) context.Args[key] = value;
    }
}
