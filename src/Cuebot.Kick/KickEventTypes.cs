namespace Cuebot.Kick;

/// <summary>Event types the Kick module publishes on the event bus.</summary>
public static class KickEventTypes
{
    public const string ChatMessage = "kick.chatMessage";
    public const string MessageDeleted = "kick.messageDeleted";
    public const string ChatCleared = "kick.chatCleared";
    public const string Follow = "kick.follow";
    public const string Subscription = "kick.subscription";
    public const string Resubscription = "kick.resubscription";
    public const string GiftSubscription = "kick.giftSubscription";
    public const string MassGiftSubscription = "kick.massGiftSubscription";
    public const string KicksGifted = "kick.kicksGifted";
    public const string RewardRedemption = "kick.rewardRedemption";
    public const string Host = "kick.host";
    public const string StreamOnline = "kick.streamOnline";
    public const string StreamOffline = "kick.streamOffline";
    public const string ViewerCountUpdate = "kick.viewerCountUpdate";
    public const string ChannelUpdate = "kick.channelUpdate";
    public const string UserBanned = "kick.userBanned";
    public const string UserTimedOut = "kick.userTimedOut";
    public const string UserUnbanned = "kick.userUnbanned";
    public const string BroadcasterAuthenticated = "kick.broadcasterAuthenticated";
    public const string ChatConnected = "kick.chatConnected";
    public const string ChatDisconnected = "kick.chatDisconnected";

    /// <summary>Platform-neutral moderation events (consumed e.g. by speech to drop queued messages).</summary>
    public const string NeutralMessageDeleted = "chat.messageDeleted";
    public const string NeutralUserBanned = "chat.userBanned";
    public const string NeutralUserTimedOut = "chat.userTimedOut";
}
