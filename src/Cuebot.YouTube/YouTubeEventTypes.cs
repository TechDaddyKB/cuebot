namespace Cuebot.YouTube;

/// <summary>Event types published by the YouTube module. Every event carries <c>platform</c> = "YouTube".</summary>
public static class YouTubeEventTypes
{
    public const string ChatMessage = "youtube.chatMessage";
    public const string SuperChat = "youtube.superChat";
    public const string SuperSticker = "youtube.superSticker";
    public const string NewSponsor = "youtube.newSponsor";
    public const string MemberMilestone = "youtube.memberMilestone";
    public const string MembershipGift = "youtube.membershipGift";
    public const string GiftMembershipReceived = "youtube.giftMembershipReceived";
    public const string JewelsGifted = "youtube.jewelsGifted";
    public const string MessageDeleted = "youtube.messageDeleted";
    public const string UserBanned = "youtube.userBanned";
    public const string UserTimedOut = "youtube.userTimedOut";
    public const string SponsorOnlyStarted = "youtube.sponsorOnlyStarted";
    public const string SponsorOnlyEnded = "youtube.sponsorOnlyEnded";
    public const string PollStarted = "youtube.pollStarted";
    public const string PollUpdated = "youtube.pollUpdated";
    public const string PollClosed = "youtube.pollClosed";
    public const string NewSubscriber = "youtube.newSubscriber";
    public const string BroadcastAdded = "youtube.broadcastAdded";
    public const string BroadcastRemoved = "youtube.broadcastRemoved";
    public const string BroadcastStarted = "youtube.broadcastStarted";
    public const string BroadcastEnded = "youtube.broadcastEnded";
    public const string BroadcastMonitoringStarted = "youtube.broadcastMonitoringStarted";
    public const string BroadcastMonitoringEnded = "youtube.broadcastMonitoringEnded";
    public const string BroadcastUpdated = "youtube.broadcastUpdated";
    public const string BroadcastStatisticsUpdated = "youtube.broadcastStatisticsUpdated";
    public const string QuotaExceeded = "youtube.quotaExceeded";

    /// <summary>Platform-neutral moderation events (read by the speech module to skip queued messages).</summary>
    public const string NeutralMessageDeleted = "chat.messageDeleted";
    public const string NeutralUserBanned = "chat.userBanned";
    public const string NeutralUserTimedOut = "chat.userTimedOut";
}
