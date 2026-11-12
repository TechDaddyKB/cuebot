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
