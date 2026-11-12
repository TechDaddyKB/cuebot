using System.Globalization;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Webhooks;

/// <summary>
/// Shows webhook money events in the activity feed (and so in event speech, "chat.activity", and the credits):
/// donations, tips and Throne contributions as <see cref="FeedKind.Tip"/>, memberships/pledges as
/// <see cref="FeedKind.Subscription"/>, Patreon free members as <see cref="FeedKind.Follow"/>, and shop orders as
/// <see cref="FeedKind.Other"/> (there is no purchase kind). Administrative events (product updates, newsletter,
/// deletions, custom hooks) are not shown.
/// </summary>
internal sealed class WebhookChatAdapter(string platform, string prefix) : IChatEventAdapter
{
    public string Platform => platform;

    public ChatMessage? ToChatMessage(BotEvent e) => null;

    public FeedItem? ToFeedItem(BotEvent e) => e.Type.StartsWith(prefix, StringComparison.Ordinal) ? Describe(e) : null;

    internal static IEnumerable<WebhookChatAdapter> All() =>
    [
        new("Ko-fi", "kofi."), new("Patreon", "patreon."), new("Shopify", "shopify."),
        new("Fourthwall", "fourthwall."), new("Throne", "throne."),
    ];

    /// <summary>The feed item for a webhook event, or null for events not shown in the feed.</summary>
    internal static FeedItem? Describe(BotEvent e)
    {
        var a = e.Args;
        var service = Get(a, "service");
        var user = Get(a, "user");
        var message = Get(a, "message");
        var amount = Get(a, "formattedAmount");
        var hasAmount = a.TryGetValue("amount", out var raw) && raw is decimal or double && Convert.ToDouble(raw, CultureInfo.InvariantCulture) > 0;
        var paid = hasAmount ? amount : "";

        // Ko-fi asks that private supports are not shown publicly.
        if (e.Type.StartsWith("kofi.", StringComparison.Ordinal) && a.TryGetValue("isPublic", out var pub) && pub is false)
        {
            user = "Someone";
            message = "";
        }

        FeedItem Item(FeedKind kind, string title, string? amountText = null, bool withMessage = true, string? spend = null) =>
            new(service, kind, user, Test(a) + title, e.Time)
            {
                Detail = withMessage && message.Length > 0 ? message : null,
                Amount = amountText is { Length: > 0 } ? amountText : null,
                Spend = spend is null || !hasAmount ? null : FeedSpend.Money(Convert.ToDouble(raw, CultureInfo.InvariantCulture), Get(a, "currency"), spend),
            };

        var tier = Get(a, "tier");
        var tierText = tier.Length > 0 ? $" ({tier})" : "";
        return e.Type switch
        {
            KofiEventTypes.Donation => Item(FeedKind.Tip, $"{user} donated {amount} on Ko-fi", amount, spend: "Donation"),
            KofiEventTypes.Subscription => Item(FeedKind.Subscription, $"{user} subscribed on Ko-fi{tierText}", amount, spend: "Membership"),
            KofiEventTypes.Resubscription => Item(FeedKind.Subscription, $"{user} renewed their Ko-fi membership{tierText}", amount, spend: "Membership"),
            KofiEventTypes.Commission => Item(FeedKind.Other, $"{user} ordered a commission ({amount})", amount, spend: "Commission"),
            KofiEventTypes.ShopOrder => Item(FeedKind.Other, $"{user} bought {Count(a, "itemCount")} on Ko-fi ({amount})", amount, spend: "Shop order"),

            PatreonEventTypes.PledgeCreated => Item(FeedKind.Subscription, $"{user} became a patron{tierText}", paid, withMessage: false, spend: "Pledge"),
            PatreonEventTypes.PledgeUpdated => Item(FeedKind.Other, $"{user} changed their pledge{tierText}", paid, withMessage: false),
            PatreonEventTypes.FollowCreated when a.TryGetValue("isGifted", out var gifted) && gifted is true =>
                Item(FeedKind.Subscription, $"{user} received a gifted Patreon membership{tierText}", withMessage: false),
            PatreonEventTypes.FollowCreated when a.TryGetValue("isFreeTrial", out var trial) && trial is true =>
                Item(FeedKind.Subscription, $"{user} started a Patreon free trial{tierText}", withMessage: false),
            // Patreon replaced following with free membership; the trigger keeps Streamer.bot's "Follow Created" id.
            PatreonEventTypes.FollowCreated => Item(FeedKind.Follow, $"{user} joined your Patreon as a free member", withMessage: false),

            ShopifyEventTypes.OrderCreated or ShopifyEventTypes.OrderPaid when a.TryGetValue("isRepeatOrder", out var rep) && rep is true => null,
            ShopifyEventTypes.OrderCreated or ShopifyEventTypes.OrderPaid =>
                Item(FeedKind.Other, $"{user} placed order {Get(a, "orderName")} ({amount})", amount, spend: "Shop order"),

            // Only purchases are money spent: a redeemed stream gift or giveaway link gets its own line, the creator's samples none.
            FourthwallEventTypes.OrderPlaced when !Flag(a, "isPurchase", true) =>
                Get(a, "fw.source") is "TWITCH_GIFT_REDEMPTION" or "GIVEAWAY_LINKS"
                    ? Item(FeedKind.Other, $"{user} redeemed a gift: {Get(a, "fw.items")}", withMessage: false)
                    : null,
            FourthwallEventTypes.OrderPlaced => Item(FeedKind.Other, $"{user} bought {Get(a, "fw.items")} ({amount})", amount, spend: "Shop order"),
            // OPEN, ABANDONED, CANCELLED and FAILED donations brought no money.
            FourthwallEventTypes.Donation when !Flag(a, "isCompleted", true) => null,
            FourthwallEventTypes.Donation => Item(FeedKind.Tip, $"{user} donated {amount} on Fourthwall", amount, spend: "Donation"),
            FourthwallEventTypes.SubscriptionPurchased => Item(FeedKind.Subscription, $"{user} became a member on Fourthwall{tierText}", paid, spend: "Membership"),
            FourthwallEventTypes.GiftPurchase =>
                Item(FeedKind.Other, $"{user} gifted {Get(a, "fw.quantity")} x {Get(a, "fw.offer.name")} ({amount})", amount, spend: "Gift"),

            ThroneEventTypes.GiftPurchased => Item(FeedKind.Tip, $"{user} bought {Get(a, "item")} from the Throne wishlist", paid, spend: "Wishlist gift"),
            ThroneEventTypes.ContributionPurchased => Item(FeedKind.Tip, $"{user} contributed {amount} to {Get(a, "item")}", paid, spend: "Wishlist gift"),
            ThroneEventTypes.GiftCrowdfunded => new FeedItem(service, FeedKind.Other, "", $"{Test(a)}{Get(a, "item")} was fully crowdfunded on Throne", e.Time)
            {
                Amount = paid.Length > 0 ? paid : null,
            },
            _ => null,
        };
    }

    private static string Count(IReadOnlyDictionary<string, object?> a, string key)
    {
        var text = Get(a, key);
        return text.Length == 0 ? "items" : text == "1" ? "1 item" : $"{text} items";
    }

    /// <summary>A boolean argument; <paramref name="fallback"/> when it is missing (older events, other sources).</summary>
    private static bool Flag(IReadOnlyDictionary<string, object?> a, string key, bool fallback) =>
        a.TryGetValue(key, out var value) && value is bool b ? b : fallback;

    private static string Test(IReadOnlyDictionary<string, object?> a) =>
        a.TryGetValue("isTest", out var t) && t is true ? "[Test] " : "";

    private static string Get(IReadOnlyDictionary<string, object?> a, string key) =>
        a.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
}
