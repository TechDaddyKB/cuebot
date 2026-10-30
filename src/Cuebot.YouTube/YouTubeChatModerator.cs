using Cuebot.Core;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.YouTube;

/// <summary>
/// Delete, timeout and ban for the combined chat, as the broadcaster (liveChatMessages / liveChatBans). Deleting needs
/// a connection; timeouts and bans also need a live broadcast, whose chat they apply to.
/// </summary>
internal sealed class YouTubeChatModerator(YouTubeService youtube) : IChatModerator
{
    public string Platform => YouTubeChatAdapter.PlatformName;

    public ChatModerationActions Available
    {
        get
        {
            if (youtube.State != ConnectionState.Connected || !youtube.IsBroadcasterSignedIn) return ChatModerationActions.None;
            var actions = ChatModerationActions.DeleteMessage;
            if (youtube.LatestBroadcast is not null) actions |= ChatModerationActions.Timeout | ChatModerationActions.Ban;
            return actions;
        }
    }

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => youtube.DeleteMessageAsync(messageId, ct);

    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) =>
        youtube.TimeoutUserAsync(Who(userId, userName), (int)Math.Ceiling(duration.TotalSeconds), ct: ct);

    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) =>
        youtube.BanUserAsync(Who(userId, userName), ct: ct);

    /// <summary>The channel id when the message carried one (exact); the name otherwise.</summary>
    private static string Who(string userId, string userName) => userId.Length > 0 ? userId : userName;
}
