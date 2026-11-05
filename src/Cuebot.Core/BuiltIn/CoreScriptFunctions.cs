using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;

namespace Cuebot.Core.BuiltIn;

/// <summary>
/// Script functions every script runtime gets: arguments, globals, logging, running actions, and running steps the
/// way the action editor does (what "Export to code" builds on).
/// </summary>
public sealed class CoreScriptFunctions : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("args.get", "Get an action argument (null if missing)", ["name"],
            (ctx, a, _) => Task.FromResult(ctx.Args.GetValueOrDefault(a.GetString("name")))),
        new("args.set", "Set an action argument", ["name", "value"], (ctx, a, _) =>
        {
            ctx.Args[a.GetString("name")] = a.GetElement("value") is { } v ? ScriptArgs.ToClr(v) : null;
            return Task.FromResult<object?>(null);
        }),
        new("args.all", "All action arguments as an object", [],
            (ctx, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>(ctx.Args))),
        new("globals.get", "Get a global variable (null if missing)", ["name"],
            (ctx, a, _) => Task.FromResult<object?>(ctx.Globals.Get(a.GetString("name")))),
        new("globals.set", "Set a global variable", ["name", "value", "persisted"], (ctx, a, _) =>
        {
            ctx.Globals.Set(a.GetString("name"), a.GetString("value"), a.GetBool("persisted", true));
            return Task.FromResult<object?>(null);
        }),
        new("log", "Write to the log", ["message", "level"], (ctx, a, _) =>
        {
            var level = Enum.TryParse<LogLevel>(a.GetString("level", "Info"), true, out var l) ? l : LogLevel.Info;
            ctx.Log.Write(level, ctx.Action.Name, a.GetString("message"));
            return Task.FromResult<object?>(null);
        }),
        new("actions.run", "Run another action by name and wait for it; returns success", ["name"], async (ctx, a, ct) =>
        {
            var name = a.GetString("name");
            var target = ctx.Engine.FindAction(name) ?? throw new ScriptException($"No action named '{name}'");
            return await ctx.Engine.RunAsync(target, ctx.Args, ctx.Depth + 1, $"Script in '{ctx.Action.Name}'", ct);
        }),
        new("args.resolve", "Fill in %variables% and ~globals~ in a text, the way step settings are", ["text"],
            (ctx, a, _) => Task.FromResult<object?>(ctx.Resolve(a.GetString("text")))),
        new("steps.run", "Run one step by its type id; returns how it ended and the branch it picked", ["type", "parameters"],
            async (ctx, a, ct) =>
            {
                var (outcome, branch) = await ctx.Engine.RunSingleStepAsync(ctx, StepFrom(a), ct).ConfigureAwait(false);
                return new Dictionary<string, object?> { ["outcome"] = OutcomeName(outcome), ["branch"] = branch };
            }),
        new("steps.runList", "Run a list of steps (with their branches) in order; returns how they ended", ["steps"],
            async (ctx, a, ct) => OutcomeName(await ctx.Engine.RunStepsAsync(ctx, StepsFrom(a), ct).ConfigureAwait(false))),
        new("steps.check", "Check a condition the way the If step does", ["left", "operator", "right"], async (ctx, a, ct) =>
        {
            var step = new SubActionDefinition
            {
                Type = "core.if",
                Parameters = { ["left"] = a.GetString("left"), ["operator"] = a.GetString("operator", "equals"), ["right"] = a.GetString("right") },
            };
            var (_, branch) = await ctx.Engine.RunSingleStepAsync(ctx, step, ct).ConfigureAwait(false);
            return branch == IfSubAction.True;
        }),
        new("steps.pick", "Pick one of several weights at random, like a random group; returns its position (0 = first)", ["weights"],
            (ctx, a, _) =>
            {
                var weights = a.GetElement("weights") is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray().Select(w => w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 1).ToArray()
                    : [];
                var indexes = Enumerable.Range(0, weights.Length).ToArray();
                var pick = WeightedRandom.Pick(indexes, i => weights[i], ctx.Engine.Random);
                return Task.FromResult<object?>(weights.Length == 0 ? -1 : pick);
            }),
    ];

    /// <summary>How a run of steps ended, as scripts see it.</summary>
    public static string OutcomeName(FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Stop => "stop",
        FlowOutcome.Break => "break",
        FlowOutcome.Continue => "continue",
        _ => "completed",
    };

    private static SubActionDefinition StepFrom(JsonElement args)
    {
        var type = args.GetString("type");
        if (string.IsNullOrWhiteSpace(type)) throw new ScriptException("steps.run needs the step's type, like \"core.log\"");
        var step = new SubActionDefinition { Type = type.Trim() };
        if (args.GetElement("parameters") is { ValueKind: JsonValueKind.Object } parameters)
        {
            foreach (var p in parameters.EnumerateObject())
            {
                step.Parameters[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => p.Value.GetRawText(),
                };
            }
        }
        return step;
    }

    private static List<SubActionDefinition> StepsFrom(JsonElement args)
    {
        if (args.GetElement("steps") is not { ValueKind: JsonValueKind.Array } steps)
        {
            throw new ScriptException("steps.runList needs a list of steps, like [{\"type\": \"core.log\", \"parameters\": {\"message\": \"Hi\"}}]");
        }
        return steps.Deserialize<List<SubActionDefinition>>(Json.Options) ?? [];
    }
}
