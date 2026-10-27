using System.Globalization;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Twitch;

/// <summary>Maps Twitch events onto the combined chat and activity feed.</summary>
internal sealed class TwitchChatAdapter : IChatEventAdapter
{
    public const string PlatformName = "Twitch";

    public string Platform => PlatformName;

    public ChatMessage? ToChatMessage(BotEvent e)
    {
        if (e.Type != TwitchEventTypes.ChatMessage) return null;
        var bits = Int(e, "bits");
        return new ChatMessage(Platform, Str(e, "messageId"), Str(e, "user"), Str(e, "userId"), Str(e, "message"), e.Time)
        {
            Roles = (Bool(e, "isBroadcaster") ? ChatRoles.Broadcaster : 0)
                    | (Bool(e, "isModerator") ? ChatRoles.Moderator : 0)
                    | (Bool(e, "isVip") ? ChatRoles.Vip : 0)
                    | (Bool(e, "isSubscriber") ? ChatRoles.Subscriber : 0),
            Color = Str(e, "color") is { Length: > 0 } color ? color : null,
            Amount = bits > 0 ? $"{bits} bits" : null,
        };
    }

    public FeedItem? ToFeedItem(BotEvent e)
    {
        var user = Str(e, "user");
        return e.Type switch
        {
            TwitchEventTypes.Follow => Item(e, FeedKind.Follow, user, $"{user} followed"),
            TwitchEventTypes.Sub => Bool(e, "isGift")
                ? null // the gifter's channel.subscription.gift event already covers gifted subs
                : Item(e, FeedKind.Subscription, user, $"{user} subscribed (tier {Str(e, "tier")})"),
            TwitchEventTypes.Resub => Item(e, FeedKind.Subscription, user,
                $"{user} resubscribed for {Int(e, "cumulativeMonths")} months", Str(e, "message")),
            TwitchEventTypes.GiftSub => Item(e, FeedKind.GiftSubscription, user,
                $"{(Bool(e, "isAnonymous") ? "An anonymous gifter" : user)} gifted {Int(e, "total")} tier {Str(e, "tier")} subs"),
            TwitchEventTypes.Cheer => Item(e, FeedKind.Tip, user, $"{user} cheered {Int(e, "bits")} bits", Str(e, "message"))
                with { Amount = $"{Int(e, "bits")} bits" },
            TwitchEventTypes.Raid => Item(e, FeedKind.Raid, user, $"{user} raided with {Int(e, "viewers")} viewers"),
            TwitchEventTypes.RewardRedemption => Item(e, FeedKind.Redemption, user,
                $"{user} redeemed {Str(e, "rewardTitle")}", Str(e, "userInput")),
            TwitchEventTypes.StreamOnline => Item(e, FeedKind.StreamOnline, user, "Stream went live"),
            TwitchEventTypes.StreamOffline => Item(e, FeedKind.StreamOffline, user, "Stream ended"),
            _ => null,
        };
    }

    private FeedItem Item(BotEvent e, FeedKind kind, string user, string title, string? detail = null) =>
        new(Platform, kind, user, title, e.Time) { Detail = string.IsNullOrEmpty(detail) ? null : detail };

    private static string Str(BotEvent e, string key) =>
        e.Args.GetValueOrDefault(key) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    private static int Int(BotEvent e, string key) =>
        int.TryParse(Str(e, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static bool Bool(BotEvent e, string key) => e.Args.GetValueOrDefault(key) is true;
}

/// <summary>Lets the combined chat post to Twitch.</summary>
internal sealed class TwitchChatSender(TwitchService twitch) : IChatSender
{
    public string Platform => TwitchChatAdapter.PlatformName;
    public bool CanSend => twitch.State == Cuebot.Core.ConnectionState.Connected;
    public Task SendAsync(string text, CancellationToken ct = default) => twitch.SendChatMessageAsync(text, ct);
}
