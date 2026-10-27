using System.Globalization;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;

namespace Cuebot.YouTube;

/// <summary>
/// Shows YouTube chat (including Super Chats and Super Stickers, with their amount) in the combined chat, and
/// paid/membership/broadcast activity in the event feed: Super Chats, stickers and Jewels as tips, memberships and
/// milestones as subscriptions, membership gifts as gift subscriptions, new channel subscribers as follows.
/// </summary>
internal sealed class YouTubeChatAdapter : IChatEventAdapter
{
    public const string PlatformName = "YouTube";

    public string Platform => PlatformName;

    public ChatMessage? ToChatMessage(BotEvent e)
    {
        var a = e.Args;
        string? amount;
        switch (e.Type)
        {
            case YouTubeEventTypes.ChatMessage:
                amount = null;
                break;
            case YouTubeEventTypes.SuperChat:
            case YouTubeEventTypes.SuperSticker:
                amount = a.GetString("amountText");
                break;
            default:
                return null;
        }
        var id = a.GetString("messageId");
        return new ChatMessage(Platform, id.Length > 0 ? id : Guid.NewGuid().ToString("N"), a.GetString("user"), a.GetString("userId"),
            a.GetString("message"), e.Time)
        {
            Roles = Roles(a),
            Amount = string.IsNullOrEmpty(amount) ? null : amount,
        };
    }

    public FeedItem? ToFeedItem(BotEvent e)
    {
        var a = e.Args;
        var user = a.GetString("user");
        return e.Type switch
        {
            YouTubeEventTypes.SuperChat => new FeedItem(Platform, FeedKind.Tip, user, $"{user} sent a {a.GetString("amountText")} Super Chat", e.Time)
            {
                Detail = a.GetString("message"),
                Amount = a.GetString("amountText"),
            },
            YouTubeEventTypes.SuperSticker => new FeedItem(Platform, FeedKind.Tip, user, $"{user} sent a {a.GetString("amountText")} Super Sticker", e.Time)
            {
                Detail = a.GetString("stickerAltText"),
                Amount = a.GetString("amountText"),
            },
            YouTubeEventTypes.JewelsGifted => new FeedItem(Platform, FeedKind.Tip, user, $"{user} sent {Jewels(a)}", e.Time)
            {
                Detail = a.GetString("giftName"),
                Amount = Jewels(a),
            },
            YouTubeEventTypes.NewSponsor => new FeedItem(Platform, FeedKind.Subscription, user,
                a.GetBool("isUpgrade") ? $"{user} upgraded their membership" : $"{user} became a member", e.Time)
            {
                Detail = Level(a),
            },
            YouTubeEventTypes.MemberMilestone => new FeedItem(Platform, FeedKind.Subscription, user,
                $"{user} has been a member for {a.GetString("months")} months", e.Time)
            {
                Detail = a.GetString("message") is { Length: > 0 } m ? m : Level(a),
            },
            YouTubeEventTypes.MembershipGift => new FeedItem(Platform, FeedKind.GiftSubscription, user, $"{user} gifted {Gifts(a.GetDouble("total"))}", e.Time)
            {
                Detail = Level(a),
            },
            YouTubeEventTypes.GiftMembershipReceived => new FeedItem(Platform, FeedKind.Subscription, user,
                a.GetString("gifterName") is { Length: > 0 } gifter ? $"{user} received a membership from {gifter}" : $"{user} received a gift membership", e.Time)
            {
                Detail = Level(a),
            },
            YouTubeEventTypes.NewSubscriber => new FeedItem(Platform, FeedKind.Follow, user, $"{user} subscribed", e.Time),
            YouTubeEventTypes.BroadcastStarted => new FeedItem(Platform, FeedKind.StreamOnline, user, "Broadcast went live", e.Time)
            {
                Detail = a.GetString("broadcastTitle"),
            },
            YouTubeEventTypes.BroadcastEnded => new FeedItem(Platform, FeedKind.StreamOffline, user, "Broadcast ended", e.Time)
            {
                Detail = a.GetString("broadcastTitle"),
            },
            _ => null,
        };
    }

    private static string Level(IReadOnlyDictionary<string, object?> a) => a.GetString("levelName") is { Length: > 0 } level ? level : "";

    private static string Jewels(IReadOnlyDictionary<string, object?> a)
    {
        var n = a.GetDouble("jewels");
        return n == 1 ? "1 Jewel" : $"{n.ToString("0", CultureInfo.InvariantCulture)} Jewels";
    }

    private static string Gifts(double total) => total == 1 ? "a membership" : $"{total.ToString("0", CultureInfo.InvariantCulture)} memberships";

    private static ChatRoles Roles(IReadOnlyDictionary<string, object?> a) =>
        ChatArgs.RolesOf(a) | (a.GetBool("isVerified") ? ChatRoles.Verified : ChatRoles.None);
}
