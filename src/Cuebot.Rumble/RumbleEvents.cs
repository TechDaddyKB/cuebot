using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Rumble.Chat;
using Cuebot.Rumble.LiveApi;

namespace Cuebot.Rumble;

/// <summary>
/// Builds the module's <see cref="BotEvent"/>s. Argument names follow the Twitch module (<c>user</c>,
/// <c>userName</c>, <c>userId</c>, <c>message</c>, ...); Rumble has no display names, so <c>user</c> and
/// <c>userName</c> are both the username. Amounts are numbers in dollars plus <c>amountText</c> such as "$5.00".
/// </summary>
internal static class RumbleEvents
{
    /// <summary>A message read from the live chat stream (which also carries rants).</summary>
    public static BotEvent ChatMessage(ChatStreamMessage m, string broadcaster) =>
        Chat(m.Id, m.UserName, m.UserId, m.Text, m.Badges, m.Color, m.RantCents, broadcaster);

    /// <summary>A message seen only through the Live Stream API, which reports no message or user ids.</summary>
    public static BotEvent ChatMessage(ApiChatMessage m, long rantCents, string broadcaster) =>
        Chat("", m.UserName, "", m.Text, m.Badges, null, rantCents, broadcaster);

    public static BotEvent Rant(string user, string userId, string message, long cents, string messageId = "") =>
        new(RumbleEventTypes.Rant, new Dictionary<string, object?>
        {
            ["user"] = user,
            ["userName"] = user,
            ["userId"] = userId,
            ["message"] = message,
            ["messageId"] = messageId,
            ["amount"] = Money.Dollars(cents),
            ["amountCents"] = cents,
            ["amountText"] = Money.Format(cents),
            ["currency"] = Money.Currency,
        });

    public static BotEvent Follow(ApiFollower f) =>
        new(RumbleEventTypes.Follow, new Dictionary<string, object?>
        {
            ["user"] = f.UserName,
            ["userName"] = f.UserName,
            ["followedAt"] = f.FollowedOn.ToString("O"),
        });

    public static BotEvent Subscribe(ApiSubscriber s) =>
        new(RumbleEventTypes.Subscribe, new Dictionary<string, object?>
        {
            ["user"] = s.UserName,
            ["userName"] = s.UserName,
            ["amount"] = Money.Dollars(s.AmountCents),
            ["amountCents"] = s.AmountCents,
            ["amountText"] = s.AmountCents > 0 ? Money.Format(s.AmountCents) : "",
            ["currency"] = Money.Currency,
            ["subscribedAt"] = s.SubscribedOn.ToString("O"),
        });

    public static BotEvent GiftSub(ApiGiftedSub g) =>
        new(RumbleEventTypes.GiftSub, new Dictionary<string, object?>
        {
            ["user"] = g.PurchasedBy,
            ["userName"] = g.PurchasedBy,
            ["total"] = g.TotalGifts,
            ["remaining"] = g.RemainingGifts,
            ["giftType"] = g.GiftType,
            ["videoId"] = g.VideoId,
        });

    public static BotEvent StreamOnline(ApiLivestream stream, string channel) => Stream(RumbleEventTypes.StreamOnline, stream, channel);

    public static BotEvent StreamOffline(ApiLivestream stream, string channel) => Stream(RumbleEventTypes.StreamOffline, stream, channel);

    private static BotEvent Stream(string type, ApiLivestream stream, string channel) =>
        new(type, new Dictionary<string, object?>
        {
            ["title"] = stream.Title,
            ["streamId"] = stream.Id,
            ["channel"] = channel,
            ["user"] = channel,
        });

    private static BotEvent Chat(string id, string user, string userId, string text, IReadOnlyList<string> badges, string? color,
        long rantCents, string broadcaster)
    {
        var roles = RumbleBadges.Roles(badges, user, broadcaster);
        return new BotEvent(RumbleEventTypes.ChatMessage, new Dictionary<string, object?>
        {
            ["user"] = user,
            ["userName"] = user,
            ["userId"] = userId,
            ["message"] = text,
            ["rawInput"] = text,
            ["messageId"] = id,
            ["isBroadcaster"] = roles.HasFlag(ChatRoles.Broadcaster),
            ["isModerator"] = roles.HasFlag(ChatRoles.Moderator),
            ["isVerified"] = roles.HasFlag(ChatRoles.Verified),
            ["isSubscriber"] = roles.HasFlag(ChatRoles.Subscriber),
            ["isPremium"] = RumbleBadges.Has(badges, RumbleBadges.Premium),
            ["badges"] = string.Join(",", badges),
            ["color"] = color is { Length: 6 } ? "#" + color : color ?? "",
            ["isRant"] = rantCents > 0,
            ["amount"] = Money.Dollars(rantCents),
            ["amountText"] = rantCents > 0 ? Money.Format(rantCents) : "",
        });
    }
}
