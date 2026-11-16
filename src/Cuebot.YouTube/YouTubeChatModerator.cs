using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Chat.Moderation;
using Cuebot.YouTube.Api;

namespace Cuebot.YouTube;

/// <summary>
/// Moderation for the combined chat, as the broadcaster (liveChatMessages / liveChatBans). Deleting needs a connection;
/// timeouts and bans also need a live broadcast and go to the chat the message came from. Unbanning needs YouTube's ban
/// id, which only the mod log (or this session) has. Warnings are a chat message to the viewer; slow mode,
/// members-only chat and pins are on YouTube Studio; there is no clear, followers-only or emote-only mode.
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

    /// <summary>YouTube Studio's live control room for the latest broadcast, or the Studio home.</summary>
    private string StudioUrl => youtube.LatestBroadcast is { } live
        ? $"https://studio.youtube.com/video/{Uri.EscapeDataString(live.Id)}/livestreaming"
        : "https://studio.youtube.com/";

    public ModerationCapability Capability(ModerationAction action)
    {
        switch (action)
        {
            case ModerationAction.Clear:
                return ModerationCapability.NotAvailable(action, "YouTube has no way to clear the whole chat.");
            case ModerationAction.FollowersOnly:
                return ModerationCapability.NotAvailable(action, "YouTube has no followers-only chat.");
            case ModerationAction.EmoteOnly:
                return ModerationCapability.NotAvailable(action, "YouTube has no emote-only chat.");
            case ModerationAction.SlowMode:
                return ModerationCapability.Website(action, StudioUrl, "Turn it on in YouTube Studio's live chat settings.");
            case ModerationAction.SubscribersOnly:
                return ModerationCapability.Website(action, StudioUrl, "Members-only chat is set in YouTube Studio.");
            case ModerationAction.Pin or ModerationAction.Unpin:
                return ModerationCapability.Website(action, StudioUrl, "Only the channel owner can pin, on YouTube itself.");
        }
        if (youtube.State != ConnectionState.Connected || !youtube.IsBroadcasterSignedIn)
            return ModerationCapability.NotAvailable(action, "Connect YouTube first.");
        if (action != ModerationAction.Delete && youtube.LatestBroadcast is null)
            return ModerationCapability.NotAvailable(action, "Only while a YouTube broadcast is live.");
        return action switch
        {
            ModerationAction.Delete => ModerationCapability.Works(action) with { QuotaCost = QuotaCosts.LiveChatMessagesDelete },
            ModerationAction.Timeout => ModerationCapability.Works(action) with
            {
                QuotaCost = QuotaCosts.LiveChatBansInsert,
                MinDuration = TimeSpan.FromSeconds(1),
                Note = "YouTube keeps no reason; Cuebot's mod log does.",
            },
            ModerationAction.Ban => ModerationCapability.Works(action) with
            {
                QuotaCost = QuotaCosts.LiveChatBansInsert,
                Note = "YouTube keeps no reason; Cuebot's mod log does.",
            },
            ModerationAction.Unban => ModerationCapability.Works(action) with
            {
                QuotaCost = QuotaCosts.LiveChatBansDelete,
                WebsiteUrl = StudioUrl,
                Note = "Works for bans Cuebot made (it keeps YouTube's ban id); others are lifted in YouTube Studio.",
            },
            ModerationAction.Warn => new ModerationCapability(action, ModerationToolState.Closest)
            {
                QuotaCost = QuotaCosts.LiveChatMessagesInsert,
                MaxReasonLength = 150,
                Note = "YouTube has no warnings: Cuebot posts a chat message that mentions the viewer.",
            },
            _ => ModerationCapability.NotAvailable(action, "YouTube cannot do this."),
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
                await youtube.DeleteMessageAsync(r.MessageId, ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            case ModerationAction.Timeout:
                var applied = capability.Apply(r.Duration ?? TimeSpan.FromMinutes(5));
                var timeoutId = await youtube.BanInChatAsync(who, (int)applied.TotalSeconds, r.ChannelId, ct).ConfigureAwait(false);
                return ModerationOutcome.Done(applied, timeoutId.Length > 0 ? timeoutId : null);
            case ModerationAction.Ban:
                var banId = await youtube.BanInChatAsync(who, null, r.ChannelId, ct).ConfigureAwait(false);
                return ModerationOutcome.Done(platformRef: banId.Length > 0 ? banId : null);
            case ModerationAction.Unban:
                try
                {
                    await youtube.UnbanAsync(who, r.PlatformRef, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (string.IsNullOrEmpty(r.PlatformRef))
                {
                    return ModerationOutcome.Failed("Cuebot has no record of this ban, so YouTube cannot lift it from here. Lift it in YouTube Studio.");
                }
                return ModerationOutcome.Done();
            case ModerationAction.Warn:
                var name = r.UserName.Length > 0 ? r.UserName : "there";
                var text = string.Format(CultureInfo.InvariantCulture, "@{0} Warning from the mods: {1}", name.TrimStart('@'),
                    string.IsNullOrWhiteSpace(r.Reason) ? "please follow the chat rules." : r.Reason.Trim());
                await youtube.SendChatMessageAsync(text, broadcastId: youtube.BroadcastForChat(r.ChannelId)?.Id, ct: ct).ConfigureAwait(false);
                return ModerationOutcome.Done();
            default:
                return ModerationOutcome.Failed("YouTube cannot do this.");
        }
    }

    /// <summary>The channel id when the message carried one (exact); the name otherwise.</summary>
    private static string Who(string userId, string userName) => userId.Length > 0 ? userId : userName;
}

/// <summary>
/// YouTube's daily quota as moderation sees it (docs/UNIFIED_MOD_CHAT_PLAN.md, section 5.4): the units Cuebot counted
/// today (the count starts again at midnight Pacific time), what is left of the project's daily quota, and a reserve
/// moderation may not spend, so reading chat keeps working until the reset.
/// </summary>
public sealed class YouTubeQuotaBudget(Func<int> usedToday, Func<int> dailyQuota, Func<int> reserve, Func<DateTimeOffset> resetsAt,
    Func<DateTimeOffset?> exhaustedUntil) : IPlatformQuota
{
    public static YouTubeQuotaBudget For(YouTubeService youtube) => new(() => youtube.QuotaUsedToday, () => youtube.Settings.DailyQuota,
        () => youtube.ModerationQuotaReserve, () => youtube.QuotaResetsAt, () => youtube.QuotaExhaustedUntil);

    public string Platform => YouTubeChatAdapter.PlatformName;

    public int UsedToday => usedToday();

    public int DailyQuota => Math.Max(0, dailyQuota());

    public int Remaining => exhaustedUntil() is not null ? 0 : Math.Max(0, DailyQuota - UsedToday);

    public int Reserve => Math.Clamp(reserve(), 0, DailyQuota);

    public DateTimeOffset ResetsAt => resetsAt();

    /// <summary>"7,400 of 10,000 units left today (2,000 kept for reading chat)".</summary>
    public string Summary => string.Format(CultureInfo.CurrentCulture, "{0:N0} of {1:N0} units left today ({2:N0} kept for reading chat)",
        Remaining, DailyQuota, Reserve);

    public string? Refuse(int units)
    {
        var at = ResetsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        if (exhaustedUntil() is not null) return $"YouTube's daily quota is used up. It starts again at {at}.";
        if (Remaining - units < Reserve)
            return string.Format(CultureInfo.CurrentCulture,
                "Not enough YouTube quota: this costs {0:N0} units, {1:N0} are left and {2:N0} are kept for reading chat. It starts again at {3}.",
                units, Remaining, Reserve, at);
        return null;
    }
}

/// <summary>The broadcaster and bot channels may only be acted against by the streamer.</summary>
internal sealed class YouTubeProtectedAccounts(YouTubeService youtube) : IProtectedAccounts
{
    public string? WhyProtected(string platform, string userId, string userName)
    {
        if (!string.Equals(platform, YouTubeChatAdapter.PlatformName, StringComparison.OrdinalIgnoreCase) || userId.Length == 0) return null;
        if (userId == youtube.Settings.Broadcaster.ChannelId) return "This is your own YouTube channel.";
        return youtube.IsBotSignedIn && userId == youtube.Settings.Bot.ChannelId ? "This is your YouTube bot account." : null;
    }
}

/// <summary>
/// Reads a linked YouTube account again (YouTube's developer policies: API data is refreshed or deleted within 30 days),
/// so identity links can keep it (docs/UNIFIED_MOD_CHAT_PLAN.md, section 9.3).
/// </summary>
internal sealed class YouTubeAccountLookup(YouTubeService youtube) : Cuebot.Core.Users.IAccountLookup
{
    public string Platform => YouTubeChatAdapter.PlatformName;

    public Task<string?> CurrentNameAsync(string userId, CancellationToken ct = default) => youtube.ChannelTitleAsync(userId, ct);
}
