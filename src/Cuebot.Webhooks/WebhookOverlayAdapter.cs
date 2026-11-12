using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Webhooks;

/// <summary>
/// Shop orders, donations and memberships that arrive by webhook as overlay alerts: orders as <c>merch-latest</c>
/// (Fourthwall purchases and stream gift purchases with <c>gift: true</c>, Shopify once paid, Ko-fi shop orders and
/// commissions), donations as <c>tip-latest</c> (Ko-fi, Fourthwall once COMPLETED), Ko-fi memberships as <c>subscriber-latest</c> (amount = payments Cuebot has seen, so a renewal counts as
/// a resub). Patreon: a new paying pledge is <c>subscriber-latest</c> (tier name, monthly price, currency), an upgrade
/// too (with <c>upgrade: true</c> and the previous tier and amount; a downgrade or an unknown direction is no alert),
/// and a free member joining (or a gifted / free-trial membership, which gets no pledge event) is <c>follower-latest</c>.
/// A paying patron's members:create is already ignored by PatreonWebhookHandler, so a join alerts once.
/// One per service, so the alert's <c>service</c> names the shop. A Ko-fi support marked private already
/// arrives as "Anonymous" with no message (see KofiWebhookHandler), and a Patreon patron who hides their identity as
/// "An anonymous patron" with no picture (their name stays in <c>privateName</c>, which never goes to overlays).
/// Late deliveries and catch-up finds (<c>isReplay</c>) and Patreon's stale backlog (<c>isStale</c>) are catch-up alerts.
/// </summary>
internal sealed class WebhookOverlayAdapter(string platform, string service, IReadOnlySet<string> orders, IReadOnlySet<string> donations,
    IReadOnlySet<string>? memberships = null)
    : IOverlayEventAdapter
{
    public static IEnumerable<WebhookOverlayAdapter> All() =>
    [
        new("Fourthwall", "fourthwall", new HashSet<string> { FourthwallEventTypes.OrderPlaced, FourthwallEventTypes.GiftPurchase },
            new HashSet<string> { FourthwallEventTypes.Donation }),
        new("Shopify", "shopify", new HashSet<string> { ShopifyEventTypes.OrderPaid }, new HashSet<string>()),
        new("Ko-fi", "kofi", new HashSet<string> { KofiEventTypes.ShopOrder, KofiEventTypes.Commission }, new HashSet<string> { KofiEventTypes.Donation },
            new HashSet<string> { KofiEventTypes.Subscription, KofiEventTypes.Resubscription }),
        new("Patreon", "patreon", new HashSet<string>(), new HashSet<string>()),
    ];

    public string Platform => platform;
    public string Service => service;

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs) => [];

    public bool IsCatchUp(BotEvent e) => Bool(e.Args, "isReplay") || Bool(e.Args, "isStale");

    public OverlayAlert? ToAlert(BotEvent e)
    {
        if (service == "patreon") return Patreon(e);
        // Only the Ko-fi adapter: the Fourthwall one comes first in the bridge and used to claim commissions as "fourthwall".
        if (e.Type == KofiEventTypes.Commission && service == "kofi") return new(Merch, Event(e, "merch", Number(e.Args, "amount"), extra: Money(e, ("items", "Commission"))));
        // Fourthwall: only a purchase (not samples or a redeemed gift) and only a COMPLETED donation is an alert.
        if (e.Type == FourthwallEventTypes.OrderPlaced && e.Args.TryGetValue("isPurchase", out var purchase) && purchase is false) return null;
        if (e.Type == FourthwallEventTypes.Donation && e.Args.TryGetValue("isCompleted", out var completed) && completed is false) return null;
        if (e.Type == FourthwallEventTypes.GiftPurchase)
        {
            // Stream gifts bought for viewers: a merch alert with gift = true and how many.
            var quantity = Number(e.Args, "fw.quantity");
            var what = $"{quantity:0} x {Str(e.Args, "fw.offer.name")} (gift)";
            return new(Merch, Event(e, "merch", Number(e.Args, "amount"), extra:
                Money(e, ("items", what)).Append(new("gift", true)).Append(new("quantity", quantity))));
        }
        if (orders.Contains(e.Type)) return new(Merch, Event(e, "merch", Number(e.Args, "amount"), extra: Money(e, ("items", Items(e.Args)))));
        if (donations.Contains(e.Type)) return new(Tip, Event(e, "tip", Number(e.Args, "amount"), extra: Money(e)));
        if (memberships?.Contains(e.Type) == true)
        {
            var months = Math.Max(1, Number(e.Args, "months"));
            if (e.Type == KofiEventTypes.Resubscription) months = Math.Max(2, months);
            return new(Subscriber, Event(e, "subscriber", months, extra:
            [
                new("tier", "1000"), new("gifted", false), new("tierName", Str(e.Args, "tier")), new("price", Number(e.Args, "amount")),
                .. Money(e),
            ]));
        }
        return null;
    }

    private static OverlayAlert? Patreon(BotEvent e)
    {
        // Patreon sends no message; the picture is empty for a patron who hides their identity.
        var who = new KeyValuePair<string, JsonNode?>[]
        {
            new("avatar", Str(e.Args, "avatarUrl")), new("providerId", Bool(e.Args, "isAnonymous") ? "" : Str(e.Args, "user.id")), new("message", ""),
        };
        switch (e.Type)
        {
            case PatreonEventTypes.FollowCreated:
                return new(Follower, Event(e, "follower", extra: [.. who, new("isGifted", Bool(e.Args, "isGifted")), new("isFreeTrial", Bool(e.Args, "isFreeTrial"))]));
            case PatreonEventTypes.PledgeCreated:
                return new(Subscriber, Event(e, "subscriber", 1, extra: [.. who, .. Membership(e)]));
            case PatreonEventTypes.PledgeUpdated when Str(e.Args, "direction") == "upgrade":
                return new(Subscriber, Event(e, "subscriber", 1, extra:
                [
                    .. who, .. Membership(e), new("upgrade", true), new("previousTier", Str(e.Args, "previousTier")),
                    new("previousAmount", OverlayAlerts.ToNode(Number(e.Args, "previousAmount"))),
                ]));
            default:
                return null;
        }
    }

    /// <summary>Patreon's tier and monthly price: <c>amount</c> stays 1 (Patreon sends no renewal, so there is no month count).</summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Membership(BotEvent e) =>
    [
        new("tier", "1000"), new("gifted", false), new("tierName", Str(e.Args, "tier")), new("price", OverlayAlerts.ToNode(Number(e.Args, "amount"))),
        .. Money(e),
    ];

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Money(BotEvent e, params (string Key, string Value)[] more) =>
    [
        new("currency", Str(e.Args, "currency")),
        new("formattedAmount", Str(e.Args, "formattedAmount")),
        new("isTest", Bool(e.Args, "isTest")),
        new("isReplay", Bool(e.Args, "isReplay")),
        .. more.Select(m => new KeyValuePair<string, JsonNode?>(m.Key, m.Value)),
    ];
}
