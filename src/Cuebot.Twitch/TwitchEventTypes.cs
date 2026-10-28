namespace Cuebot.Twitch;

/// <summary>Event types the Twitch module publishes on the event bus.</summary>
public static class TwitchEventTypes
{
    // Chat
    public const string ChatMessage = "twitch.chatMessage";
    public const string Announcement = "twitch.announcement";
    public const string BitsBadgeTier = "twitch.bitsBadgeTier";
    public const string Whisper = "twitch.whisper";
    public const string BotWhisper = "twitch.botWhisper";
    public const string ChatMessageDeleted = "twitch.chatMessageDeleted";
    public const string ChatCleared = "twitch.chatCleared";
    public const string UserMessagesCleared = "twitch.userMessagesCleared";
    public const string ChatSettingsUpdated = "twitch.chatSettingsUpdated";
    public const string EmoteModeOn = "twitch.emoteModeOn";
    public const string EmoteModeOff = "twitch.emoteModeOff";
    public const string FollowerModeOn = "twitch.followerModeOn";
    public const string FollowerModeOff = "twitch.followerModeOff";
    public const string FollowerModeChanged = "twitch.followerModeChanged";
    public const string SlowModeOn = "twitch.slowModeOn";
    public const string SlowModeOff = "twitch.slowModeOff";
    public const string SlowModeChanged = "twitch.slowModeChanged";
    public const string SubscriberModeOn = "twitch.subscriberModeOn";
    public const string SubscriberModeOff = "twitch.subscriberModeOff";
    public const string UniqueModeOn = "twitch.uniqueModeOn";
    public const string UniqueModeOff = "twitch.uniqueModeOff";
    public const string WatchStreak = "twitch.watchStreak";
    public const string Modiversary = "twitch.modiversary";
    public const string Pyramid = "twitch.pyramid";
    public const string PyramidBroken = "twitch.pyramidBroken";

    // Channel
    public const string Follow = "twitch.follow";
    public const string StreamOnline = "twitch.streamOnline";
    public const string StreamOffline = "twitch.streamOffline";
    public const string StreamUpdate = "twitch.streamUpdate";
    public const string ViewerCountUpdate = "twitch.viewerCountUpdate";
    public const string PresentViewers = "twitch.presentViewers";

    // Subscriptions and bits
    public const string Sub = "twitch.sub";
    public const string Resub = "twitch.resub";
    public const string GiftSub = "twitch.giftSub";
    public const string SubGift = "twitch.subGift";
    public const string GiftBomb = "twitch.giftBomb";
    public const string GiftPaidUpgrade = "twitch.giftPaidUpgrade";
    public const string PrimePaidUpgrade = "twitch.primePaidUpgrade";
    public const string PayItForward = "twitch.payItForward";
    public const string Cheer = "twitch.cheer";
    public const string BitsUsed = "twitch.bitsUsed";
    public const string PowerUp = "twitch.powerUp";
    public const string CustomPowerUpRedemption = "twitch.customPowerUpRedemption";
    public const string SubCounterRollover = "twitch.subCounterRollover";

    // Raids
    public const string Raid = "twitch.raid";
    public const string RaidStart = "twitch.raidStart";
    public const string RaidSend = "twitch.raidSend";
    public const string RaidCancelled = "twitch.raidCancelled";

    // Channel points
    public const string RewardRedemption = "twitch.rewardRedemption";
    public const string RewardRedemptionUpdated = "twitch.rewardRedemptionUpdated";
    public const string AutomaticRewardRedemption = "twitch.automaticRewardRedemption";
    public const string RewardCreated = "twitch.rewardCreated";
    public const string RewardUpdated = "twitch.rewardUpdated";
    public const string RewardDeleted = "twitch.rewardDeleted";

    // Polls and predictions
    public const string PollCreated = "twitch.pollCreated";
    public const string PollUpdated = "twitch.pollUpdated";
    public const string PollCompleted = "twitch.pollCompleted";
    public const string PollTerminated = "twitch.pollTerminated";
    public const string PollArchived = "twitch.pollArchived";
    public const string PredictionCreated = "twitch.predictionCreated";
    public const string PredictionUpdated = "twitch.predictionUpdated";
    public const string PredictionLocked = "twitch.predictionLocked";
    public const string PredictionCompleted = "twitch.predictionCompleted";
    public const string PredictionCanceled = "twitch.predictionCanceled";

    // Hype train, goals, charity
    public const string HypeTrainStart = "twitch.hypeTrainStart";
    public const string HypeTrainUpdate = "twitch.hypeTrainUpdate";
    public const string HypeTrainLevelUp = "twitch.hypeTrainLevelUp";
    public const string HypeTrainEnd = "twitch.hypeTrainEnd";
    public const string GoalBegin = "twitch.goalBegin";
    public const string GoalProgress = "twitch.goalProgress";
    public const string GoalEnd = "twitch.goalEnd";
    public const string CharityStarted = "twitch.charityStarted";
    public const string CharityProgress = "twitch.charityProgress";
    public const string CharityDonation = "twitch.charityDonation";
    public const string CharityCompleted = "twitch.charityCompleted";

    // Ads, shoutouts, shield mode, shared chat
    public const string AdRun = "twitch.adRun";
    public const string UpcomingAd = "twitch.upcomingAd";
    public const string ShoutoutCreated = "twitch.shoutoutCreated";
    public const string ShoutoutReceived = "twitch.shoutoutReceived";
    public const string ShieldModeBegin = "twitch.shieldModeBegin";
    public const string ShieldModeEnd = "twitch.shieldModeEnd";
    public const string SharedChatBegin = "twitch.sharedChatBegin";
    public const string SharedChatUpdate = "twitch.sharedChatUpdate";
    public const string SharedChatEnd = "twitch.sharedChatEnd";
    public const string SharedChatSub = "twitch.sharedChatSub";
    public const string SharedChatResub = "twitch.sharedChatResub";
    public const string SharedChatSubGift = "twitch.sharedChatSubGift";
    public const string SharedChatGiftBomb = "twitch.sharedChatGiftBomb";
    public const string SharedChatGiftPaidUpgrade = "twitch.sharedChatGiftPaidUpgrade";
    public const string SharedChatPrimePaidUpgrade = "twitch.sharedChatPrimePaidUpgrade";
    public const string SharedChatPayItForward = "twitch.sharedChatPayItForward";
    public const string SharedChatRaid = "twitch.sharedChatRaid";
    public const string SharedChatAnnouncement = "twitch.sharedChatAnnouncement";
    public const string SharedChatModiversary = "twitch.sharedChatModiversary";
    public const string SharedChatMessageDeleted = "twitch.sharedChatMessageDeleted";
    public const string SharedChatUserBanned = "twitch.sharedChatUserBanned";
    public const string SharedChatUserTimedOut = "twitch.sharedChatUserTimedOut";
    public const string SharedChatUserUnbanned = "twitch.sharedChatUserUnbanned";
    public const string SharedChatUserUntimedOut = "twitch.sharedChatUserUntimedOut";

    // Guest Star (a Twitch beta API)
    public const string GuestStarSessionBegin = "twitch.guestStarSessionBegin";
    public const string GuestStarSessionEnd = "twitch.guestStarSessionEnd";
    public const string GuestStarGuestUpdate = "twitch.guestStarGuestUpdate";
    public const string GuestStarSettingsUpdate = "twitch.guestStarSettingsUpdate";

    // Moderation
    public const string UserBanned = "twitch.userBanned";
    public const string UserTimedOut = "twitch.userTimedOut";
    public const string UserUnbanned = "twitch.userUnbanned";
    public const string UserUntimedOut = "twitch.userUntimedOut";
    public const string ModeratorAdded = "twitch.moderatorAdded";
    public const string ModeratorRemoved = "twitch.moderatorRemoved";
    public const string VipAdded = "twitch.vipAdded";
    public const string VipRemoved = "twitch.vipRemoved";
    public const string UserWarned = "twitch.userWarned";
    public const string WarningAcknowledged = "twitch.warningAcknowledged";
    public const string UnbanRequestCreated = "twitch.unbanRequestCreated";
    public const string UnbanRequestApproved = "twitch.unbanRequestApproved";
    public const string UnbanRequestDenied = "twitch.unbanRequestDenied";
    public const string SuspiciousUserUpdate = "twitch.suspiciousUserUpdate";
    public const string SuspiciousUserMessage = "twitch.suspiciousUserMessage";
    public const string AutomodMessageHeld = "twitch.automodMessageHeld";
    public const string AutomodMessageUpdated = "twitch.automodMessageUpdated";
    public const string BlockedTermsAdded = "twitch.blockedTermsAdded";
    public const string BlockedTermsDeleted = "twitch.blockedTermsDeleted";
    public const string PermittedTermsAdded = "twitch.permittedTermsAdded";
    public const string PermittedTermsDeleted = "twitch.permittedTermsDeleted";

    // Connections
    public const string BroadcasterAuthenticated = "twitch.broadcasterAuthenticated";
    public const string BroadcasterConnected = "twitch.broadcasterConnected";
    public const string BroadcasterDisconnected = "twitch.broadcasterDisconnected";
    public const string BroadcasterChatConnected = "twitch.broadcasterChatConnected";
    public const string BroadcasterChatDisconnected = "twitch.broadcasterChatDisconnected";
    public const string BotConnected = "twitch.botConnected";
    public const string BotDisconnected = "twitch.botDisconnected";
}

/// <summary>Platform-neutral moderation events (consumed by speech and other modules).</summary>
public static class NeutralChatEvents
{
    /// <summary>Args: platform, messageId, userId, user, userName.</summary>
    public const string MessageDeleted = "chat.messageDeleted";

    /// <summary>Args: platform, userId, user, userName, duration (0), reason.</summary>
    public const string UserBanned = "chat.userBanned";

    /// <summary>Args: platform, userId, user, userName, duration (seconds), reason.</summary>
    public const string UserTimedOut = "chat.userTimedOut";
}
