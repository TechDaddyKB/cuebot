using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.StreamApps.Tits;

/// <summary>
/// T.I.T.S. functions for user scripts. Streamer.bot's <c>ThrowingSystemThrowItemByName(itemName, delay = 0.05, amount = 1)</c>
/// and <c>ThrowingSystemActivateTriggerByName(triggerName)</c> map to <c>tits.throwItemByName</c> and <c>tits.activateTriggerByName</c>.
/// </summary>
internal sealed class TitsScriptFunctions(TitsService tits) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("tits.isConnected", "True while T.I.T.S.'s request socket is connected", [], (_, _, _) => Task.FromResult<object?>(tits.IsConnected)),
        new("tits.connect", "Connect to T.I.T.S. (returns immediately; it keeps retrying in the background)", [], async (_, _, ct) =>
        {
            await tits.ConnectAsync(ct).ConfigureAwait(false);
            return true;
        }),
        new("tits.disconnect", "Disconnect from T.I.T.S.", [], async (_, _, _) =>
        {
            await tits.DisconnectAsync().ConfigureAwait(false);
            return true;
        }),
        new("tits.throwItemByName", "Throw an item by name: {itemName, delay (s, default 0.05), amount (default 1)}; true when thrown",
            ["itemName", "delay", "amount"], async (_, a, ct) =>
                await tits.ThrowItemsByNameAsync([a.GetString("itemName")], Number(a, "delay", 0.05), (int)Number(a, "amount", 1), ct).ConfigureAwait(false) > 0),
        new("tits.throwItems", "Throw items picked at random from a list of names or ids: {items:[...], delay, amount}; returns the number thrown",
            ["items", "delay", "amount"], async (_, a, ct) =>
            {
                var items = a.GetElement("items") is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray().Select(e => e.ToString()).ToArray()
                    : TitsProtocol.SplitList(a.GetString("items"));
                return await tits.ThrowItemsByNameAsync(items, Number(a, "delay", 0.05), (int)Number(a, "amount", 1), ct).ConfigureAwait(false);
            }),
        new("tits.activateTriggerByName", "Activate a T.I.T.S. trigger by name; true when T.I.T.S. accepted it", ["triggerName"], async (_, a, ct) =>
        {
            await tits.ActivateTriggerByNameAsync(a.GetString("triggerName"), ct).ConfigureAwait(false);
            return true;
        }),
        new("tits.activateTrigger", "Activate a T.I.T.S. trigger by id or name", ["trigger"], async (_, a, ct) =>
        {
            await tits.ActivateTriggerAsync(a.GetString("trigger"), ct).ConfigureAwait(false);
            return true;
        }),
        new("tits.getItems", "Items in the model importer: [{id, name}]", [], async (_, _, ct) =>
            (await tits.GetItemsAsync(ct).ConfigureAwait(false)).Select(i => new { id = i.Id, name = i.Name }).ToArray()),
        new("tits.getTriggers", "Triggers set up in T.I.T.S.: [{id, name}]", [], async (_, _, ct) =>
            (await tits.GetTriggersAsync(ct).ConfigureAwait(false)).Select(t => new { id = t.Id, name = t.Name }).ToArray()),
    ];

    private static double Number(JsonElement args, string name, double fallback) =>
        args.GetElement(name) switch
        {
            { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
            { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => fallback,
        };
}
