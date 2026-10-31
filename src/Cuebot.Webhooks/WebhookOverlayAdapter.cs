using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Webhooks;

/// <summary>
/// Shop orders and donations that arrive by webhook as overlay alerts: orders as <c>merch-latest</c> (Fourthwall,
/// Shopify once paid, Ko-fi shop orders), donations as <c>tip-latest</c> (Ko-fi, Fourthwall). One per service, so the
/// alert's <c>service</c> names the shop.
/// </summary>
internal sealed class WebhookOverlayAdapter(string platform, string service, IReadOnlySet<string> orders, IReadOnlySet<string> donations)
    : IOverlayEventAdapter
{
    public static IEnumerable<WebhookOverlayAdapter> All() =>
    [
        new("Fourthwall", "fourthwall", new HashSet<string> { FourthwallEventTypes.OrderPlaced }, new HashSet<string> { FourthwallEventTypes.Donation }),
        new("Shopify", "shopify", new HashSet<string> { ShopifyEventTypes.OrderPaid }, new HashSet<string>()),
        new("Ko-fi", "kofi", new HashSet<string> { KofiEventTypes.ShopOrder }, new HashSet<string> { KofiEventTypes.Donation }),
    ];

    public string Platform => platform;
    public string Service => service;

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs) => [];

    public OverlayAlert? ToAlert(BotEvent e)
    {
        if (orders.Contains(e.Type)) return new(Merch, Event(e, "merch", Number(e.Args, "amount"), extra: Money(e, ("items", Items(e.Args)))));
        if (donations.Contains(e.Type)) return new(Tip, Event(e, "tip", Number(e.Args, "amount"), extra: Money(e)));
        return null;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Money(BotEvent e, params (string Key, string Value)[] more) =>
    [
        new("currency", Str(e.Args, "currency")),
        new("formattedAmount", Str(e.Args, "formattedAmount")),
        new("isTest", Bool(e.Args, "isTest")),
        .. more.Select(m => new KeyValuePair<string, JsonNode?>(m.Key, m.Value)),
    ];
}
