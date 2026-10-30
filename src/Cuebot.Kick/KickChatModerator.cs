using Cuebot.Core;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Kick.Auth;

namespace Cuebot.Kick;

/// <summary>
/// Delete, timeout and ban for the combined chat, as the broadcaster (Kick's public moderation API). Offered while
/// connected and only for the scopes the broadcaster granted.
/// </summary>
internal sealed class KickChatModerator(KickService kick) : IChatModerator
{
    public string Platform => KickChatAdapter.PlatformName;

    public ChatModerationActions Available
    {
        get
        {
            if (kick.State != ConnectionState.Connected || !kick.IsBroadcasterSignedIn) return ChatModerationActions.None;
            var scopes = kick.Settings.Broadcaster.Scopes;
            var actions = ChatModerationActions.None;
            if (scopes.Contains(KickScopes.ModerationChat)) actions |= ChatModerationActions.DeleteMessage;
            if (scopes.Contains(KickScopes.ModerationBan)) actions |= ChatModerationActions.Timeout | ChatModerationActions.Ban;
            return actions;
        }
    }

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => kick.DeleteChatMessageAsync(messageId, ct);

    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) =>
        kick.TimeoutAsync(Who(userId, userName), (int)Math.Ceiling(duration.TotalSeconds), reason, ct);

    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) =>
        kick.BanAsync(Who(userId, userName), reason, ct);

    /// <summary>The numeric user id when the message carried one; the name otherwise (both are accepted).</summary>
    private static string Who(string userId, string userName) => userId.Length > 0 ? userId : userName;
}
