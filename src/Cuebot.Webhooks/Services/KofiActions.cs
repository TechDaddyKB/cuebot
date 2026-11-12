using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Webhooks.Services;

/// <summary>Sub-actions for the Ko-fi goal and commission queue that <see cref="KofiTracker"/> keeps.</summary>
internal static class KofiSubActions
{
    public const string Category = "Ko-fi";

    private sealed class Step(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Action<ActionRunContext, ParameterValues> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => KofiSubActions.Category;
        public string DisplayName => name;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            run(context, p);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    public static IReadOnlyList<ISubActionHandler> Create(KofiTracker tracker) =>
    [
        new Step("kofi.getGoal", "Get the Ko-fi goal", [], (c, _) => KofiTracker.AddGoal(c.Args, tracker.Goal())),
        new Step("kofi.setGoal", "Set the Ko-fi goal",
            [
                new("title", "Title") { Help = "Leave empty to keep the title" },
                new("target", "Target") { Help = "Leave empty to keep it; 0 = no goal" },
                new("startAmount", "Raised before Cuebot counted") { Help = "What your Ko-fi goal showed already; leave empty to keep it" },
                new("currency", "Currency") { Help = "Like USD; only payments in it count. Leave empty to keep it" },
            ],
            (c, p) => KofiTracker.AddGoal(c.Args, tracker.SetGoal(
                Optional(p["title"]), OptionalNumber(p["target"]), OptionalNumber(p["startAmount"]), Optional(p["currency"])))),
        new Step("kofi.addToGoal", "Add to the Ko-fi goal",
            [new("amount", "Amount", ParameterKind.Number) { DefaultValue = "5", Help = "A negative number takes it away. Use it for money that came in some other way" }],
            (c, p) => KofiTracker.AddGoal(c.Args, tracker.AddToGoal(p.GetDouble("amount"), c.Args.GetValueOrDefault("user") as string ?? ""))),
        new Step("kofi.resetGoal", "Reset the Ko-fi goal",
            [new("clearStart", "Also clear the start amount", ParameterKind.Boolean) { DefaultValue = "false" }],
            (c, p) => KofiTracker.AddGoal(c.Args, tracker.ResetGoal(p.GetBool("clearStart")))),
        new Step("kofi.addCommission", "Add a commission",
            [
                new("name", "Who") { DefaultValue = "%user%" },
                new("text", "What they asked for", ParameterKind.Multiline) { DefaultValue = "%message%" },
                new("amount", "Amount", ParameterKind.Number) { DefaultValue = "0" },
                new("currency", "Currency") { Help = "Like USD" },
            ],
            (c, p) =>
            {
                var added = tracker.AddCommission(p["name"], p["text"], p.GetDouble("amount"), p["currency"]);
                c.Args["commissionId"] = added.Id;
                c.Args["commissionsOpen"] = tracker.Commissions().Count;
            }),
        new Step("kofi.completeCommission", "Mark a commission done",
            [new("which", "Which") { Help = "Empty = the oldest open one; or its number in the list (1, 2...) or its id (%commissionId%)" }],
            (c, p) =>
            {
                var done = tracker.CompleteCommission(p["which"]);
                c.Args["commissionFound"] = done is not null;
                c.Args["commissionName"] = done?.Name ?? "";
                c.Args["commissionText"] = done?.Message ?? "";
                c.Args["commissionsOpen"] = tracker.Commissions().Count;
            }),
        new Step("kofi.getCommissions", "Get open commissions", [], (c, _) => StoreCommissions(c.Args, tracker.Commissions())),
        new Step("kofi.clearCommissions", "Clear done commissions",
            [new("all", "Clear open ones too", ParameterKind.Boolean) { DefaultValue = "false" }],
            (c, p) => c.Args["commissionsCleared"] = tracker.ClearCommissions(p.GetBool("all"))),
    ];

    internal static void StoreCommissions(Dictionary<string, object?> args, IReadOnlyList<KofiCommission> open)
    {
        args["commissionsOpen"] = open.Count;
        args["commissionsText"] = string.Join("; ", open.Select((c, i) =>
            c.Message.Length > 0 ? $"{i + 1}. {c.Name}: {c.Message}" : $"{i + 1}. {c.Name}"));
        for (var i = 0; i < open.Count; i++)
        {
            args[$"commission{i}.id"] = open[i].Id;
            args[$"commission{i}.name"] = open[i].Name;
            args[$"commission{i}.text"] = open[i].Message;
            args[$"commission{i}.amount"] = (decimal)open[i].Amount;
        }
    }

    private static string? Optional(string text) => text.Trim().Length == 0 ? null : text.Trim();

    private static double? OptionalNumber(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>Script functions: <c>kofi.goal</c>, <c>kofi.setGoal</c>, <c>kofi.addToGoal</c>, <c>kofi.resetGoal</c>, <c>kofi.commissions</c>, <c>kofi.completeCommission</c>, <c>kofi.members</c>.</summary>
internal sealed class KofiScriptFunctions(KofiTracker tracker) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("kofi.goal", "The Ko-fi goal Cuebot keeps: title, current, target, percent, currency, reached", [],
            (_, _, _) => Task.FromResult<object?>(Goal(tracker.Goal()))),
        new("kofi.setGoal", "Change the Ko-fi goal's title, target, start amount or currency (leave any out to keep it)",
            ["title", "target", "startAmount", "currency"], (_, a, _) => Task.FromResult<object?>(Goal(tracker.SetGoal(
                a.GetElement("title") is not null ? a.GetString("title") : null, NumberOrNull(a, "target"), NumberOrNull(a, "startAmount"),
                a.GetElement("currency") is not null ? a.GetString("currency") : null)))),
        new("kofi.addToGoal", "Add an amount to the Ko-fi goal (negative takes it away)", ["amount"],
            (_, a, _) => Task.FromResult<object?>(Goal(tracker.AddToGoal(NumberOrNull(a, "amount") ?? 0)))),
        new("kofi.resetGoal", "Start the Ko-fi goal again from its start amount", ["clearStart"],
            (_, a, _) => Task.FromResult<object?>(Goal(tracker.ResetGoal(a.GetBool("clearStart"))))),
        new("kofi.commissions", "Open Ko-fi commissions, oldest first (includeDone: also finished ones)", ["includeDone"],
            (_, a, _) => Task.FromResult<object?>(tracker.Commissions(a.GetBool("includeDone")).Select(c => new
            {
                id = c.Id, name = c.Name, message = c.Message, amount = c.Amount, currency = c.Currency, formattedAmount = c.FormattedAmount,
                time = c.Time, done = c.Done,
            }).ToArray())),
        new("kofi.completeCommission", "Mark a commission done (empty: the oldest; or its number or id); returns it or null", ["which"],
            (_, a, _) => Task.FromResult<object?>(tracker.CompleteCommission(a.GetString("which")) is { } c
                ? new { id = c.Id, name = c.Name, message = c.Message } : null)),
        new("kofi.members", "Ko-fi members Cuebot has seen pay, newest payment first, with lapsed (an estimate)", [],
            (_, _, _) => Task.FromResult<object?>(tracker.Members().Select(m => new
            {
                name = m.Name, tier = m.Tier, discordUserId = m.DiscordUserId, lastPayment = m.LastPayment, payments = m.Payments, lapsed = m.LapseReported,
            }).ToArray())),
    ];

    private static object Goal(KofiGoalState g) => new
    {
        title = g.Title, current = g.Current, target = g.Target, percent = g.Percent, currency = g.Currency, reached = g.Reached,
    };

    private static double? NumberOrNull(JsonElement a, string name) =>
        a.GetElement(name) is { } v ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null : null;
}
