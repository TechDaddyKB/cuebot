using System.Globalization;
using Cuebot.Core.Events;
using static Cuebot.Core.Variables.StreamerBotArgs;
using T = Cuebot.Twitch.TwitchEventTypes;

namespace Cuebot.Twitch.Compat;

/// <summary>The broadcaster account, for Streamer.bot's <c>broadcastUser*</c> arguments on every Twitch event.</summary>
internal sealed record TwitchBroadcasterArgs(string Login, string UserId, string DisplayName = "", string BroadcasterType = "");

/// <summary>
/// Adds Streamer.bot's documented trigger argument names (docs: <c>3.api/2.triggers/twitch/**</c> and the
/// <c>TwitchUser</c>, <c>TwitchChat</c>, <c>TwitchBroadcaster</c>, <c>TwitchSharedChatSource</c> variable sets) to
/// Twitch events, derived from the names Cuebot already sets. Cuebot's own names are kept; nothing is overwritten.
/// Pure: values that need memory across events (reward settings, old title, raid target...) are added by the router.
/// </summary>
internal static class StreamerBotVariables
{
    public static BotEvent Apply(BotEvent e, TwitchBroadcasterArgs? broadcaster = null)
    {
        var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
        Apply(e.Type, args, broadcaster, e.Time);
        return new BotEvent(e.Type, args);
    }

    internal static void Apply(string type, Dictionary<string, object?> a, TwitchBroadcasterArgs? broadcaster, DateTimeOffset at)
    {
        Broadcaster(a, broadcaster);
        User(a);
        if (IsChat(type, a)) Chat(type, a);
        SharedChatSource(a);

        switch (type)
        {
            case T.RewardRedemption or T.RewardRedemptionUpdated:
                Alias(a, "rewardName", "rewardTitle");
                if (type == T.RewardRedemptionUpdated) Alias(a, "rewardStatus", "status");
                AddInput(a, Text(a, "userInput"), words: false);
                break;
            case T.RewardCreated or T.RewardUpdated or T.RewardDeleted:
                Alias(a, "rewardName", "rewardTitle");
                break;
            case T.AutomaticRewardRedemption:
                AutomaticReward(a);
                break;

            case T.Sub or T.SharedChatSub:
                MultiMonth(a);
                break;
            case T.Resub or T.SharedChatResub:
                Alias(a, "cumulative", "cumulativeMonths");
                Alias(a, "monthStreak", "streakMonths");
                MultiMonth(a);
                break;
            case T.SubGift or T.SharedChatSubGift:
                Alias(a, "recipientId", "recipientUserId");
                Alias(a, "monthsGifted", "durationMonths");
                Alias(a, "totalSubsGifted", "cumulativeTotal");
                Alias(a, "anonymous", "isAnonymous");
                Set(a, "fromGiftBomb", Text(a, "communityGiftId").Length > 0);
                Set(a, "random", Text(a, "communityGiftId").Length > 0);
                break;
            case T.GiftBomb or T.SharedChatGiftBomb or T.GiftSub:
                Alias(a, "gifts", "total");
                Alias(a, "totalGifts", "cumulativeTotal");
                Alias(a, "anonymous", "isAnonymous");
                break;
            case T.PrimePaidUpgrade or T.SharedChatPrimePaidUpgrade:
                if (Number(a, "tier") is var tier and > 0)
                {
                    Set(a, "upgradeTier", (int)tier);
                    Set(a, "upgradeTierString", "tier " + tier.ToString(CultureInfo.InvariantCulture));
                }
                break;

            case T.PollCreated or T.PollUpdated or T.PollCompleted or T.PollTerminated or T.PollArchived:
                Poll(type, a, at);
                break;
            case T.PredictionCreated or T.PredictionUpdated or T.PredictionLocked or T.PredictionCompleted or T.PredictionCanceled:
                Prediction(a);
                break;

            case T.HypeTrainStart or T.HypeTrainUpdate or T.HypeTrainLevelUp or T.HypeTrainEnd:
                Alias(a, "id", "hypeTrainId");
                Alias(a, "isGoldenKappaTrain", "isGoldenKappa");
                Set(a, "isTreasureTrain", Text(a, "trainType").Equals("treasure", StringComparison.OrdinalIgnoreCase));
                Set(a, "percentDecimal", Number(a, "percent") / 100.0);
                break;
            case T.GoalBegin or T.GoalProgress or T.GoalEnd:
                Alias(a, "goal.id", "goalId");
                Alias(a, "goal.type", "goalType");
                Alias(a, "goal.description", "description");
                Alias(a, "goal.currentAmount", "currentAmount");
                Alias(a, "goal.targetAmount", "targetAmount");
                SetTime(a, "goal.startedAt", a.GetValueOrDefault("startedAt"));
                if (type == T.GoalEnd) SetTime(a, "goal.endedAt", a.GetValueOrDefault("endedAt"));
                break;
            case T.CharityStarted or T.CharityProgress or T.CharityDonation or T.CharityCompleted:
                Alias(a, type == T.CharityDonation ? "charity.campaignId" : "charity.id", "campaignId");
                Alias(a, "charity.name", "charityName");
                Alias(a, "charity.description", "charityDescription");
                Alias(a, "charity.website", "charityWebsite");
                Alias(a, "charity.logo", "charityLogo");
                break;

            case T.UserBanned or T.UserTimedOut or T.SharedChatUserBanned or T.SharedChatUserTimedOut:
            case T.UserUnbanned or T.UserUntimedOut or T.SharedChatUserUnbanned or T.SharedChatUserUntimedOut:
                Alias(a, "createdById", "moderatorUserId");
                Alias(a, "createdByUsername", "moderatorUserName");
                Alias(a, "createdByDisplayName", "moderator");
                SetTime(a, "createdAt", a.GetValueOrDefault("bannedAt") is string { Length: > 0 } banned ? banned : at);
                break;
            case T.ChatMessageDeleted or T.SharedChatMessageDeleted:
                Alias(a, "targetMessageId", "messageId");
                break;
            case T.UserWarned or T.SuspiciousUserUpdate:
                Alias(a, "moderator.userName", "moderator");
                Alias(a, "moderator.login", "moderatorUserName");
                Alias(a, "moderator.id", "moderatorUserId");
                break;
            case T.BlockedTermsAdded or T.BlockedTermsDeleted or T.PermittedTermsAdded or T.PermittedTermsDeleted:
                Alias(a, "moderatorUsername", "userName");
                Alias(a, "moderatorDisplayName", "user");
                Alias(a, "moderatorId", "userId");
                SetTime(a, "createdAt", at);
                break;
            case T.ShoutoutCreated:
                Alias(a, "targetUserId", "userId");
                Alias(a, "targetUserLogin", "userName");
                Alias(a, "targetUserDisplayName", "user");
                Alias(a, "viewerCount", "viewers");
                break;
            case T.WatchStreak:
                // EventSub has no separate streak id; the notification's message id identifies it.
                Alias(a, "watchStreakId", "messageId");
                break;
            case T.ShoutoutReceived:
                Alias(a, "viewerCount", "viewers");
                break;
            case T.UnbanRequestCreated:
                Alias(a, "text", "message");
                break;
            case T.AutomodMessageHeld or T.AutomodMessageUpdated:
                AddInput(a, Text(a, "message"));
                break;

            case T.RaidStart or T.RaidSend:
                Alias(a, "raidUser", "user");
                Alias(a, "raidUserName", "userName");
                Alias(a, "raidUserId", "userId");
                RaidImage(a);
                break;
            case T.RaidCancelled:
                RaidImage(a);
                break;
            case T.Pyramid or T.PyramidBroken:
                Alias(a, "pyramidWidth", "width");
                Alias(a, "pyramidEmote", "emote");
                if (type == T.PyramidBroken)
                {
                    Alias(a, "pyramidOwnerId", "userId");
                    Alias(a, "pyramidOwnerUsername", "userName");
                    Alias(a, "pyramidOwnerDisplayName", "user");
                }
                break;

            case T.StreamUpdate:
                Alias(a, "status", "title");
                break;
            case T.StreamOffline:
                SetTime(a, "endedAt", at);
                break;
            case T.AdRun:
                Alias(a, "adLength", "duration");
                Set(a, "adLengthMs", Number(a, "duration") * 1000);
                Alias(a, "adScheduled", "isAutomatic");
                break;
            case T.PresentViewers:
                Set(a, "isTest", false);
                break;

            case T.Announcement or T.SharedChatAnnouncement:
                Alias(a, "announceColor", "announcementColor");
                break;
            case T.SubscriberModeOn or T.SubscriberModeOff or T.UniqueModeOn or T.UniqueModeOff or T.ChatSettingsUpdated:
            case T.EmoteModeOn or T.EmoteModeOff or T.FollowerModeOn or T.FollowerModeOff or T.FollowerModeChanged:
            case T.SlowModeOn or T.SlowModeOff or T.SlowModeChanged:
                Alias(a, "subscriberOnly", "subscriberMode");
                Alias(a, "uniqueMode", "uniqueChatMode");
                break;
            case T.SharedChatBegin or T.SharedChatUpdate or T.SharedChatEnd:
                Alias(a, "host.userId", "userId");
                Alias(a, "host.userLogin", "userName");
                Alias(a, "host.userName", "user");
                break;
            case T.GuestStarGuestUpdate:
                Alias(a, "moderatorUserLogin", "moderatorUserName");
                Alias(a, "guestUserName", "user");
                Alias(a, "guestUserLogin", "userName");
                Alias(a, "guestUserId", "userId");
                break;
        }

        Placeholders(type, a);
    }

    /// <summary>
    /// Documented by Streamer.bot but not sent by EventSub or Helix: present with a neutral value so imported actions
    /// that read them keep working. Runs last, so real values set above (or by the router) win.
    /// </summary>
    private static void Placeholders(string type, Dictionary<string, object?> a)
    {
        if (Text(a, "userId").Length > 0)
        {
            Placeholder(a, "subscriptionTier", 0);          // TwitchUser: number (1000/2000/3000) when subscribed
            Placeholder(a, "userGroups", "");               // TwitchUser: comma-delimited groups (Streamer.bot user database)
            Placeholder(a, "userPreviousActive", default(DateTime));
        }
        if (IsChat(type, a))
        {
            Placeholder(a, "isAction", false);              // EventSub does not flag /me messages
            Placeholder(a, "inSharedChat", false);
        }
        if (Text(a, "sourceBroadcasterId").Length > 0)
        {
            Placeholder(a, "source.isSubscribed", false);
            Placeholder(a, "source.isModerator", false);
            Placeholder(a, "source.isVip", false);
        }
        switch (type)
        {
            case T.Sub or T.SharedChatSub:
                Placeholder(a, "isMultiMonth", false);
                Placeholder(a, "multiMonthDuration", 0);
                Placeholder(a, "multiMonthTenure", 0);
                break;
            case T.Resub or T.SharedChatResub:
                Placeholder(a, "multiMonthTenure", 0);
                break;
            case T.SubGift or T.SharedChatSubGift:
                Placeholder(a, "cumulativeMonths", 0);         // the recipient's months
                break;
            case T.GiftBomb or T.SharedChatGiftBomb:
                Placeholder(a, "bonusGifts", false);
                break;
            case T.StreamOnline:
                Placeholder(a, "game", "");
                Placeholder(a, "gameId", "");
                Placeholder(a, "tagCount", 0);
                Placeholder(a, "tags", new List<string>());
                Placeholder(a, "tagsDelimited", "");
                break;
            case T.StreamUpdate:
                Placeholder(a, "gameBoxArt", "");
                Placeholder(a, "oldGameBoxArt", "");
                break;
            case T.ShoutoutCreated:
                Placeholder(a, "shoutoutId", "");
                break;
            case T.SharedChatBegin or T.SharedChatUpdate or T.SharedChatEnd:
                Placeholder(a, "host.isSubscribed", false);
                Placeholder(a, "host.isModerator", false);
                Placeholder(a, "host.isVip", false);
                break;
            case T.PresentViewers:
                Placeholder(a, "isLive", false);
                break;
        }
    }

    /// <summary>TwitchBroadcaster: broadcastUser, broadcastUserName, broadcastUserId, broadcastIsAffiliate, broadcastIsPartner.</summary>
    private static void Broadcaster(Dictionary<string, object?> a, TwitchBroadcasterArgs? b)
    {
        if (b is null || b.UserId.Length == 0) return;
        Set(a, "broadcastUser", b.DisplayName.Length > 0 ? b.DisplayName : b.Login);
        Set(a, "broadcastUserName", b.Login);
        Set(a, "broadcastUserId", b.UserId);
        if (b.BroadcasterType.Length == 0 && b.DisplayName.Length == 0) return;
        Set(a, "broadcastIsAffiliate", b.BroadcasterType == "affiliate");
        Set(a, "broadcastIsPartner", b.BroadcasterType == "partner");
    }

    /// <summary>TwitchUser: userType, isSubscribed and badgeCount where the event has a user.</summary>
    private static void User(Dictionary<string, object?> a)
    {
        if (Text(a, "userId").Length == 0 && !a.ContainsKey("isAnonymous")) return;
        Set(a, "userType", "twitch");
        Alias(a, "isSubscribed", "isSubscriber");
        if (a.TryGetValue("badges", out var badges) && badges is string list)
            Set(a, "badgeCount", list.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>Events that carry Streamer.bot's TwitchChat variables: chat messages and chat notifications with text.</summary>
    private static bool IsChat(string type, Dictionary<string, object?> a) => type switch
    {
        T.ChatMessageDeleted or T.SharedChatMessageDeleted or T.SuspiciousUserMessage or T.AutomodMessageHeld
            or T.AutomodMessageUpdated or T.UnbanRequestCreated => false,
        T.Resub or T.Cheer or T.BitsUsed or T.PowerUp or T.AutomaticRewardRedemption => true,
        _ => a.ContainsKey("message") && a.ContainsKey("messageId"),
    };

    /// <summary>TwitchChat: msgId, role, colorR/G/B, messageStripped, isHighlight, reply.*, fromSharedChat and the input words.</summary>
    private static void Chat(string type, Dictionary<string, object?> a)
    {
        Alias(a, "msgId", "messageId");
        if (a.ContainsKey("isBroadcaster") || a.ContainsKey("isModerator"))
            Set(a, "role", Flag(a, "isBroadcaster") ? 4 : Flag(a, "isModerator") ? 3 : Flag(a, "isVip") ? 2 : 1);
        AddColor(a, Text(a, "color"));
        var message = Text(a, "message");
        Set(a, "messageStripped", Strip(message, Text(a, "emoteNames")));
        if (a.ContainsKey("bits")) Set(a, "messageCheermotesStripped", StripCheermotes(message, Number(a, "bits") > 0));
        Alias(a, "isHighlight", "isHighlighted");
        Alias(a, "fromSharedChat", "isSharedChat");
        if (Flag(a, "isReply"))
        {
            Alias(a, "reply.msgId", "replyParentMessageId");
            Alias(a, "reply.userId", "replyParentUserId");
            Alias(a, "reply.userLogin", "replyParentUserName");
            Alias(a, "reply.userName", "replyParentUser");
            Alias(a, "reply.msgBody", "replyParentMessage");
            Alias(a, "reply.threadMsgId", "replyThreadMessageId");
            Alias(a, "reply.threadUserLogin", "replyThreadUserName");
        }
        // Reward redemptions keep the redemption text in rawInput; chat-style events use the message.
        if (type is not (T.RewardRedemption or T.RewardRedemptionUpdated)) AddInput(a, a.ContainsKey("rawInput") ? Text(a, "rawInput") : message);
    }

    /// <summary>TwitchSharedChatSource: source.userId, source.userLogin, source.userName.</summary>
    private static void SharedChatSource(Dictionary<string, object?> a)
    {
        if (Text(a, "sourceBroadcasterId").Length == 0) return;
        Alias(a, "source.userId", "sourceBroadcasterId");
        Alias(a, "source.userLogin", "sourceBroadcasterUserName");
        Alias(a, "source.userName", "sourceBroadcaster");
    }

    private static void MultiMonth(Dictionary<string, object?> a)
    {
        if (!a.ContainsKey("durationMonths")) return;
        var months = Number(a, "durationMonths");
        Set(a, "isMultiMonth", months > 1);
        Set(a, "multiMonthDuration", (int)Math.Max(months, 1));
    }

    private static void AutomaticReward(Dictionary<string, object?> a)
    {
        var rewardType = Text(a, "rewardType");
        var id = Text(a, "emoteId");
        if (rewardType == "gigantify_an_emote")
        {
            Alias(a, "gigantifiedEmoteId", "emoteId");
            Alias(a, "gigantifiedEmoteName", "emoteName");
            if (id.Length > 0) Set(a, "gigantifiedEmoteUrl", $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/3.0");
        }
        else if (rewardType.Contains("emote_unlock", StringComparison.Ordinal))
        {
            Alias(a, "unlockedEmoteId", "emoteId");
            Alias(a, "unlockedEmoteName", "emoteName");
        }
    }

    private static void RaidImage(Dictionary<string, object?> a)
    {
        Alias(a, "raidUserProfileImageURL", "profileImageUrl");
        if (a.ContainsKey("raidUserProfileImageURL")) Set(a, "raidUserProfileImageEscaped", Escape(Text(a, "raidUserProfileImageURL")));
    }

    /// <summary>poll.* from Cuebot's flat poll arguments (pollId, title, choice#Title, choice#Votes...).</summary>
    private static void Poll(string type, Dictionary<string, object?> a, DateTimeOffset at)
    {
        Alias(a, "poll.Title", "title");
        Alias(a, "poll.Id", "pollId");
        SetTime(a, "poll.StartedAt", a.GetValueOrDefault("startedAt"));
        SetTime(a, "poll.EndsAt", a.GetValueOrDefault("endsAt"));
        var ended = type is T.PollCompleted or T.PollTerminated or T.PollArchived;
        if (ended) SetTime(a, "poll.endedAt", Text(a, "endedAt").Length > 0 ? a["endedAt"] : at);
        if (Time(a.GetValueOrDefault("startedAt")) is { } started && Time(a.GetValueOrDefault("endsAt")) is { } ends)
        {
            Set(a, "poll.Duration", (int)Math.Round((ends - started).TotalSeconds));
            Set(a, "poll.DurationRemaining", ended ? 0 : (int)Math.Max(0, Math.Round((ends - at.LocalDateTime).TotalSeconds)));
        }
        var count = (int)Number(a, "choiceCount");
        Set(a, "poll.choices.count", count);
        long votes = 0, rewardVotes = 0, total = 0;
        var winner = -1;
        long best = -1;
        for (var i = 0; i < count; i++)
        {
            var p = $"poll.choice{i}.";
            var choiceTotal = Number(a, $"choice{i}Votes");
            var choiceReward = Number(a, $"choice{i}ChannelPointsVotes");
            Alias(a, p + "title", $"choice{i}Title");
            Alias(a, p + "id", $"choice{i}Id");
            Set(a, p + "votes", (int)(choiceTotal - choiceReward));
            Set(a, p + "rewardVotes", (int)choiceReward);
            Set(a, p + "totalVotes", (int)choiceTotal);
            votes += choiceTotal - choiceReward;
            rewardVotes += choiceReward;
            total += choiceTotal;
            if (choiceTotal > best)
            {
                best = choiceTotal;
                winner = i;
            }
        }
        Set(a, "poll.votes", (int)votes);
        Set(a, "poll.rewardVotes", (int)rewardVotes);
        Set(a, "poll.totalVotes", (int)total);
        if (!ended || winner < 0) return;
        Set(a, "poll.winningIndex", winner);
        foreach (var field in new[] { "id", "title", "votes", "rewardVotes", "totalVotes" })
            Alias(a, "poll.winningChoice." + field, $"poll.choice{winner}.{field}");
    }

    /// <summary>prediction.* from Cuebot's flat prediction arguments (predictionId, outcome#Title...).</summary>
    private static void Prediction(Dictionary<string, object?> a)
    {
        Alias(a, "prediction.Id", "predictionId");
        Alias(a, "prediction.Title", "title");
        SetTime(a, "prediction.CreatedAt", a.GetValueOrDefault("startedAt"));
        SetTime(a, "prediction.LockedAt", a.GetValueOrDefault("lockedAt"));
        SetTime(a, "prediction.EndedAt", a.GetValueOrDefault("endedAt"));
        if (Time(a.GetValueOrDefault("startedAt")) is { } started && Time(a.GetValueOrDefault("locksAt")) is { } locks)
            Set(a, "prediction.PredictionWindow", (int)Math.Round((locks - started).TotalSeconds));
        var count = (int)Number(a, "outcomeCount");
        var winningId = Text(a, "winningOutcomeId");
        for (var i = 0; i < count; i++)
        {
            var p = $"prediction.outcome{i}.";
            Alias(a, p + "id", $"outcome{i}Id");
            Alias(a, p + "title", $"outcome{i}Title");
            Alias(a, p + "users", $"outcome{i}Users");
            Alias(a, p + "points", $"outcome{i}ChannelPoints");
            Alias(a, p + "color", $"outcome{i}Color");
            if (winningId.Length == 0 || Text(a, $"outcome{i}Id") != winningId) continue;
            Set(a, "prediction.winningIndex", i);
            foreach (var field in new[] { "id", "title", "users", "points", "color" })
                Alias(a, "prediction.winningOutcome." + field, p + field);
        }
    }

    /// <summary>The message without its emotes (<paramref name="emoteNames"/> is Cuebot's comma separated list).</summary>
    internal static string Strip(string message, string emoteNames)
    {
        if (emoteNames.Length == 0) return message.Trim();
        var names = emoteNames.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !names.Contains(w)));
    }

    /// <summary>The message without cheermotes ("Cheer100", "Kappa50") when the message carried bits.</summary>
    internal static string StripCheermotes(string message, bool hasBits)
    {
        if (!hasBits) return message.Trim();
        static bool IsCheermote(string w)
        {
            var digits = w.Length - w.AsSpan().TrimEnd("0123456789").Length;
            return digits > 0 && digits < w.Length && char.IsLetter(w[0]);
        }
        return string.Join(' ', message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !IsCheermote(w)));
    }
}
