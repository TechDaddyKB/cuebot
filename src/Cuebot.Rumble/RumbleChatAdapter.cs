using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Rumble;

/// <summary>Shows Rumble chat in the combined chat and Rumble activity (rants, follows, ...) in the event feed.</summary>
internal sealed class RumbleChatAdapter : IChatEventAdapter
{
    public const string PlatformName = "Rumble";

    public string Platform => PlatformName;

    public ChatMessage? ToChatMessage(BotEvent e)
    {
        if (e.Type != RumbleEventTypes.ChatMessage) return null;
        var a = e.Args;
        var id = a.GetString("messageId");
        var color = a.GetString("color");
        return new ChatMessage(Platform, id.Length > 0 ? id : Guid.NewGuid().ToString("N"), a.GetString("user"), a.GetString("userId"),
            a.GetString("message"), e.Time)
        {
            Roles = Roles(a),
            Color = color.Length > 0 ? color : null,
            Amount = a.GetBool("isRant") ? a.GetString("amountText") : null,
        };
    }

    public FeedItem? ToFeedItem(BotEvent e)
    {
        var a = e.Args;
        var user = a.GetString("user");
        return e.Type switch
        {
            RumbleEventTypes.Rant => new FeedItem(Platform, FeedKind.Tip, user, $"{user} sent a {a.GetString("amountText")} rant", e.Time)
            {
                Detail = a.GetString("message"),
                Amount = a.GetString("amountText"),
            },
            RumbleEventTypes.Follow => new FeedItem(Platform, FeedKind.Follow, user, $"{user} followed", e.Time),
            RumbleEventTypes.Subscribe => new FeedItem(Platform, FeedKind.Subscription, user, $"{user} subscribed", e.Time)
            {
                Amount = a.GetString("amountText") is { Length: > 0 } amount ? amount : null,
            },
            RumbleEventTypes.GiftSub => new FeedItem(Platform, FeedKind.GiftSubscription, user, $"{user} gifted {Gifts(a.GetDouble("total"))}", e.Time),
            RumbleEventTypes.StreamOnline => new FeedItem(Platform, FeedKind.StreamOnline, user, "Stream went live", e.Time)
            {
                Detail = a.GetString("title"),
            },
            RumbleEventTypes.StreamOffline => new FeedItem(Platform, FeedKind.StreamOffline, user, "Stream ended", e.Time),
            _ => null,
        };
    }

    private static string Gifts(double total) => total == 1 ? "a subscription" : $"{total:0} subscriptions";

    private static ChatRoles Roles(IReadOnlyDictionary<string, object?> a) =>
        (a.GetBool("isBroadcaster") ? ChatRoles.Broadcaster : ChatRoles.None)
        | (a.GetBool("isModerator") ? ChatRoles.Moderator : ChatRoles.None)
        | (a.GetBool("isSubscriber") ? ChatRoles.Subscriber : ChatRoles.None)
        | (a.GetBool("isVerified") ? ChatRoles.Verified : ChatRoles.None);
}
