using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Integrations.Common;
using E = Cuebot.Integrations.StreamLoots.StreamLootsEvents;

namespace Cuebot.Integrations.StreamLoots;

/// <summary>StreamLoots triggers (category "StreamLoots"), named as in Streamer.bot plus an "Other alert" catch-all.</summary>
internal sealed class StreamLootsTriggers : ITriggerProvider
{
    public const string Category = "StreamLoots";

    private static readonly string[] Rarities = ["Any", "common", "rare", "epic", "legendary"];

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Steps.Trigger(Category, E.Connected, "Connected"),
        Steps.Trigger(Category, E.Disconnected, "Disconnected"),
        Steps.Trigger(Category, E.PackPurchased, "Pack purchased", [MinQuantity()], (e, s) => QuantityAtLeast(e, s)),
        Steps.Trigger(Category, E.PackGifted, "Pack gifted",
            [MinQuantity(), new("includeCommunity", "Include community gifts (random viewers)", ParameterKind.Boolean) { DefaultValue = "true" }],
            (e, s) => QuantityAtLeast(e, s) && (s.GetBool("includeCommunity", true) || e.Flag("communityGift") != true)),
        Steps.Trigger(Category, E.CardRedeemed, "Card redeemed",
            [
                new("cardName", "Card names") { Help = "Comma-separated; leave empty for any card" },
                new("cardRarity", "Rarity", ParameterKind.Choice) { DefaultValue = "Any", Choices = Rarities },
            ],
            (e, s) => Steps.MatchesList(s["cardName"], e.Text("cardName"))
                && (s["cardRarity"] is "Any" or "" || string.Equals(s["cardRarity"], e.Text("cardRarity"), StringComparison.OrdinalIgnoreCase))),
        Steps.Trigger(Category, E.OtherAlert, "Other alert (e.g. reactions)",
            [new("alertType", "Alert types") { Help = "StreamLoots' type names, comma-separated; leave empty for any" }],
            (e, s) => Steps.MatchesList(s["alertType"], e.Text("alertType"))),
    ];

    private static ParameterDescriptor MinQuantity() =>
        new("minQuantity", "Minimum chests", ParameterKind.Number) { DefaultValue = "1" };

    private static bool QuantityAtLeast(BotEvent e, ParameterValues s) =>
        (e.Args.TryGetValue("quantity", out var q) && q is int n ? n : 0) >= s.GetDouble("minQuantity", 1);
}

/// <summary>
/// Shows StreamLoots alerts in the activity feed. Card redemptions are <see cref="FeedKind.Redemption"/> (so they reach the
/// credits' redemptions list); chest purchases and gifts are <see cref="FeedKind.Other"/> because the stream carries a chest
/// count, not a money amount.
/// </summary>
internal sealed class StreamLootsFeedAdapter : IChatEventAdapter
{
    public string Platform => "StreamLoots";

    public ChatMessage? ToChatMessage(BotEvent e) => null;

    public FeedItem? ToFeedItem(BotEvent e) => Describe(e);

    internal static FeedItem? Describe(BotEvent e)
    {
        var user = e.Text("user") is { Length: > 0 } u ? u : "Someone";
        var test = e.Flag("isTest") == true ? "[Test] " : "";
        var quantity = e.Args.TryGetValue("quantity", out var q) ? Convert.ToString(q, CultureInfo.InvariantCulture) : "";
        var chests = quantity == "1" ? "1 chest" : $"{quantity} chests";
        FeedItem Item(FeedKind kind, string title, string? amount = null) => new("StreamLoots", kind, user, test + title, e.Time)
        {
            Detail = e.Text("message") is { Length: > 0 } m ? m : null,
            Amount = amount,
        };
        return e.Type switch
        {
            E.PackPurchased => Item(FeedKind.Other, $"{user} bought {chests}", chests),
            E.PackGifted when e.Flag("communityGift") == true => Item(FeedKind.Other, $"{user} gifted {chests} to the community", chests),
            E.PackGifted => Item(FeedKind.Other, $"{user} gifted {chests} to {e.Text("giftee")}", chests),
            E.CardRedeemed => Item(FeedKind.Redemption, $"{user} redeemed {e.Text("cardName")}"),
            E.OtherAlert => Item(FeedKind.Other, e.Text("alertMessage") is { Length: > 0 } a ? a : $"StreamLoots {e.Text("alertType")}"),
            _ => null,
        };
    }
}

/// <summary>"Send test event" (category "StreamLoots"): publishes a sample alert locally.</summary>
internal static class StreamLootsSubActions
{
    public static IReadOnlyList<ISubActionHandler> Create(StreamLootsService streamLoots) =>
    [
        new IntegrationSubAction("streamloots.sendTestEvent", StreamLootsTriggers.Category, "Send test event",
            [new("kind", "Alert", ParameterKind.Choice) { DefaultValue = "redemption", Choices = [.. streamLoots.TestEventKinds] }],
            (_, v, _) => streamLoots.SendTestEvent(v["kind"]) ? Task.CompletedTask : throw new ArgumentException($"Unknown test alert '{v["kind"]}'.")),
    ];
}
