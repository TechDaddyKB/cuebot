using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Donations.Triggers;

/// <summary>Trigger filters shared by both services.</summary>
internal static class DonationFilters
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

    public static bool AtLeast(BotEvent e, ParameterValues s, string arg, string setting) =>
        MatchesTest(e, s) && Number(e.Args, arg) >= s.GetDouble(setting);

    private static double Number(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? v switch
        {
            double d => d,
            int i => i,
            long l => l,
            string str => JsonReaders.ParseNumber(str),
            _ => 0,
        } : 0;

    private static string Text(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>Streamlabs triggers (category "Streamlabs").</summary>
internal sealed class StreamlabsTriggers : ITriggerProvider
{
    public const string Category = "Streamlabs";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Event(StreamlabsEventTypes.Connected, "Connected"),
        Event(StreamlabsEventTypes.Disconnected, "Disconnected"),
        Event(StreamlabsEventTypes.Donation, "Donation", DonationFilters.Amount, DonationFilters.MatchesAmount),
        Event(StreamlabsEventTypes.CharityDonation, "Charity donation", DonationFilters.Amount, DonationFilters.MatchesAmount),
        Event(StreamlabsEventTypes.Merch, "Merchandise", DonationFilters.TestsOnly, DonationFilters.MatchesTest),
        Event(StreamlabsEventTypes.LoyaltyStoreRedemption, "Loyalty store redemption", DonationFilters.TestsOnly, DonationFilters.MatchesTest),
        Event(StreamlabsEventTypes.PrimeSubGift, "Prime gift", DonationFilters.TestsOnly, DonationFilters.MatchesTest),
        Event(StreamlabsEventTypes.Follow, "Relayed follow"),
        Event(StreamlabsEventTypes.Subscription, "Relayed subscription"),
        Event(StreamlabsEventTypes.Bits, "Relayed bits",
            [new("minBits", "Minimum bits", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => DonationFilters.AtLeast(e, s, "bits", "minBits")),
        Event(StreamlabsEventTypes.Raid, "Relayed raid",
            [new("minViewers", "Minimum viewers", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => DonationFilters.AtLeast(e, s, "viewers", "minViewers")),
        Event(StreamlabsEventTypes.Host, "Relayed host"),
        Event(StreamlabsEventTypes.Superchat, "Relayed Super Chat", DonationFilters.Amount, DonationFilters.MatchesAmount),
    ];

    internal static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        Build(Category, eventType, displayName, parameters, filter);

    internal static EventTriggerType Build(string category, string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters, Func<BotEvent, ParameterValues, bool>? filter) =>
        new(eventType, category, displayName, eventType, parameters, filter is null ? null : (e, settings, _) => filter(e, settings));
}

/// <summary>StreamElements triggers (category "StreamElements").</summary>
internal sealed class StreamElementsTriggers : ITriggerProvider
{
    public const string Category = "StreamElements";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Event(StreamElementsEventTypes.Connected, "Connected"),
        Event(StreamElementsEventTypes.Disconnected, "Disconnected"),
        Event(StreamElementsEventTypes.Tip, "Tip", DonationFilters.Amount, DonationFilters.MatchesAmount),
        Event(StreamElementsEventTypes.Merch, "Merch", DonationFilters.Amount, DonationFilters.MatchesAmount),
        Event(StreamElementsEventTypes.Redemption, "Store redemption",
            [new("item", "Item name") { Help = "Leave empty for any item" }, .. DonationFilters.TestsOnly],
            (e, s) => DonationFilters.MatchesTest(e, s) && (s["item"].Length == 0
                || string.Equals(Convert.ToString(e.Args.GetValueOrDefault("item"), CultureInfo.InvariantCulture), s["item"], StringComparison.OrdinalIgnoreCase))),
        Event(StreamElementsEventTypes.Follow, "Relayed follow"),
        Event(StreamElementsEventTypes.Subscriber, "Relayed subscriber"),
        Event(StreamElementsEventTypes.Cheer, "Relayed cheer",
            [new("minBits", "Minimum bits", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => DonationFilters.AtLeast(e, s, "bits", "minBits")),
        Event(StreamElementsEventTypes.Raid, "Relayed raid",
            [new("minViewers", "Minimum viewers", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => DonationFilters.AtLeast(e, s, "viewers", "minViewers")),
        Event(StreamElementsEventTypes.Host, "Relayed host"),
        Event(StreamElementsEventTypes.Superchat, "Relayed Super Chat", DonationFilters.Amount, DonationFilters.MatchesAmount),
        Event(StreamElementsEventTypes.Sponsor, "Relayed YouTube membership"),
        Event(StreamElementsEventTypes.CommunityGift, "Relayed community gift"),
        Event(StreamElementsEventTypes.CharityDonation, "Relayed charity donation", DonationFilters.Amount, DonationFilters.MatchesAmount),
        Event(StreamElementsEventTypes.Activity, "Other relayed activity",
            [new("activityType", "Activity type") { Help = "e.g. hypetrainStart; leave empty for any" }],
            (e, s) => s["activityType"].Length == 0
                || string.Equals(Convert.ToString(e.Args.GetValueOrDefault("activityType"), CultureInfo.InvariantCulture), s["activityType"], StringComparison.OrdinalIgnoreCase)),
    ];

    private static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        StreamlabsTriggers.Build(Category, eventType, displayName, parameters, filter);
}
