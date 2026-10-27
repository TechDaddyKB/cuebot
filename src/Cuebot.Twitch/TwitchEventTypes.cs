namespace Cuebot.Twitch;

/// <summary>Event types the Twitch module publishes on the event bus.</summary>
public static class TwitchEventTypes
{
    public const string ChatMessage = "twitch.chatMessage";
    public const string Follow = "twitch.follow";
    public const string Sub = "twitch.sub";
    public const string Resub = "twitch.resub";
    public const string GiftSub = "twitch.giftSub";
    public const string Cheer = "twitch.cheer";
    public const string Raid = "twitch.raid";
    public const string RewardRedemption = "twitch.rewardRedemption";
    public const string StreamOnline = "twitch.streamOnline";
    public const string StreamOffline = "twitch.streamOffline";
}
