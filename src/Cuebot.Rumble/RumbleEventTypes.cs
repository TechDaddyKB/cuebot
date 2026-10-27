namespace Cuebot.Rumble;

/// <summary>Event types the Rumble module publishes on the event bus.</summary>
public static class RumbleEventTypes
{
    public const string ChatMessage = "rumble.chatMessage";
    public const string Rant = "rumble.rant";
    public const string Follow = "rumble.follow";
    public const string Subscribe = "rumble.subscribe";
    public const string GiftSub = "rumble.giftSub";
    public const string StreamOnline = "rumble.streamOnline";
    public const string StreamOffline = "rumble.streamOffline";
}
