using System.Globalization;
using Cuebot.Core.Events;
using static Cuebot.Core.Variables.StreamerBotArgs;
using T = Cuebot.Twitch.TwitchEventTypes;

namespace Cuebot.Twitch.Compat;

/// <summary>
/// The broadcaster account, for Streamer.bot's <c>broadcastUser*</c> and <c>broadcaster*</c> arguments on every Twitch
/// event. The details are read from Helix once per connection (not per event).
/// </summary>
internal sealed record TwitchBroadcasterArgs(string Login, string UserId, string DisplayName = "", string BroadcasterType = "")
{
    public string Description { get; init; } = "";
    public string ProfileImageUrl { get; init; } = "";
    public string OfflineImageUrl { get; init; } = "";

    /// <summary>When the account was created (UTC, as Streamer.bot reports it).</summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>The channel's current title and category; null until read.</summary>
    public TwitchChannelArgs? Channel { get; init; }
}

/// <summary>The broadcaster's channel, for Streamer.bot's <c>broadcasterChannel.*</c> arguments.</summary>
internal sealed record TwitchChannelArgs(string Title, string GameId, string GameName);

/// <summary>A user's roles in the broadcaster's chat as last seen (chat badges, moderator and VIP changes).</summary>
internal readonly record struct TwitchUserRoles(bool IsModerator, bool IsVip, bool IsSubscriber);

/// <summary>
/// Adds Streamer.bot's documented trigger argument names (docs: <c>3.api/2.triggers/twitch/**</c> and the
/// <c>TwitchUser</c>, <c>TwitchChat</c>, <c>TwitchBroadcaster</c>, <c>TwitchSharedChatSource</c> variable sets) to
/// Twitch events, derived from the names Cuebot already sets. Cuebot's own names are kept; nothing is overwritten.
/// Pure: values that need memory across events (reward settings, old title, raid target...) are added by the router.
/// </summary>
internal static class StreamerBotVariables
{
    /// <param name="roles">The roles last seen for a user id (null when unknown), for events without chat badges.</param>
    public static BotEvent Apply(BotEvent e, TwitchBroadcasterArgs? broadcaster = null, Func<string, TwitchUserRoles?>? roles = null)
    {
        var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
        Apply(e.Type, args, broadcaster, e.Time, roles);
        return new BotEvent(e.Type, args);
    }

    internal static void Apply(string type, Dictionary<string, object?> a, TwitchBroadcasterArgs? broadcaster, DateTimeOffset at,
        Func<string, TwitchUserRoles?>? roles = null)
    {
        Broadcaster(a, broadcaster);
        User(a, roles);
        if (IsChat(type, a)) Chat(type, a);
        SharedChatSource(a);

        switch (type)
        {
            case T.RewardRedemption or T.RewardRedemptionUpdated:
                Alias(a, "rewardName", "rewardTitle");
                if (type == T.RewardRedemptionUpdated) Alias(a, "rewardStatus", "status");
                AddInput(a, Text(a, "userInput"), words: false);
                break;
            case T.CustomPowerUpRedemption:
                Alias(a, "rewardName", "rewardTitle");
                Alias(a, "customPowerUpId", "rewardId");
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
                SubMessage(type, a);
                break;
            case T.Resub or T.SharedChatResub:
                Alias(a, "cumulative", "cumulativeMonths");
                Alias(a, "monthStreak", "streakMonths");
                MultiMonth(a);
                SubMessage(type, a);
                break;
            case T.SubGift or T.SharedChatSubGift:
                Alias(a, "recipientId", "recipientUserId");
                // Streamer.bot reports the gifted months as text ("3").
                if (a.ContainsKey("durationMonths")) Set(a, "monthsGifted", Number(a, "durationMonths").ToString(CultureInfo.InvariantCulture));
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

            case T.StreamOnline or T.ShieldModeBegin:
                ToTime(a, "startedAt");
                break;
            case T.ShieldModeEnd:
                ToTime(a, "endedAt");
                break;
            case T.GuestStarSessionBegin or T.GuestStarSessionEnd:
                SetTime(a, "sartedAt", a.GetValueOrDefault("startedAt")); // sic: Streamer.bot's spelling
                ToTime(a, "endedAt");
                break;
            case T.UpcomingAd:
                ToTime(a, "nextAdAt");
                break;

            case T.HypeTrainStart or T.HypeTrainUpdate or T.HypeTrainLevelUp or T.HypeTrainEnd:
                ToTime(a, "startedAt");
                ToTime(a, "expiresAt");
                if (type != T.HypeTrainEnd && a.GetValueOrDefault("startedAt") is DateTime trainStarted
                    && a.GetValueOrDefault("expiresAt") is DateTime trainExpires)
                    Set(a, "duration", (int)Math.Round((trainExpires - trainStarted).TotalSeconds));
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
                Alias(a, "low_trust_status", "lowTrustStatus"); // sic: Streamer.bot's spelling
                break;
            case T.SuspiciousUserMessage:
                Alias(a, "low_trust_status", "lowTrustStatus");
                break;
            case T.UnbanRequestApproved or T.UnbanRequestDenied:
                Alias(a, "message", "resolutionText");
                Alias(a, "moderatorId", "moderatorUserId");
                Alias(a, "moderatorUsername", "moderatorUserName");
                Alias(a, "moderatorDisplayName", "moderator");
                break;
            case T.BlockedTermsAdded or T.BlockedTermsDeleted or T.PermittedTermsAdded or T.PermittedTermsDeleted:
                Alias(a, "moderatorUsername", "userName");
                Alias(a, "moderatorDisplayName", "user");
                Alias(a, "moderatorId", "userId");
                SetTime(a, "createdAt", at);
                for (var i = 0; a.ContainsKey($"term.{i}"); i++) Alias(a, $"term.[{i}]", $"term.{i}"); // the docs' spelling
                break;
            case T.ShoutoutCreated:
                ToTime(a, "cooldownEndsAt");
                ToTime(a, "targetCooldownEndsAt");
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
                ToTime(a, "createdAt");
                break;
            case T.AutomodMessageHeld or T.AutomodMessageUpdated:
                AddInput(a, Text(a, "message"));
                break;

            case T.RaidStart or T.RaidSend:
                Alias(a, "viewers", "viewerCount");
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
                Set(a, "adLengthMs", (int)Number(a, "duration") * 1000);
                Alias(a, "adScheduled", "isAutomatic");
                break;
            case T.PresentViewers:
                Set(a, "isTest", false);
                break;

            case T.Announcement or T.SharedChatAnnouncement:
                Alias(a, "announceColor", "announcementColor");
                Placeholder(a, "fromSharedChat", type == T.SharedChatAnnouncement);
                break;
            case T.Modiversary:
                Placeholder(a, "fromSharedChat", false);
                break;
            case T.Cheer:
                Alias(a, "anonymous", "isAnonymous");
                Cheermotes(a);
                break;
            case T.SubscriberModeOn or T.SubscriberModeOff or T.UniqueModeOn or T.UniqueModeOff or T.ChatSettingsUpdated:
            case T.EmoteModeOn or T.EmoteModeOff or T.FollowerModeOn or T.FollowerModeOff or T.FollowerModeChanged:
            case T.SlowModeOn or T.SlowModeOff or T.SlowModeChanged:
                Alias(a, "subscriberOnly", "subscriberMode");
                Alias(a, "uniqueMode", "uniqueChatMode");
                Alias(a, "emoteOnly", "emoteMode");
                break;
            case T.SharedChatBegin or T.SharedChatUpdate or T.SharedChatEnd:
                Alias(a, "host.userId", "userId");
                Alias(a, "host.userLogin", "userName");
                Alias(a, "host.userName", "user");
                break;
            case T.GuestStarGuestUpdate:
                // Streamer.bot types the slot as a number.
                if (int.TryParse(Text(a, "slotId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) a["slotId"] = slot;
                Alias(a, "moderatorUserLogin", "moderatorUserName");
                Alias(a, "guestUserName", "user");
                Alias(a, "guestUserLogin", "userName");
                Alias(a, "guestUserId", "userId");
                break;
        }

        // Shared chat copies of channel events (not the session events) are always from the shared chat.
        if (type.StartsWith("twitch.sharedChat", StringComparison.Ordinal) && type is not (T.SharedChatBegin or T.SharedChatUpdate or T.SharedChatEnd))
            Set(a, "fromSharedChat", true);

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
            Placeholder(a, "subscriptionTier", "0");        // TwitchUser: text ("1000"/"2000"/"3000") when subscribed
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
                Placeholder(a, "isSubscribed", false);
                break;
            case T.Cheer:
                // channel.cheer is not a chat message: no message id, badges, emotes or color.
                Placeholder(a, "msgId", "");
                Placeholder(a, "color", "");
                Placeholder(a, "badges", "");
                Placeholder(a, "badgeCount", 0);
                Placeholder(a, "emotes", "");
                Placeholder(a, "emoteCount", 0);
                break;
            case T.HypeTrainStart or T.HypeTrainUpdate or T.HypeTrainLevelUp or T.HypeTrainEnd:
                Placeholder(a, "contributors", 0);
                break;
            case T.RaidStart:
                Placeholder(a, "viewers", 0);
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
                Placeholder(a, "users", new List<Dictionary<string, object?>>());
                Placeholder(a, "title", "");
                Placeholder(a, "viewerCount", 0);
                Placeholder(a, "startedAt", default(DateTime));
                break;
        }
        if (type is T.Sub or T.SharedChatSub or T.Resub or T.SharedChatResub)
        {
            // TwitchChat names of the sub notice; EventSub's subscribe events have no badges, emotes or color.
            Placeholder(a, "badges", "");
            Placeholder(a, "badgeCount", 0);
            Placeholder(a, "emotes", "");
            Placeholder(a, "emoteCount", 0);
            Placeholder(a, "color", "");
        }
    }

    /// <summary>Subscription notices: rawInput (and its forms) and messageStripped from the sub message, "" without one.</summary>
    private static void SubMessage(string type, Dictionary<string, object?> a)
    {
        if (IsChat(type, a)) return;   // Chat() added them
        var message = Text(a, "message");
        AddInput(a, message);
        Set(a, "messageStripped", Strip(message, Text(a, "emoteNames")));
    }

    /// <summary>
    /// TwitchBroadcaster: broadcastUser, broadcastUserName, broadcastUserId, broadcastIsAffiliate, broadcastIsPartner;
    /// and the account block Streamer.bot adds to every event of a connected account: broadcasterChannel.gameId,
    /// .gameName, .title, broadcasterCreatedAt, broadcasterDescription, broadcasterIsAffiliate, broadcasterIsPartner,
    /// broadcasterIsOnboarded, broadcasterProfileUrl, broadcasterOfflineUrl. Values not read yet are neutral.
    /// </summary>
    private static void Broadcaster(Dictionary<string, object?> a, TwitchBroadcasterArgs? b)
    {
        if (b is null || b.UserId.Length == 0) return;
        Set(a, "broadcastUser", b.DisplayName.Length > 0 ? b.DisplayName : b.Login);
        Set(a, "broadcastUserName", b.Login);
        Set(a, "broadcastUserId", b.UserId);
        if (b.BroadcasterType.Length > 0 || b.DisplayName.Length > 0)
        {
            Set(a, "broadcastIsAffiliate", b.BroadcasterType == "affiliate");
            Set(a, "broadcastIsPartner", b.BroadcasterType == "partner");
        }
        Set(a, "broadcasterChannel.gameId", b.Channel?.GameId ?? "");
        Set(a, "broadcasterChannel.gameName", b.Channel?.GameName ?? "");
        Set(a, "broadcasterChannel.title", b.Channel?.Title ?? "");
        Set(a, "broadcasterCreatedAt", b.CreatedAt ?? default);
        Set(a, "broadcasterDescription", b.Description);
        Set(a, "broadcasterIsAffiliate", b.BroadcasterType == "affiliate");
        Set(a, "broadcasterIsPartner", b.BroadcasterType == "partner");
        Placeholder(a, "broadcasterIsOnboarded", false);   // not in Helix
        Set(a, "broadcasterProfileUrl", b.ProfileImageUrl);
        Set(a, "broadcasterOfflineUrl", b.OfflineImageUrl);
    }

    /// <summary>
    /// TwitchUser: userType, isSubscribed, isModerator, isVip and badgeCount where the event has a user. Events without
    /// chat badges get the roles last seen for the user (false when not seen).
    /// </summary>
    private static void User(Dictionary<string, object?> a, Func<string, TwitchUserRoles?>? roles)
    {
        var userId = Text(a, "userId");
        if (userId.Length == 0 && !a.ContainsKey("isAnonymous")) return;
        Set(a, "userType", "twitch");
        Alias(a, "isSubscribed", "isSubscriber");
        if (userId.Length > 0)
        {
            var known = roles?.Invoke(userId);
            Set(a, "isModerator", known?.IsModerator ?? false);
            Set(a, "isVip", known?.IsVip ?? false);
            Set(a, "isSubscribed", known?.IsSubscriber ?? false);
        }
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

    /// <summary>cheerEmotes (Cuebot's comma separated list of the message's cheermotes, "Cheer100,Kappa50") and cheerEmoteCount.</summary>
    private static void Cheermotes(Dictionary<string, object?> a)
    {
        if (Number(a, "bits") <= 0) return;
        var found = Text(a, "message").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsCheermote).ToList();
        Set(a, "cheerEmotes", string.Join(',', found));
        Set(a, "cheerEmoteCount", found.Count);
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
        // Streamer.bot types the poll id as a Guid (Twitch's ids are GUIDs).
        if (Text(a, "pollId") is { Length: > 0 } pollId) Set(a, "poll.Id", Guid.TryParse(pollId, out var guid) ? guid : pollId);
        SetTime(a, "poll.StartedAt", a.GetValueOrDefault("startedAt"));
        SetTime(a, "poll.EndsAt", a.GetValueOrDefault("endsAt"));
        var ended = type is T.PollCompleted or T.PollTerminated or T.PollArchived;
        if (ended) SetTime(a, "poll.endedAt", Text(a, "endedAt").Length > 0 ? a["endedAt"] : at);
        if (Time(a.GetValueOrDefault("startedAt")) is { } started && Time(a.GetValueOrDefault("endsAt")) is { } ends)
        {
            Set(a, "poll.Duration", (int)Math.Round((ends - started).TotalSeconds));
            Set(a, "poll.DurationRemaining", ended ? 0 : (int)Math.Max(0, Math.Round((ends - at.LocalDateTime).TotalSeconds)));
        }
        // channel.poll.end has no ends_at: the time the poll ran.
        if (ended && Time(a.GetValueOrDefault("startedAt")) is { } began && a.GetValueOrDefault("poll.endedAt") is DateTime finished)
            Set(a, "poll.Duration", (int)Math.Max(0, Math.Round((finished - began).TotalSeconds)));
        Placeholder(a, "poll.Duration", 0);
        Placeholder(a, "poll.DurationRemaining", 0);
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
        SetTime(a, "prediction.LocksAt", a.GetValueOrDefault("locksAt"));
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
        return string.Join(' ', message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !IsCheermote(w)));
    }

    /// <summary>A word that is a cheermote: letters followed by a bit count ("Cheer100", "Kappa50").</summary>
    private static bool IsCheermote(string w)
    {
        var digits = w.Length - w.AsSpan().TrimEnd("0123456789").Length;
        return digits > 0 && digits < w.Length && char.IsLetter(w[0]);
    }
}
