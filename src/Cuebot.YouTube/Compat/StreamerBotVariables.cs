using System.Globalization;
using Cuebot.Core.Events;
using Cuebot.YouTube.Api;
using static Cuebot.Core.Variables.StreamerBotArgs;
using Y = Cuebot.YouTube.YouTubeEventTypes;

namespace Cuebot.YouTube.Compat;

/// <summary>
/// Adds Streamer.bot's documented trigger argument names (docs: <c>3.api/2.triggers/youtube/**</c> and the
/// <c>YouTubeUser</c>, <c>YouTubeBroadcaster</c>, <c>YouTubeBroadcast</c> variable sets) to YouTube events, derived
/// from the names Cuebot already sets. Cuebot's own names are kept; nothing is overwritten.
/// </summary>
internal static class StreamerBotVariables
{
    public static BotEvent Apply(BotEvent e, YouTubeAccount? broadcaster = null, YouTubeBroadcast? broadcast = null)
    {
        if (!e.Type.StartsWith("youtube.", StringComparison.Ordinal)) return e;
        var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
        Apply(e.Type, args, broadcaster, broadcast);
        return new BotEvent(e.Type, args);
    }

    internal static void Apply(string type, Dictionary<string, object?> a, YouTubeAccount? broadcaster, YouTubeBroadcast? broadcast)
    {
        // YouTubeBroadcaster
        if (broadcaster is { ChannelId.Length: > 0 })
        {
            Set(a, "broadcastUsername", broadcaster.ChannelTitle);
            Set(a, "broadcastUserId", broadcaster.ChannelId);
            Set(a, "broadcastUserProfileImage", broadcaster.ProfileImageUrl);
        }

        // YouTubeBroadcast
        if (broadcast is not null)
        {
            Set(a, "broadcastId", broadcast.Id);
            Set(a, "broadcast.id", broadcast.Id);
            Set(a, "broadcast.title", broadcast.Title);
            Set(a, "broadcast.description", broadcast.Description);
            Set(a, "broadcast.privacy", broadcast.PrivacyStatus);
            Set(a, "broadcast.status", broadcast.LifeCycleStatus);
        }
        else
        {
            Alias(a, "broadcast.id", "broadcastId");
            Alias(a, "broadcast.title", "broadcastTitle");
            Alias(a, "broadcast.description", "broadcastDescription");
            Alias(a, "broadcast.privacy", "broadcastPrivacy");
            Alias(a, "broadcast.status", "broadcastStatus");
        }

        // YouTubeUser
        if (Text(a, "userId").Length > 0 && type is not (Y.BroadcastAdded or Y.BroadcastRemoved or Y.BroadcastStarted or Y.BroadcastEnded
                or Y.BroadcastMonitoringStarted or Y.BroadcastMonitoringEnded or Y.BroadcastUpdated or Y.BroadcastStatisticsUpdated))
        {
            Set(a, "userType", "youtube");
            Alias(a, "userIsSponsor", "isSponsor");
        }

        switch (type)
        {
            case Y.ChatMessage or Y.SuperChat or Y.MemberMilestone:
                if (a.ContainsKey("rawInput")) AddInput(a, Text(a, "rawInput"));
                AddIndexedEscapes(a);
                break;
        }

        switch (type)
        {
            case Y.SuperChat or Y.SuperSticker:
                Alias(a, "microAmount", "amountMicros");
                Alias(a, "currencyCode", "currency");
                break;
            case Y.JewelsGifted:
                Alias(a, "gift.jewelsAmount", "jewels");
                Alias(a, "gift.name", "giftName");
                Alias(a, "gift.comboCount", "comboCount");
                Set(a, "gift.isCombo", Number(a, "comboCount") > 1);
                break;
            case Y.UserBanned or Y.UserTimedOut:
                Alias(a, "banDuration", "duration");
                break;
            case Y.GiftMembershipReceived:
                Alias(a, "id", "giftId");
                Alias(a, "tier", "levelName");
                Alias(a, "gifterUser", "gifterName");
                Alias(a, "gifterUserName", "gifterName");
                Alias(a, "gifterUserId", "gifterId");
                if (Text(a, "gifterId").Length > 0) Set(a, "gifterUserType", "youtube");
                break;
            case Y.MembershipGift:
                Alias(a, "id", "messageId");
                Alias(a, "count", "total");
                Alias(a, "tier", "levelName");
                break;
            case Y.PollStarted or Y.PollUpdated or Y.PollClosed:
                Poll(a);
                break;
            case Y.BroadcastStatisticsUpdated:
                // The YouTube Data API no longer reports dislikes, and favorites are always 0.
                Set(a, "dislikeCount", 0L);
                Set(a, "favoriteCount", 0L);
                break;
        }
    }

    /// <summary>The documented <c>input#Escaped</c> spelling (First Words) next to <c>inputEscaped#</c>.</summary>
    private static void AddIndexedEscapes(Dictionary<string, object?> a)
    {
        for (var i = 0; a.ContainsKey("input" + i.ToString(CultureInfo.InvariantCulture)); i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            Set(a, $"input{n}Escaped", Escape(Text(a, "input" + n)));
        }
    }

    /// <summary>poll.* from Cuebot's flat poll arguments (pollTitle, pollChoice#Title, pollChoice#Votes...).</summary>
    private static void Poll(Dictionary<string, object?> a)
    {
        Alias(a, "poll.question", "pollTitle");
        Alias(a, "poll.totalVotes", "pollTotalVotes");
        var count = (int)Number(a, "pollChoiceCount");
        Set(a, "poll.options.count", count);
        var winner = -1;
        long best = -1;
        for (var i = 0; i < count; i++)
        {
            Alias(a, $"poll.option{i}.text", $"pollChoice{i}Title");
            Alias(a, $"poll.option{i}.votes", $"pollChoice{i}Votes");
            if (Number(a, $"pollChoice{i}Votes") > best)
            {
                best = Number(a, $"pollChoice{i}Votes");
                winner = i;
            }
        }
        if (winner < 0) return;
        Alias(a, "poll.winningOption.text", $"pollChoice{winner}Title");
        Alias(a, "poll.winningOption.votes", $"pollChoice{winner}Votes");
    }
}
