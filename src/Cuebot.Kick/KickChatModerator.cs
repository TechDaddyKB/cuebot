using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Kick.Auth;

namespace Cuebot.Kick;

/// <summary>
/// Moderation for the combined chat, as the broadcaster (Kick's public moderation API): delete, ban, unban, and
/// timeouts in whole minutes (1 minute to 7 days; other lengths are rounded up, and the UI shows the result). Each is
/// offered while connected and only for the scopes the broadcaster granted. Warnings are a chat message to the viewer;
/// clear, the chat modes and pins have no API and are done in Kick's own chat.
/// </summary>
internal sealed class KickChatModerator(KickService kick) : IChatModerator
{
    /// <summary>Kick stores at most this many characters of a ban reason.</summary>
    internal const int MaxReason = 100;

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

    /// <summary>The channel page, where Kick's own chat (with /slow, /clear and the mod menu) is.</summary>
    private string ChannelUrl => kick.Settings.ChannelSlug.Length > 0
        ? "https://kick.com/" + Uri.EscapeDataString(kick.Settings.ChannelSlug)
        : "https://kick.com/";

    public ModerationCapability Capability(ModerationAction action)
    {
        switch (action)
        {
            case ModerationAction.Clear:
                return ModerationCapability.Website(action, ChannelUrl, "Type /clear in your Kick chat.");
            case ModerationAction.SlowMode:
                return ModerationCapability.Website(action, ChannelUrl, "Type /slow in your Kick chat, or use the chat settings there.");
            case ModerationAction.FollowersOnly or ModerationAction.SubscribersOnly or ModerationAction.EmoteOnly:
                return ModerationCapability.Website(action, ChannelUrl, "Use the chat settings in your Kick chat.");
            case ModerationAction.Pin or ModerationAction.Unpin:
                return ModerationCapability.Website(action, ChannelUrl, "Pin it from the message's menu in your Kick chat.");
        }
        if (kick.State != ConnectionState.Connected || !kick.IsBroadcasterSignedIn)
            return ModerationCapability.NotAvailable(action, "Connect Kick first.");
        var scopes = kick.Settings.Broadcaster.Scopes;
        var scope = action switch
        {
            ModerationAction.Delete => KickScopes.ModerationChat,
            ModerationAction.Warn => KickScopes.ChatWrite,
            _ => KickScopes.ModerationBan,
        };
        if (!scopes.Contains(scope))
            return ModerationCapability.NotAvailable(action, "Sign in to Kick again and allow moderation.");
        return action switch
        {
            ModerationAction.Timeout => new ModerationCapability(action, ModerationToolState.Closest)
            {
                DurationUnit = TimeSpan.FromMinutes(1),
                MinDuration = TimeSpan.FromMinutes(1),
                MaxDuration = TimeSpan.FromDays(7),
                MaxReasonLength = MaxReason,
                Note = "Kick counts whole minutes (1 minute to 7 days); other lengths are rounded up.",
            },
            ModerationAction.Ban => ModerationCapability.Works(action) with { MaxReasonLength = MaxReason },
            ModerationAction.Warn => new ModerationCapability(action, ModerationToolState.Closest)
            {
                MaxReasonLength = 300,
                Note = "Kick has no warnings: Cuebot posts a chat message that mentions the viewer.",
            },
            _ => ModerationCapability.Works(action),
        };
    }

    public async Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
    {
        var capability = Capability(r.Action);
        if (!capability.CanRun) return ModerationOutcome.Failed(capability.Note);
        var who = Who(r.UserId, r.UserName);
        switch (r.Action)
        {
            case ModerationAction.Delete:
                await kick.DeleteChatMessageAsync(r.MessageId, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            case ModerationAction.Timeout:
                var applied = capability.Apply(r.Duration ?? TimeSpan.FromMinutes(10));
                await kick.TimeoutAsync(who, (int)applied.TotalSeconds, r.Reason, ct).ConfigureAwait(false);
                return ModerationOutcome.Done(applied);
            case ModerationAction.Ban:
                await kick.BanAsync(who, r.Reason, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            case ModerationAction.Unban:
                await kick.UnbanAsync(who, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            case ModerationAction.Warn:
                var name = (r.UserName.Length > 0 ? r.UserName : "there").TrimStart('@');
                var text = string.Format(CultureInfo.InvariantCulture, "@{0} Warning from the mods: {1}", name,
                    string.IsNullOrWhiteSpace(r.Reason) ? "please follow the chat rules." : r.Reason.Trim());
                await kick.SendChatMessageAsync(text, ct: ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            default:
                return ModerationOutcome.Failed("Kick cannot do this from Cuebot.");
        }
    }

    /// <summary>The numeric user id when the message carried one; the name otherwise (both are accepted).</summary>
    private static string Who(string userId, string userName) => userId.Length > 0 ? userId : userName;
}

/// <summary>The broadcaster and bot accounts may only be acted against by the streamer.</summary>
internal sealed class KickProtectedAccounts(KickService kick) : IProtectedAccounts
{
    public string? WhyProtected(string platform, string userId, string userName)
    {
        if (!string.Equals(platform, KickChatAdapter.PlatformName, StringComparison.OrdinalIgnoreCase) || userId.Length == 0) return null;
        var id = long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (id <= 0) return null;
        if (id == kick.Settings.Broadcaster.UserId) return "This is your own Kick account.";
        return kick.IsBotSignedIn && id == kick.Settings.Bot.UserId ? "This is your Kick bot account." : null;
    }
}
