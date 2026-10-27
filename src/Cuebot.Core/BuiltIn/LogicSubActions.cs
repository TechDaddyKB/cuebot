using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

public sealed class IfSubAction : ISubActionHandler
{
    public const string True = "True";
    public const string False = "False";

    public string TypeId => "core.if";
    public string Category => Categories.Logic;
    public string DisplayName => "If";
    public IReadOnlyList<string> Branches { get; } = [True, False];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("left", "Value") { DefaultValue = "%message%" },
        new("operator", "Operator", ParameterKind.Choice) { DefaultValue = "equals", Choices = Conditions.Operators },
        new("right", "Compare to"),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.TakeBranch(Conditions.Evaluate(p["left"], p["operator"], p["right"]) ? True : False));
}

/// <summary>
/// Switch: compares an input with each case in order and runs the first matching case's branch, else "Default".
/// Parameter "cases" holds one case per line; a line may list several values separated by '|'. The line text
/// (trimmed) is the branch name, so the branches are dynamic (<see cref="IDynamicBranchesProvider"/>).
/// Case values may contain variables; they are resolved when the switch runs. Sets %switchCase% to the matched
/// branch name.
/// </summary>
public sealed class SwitchSubAction : IFlowContainerHandler, IDynamicBranchesProvider
{
    public const string Id = "core.switch";
    public const string Default = "Default";
    public const char ValueSeparator = '|';

    public string TypeId => Id;
    public string Category => Categories.Logic;
    public string DisplayName => "Switch";
    public IReadOnlyList<string> Branches { get; } = [Default];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("input", "Value") { DefaultValue = "%message%" },
        new("cases", "Cases", ParameterKind.Multiline)
        {
            Help = "One case per line. Separate several values for the same case with |. The first matching case wins.",
        },
        new("operator", "Match with", ParameterKind.Choice) { DefaultValue = "equals", Choices = Conditions.Operators },
    ];

    /// <summary>The case lines of a raw "cases" parameter: trimmed, non-empty, distinct, in order.</summary>
    public static IReadOnlyList<string> ParseCases(string? cases) =>
        (cases ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !string.Equals(line, Default, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The values of one case line.</summary>
    public static IReadOnlyList<string> SplitValues(string caseLine) =>
        caseLine.Split(ValueSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public IReadOnlyList<string> GetBranches(IReadOnlyDictionary<string, string> parameters) =>
        [.. ParseCases(parameters.GetValueOrDefault("cases")), Default];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct) =>
        throw new NotSupportedException("Switch runs through ExecuteFlowAsync");

    public Task<FlowOutcome> ExecuteFlowAsync(ActionRunContext context, FlowStep step, CancellationToken ct)
    {
        var p = step.ResolveParameters();
        var input = p["input"];
        var op = p["operator"];
        var matched = ParseCases(step.Definition.Parameters.GetValueOrDefault("cases"))
            .FirstOrDefault(line => SplitValues(line).Any(value => Conditions.Evaluate(input, op, context.Resolve(value))))
            ?? Default;
        context.Args["switchCase"] = matched;
        return step.RunBranchAsync(matched, ct);
    }
}

/// <summary>
/// While: repeats its "Body" branch while the condition holds (checked before each iteration, with variables
/// re-resolved). Loop Break / Loop Continue inside the body (even nested in groups or If branches) control it.
/// Sets %loopIteration% (0-based; the previous value is restored when the loop ends). Stops after "maxIterations" (0 = no limit) as a safety net.
/// </summary>
public sealed class WhileSubAction : IFlowContainerHandler
{
    public const string Id = "core.while";
    public const string Body = "Body";
    public const string IterationArg = "loopIteration";

    public string TypeId => Id;
    public string Category => Categories.Logic;
    public string DisplayName => "While";
    public IReadOnlyList<string> Branches { get; } = [Body];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("left", "Value") { DefaultValue = "%loopIteration%" },
        new("operator", "Operator", ParameterKind.Choice) { DefaultValue = "less than", Choices = Conditions.Operators },
        new("right", "Compare to") { DefaultValue = "10" },
        new("maxIterations", "Safety limit (iterations, 0 = none)", ParameterKind.Number) { DefaultValue = "10000" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct) =>
        throw new NotSupportedException("While runs through ExecuteFlowAsync");

    public async Task<FlowOutcome> ExecuteFlowAsync(ActionRunContext context, FlowStep step, CancellationToken ct)
    {
        // Restore the enclosing loop's %loopIteration% afterwards, so nested loops don't clobber it.
        var hadOuter = context.Args.TryGetValue(IterationArg, out var outer);
        try
        {
            return await LoopAsync(context, step, ct).ConfigureAwait(false);
        }
        finally
        {
            if (hadOuter) context.Args[IterationArg] = outer;
        }
    }

    private static async Task<FlowOutcome> LoopAsync(ActionRunContext context, FlowStep step, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            context.Args[IterationArg] = i;
            var p = step.ResolveParameters();
            var max = p.GetInt("maxIterations", 10000);
            if (max > 0 && i >= max)
            {
                context.Log.Warn(context.Action.Name, $"While loop stopped after {max} iterations (safety limit)");
                return FlowOutcome.Completed;
            }
            if (!Conditions.Evaluate(p["left"], p["operator"], p["right"])) return FlowOutcome.Completed;

            var outcome = await step.RunBranchAsync(Body, ct).ConfigureAwait(false);
            if (outcome == FlowOutcome.Stop) return FlowOutcome.Stop;
            if (outcome == FlowOutcome.Break) return FlowOutcome.Completed;
            // Completed or Continue: next iteration. Yield now and then so a tight loop never starves the thread.
            if (i % 64 == 63) await Task.Yield();
        }
    }
}

/// <summary>Leaves the nearest enclosing loop. Outside a loop it ends the action like Stop.</summary>
public sealed class LoopBreakSubAction : ISubActionHandler
{
    public string TypeId => "core.loopBreak";
    public string Category => Categories.Logic;
    public string DisplayName => "Loop break";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.BreakLoop);
}

/// <summary>Skips to the next iteration of the nearest enclosing loop. Outside a loop it ends the action like Stop.</summary>
public sealed class LoopContinueSubAction : ISubActionHandler
{
    public string TypeId => "core.loopContinue";
    public string Category => Categories.Logic;
    public string DisplayName => "Loop continue";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.ContinueLoop);
}

/// <summary>
/// Get input: splits a text (default %rawInput%) into %input0%..%inputN% and %inputCount%. A blank separator splits
/// on whitespace. Stale %inputN% arguments beyond the new count are removed.
/// </summary>
public sealed class GetInputSubAction : ISubActionHandler
{
    public string TypeId => "core.getInput";
    public string Category => Categories.Logic;
    public string DisplayName => "Get input";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("input", "Text") { DefaultValue = "%rawInput%" },
        new("separator", "Separator") { Help = "Leave blank to split on spaces" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var text = p["input"];
        // An unresolved default (no rawInput argument) means no input.
        if (text == "%rawInput%") text = "";
        var separator = p["separator"];
        var parts = string.IsNullOrEmpty(separator)
            ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (var i = parts.Length; context.Args.Remove($"input{i}"); i++)
        {
        }
        for (var i = 0; i < parts.Length; i++) context.Args[$"input{i}"] = parts[i];
        context.Args["inputCount"] = parts.Length;
        return Task.FromResult(SubActionResult.Next);
    }
}
