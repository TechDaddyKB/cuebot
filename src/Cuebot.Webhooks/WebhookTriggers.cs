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

/// <summary>
/// Ko-fi triggers (Streamer.bot: Commission, Donation, Resubscription, Shop Order, Subscription; Cuebot adds Goal
/// Reached and Membership Lapsed). The payment triggers can be limited to public or private supports and can skip late
/// (relay-replayed) deliveries.
/// </summary>
internal sealed class KofiTriggers : ITriggerProvider
{
    public const string Category = "Ko-fi";

    internal const string AnyVisibility = "Any";
    internal const string PublicOnly = "Public only";
    internal const string PrivateOnly = "Private only";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Payment(KofiEventTypes.Donation, "Donation"),
        Payment(KofiEventTypes.Subscription, "Subscription", tiered: true),
        Payment(KofiEventTypes.Resubscription, "Resubscription", tiered: true),
        Payment(KofiEventTypes.Commission, "Commission"),
        Payment(KofiEventTypes.ShopOrder, "Shop Order"),
        WebhookFilters.Event(Category, KofiEventTypes.GoalReached, "Goal Reached"),
        WebhookFilters.Event(Category, KofiEventTypes.MembershipLapsed, "Membership Lapsed",
            [new("tier", "Tier name") { Help = "Leave empty for any tier; several with commas, e.g. Gold, Silver" }],
            (e, s) => TierMatches(e, s)),
    ];

    private static IReadOnlyList<ParameterDescriptor> Extra { get; } =
    [
        new("visibility", "Supporter's choice", ParameterKind.Choice)
        {
            DefaultValue = AnyVisibility, Choices = [AnyVisibility, PublicOnly, PrivateOnly],
            Help = "Private supports arrive as \"Anonymous\" with no message unless you turn on showing them",
        },
        new("skipLate", "Skip late deliveries", ParameterKind.Boolean)
        {
            DefaultValue = "false", Help = "Do not run for payments the relay kept while Cuebot was closed (they still count in totals and the goal)",
        },
    ];

    private static EventTriggerType Payment(string type, string name, bool tiered = false) =>
        WebhookFilters.Event(Category, type, name,
            tiered
                ? [new("tier", "Tier name") { Help = "Leave empty for any tier; several with commas, e.g. Gold, Silver" }, .. WebhookFilters.Amount, .. Extra]
                : [.. WebhookFilters.Amount, .. Extra],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && Visible(e, s) && !(s.GetBool("skipLate") && e.Args.TryGetValue("isReplay", out var r) && r is true)
                && (!tiered || TierMatches(e, s)));

    private static bool Visible(BotEvent e, ParameterValues s)
    {
        var isPublic = !(e.Args.TryGetValue("isPublic", out var p) && p is false);
        return s["visibility"] switch
        {
            PublicOnly => isPublic,
            PrivateOnly => !isPublic,
            _ => true,
        };
    }

    /// <summary>Empty matches any tier; otherwise one of the comma-separated names, ignoring case and spaces.</summary>
    internal static bool TierMatches(BotEvent e, ParameterValues s)
    {
        static string Norm(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        var wanted = s["tier"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Norm).Where(t => t.Length > 0).ToArray();
        return wanted.Length == 0 || wanted.Contains(Norm(WebhookFilters.Text(e.Args, "tier")));
    }
}

/// <summary>
/// Patreon triggers (Streamer.bot's five plus member updated and post published / updated / deleted). Each skips
/// stale deliveries (a backlog after a paused webhook) unless told otherwise; Pledge Updated can be limited to upgrades
/// or downgrades; the tier filter picks from the tiers Cuebot knows ("Gold #1234": matched by id, so renaming a tier
/// keeps the trigger working).
/// </summary>
internal sealed class PatreonTriggers(Services.PatreonTracker? tracker = null) : ITriggerProvider
{
    public const string Category = "Patreon";

    internal const string AnyChange = "Any change";
    internal const string UpgradesOnly = "Upgrades only";
    internal const string DowngradesOnly = "Downgrades only";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Plain(PatreonEventTypes.FollowCreated, "Follow Created"),
        Plain(PatreonEventTypes.FollowDeleted, "Follow Deleted"),
        Tiered(PatreonEventTypes.PledgeCreated, "Pledge Created"),
        Tiered(PatreonEventTypes.PledgeUpdated, "Pledge Updated", direction: true),
        Plain(PatreonEventTypes.PledgeDeleted, "Pledge Deleted"),
        Plain(PatreonEventTypes.MemberUpdated, "Member Updated"),
        Plain(PatreonEventTypes.PostPublished, "Post Published"),
        Plain(PatreonEventTypes.PostUpdated, "Post Updated"),
        Plain(PatreonEventTypes.PostDeleted, "Post Deleted"),
    ];

    private static readonly ParameterDescriptor SkipStale = new("skipStale", "Skip stale deliveries", ParameterKind.Boolean)
    {
        DefaultValue = "true",
        Help = "Do not run for a backlog Patreon sends after a paused webhook is resumed (see the stale setting on the Webhooks page)",
    };

    private static bool Fresh(BotEvent e, ParameterValues s) =>
        !(s.GetBool("skipStale", true) && e.Args.TryGetValue("isStale", out var stale) && stale is true);

    private static EventTriggerType Plain(string type, string name) =>
        WebhookFilters.Event(Category, type, name, [.. WebhookFilters.TestsOnly, SkipStale], (e, s) => WebhookFilters.MatchesTest(e, s) && Fresh(e, s));

    private EventTriggerType Tiered(string type, string name, bool direction = false)
    {
        ParameterDescriptor tier = new("tier", "Tier")
        {
            Help = "Leave empty for any tier; pick one (matched by its id) or type names with commas",
            Suggestions = tracker is null ? null : tracker.TierSuggestions,
        };
        ParameterDescriptor change = new("direction", "Change", ParameterKind.Choice)
        {
            DefaultValue = AnyChange, Choices = [AnyChange, UpgradesOnly, DowngradesOnly],
            Help = "Upgrades and downgrades are known from the pledge history (with the Patreon API connected) or from the member list",
        };
        return WebhookFilters.Event(Category, type, name,
            direction ? [tier, change, .. WebhookFilters.Amount, SkipStale] : [tier, .. WebhookFilters.Amount, SkipStale],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && Fresh(e, s) && TierMatches(e, s["tier"])
                && (!direction || DirectionMatches(e, s["direction"])));
    }

    private static bool DirectionMatches(BotEvent e, string setting) => setting switch
    {
        UpgradesOnly => WebhookFilters.Text(e.Args, "direction") == "upgrade",
        DowngradesOnly => WebhookFilters.Text(e.Args, "direction") == "downgrade",
        _ => true,
    };

    /// <summary>
    /// Empty matches any tier. Otherwise the whole setting may be one tier title (titles can contain commas), or a comma
    /// list of titles, tier ids or "Title #id" picks (matched by id), ignoring case and spaces.
    /// </summary>
    internal static bool TierMatches(BotEvent e, string setting)
    {
        static string Norm(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        if (setting.Trim().Length == 0) return true;
        var titles = e.Args.TryGetValue("tiers", out var list) && list is IEnumerable<string> l
            ? l.Select(Norm).ToHashSet()
            : WebhookFilters.Text(e.Args, "tier").Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(Norm).ToHashSet();
        var ids = e.Args.TryGetValue("tierIds", out var idList) && idList is IEnumerable<string> il ? il.ToHashSet(StringComparer.Ordinal) : [];
        if (titles.Contains(Norm(setting))) return true;
        foreach (var wanted in setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hash = wanted.LastIndexOf('#');
            if (hash >= 0 && wanted[(hash + 1)..].Trim() is { Length: > 0 } id && id.All(char.IsAsciiDigit))
            {
                if (ids.Contains(id)) return true;
                continue;
            }
            if (ids.Contains(wanted) || titles.Contains(Norm(wanted))) return true;
        }
        return false;
    }
}

/// <summary>
/// Shopify triggers: Streamer.bot's Order Created and Order Paid, plus Order Cancelled, Refund Created, Low Stock, Sold
/// Out, Restocked and Merch Goal Reached. The order triggers can be limited to products (name contains), SKUs and
/// discount codes; Order Created to a payment status (it also runs for orders that are never paid). Shopify sends each
/// webhook at least once and in no set order: Order Created can arrive after Order Paid, so celebrate on Order Paid.
/// </summary>
internal sealed class ShopifyTriggers : ITriggerProvider
{
    public const string Category = "Shopify";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Order(ShopifyEventTypes.OrderCreated, "Order Created",
            [new("financialStatus", "Payment status") { Help = "Leave empty for any; several with commas, e.g. paid, pending. Order Created also runs for orders that are never paid" }]),
        Order(ShopifyEventTypes.OrderPaid, "Order Paid", [SkipReplays]),
        Order(ShopifyEventTypes.OrderCancelled, "Order Cancelled", []),
        Order(ShopifyEventTypes.RefundCreated, "Refund Created", []),
        Stock(ShopifyEventTypes.LowStock, "Low Stock"),
        Stock(ShopifyEventTypes.SoldOut, "Sold Out"),
        Stock(ShopifyEventTypes.Restocked, "Restocked"),
        WebhookFilters.Event(Category, ShopifyEventTypes.GoalReached, "Merch Goal Reached"),
    ];

    private static readonly ParameterDescriptor SkipReplays = new("skipReplays", "Skip caught-up orders", ParameterKind.Boolean)
    {
        DefaultValue = "false",
        Help = "Do not run for paid orders Cuebot found later through the Shopify API because no delivery brought them (they still count in the merch counter)",
    };

    private static IReadOnlyList<ParameterDescriptor> ItemFilters { get; } =
    [
        new("product", "Product name contains") { Help = "Leave empty for any; several with commas, e.g. hoodie, tee. Runs when any item matches" },
        new("sku", "SKU") { Help = "Leave empty for any; several with commas. Runs when any item has one of them" },
        new("discountCode", "Discount code") { Help = "Leave empty for any; several with commas, e.g. your creator code" },
    ];

    private static EventTriggerType Order(string type, string name, IReadOnlyList<ParameterDescriptor> extra) =>
        WebhookFilters.Event(Category, type, name, [.. ItemFilters, .. extra, .. WebhookFilters.Amount],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && ItemsMatch(e, s)
                && ListMatches(s["discountCode"], WebhookFilters.Text(e.Args, "discountCodes").Split(',', StringSplitOptions.TrimEntries))
                && ListMatches(s["financialStatus"], [WebhookFilters.Text(e.Args, "financialStatus")])
                && !(s.GetBool("skipReplays") && e.Args.TryGetValue("isReplay", out var r) && r is true));

    private static EventTriggerType Stock(string type, string name) =>
        WebhookFilters.Event(Category, type, name,
            [
                new("product", "Product name contains") { Help = "Leave empty for any; several with commas. Product names need the Shopify API connected (otherwise use the SKU or inventory item id)" },
                new("sku", "SKU or inventory item id") { Help = "Leave empty for any; several with commas" },
                .. WebhookFilters.TestsOnly,
            ],
            (e, s) => WebhookFilters.MatchesTest(e, s) && Contains(s["product"], [WebhookFilters.Text(e.Args, "productName"), WebhookFilters.Text(e.Args, "item")])
                && ListMatches(s["sku"], [WebhookFilters.Text(e.Args, "sku"), WebhookFilters.Text(e.Args, "inventoryItemId")]));

    /// <summary>Product and SKU filters: any item of the order matches (empty settings match anything).</summary>
    internal static bool ItemsMatch(BotEvent e, ParameterValues s)
    {
        var names = new List<string>();
        var skus = new List<string>();
        for (var i = 0; e.Args.ContainsKey($"item{i}.name"); i++)
        {
            names.Add(WebhookFilters.Text(e.Args, $"item{i}.name"));
            names.Add(WebhookFilters.Text(e.Args, $"item{i}.variant"));
            skus.Add(WebhookFilters.Text(e.Args, $"item{i}.sku"));
        }
        if (names.Count == 0) names.Add(WebhookFilters.Text(e.Args, "items"));
        return Contains(s["product"], names) && ListMatches(s["sku"], skus);
    }

    private static string[] Wanted(string setting) =>
        setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Contains(string setting, IReadOnlyList<string> values)
    {
        var wanted = Wanted(setting);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => v.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ListMatches(string setting, IReadOnlyList<string> values)
    {
        var wanted = Wanted(setting);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => string.Equals(v.Trim(), w, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Fourthwall triggers: the 13 Streamer.bot documents plus Promotion Created / Updated / Status Changed and Collection
/// Updated. Donation runs for COMPLETED donations only unless told otherwise; Order Placed for purchases only (not the
/// creator's samples or redeemed gifts); Product Created / Updated for public products only. Every trigger can skip events
/// Cuebot replayed from Fourthwall's event list (isReplay). Fourthwall sends each event at least once and in no set order
/// (an Order Updated can come before its Order Placed).
/// </summary>
internal sealed class FourthwallTriggers : ITriggerProvider
{
    public const string Category = "Fourthwall";

    internal const string Any = "Any";
    internal const string Completed = "Completed";
    internal const string NotCompleted = "Not completed";
    internal const string Purchases = "Purchases";
    internal const string GiftRedemptions = "Gift redemptions";
    internal const string Samples = "Samples";
    internal const string Cancellations = "Cancellations";
    internal const string Upgrades = "Upgrades";
    internal const string Downgrades = "Downgrades";
    internal const string NotCancellations = "Not cancellations";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Event(FourthwallEventTypes.Donation, "Donation",
            [
                new("status", "Donation status", ParameterKind.Choice)
                {
                    DefaultValue = Completed, Choices = [Completed, NotCompleted, Any],
                    Help = "Fourthwall also reports donations that were started but not paid (open, abandoned, cancelled, failed); those have amount 0",
                },
                .. WebhookFilters.Amount,
            ],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && StatusMatches(e, s["status"])),
        Event(FourthwallEventTypes.GiftDrawEnded, "Gift Draw Ended", [.. WebhookFilters.TestsOnly], WebhookFilters.MatchesTest),
        Event(FourthwallEventTypes.GiftDrawStarted, "Gift Draw Started", [.. WebhookFilters.TestsOnly], WebhookFilters.MatchesTest),
        Event(FourthwallEventTypes.GiftPurchase, "Gift Purchase", [.. WebhookFilters.Amount], WebhookFilters.MatchesAmount),
        Event(FourthwallEventTypes.NewsletterSubscribed, "Newsletter Subscribed", [.. WebhookFilters.TestsOnly], WebhookFilters.MatchesTest),
        Event(FourthwallEventTypes.OrderPlaced, "Order Placed", [Source(Purchases), .. WebhookFilters.Amount],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && SourceMatches(e, s["source"])),
        Event(FourthwallEventTypes.OrderUpdated, "Order Updated",
            [
                new("updateType", "What changed", ParameterKind.Choice)
                {
                    DefaultValue = Any, Choices = [Any, "STATUS", "SHIPPING.ADDRESS", "EMAIL"],
                    Help = "STATUS: the order moved on (in production, shipped, delivered, cancelled...)",
                },
                new("orderStatus", "Order status") { Help = "Leave empty for any; several with commas, e.g. SHIPPED, CANCELLED" },
                Source(Any),
                .. WebhookFilters.TestsOnly,
            ],
            (e, s) => WebhookFilters.MatchesTest(e, s) && SourceMatches(e, s["source"])
                && (s["updateType"] is "" or Any || string.Equals(WebhookFilters.Text(e.Args, "fw.updateType"), s["updateType"], StringComparison.OrdinalIgnoreCase))
                && ListMatches(s["orderStatus"], WebhookFilters.Text(e.Args, "fw.status"))),
        Event(FourthwallEventTypes.ProductCreated, "Product Created", [PublicOnly(true), .. WebhookFilters.TestsOnly], ProductMatches),
        Event(FourthwallEventTypes.ProductUpdated, "Product Updated", [PublicOnly(true), .. WebhookFilters.TestsOnly], ProductMatches),
        Event(FourthwallEventTypes.SubscriptionChanged, "Subscription Changed",
            [
                new("change", "Change", ParameterKind.Choice)
                {
                    DefaultValue = Any, Choices = [Any, Cancellations, NotCancellations, Upgrades, Downgrades],
                    Help = "Fourthwall sends Subscription Changed when a member cancels too (state CANCELLED). Upgrades and downgrades need Cuebot to have seen the member's earlier tier (a webhook or a member sync)",
                },
                Tier,
                .. WebhookFilters.TestsOnly,
            ],
            (e, s) => WebhookFilters.MatchesTest(e, s) && ChangeMatches(e, s["change"]) && TierMatches(e, s["tier"])),
        Event(FourthwallEventTypes.SubscriptionExpired, "Subscription Expired", [.. WebhookFilters.TestsOnly], WebhookFilters.MatchesTest),
        Event(FourthwallEventTypes.SubscriptionPurchased, "Subscription Purchased", [Tier, .. WebhookFilters.Amount],
            (e, s) => WebhookFilters.MatchesAmount(e, s) && TierMatches(e, s["tier"])),
        Event(FourthwallEventTypes.ThankYouSent, "Thank You Sent",
            [
                new("contributionType", "For", ParameterKind.Choice)
                {
                    DefaultValue = Any, Choices = [Any, "ORDER", "DONATION", "GIFT_PURCHASE", "MEMBERSHIP_PAYMENT_CHARGE"],
                },
                .. WebhookFilters.TestsOnly,
            ],
            (e, s) => WebhookFilters.MatchesTest(e, s)
                && (s["contributionType"] is "" or Any || string.Equals(WebhookFilters.Text(e.Args, "fw.contributionType"), s["contributionType"], StringComparison.OrdinalIgnoreCase))),
        Event(FourthwallEventTypes.PromotionCreated, "Promotion Created", [.. WebhookFilters.TestsOnly], WebhookFilters.MatchesTest),
        Event(FourthwallEventTypes.PromotionUpdated, "Promotion Updated", [.. WebhookFilters.TestsOnly], WebhookFilters.MatchesTest),
        Event(FourthwallEventTypes.PromotionStatusChanged, "Promotion Status Changed",
            [
                new("promotionStatus", "New status", ParameterKind.Choice)
                {
                    DefaultValue = Any, Choices = [Any, "Live", "Ended", "AllUsed", "Archived"], Help = "AllUsed: every use of the code is taken",
                },
                .. WebhookFilters.TestsOnly,
            ],
            (e, s) => WebhookFilters.MatchesTest(e, s) && (s["promotionStatus"] is "" or Any
                || string.Equals(WebhookFilters.Text(e.Args, "fw.status"), s["promotionStatus"], StringComparison.OrdinalIgnoreCase))),
        Event(FourthwallEventTypes.CollectionUpdated, "Collection Updated", [PublicOnly(false), .. WebhookFilters.TestsOnly], ProductMatches),
    ];

    private static readonly ParameterDescriptor SkipReplays = new("skipReplays", "Skip caught-up events", ParameterKind.Boolean)
    {
        DefaultValue = "false",
        Help = "Do not run for events Cuebot found later in Fourthwall's event list because no delivery brought them (they still count in totals)",
    };

    private static readonly ParameterDescriptor Tier = new("tier", "Tier name")
    {
        Help = "Leave empty for any tier; several with commas. Tier names need the Fourthwall API connected (otherwise use the tier id)",
    };

    private static ParameterDescriptor Source(string fallback) => new("source", "Order kind", ParameterKind.Choice)
    {
        DefaultValue = fallback, Choices = [Purchases, GiftRedemptions, Samples, Any],
        Help = "Purchases: real orders. Gift redemptions: a viewer claiming a stream gift or giveaway link. Samples: your own samples orders",
    };

    private static ParameterDescriptor PublicOnly(bool on) => new("publicOnly", "Only public ones", ParameterKind.Boolean)
    {
        DefaultValue = on ? "true" : "false",
        Help = "Skip hidden, private and archived ones, so an announcement never shows something you have not released",
    };

    private static EventTriggerType Event(string type, string name, IReadOnlyList<ParameterDescriptor> parameters, Func<BotEvent, ParameterValues, bool> filter) =>
        WebhookFilters.Event(Category, type, name, [.. parameters, SkipReplays],
            (e, s) => filter(e, s) && !(s.GetBool("skipReplays") && e.Args.TryGetValue("isReplay", out var r) && r is true));

    private static bool Flag(BotEvent e, string key, bool fallback) => e.Args.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    internal static bool StatusMatches(BotEvent e, string setting) => setting switch
    {
        Any => true,
        NotCompleted => !Flag(e, "isCompleted", true),
        _ => Flag(e, "isCompleted", true),
    };

    internal static bool SourceMatches(BotEvent e, string setting)
    {
        var source = WebhookFilters.Text(e.Args, "fw.source");
        return setting switch
        {
            Purchases => Services.FourthwallWebhookHandler.IsPurchaseSource(source),
            GiftRedemptions => source is "TWITCH_GIFT_REDEMPTION" or "GIVEAWAY_LINKS",
            Samples => source == "SAMPLES_ORDER",
            _ => true,
        };
    }

    private static bool ProductMatches(BotEvent e, ParameterValues s) =>
        WebhookFilters.MatchesTest(e, s) && (!s.GetBool("publicOnly") || Flag(e, "fw.isPublic", true));

    internal static bool ChangeMatches(BotEvent e, string setting)
    {
        var cancelled = Flag(e, "fw.isCancelled", false);
        var direction = WebhookFilters.Text(e.Args, "direction");
        return setting switch
        {
            Cancellations => cancelled,
            NotCancellations => !cancelled,
            Upgrades => !cancelled && direction == "upgrade",
            Downgrades => !cancelled && direction == "downgrade",
            _ => true,
        };
    }

    /// <summary>Empty matches any; otherwise one of the comma-separated tier names or ids, ignoring case and spaces.</summary>
    internal static bool TierMatches(BotEvent e, string setting)
    {
        static string Norm(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        var wanted = setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Norm).Where(t => t.Length > 0).ToArray();
        return wanted.Length == 0 || wanted.Contains(Norm(WebhookFilters.Text(e.Args, "tier"))) || wanted.Contains(Norm(WebhookFilters.Text(e.Args, "fw.tierId")));
    }

    private static bool ListMatches(string setting, string value)
    {
        var wanted = setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase));
    }
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
