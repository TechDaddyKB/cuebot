using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.StreamApps.Common;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Tits.TitsEvents;

namespace Cuebot.StreamApps.Tits;

/// <summary>T.I.T.S. triggers (category "T.I.T.S."). Type ids are stored in actions.json and must never change.</summary>
internal sealed class TitsTriggers(TitsService tits) : ITriggerProvider
{
    public const string Category = "T.I.T.S.";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor TriggerFilter() => new("trigger", "T.I.T.S. triggers")
        {
            Help = "Comma-separated trigger names or ids; leave empty for any",
            Suggestions = () => tits.Triggers.Select(t => t.Name),
        };
        return
        [
            Trigger(Category, E.Connected, "Connected"),
            Trigger(Category, E.Disconnected, "Disconnected"),
            Trigger(Category, E.WebsocketConnected, "Websocket connected"),
            Trigger(Category, E.WebsocketDisconnected, "Websocket disconnected"),
            Trigger(Category, E.EventsConnected, "Events connected"),
            Trigger(Category, E.EventsDisconnected, "Events disconnected"),
            Trigger(Category, E.ItemHit, "Item hit",
                [
                    new("item", "Items")
                    {
                        Help = "Comma-separated item names or ids; leave empty for any",
                        Suggestions = () => tits.Items.Select(i => i.Name),
                    },
                    TriggerFilter(),
                    new("minStrength", "Minimum strength", ParameterKind.Number) { Help = "Leave empty for any hit" },
                ],
                (e, s) => MatchesList(s["item"], e.Text("itemName"), e.Text("itemId"))
                          && MatchesList(s["trigger"], e.Text("triggerName"), e.Text("triggerId"))
                          && (OptionalNumber(s["minStrength"], "Minimum strength") is not { } min || Strength(e) >= min)),
            Trigger(Category, E.TriggerActivated, "Trigger activated", [TriggerFilter()],
                (e, s) => MatchesList(s["trigger"], e.Text("triggerName"), e.Text("triggerId"))),
            Trigger(Category, E.TriggerEnded, "Trigger ended", [TriggerFilter()],
                (e, s) => MatchesList(s["trigger"], e.Text("triggerName"), e.Text("triggerId"))),
        ];
    }

    private static double Strength(BotEvent e) => e.Args.TryGetValue("strength", out var v) && v is double d ? d : 0;
}

/// <summary>T.I.T.S. sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class TitsSubActions
{
    public static readonly string[] TypeIds =
    [
        "tits.activateTrigger", "tits.activateTriggerByName", "tits.throwItem", "tits.throwItemByName", "tits.throwItems",
        "tits.throwItemsByName", "tits.getItems", "tits.getTriggers",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(TitsService tits)
    {
        ParameterDescriptor Item(string key = "item", string label = "Item", string help = "Item name (or id)") => new(key, label)
        {
            Help = help,
            Suggestions = () => tits.Items.Select(i => i.Name),
        };
        ParameterDescriptor Items(string help) => new("items", "Items", ParameterKind.Multiline)
        {
            Help = help,
            Suggestions = () => tits.Items.Select(i => i.Name),
        };
        ParameterDescriptor TriggerName(string help) => new("trigger", "Trigger")
        {
            Help = help,
            Suggestions = () => tits.Triggers.Select(t => t.Name),
        };
        ParameterDescriptor[] Throw() =>
        [
            new("delay", "Delay between throws (seconds)", ParameterKind.Number) { DefaultValue = "0.05", Help = "e.g. 0.1; T.I.T.S. default is 0.05" },
            new("amount", "Amount", ParameterKind.Number) { DefaultValue = "1", Help = "How many items to throw" },
        ];

        return
        [
            Make("tits.activateTrigger", "Activate trigger", [TriggerName("Trigger name or id")],
                (_, v, ct) => tits.ActivateTriggerAsync(Require(v["trigger"], "Trigger"), ct)),
            Make("tits.activateTriggerByName", "Activate trigger by name", [TriggerName("The trigger's name in T.I.T.S.")],
                (_, v, ct) => tits.ActivateTriggerByNameAsync(Require(v["trigger"], "Trigger"), ct)),
            Make("tits.throwItem", "Throw item", [Item(), .. Throw()],
                (ctx, v, ct) => ThrowAsync(tits, ctx, [Require(v["item"], "Item")], v, ct)),
            Make("tits.throwItemByName", "Throw item by name", [Item(help: "The item's name in T.I.T.S."), .. Throw()],
                (ctx, v, ct) => ThrowAsync(tits, ctx, [Require(v["item"], "Item")], v, ct)),
            Make("tits.throwItems", "Throw items", [Items("Item names or ids, one per line or comma-separated; each throw picks one at random"), .. Throw()],
                (ctx, v, ct) => ThrowAsync(tits, ctx, TitsProtocol.SplitList(v["items"]), v, ct)),
            Make("tits.throwItemsByName", "Throw items by name", [Items("Item names, one per line or comma-separated; each throw picks one at random"), .. Throw()],
                (ctx, v, ct) => ThrowAsync(tits, ctx, TitsProtocol.SplitList(v["items"]), v, ct)),
            Make("tits.getItems", "Get items", [], async (ctx, _, ct) =>
            {
                var items = await tits.GetItemsAsync(ct).ConfigureAwait(false);
                ctx.Args["tits.items"] = JsonSerializer.Serialize(items.Select(i => new { id = i.Id, name = i.Name }));
                ctx.Args["tits.itemCount"] = items.Count;
            }),
            Make("tits.getTriggers", "Get triggers", [], async (ctx, _, ct) =>
            {
                var triggers = await tits.GetTriggersAsync(ct).ConfigureAwait(false);
                ctx.Args["tits.triggers"] = JsonSerializer.Serialize(triggers.Select(t => new { id = t.Id, name = t.Name }));
                ctx.Args["tits.triggerCount"] = triggers.Count;
            }),
        ];
    }

    private static async Task ThrowAsync(TitsService tits, ActionRunContext ctx, IReadOnlyList<string> names, ParameterValues v, CancellationToken ct)
    {
        if (names.Count == 0) throw new ArgumentException("At least one item is required.");
        var delay = OptionalNumber(v["delay"], "Delay") ?? 0.05;
        var amount = (int)Math.Round(OptionalNumber(v["amount"], "Amount") ?? 1);
        ctx.Args["tits.thrownItems"] = await tits.ThrowItemsByNameAsync(names, delay, amount, ct).ConfigureAwait(false);
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new StreamAppSubAction(typeId, TitsTriggers.Category, name, parameters, run);
}
