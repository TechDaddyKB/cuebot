using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Integrations.Common;
using E = Cuebot.Integrations.CrowdControl.CrowdControlEvents;

namespace Cuebot.Integrations.CrowdControl;

/// <summary>Crowd Control triggers (category "Crowd Control"): Streamer.bot's nine plus refund, retry and session pause.</summary>
internal sealed class CrowdControlTriggers : ITriggerProvider
{
    public const string Category = "Crowd Control";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Steps.Trigger(Category, E.Connected, "Connected"),
        Steps.Trigger(Category, E.Disconnected, "Disconnected"),
        Effect(E.EffectRequest, "Effect request"),
        Effect(E.EffectSuccess, "Effect success"),
        Effect(E.EffectFailure, "Effect failure"),
        Effect(E.EffectRefund, "Effect refunded"),
        Effect(E.EffectRetry, "Effect retry"),
        Effect(E.TimedEffectStarted, "Timed effect started"),
        Effect(E.TimedEffectUpdated, "Timed effect update (paused / resumed)"),
        Effect(E.TimedEffectEnded, "Timed effect ended"),
        Steps.Trigger(Category, E.CoinExchange, "Coin exchange",
            [
                new("coinType", "Coin type", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "paid", "free"] },
                new("minAmount", "Minimum coins", ParameterKind.Number) { DefaultValue = "0" },
            ],
            (e, s) => (s["coinType"] is "Any" or "" || string.Equals(s["coinType"], e.Text("coinType"), StringComparison.OrdinalIgnoreCase))
                && Number(e, "amount") >= s.GetDouble("minAmount")),
        Steps.Trigger(Category, E.GameSessionStart, "Game session start", [GameFilter()], (e, s) => Steps.MatchesList(s["gameName"], e.Text("gameName"), e.Text("gameId"))),
        Steps.Trigger(Category, E.GameSessionEnd, "Game session end"),
        Steps.Trigger(Category, E.GameSessionPaused, "Game session paused / resumed", [Steps.StateFilter("Paused")], (e, s) => Steps.MatchesState(e, s, "paused")),
    ];

    private static ITriggerType Effect(string eventType, string name) => Steps.Trigger(Category, eventType, name,
        [
            new("effectName", "Effects") { Help = "Effect names or ids, comma-separated; leave empty for any" },
            GameFilter(),
            new("minPrice", "Minimum price (coins)", ParameterKind.Number) { DefaultValue = "0" },
        ],
        (e, s) => Steps.MatchesList(s["effectName"], e.Text("effectName"), e.Text("effectId"))
            && Steps.MatchesList(s["gameName"], e.Text("gameName"), e.Text("gameId"))
            && Number(e, "price") >= s.GetDouble("minPrice"));

    private static ParameterDescriptor GameFilter() => new("gameName", "Games") { Help = "Game names or ids, comma-separated; leave empty for any" };

    private static double Number(BotEvent e, string key) =>
        double.TryParse(e.Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>
/// Shows Crowd Control in the activity feed: a successful effect is a <see cref="FeedKind.Redemption"/> ("Alice redeemed
/// Spawn Creeper", reaching the credits' redemptions list); coin purchases are <see cref="FeedKind.Other"/> (amounts are
/// Crowd Control coins, not money); session start/end are <see cref="FeedKind.Other"/>.
/// </summary>
internal sealed class CrowdControlFeedAdapter : IChatEventAdapter
{
    public string Platform => "Crowd Control";

    public ChatMessage? ToChatMessage(BotEvent e) => null;

    public FeedItem? ToFeedItem(BotEvent e) => Describe(e);

    internal static FeedItem? Describe(BotEvent e)
    {
        var user = e.Text("user") is { Length: > 0 } u ? u : "Someone";
        var test = e.Flag("isTest") == true ? "[Test] " : "";
        FeedItem Item(FeedKind kind, string title, string? amount = null, string who = "") => new("Crowd Control", kind, who, test + title, e.Time)
        {
            Detail = e.Text("message") is { Length: > 0 } m ? m : null,
            Amount = amount,
        };
        var quantity = e.Text("quantity") is { Length: > 0 } q && q != "1" ? $" ×{q}" : "";
        return e.Type switch
        {
            E.EffectSuccess => Item(FeedKind.Redemption, $"{user} redeemed {e.Text("effectName")}{quantity}",
                e.Text("price") is { Length: > 0 } p && p != "0" ? $"{p} coins" : null, user),
            E.CoinExchange when e.Text("coinType") == "paid" => Item(FeedKind.Other, $"{user} bought {e.Text("amount")} coins", $"{e.Text("amount")} coins", user),
            E.GameSessionStart => Item(FeedKind.Other, $"Crowd Control session started: {e.Text("gameName")}"),
            E.GameSessionEnd => Item(FeedKind.Other, "Crowd Control session ended"),
            _ => null,
        };
    }
}

/// <summary>"Get session" (category "Crowd Control"): the current game session and running timed effects.</summary>
internal static class CrowdControlSubActions
{
    public static IReadOnlyList<ISubActionHandler> Create(CrowdControlService cc) =>
    [
        new IntegrationSubAction("crowdcontrol.getSession", CrowdControlTriggers.Category, "Get session", [], (ctx, _, _) =>
        {
            var session = cc.Session;
            var timed = cc.TimedEffects;
            ctx.Args["ccConnected"] = cc.IsConnected;
            ctx.Args["ccSessionActive"] = session is not null;
            ctx.Args["ccSessionPaused"] = session?.Paused ?? false;
            ctx.Args["ccGameSessionId"] = session?.GameSessionId ?? "";
            ctx.Args["ccGameName"] = session?.GameName ?? "";
            ctx.Args["ccGamePackName"] = session?.GamePackName ?? "";
            ctx.Args["ccSessionStarted"] = session?.StartedAt.ToString("o", CultureInfo.InvariantCulture) ?? "";
            ctx.Args["ccTimedEffectCount"] = timed.Count;
            ctx.Args["ccTimedEffects"] = string.Join(", ", timed.Select(t => t.EffectName));
            return Task.CompletedTask;
        }),
        new IntegrationSubAction("crowdcontrol.sendTestEvent", CrowdControlTriggers.Category, "Send test event",
            [new("kind", "Event", ParameterKind.Choice) { DefaultValue = "effectSuccess", Choices = [.. cc.TestEventKinds] }],
            (_, v, _) => cc.SendTestEvent(v["kind"]) ? Task.CompletedTask : throw new ArgumentException($"Unknown test event '{v["kind"]}'.")),
    ];
}
