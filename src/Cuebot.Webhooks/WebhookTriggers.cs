using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Webhooks;

/// <summary>Trigger filters shared by the webhook services.</summary>
internal static class WebhookFilters
{
    public static IReadOnlyList<ParameterDescriptor> Amount { get; } =
    [
        new("minAmount", "Minimum amount", ParameterKind.Number) { DefaultValue = "0" },
        new("maxAmount", "Maximum amount (0 = no limit)", ParameterKind.Number) { DefaultValue = "0" },
        new("currency", "Currencies") { Help = "Comma-separated currency codes, e.g. USD, EUR. Leave empty for any currency." },
        new("includeTests", "Include test events", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public static IReadOnlyList<ParameterDescriptor> TestsOnly { get; } =
        [new("includeTests", "Include test events", ParameterKind.Boolean) { DefaultValue = "true" }];

    public static bool MatchesAmount(BotEvent e, ParameterValues s)
    {
        if (!MatchesTest(e, s)) return false;
        var amount = Number(e.Args, "amount");
        var max = s.GetDouble("maxAmount");
        return amount >= s.GetDouble("minAmount")
            && (max <= 0 || amount <= max)
            && Money.CurrencyAllowed(s["currency"], Text(e.Args, "currency"));
    }

    public static bool MatchesTest(BotEvent e, ParameterValues s) =>
        s.GetBool("includeTests", true) || !(e.Args.TryGetValue("isTest", out var t) && t is true);

    /// <summary>Empty setting matches anything; otherwise a case-insensitive comparison with the argument.</summary>
    public static bool TextIs(BotEvent e, ParameterValues s, string setting, string arg) =>
        s[setting].Trim().Length == 0 || string.Equals(Text(e.Args, arg), s[setting].Trim(), StringComparison.OrdinalIgnoreCase);

    internal static double Number(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? v switch
        {
            double d => d,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string str => JsonReaders.ParseNumber(str),
            _ => 0,
        } : 0;

    internal static string Text(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    internal static EventTriggerType Event(string category, string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, category, displayName, eventType, parameters, filter is null ? null : (e, settings, _) => filter(e, settings));

    internal static EventTriggerType Paid(string category, string eventType, string displayName) =>
        Event(category, eventType, displayName, Amount, MatchesAmount);

    internal static EventTriggerType Plain(string category, string eventType, string displayName) =>
        Event(category, eventType, displayName, TestsOnly, MatchesTest);
}

/// <summary>Ko-fi triggers (Streamer.bot: Commission, Donation, Resubscription, Shop Order, Subscription).</summary>
internal sealed class KofiTriggers : ITriggerProvider
{
    public const string Category = "Ko-fi";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        WebhookFilters.Paid(Category, KofiEventTypes.Donation, "Donation"),
        Tiered(KofiEventTypes.Subscription, "Subscription"),
        Tiered(KofiEventTypes.Resubscription, "Resubscription"),
        WebhookFilters.Paid(Category, KofiEventTypes.Commission, "Commission"),
        WebhookFilters.Paid(Category, KofiEventTypes.ShopOrder, "Shop Order"),
    ];

    private static EventTriggerType Tiered(string type, string name) =>
        WebhookFilters.Event(Category, type, name,
            [new("tier", "Tier name") { Help = "Leave empty for any tier" }, .. WebhookFilters.Amount],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && WebhookFilters.TextIs(e, s, "tier", "tier"));
}

/// <summary>Patreon triggers (Streamer.bot's five plus member updated and post published).</summary>
internal sealed class PatreonTriggers : ITriggerProvider
{
    public const string Category = "Patreon";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        WebhookFilters.Plain(Category, PatreonEventTypes.FollowCreated, "Follow Created"),
        WebhookFilters.Plain(Category, PatreonEventTypes.FollowDeleted, "Follow Deleted"),
        Tiered(PatreonEventTypes.PledgeCreated, "Pledge Created"),
        Tiered(PatreonEventTypes.PledgeUpdated, "Pledge Updated"),
        WebhookFilters.Plain(Category, PatreonEventTypes.PledgeDeleted, "Pledge Deleted"),
        WebhookFilters.Plain(Category, PatreonEventTypes.MemberUpdated, "Member Updated"),
        WebhookFilters.Plain(Category, PatreonEventTypes.PostPublished, "Post Published"),
    ];

    private static EventTriggerType Tiered(string type, string name) =>
        WebhookFilters.Event(Category, type, name,
            [new("tier", "Tier name") { Help = "Leave empty for any tier" }, .. WebhookFilters.Amount],
            (e, s) => WebhookFilters.MatchesAmount(e, s)
                && (s["tier"].Trim().Length == 0 || WebhookFilters.Text(e.Args, "tier").Split(", ").Contains(s["tier"].Trim(), StringComparer.OrdinalIgnoreCase)));
}

/// <summary>Shopify triggers (Order Created, Order Paid).</summary>
internal sealed class ShopifyTriggers : ITriggerProvider
{
    public const string Category = "Shopify";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        WebhookFilters.Paid(Category, ShopifyEventTypes.OrderCreated, "Order Created"),
        WebhookFilters.Paid(Category, ShopifyEventTypes.OrderPaid, "Order Paid"),
    ];
}

/// <summary>Fourthwall triggers (all 13 Streamer.bot documents).</summary>
internal sealed class FourthwallTriggers : ITriggerProvider
{
    public const string Category = "Fourthwall";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        WebhookFilters.Paid(Category, FourthwallEventTypes.Donation, "Donation"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.GiftDrawEnded, "Gift Draw Ended"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.GiftDrawStarted, "Gift Draw Started"),
        WebhookFilters.Paid(Category, FourthwallEventTypes.GiftPurchase, "Gift Purchase"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.NewsletterSubscribed, "Newsletter Subscribed"),
        WebhookFilters.Paid(Category, FourthwallEventTypes.OrderPlaced, "Order Placed"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.OrderUpdated, "Order Updated"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.ProductCreated, "Product Created"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.ProductUpdated, "Product Updated"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.SubscriptionChanged, "Subscription Changed"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.SubscriptionExpired, "Subscription Expired"),
        WebhookFilters.Paid(Category, FourthwallEventTypes.SubscriptionPurchased, "Subscription Purchased"),
        WebhookFilters.Plain(Category, FourthwallEventTypes.ThankYouSent, "Thank You Sent"),
    ];
}

/// <summary>Throne triggers (Contribution Purchased, Gift Crowdfunded, Gift Purchased).</summary>
internal sealed class ThroneTriggers : ITriggerProvider
{
    public const string Category = "Throne";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        WebhookFilters.Paid(Category, ThroneEventTypes.ContributionPurchased, "Contribution Purchased"),
        WebhookFilters.Plain(Category, ThroneEventTypes.GiftCrowdfunded, "Gift Crowdfunded"),
        WebhookFilters.Paid(Category, ThroneEventTypes.GiftPurchased, "Gift Purchased"),
    ];
}

/// <summary>Custom webhook trigger, optionally limited to one hook.</summary>
internal sealed class CustomWebhookTriggers : ITriggerProvider
{
    public const string Category = "Custom Webhooks";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        WebhookFilters.Event(Category, CustomWebhookEventTypes.Received, "Custom Webhook",
            [new("hookName", "Hook name") { Help = "Leave empty for every custom hook" }, .. WebhookFilters.TestsOnly],
            (e, s) => WebhookFilters.MatchesTest(e, s) && WebhookFilters.TextIs(e, s, "hookName", "hookName")),
    ];
}
