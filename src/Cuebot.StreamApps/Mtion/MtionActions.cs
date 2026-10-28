using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.Mtion;

/// <summary>mtion triggers (Cuebot extras: Streamer.bot documents none for mtion). Type ids must never change.</summary>
internal sealed class MtionTriggers : ITriggerProvider
{
    public const string Category = "mtion studio";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, MtionService.ConnectedEvent, "mtion studio started"),
        Trigger(Category, MtionService.DisconnectedEvent, "mtion studio closed"),
        Trigger(Category, MtionService.ClubhouseChangedEvent, "Clubhouse changed",
            [new("clubhouse", "Clubhouses") { Help = "Comma-separated names or ids; empty for any" }],
            (e, s) => MatchesList(s["clubhouse"], e.Text("mtion.clubhouse.name"), e.Text("mtion.clubhouse.id"))),
        Trigger(Category, MtionService.TriggerSentEvent, "External trigger fired",
            [new("trigger", "Triggers") { Help = "Comma-separated names or ids; empty for any" }],
            (e, s) => MatchesList(s["trigger"], e.Text("mtion.trigger.name"), e.Text("mtion.trigger.id"))),
    ];
}

/// <summary>mtion sub-actions (Streamer.bot's "Send Trigger" and "Get Active Clubhouse", plus a trigger list). Type ids must never change.</summary>
internal static class MtionSubActions
{
    public static readonly string[] TypeIds = ["mtion.sendTrigger", "mtion.getActiveClubhouse", "mtion.getTriggers"];

    public static IReadOnlyList<ISubActionHandler> Create(MtionService mtion) =>
    [
        Make("mtion.sendTrigger", "Send trigger",
            [
                new("trigger", "Trigger")
                {
                    Help = "External trigger name (or id) in the open clubhouse",
                    Suggestions = () => mtion.Clubhouse?.Triggers.Select(t => t.Name) ?? [],
                },
                new("parameters", "Parameters", ParameterKind.Multiline)
                {
                    Help = "One name=value per line (or a JSON object); names are the trigger's parameter names (or 0, 1, ...). Enums take the option number.",
                },
            ],
            async (ctx, v, ct) =>
            {
                var result = await mtion.FireTriggerAsync(Require(v["trigger"], "Trigger"), ParsePairs(v["parameters"]), ct).ConfigureAwait(false);
                ctx.Args["mtion.running"] = result.Running;
                ctx.Args["mtion.trigger.found"] = result.Found;
                ctx.Args["mtion.trigger.sent"] = result.Sent;
                ctx.Args["mtion.trigger.id"] = result.Trigger?.Id ?? "";
                ctx.Args["mtion.trigger.name"] = result.Trigger?.Name ?? "";
                if (!result.Running) ctx.Log.Warn(MtionTriggers.Category, "mtion studio is not running.");
            }),
        Make("mtion.getActiveClubhouse", "Get active clubhouse", [], async (ctx, _, ct) =>
        {
            var (running, clubhouse) = await mtion.GetActiveClubhouseAsync(ct).ConfigureAwait(false);
            ctx.Args["mtion.running"] = running;
            foreach (var (key, value) in MtionService.ClubhouseArgs(clubhouse)) ctx.Args[key] = value;
        }),
        Make("mtion.getTriggers", "Get external triggers", [], async (ctx, _, ct) =>
        {
            var (running, triggers) = await mtion.GetTriggersAsync(ct).ConfigureAwait(false);
            ctx.Args["mtion.running"] = running;
            ctx.Args["mtion.triggerCount"] = triggers.Count;
            ctx.Args["mtion.triggerNames"] = string.Join(", ", triggers.Select(t => t.Name));
            ctx.Args["mtion.triggers"] = JsonSerializer.Serialize(triggers.Select(ToJson));
        }),
    ];

    internal static object ToJson(MtionTrigger t) => new
    {
        id = t.Id,
        name = t.Name,
        parameters = t.Parameters.Select(p => new { index = p.Index, name = p.Name, type = p.DataType }).ToArray(),
    };

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new StreamAppSubAction(typeId, MtionTriggers.Category, name, parameters, run);
}

/// <summary>mtion functions for user scripts.</summary>
internal sealed class MtionScriptFunctions(MtionService mtion) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("mtion.isRunning", "True when mtion studio answered the last request", [], (_, _, _) => Task.FromResult<object?>(mtion.IsConnected)),
        new("mtion.sendTrigger", "Fire an external trigger, e.g. {trigger:'Confetti', parameters:{Amount:5}}; returns {running, found, sent}",
            ["trigger", "parameters"], async (_, a, ct) =>
            {
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                if (a.GetElement("parameters") is { ValueKind: JsonValueKind.Object } obj)
                    foreach (var p in obj.EnumerateObject())
                        parameters[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                var r = await mtion.FireTriggerAsync(a.GetString("trigger"), parameters, ct).ConfigureAwait(false);
                return new { running = r.Running, found = r.Found, sent = r.Sent };
            }),
        new("mtion.getActiveClubhouse", "The open clubhouse {id, name, description} or null", [], async (_, _, ct) =>
        {
            var (_, c) = await mtion.GetActiveClubhouseAsync(ct).ConfigureAwait(false);
            return c is null ? null : new { id = c.Id, name = c.Name, description = c.Description };
        }),
        new("mtion.getTriggers", "External triggers of the open clubhouse: id, name, parameters", [], async (_, _, ct) =>
            (await mtion.GetTriggersAsync(ct).ConfigureAwait(false)).Triggers.Select(MtionSubActions.ToJson).ToArray()),
        new("mtion.getClubhouses", "Every clubhouse of the signed-in user: id, name, description", [], async (_, _, ct) =>
            (await mtion.GetClubhousesAsync(ct).ConfigureAwait(false)).Clubhouses.Select(c => new { id = c.Id, name = c.Name, description = c.Description }).ToArray()),
    ];
}
