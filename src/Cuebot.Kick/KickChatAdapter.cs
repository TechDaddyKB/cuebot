using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Kick.Events;

namespace Cuebot.Kick;

/// <summary>Shows Kick chat in the combined chat and Kick activity (follows, subs, gifts, Kicks, hosts...) in the feed.</summary>
internal sealed class KickChatAdapter : IChatEventAdapter
{
    public const string PlatformName = KickArgs.Platform;

    public string Platform => PlatformName;

    public ChatMessage? ToChatMessage(BotEvent e)
    {
        if (e.Type != KickEventTypes.ChatMessage) return null;
        var a = e.Args;
        var id = a.GetString("messageId");
        var color = a.GetString("color");
        return new ChatMessage(Platform, id.Length > 0 ? id : Guid.NewGuid().ToString("N"), a.GetString("user"), a.GetString("userId"),
            a.GetString("message"), e.Time)
        {
            Roles = Roles(a),
            Color = color.Length > 0 ? color : null,
        };
    }

    public FeedItem? ToFeedItem(BotEvent e)
    {
        var a = e.Args;
        var user = a.GetString("user");
        return e.Type switch
        {
            KickEventTypes.Follow => new FeedItem(Platform, FeedKind.Follow, user, $"{user} followed", e.Time),
            KickEventTypes.Subscription => new FeedItem(Platform, FeedKind.Subscription, user, $"{user} subscribed", e.Time),
            KickEventTypes.Resubscription => new FeedItem(Platform, FeedKind.Subscription, user,
                $"{user} resubscribed ({a.GetDouble("months"):0} months)", e.Time),
            KickEventTypes.GiftSubscription when !a.GetBool("fromMassGift") => new FeedItem(Platform, FeedKind.GiftSubscription, user,
                $"{user} gifted a subscription to {a.GetString("recipient")}", e.Time),
            KickEventTypes.MassGiftSubscription => new FeedItem(Platform, FeedKind.GiftSubscription, user,
                $"{user} gifted {a.GetDouble("gifts"):0} subscriptions", e.Time) { Detail = a.GetString("recipients") },
            KickEventTypes.KicksGifted => new FeedItem(Platform, FeedKind.Tip, user, $"{user} sent {a.GetDouble("kicks"):0} Kicks", e.Time)
            {
                Detail = a.GetString("message") is { Length: > 0 } m ? m : a.GetString("giftName"),
                Amount = $"{a.GetDouble("kicks"):0} Kicks",
            },
            KickEventTypes.Host => new FeedItem(Platform, FeedKind.Raid, user, $"{user} is hosting with {a.GetDouble("viewers"):0} viewers", e.Time)
            {
                Detail = a.GetString("message") is { Length: > 0 } m ? m : null,
            },
            KickEventTypes.RewardRedemption => new FeedItem(Platform, FeedKind.Redemption, user, $"{user} redeemed {a.GetString("rewardTitle")}", e.Time)
            {
                Detail = a.GetString("userInput") is { Length: > 0 } input ? input : null,
            },
            KickEventTypes.StreamOnline => new FeedItem(Platform, FeedKind.StreamOnline, "", "Stream went live", e.Time) { Detail = a.GetString("title") },
            KickEventTypes.StreamOffline => new FeedItem(Platform, FeedKind.StreamOffline, "", "Stream ended", e.Time),
            _ => null,
        };
    }

    private static ChatRoles Roles(IReadOnlyDictionary<string, object?> a) =>
        (a.GetBool("isBroadcaster") ? ChatRoles.Broadcaster : ChatRoles.None)
        | (a.GetBool("isModerator") ? ChatRoles.Moderator : ChatRoles.None)
        | (a.GetBool("isVip") ? ChatRoles.Vip : ChatRoles.None)
        | (a.GetBool("isSubscriber") ? ChatRoles.Subscriber : ChatRoles.None)
        | (a.GetBool("isVerified") ? ChatRoles.Verified : ChatRoles.None);
}

/// <summary>Lets the combined chat send to Kick while the broadcaster is signed in.</summary>
internal sealed class KickChatSender(KickService kick) : IChatSender
{
    public string Platform => KickChatAdapter.PlatformName;

    public bool CanSend => kick.IsBroadcasterSignedIn && kick.Settings.Broadcaster.UserId > 0;

    public Task SendAsync(string text, CancellationToken ct = default) => kick.SendChatMessageAsync(text, ct: ct);
}
