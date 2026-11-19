using System.Globalization;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Rumble.Chat;

namespace Cuebot.Rumble;

/// <summary>
/// Rumble has no moderation API (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 4.1). By default every tool opens Rumble's own
/// pages. With <see cref="RumbleSettings.UnofficialModeration"/> on (off by default; Rumble's terms forbid automated use
/// without permission) deleting and muting go through the unofficial addresses Rumble's web chat uses: a timeout is a
/// five-minute mute or, for anything longer, a mute until the stream ends; a ban is a permanent mute. Unmuting needs the
/// list on Rumble's website.
/// </summary>
internal sealed class RumbleChatModerator(RumbleService rumble) : IChatModerator
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    public string Platform => RumbleChatAdapter.PlatformName;

    public ChatModerationActions Available =>
        rumble.UnofficialModerationReady && rumble.IsLive
            ? ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban
            : ChatModerationActions.None;

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => rumble.DeleteChatMessageAsync(messageId, ct);

    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) =>
        rumble.MuteAsync(userName, duration <= FiveMinutes ? RumbleMute.FiveMinutes : RumbleMute.ThisStream, ct);

    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) =>
        rumble.MuteAsync(userName, RumbleMute.Permanent, ct);

    /// <summary>Rumble's pop-out chat of the live stream (its moderation menu is on each message), or the home page.</summary>
    private string ChatUrl => rumble.LiveChatId is { } chat
        ? "https://rumble.com/chat/popup/" + chat.ToString(CultureInfo.InvariantCulture)
        : rumble.Endpoints.Site.ToString();

    public ModerationCapability Capability(ModerationAction action)
    {
        switch (action)
        {
            case ModerationAction.Warn:
                return ModerationCapability.NotAvailable(action, "Rumble has no warnings.");
            case ModerationAction.Clear or ModerationAction.FollowersOnly or ModerationAction.EmoteOnly:
                return ModerationCapability.NotAvailable(action, "Rumble has no such chat setting.");
            case ModerationAction.SlowMode or ModerationAction.SubscribersOnly:
                return ModerationCapability.Website(action, ChatUrl, "Change it in your Rumble chat's settings.");
            case ModerationAction.Pin or ModerationAction.Unpin:
                return ModerationCapability.Website(action, ChatUrl, "Pin it from the message's menu in your Rumble chat.");
            case ModerationAction.Unban:
                return ModerationCapability.Website(action, rumble.Endpoints.MutedUsersPage.ToString(),
                    "Unmute the viewer in the list of muted users on Rumble.");
        }
        if (!rumble.UnofficialModerationReady)
            return ModerationCapability.Website(action, ChatUrl, rumble.Settings.UnofficialModeration
                ? "Sign in to Rumble chat (Rumble page) so Cuebot can do this; until then, do it in your Rumble chat."
                : "Rumble has no official way to do this. Do it in your Rumble chat.");
        if (!rumble.IsLive) return ModerationCapability.NotAvailable(action, "Only while you are live on Rumble.");
        const string unofficial = "Through Rumble's unofficial chat addresses (Rumble settings).";
        return action switch
        {
            ModerationAction.Delete => ModerationCapability.Works(action) with { Note = unofficial },
            ModerationAction.Timeout => new ModerationCapability(action, ModerationToolState.Closest)
            {
                MinDuration = FiveMinutes,
                Note = "Rumble mutes for 5 minutes, or until the stream ends for anything longer. " + unofficial,
            },
            ModerationAction.Ban => new ModerationCapability(action, ModerationToolState.Closest)
            {
                Note = "A permanent mute in your Rumble chats. Rumble keeps no reason. " + unofficial,
            },
            _ => ModerationCapability.NotAvailable(action, "Rumble cannot do this."),
        };
    }

    public async Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
    {
        var capability = Capability(r.Action);
        if (!capability.CanRun) return ModerationOutcome.Failed(capability.Note);
        switch (r.Action)
        {
            case ModerationAction.Delete:
                await rumble.DeleteChatMessageAsync(r.MessageId, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            case ModerationAction.Timeout:
                var requested = r.Duration ?? FiveMinutes;
                var mute = requested <= FiveMinutes ? RumbleMute.FiveMinutes : RumbleMute.ThisStream;
                await rumble.MuteAsync(r.UserName, mute, ct).ConfigureAwait(false);
                // "Until the stream ends" has no fixed length; the log keeps what was asked for.
                return ModerationOutcome.Done(mute == RumbleMute.FiveMinutes ? FiveMinutes : requested);
            case ModerationAction.Ban:
                await rumble.MuteAsync(r.UserName, RumbleMute.Permanent, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            default:
                return ModerationOutcome.Failed("Rumble cannot do this from Cuebot.");
        }
    }
}
