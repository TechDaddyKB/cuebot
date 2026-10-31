using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

/// <summary>
/// If / Else. "matches regex" also sets Streamer.bot's capture arguments when it matches: %match.count% (groups,
/// including group 0), %match[0]%.. (each group's text) and one argument per named group. "does not exist" / "exists"
/// test whether an argument is defined; the value is its bare name (or %name%).
/// </summary>
public sealed class IfSubAction : ISubActionHandler
{
    public const string True = "True";
    public const string False = "False";
    public const string DoesNotExist = "does not exist";
    public const string Exists = "exists";

    public static readonly IReadOnlyList<string> Operators = [.. Conditions.Operators, DoesNotExist, Exists];

    public string TypeId => "core.if";
    public string Category => Categories.Logic;
    public string DisplayName => "If";
    public IReadOnlyList<string> Branches { get; } = [True, False];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("left", "Value") { DefaultValue = "%message%", Help = "For \"does not exist\" / \"exists\": the argument's name" },
        new("operator", "Operator", ParameterKind.Choice) { DefaultValue = "equals", Choices = Operators },
        new("right", "Compare to"),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var op = p["operator"].Trim();
        bool result;
        if (op.Equals(DoesNotExist, StringComparison.OrdinalIgnoreCase) || op.Equals(Exists, StringComparison.OrdinalIgnoreCase))
        {
            result = ArgumentExists(context, p["left"]) == op.Equals(Exists, StringComparison.OrdinalIgnoreCase);
        }
        else if (op.Equals("matches regex", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(p["left"], p["right"],
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            result = match.Success;
            if (match.Success) SetCaptures(context.Args, match);
        }
        else
        {
            result = Conditions.Evaluate(p["left"], op, p["right"]);
        }
        return Task.FromResult(SubActionResult.TakeBranch(result ? True : False));
    }

    /// <summary>Whether an argument is defined. A resolved %name% that became a value means it exists.</summary>
    private static bool ArgumentExists(ActionRunContext context, string text)
    {
        var name = text.Trim();
        // The value field is resolved before we see it: a bare name stays as it is, %name% of a missing argument stays
        // "%name%", and %name% of a defined one became its value.
        if (name.Length > 1 && name[0] == '%' && name[^1] == '%') return false;
        if (name.Length == 0) return false;
        return context.Args.ContainsKey(name);
    }

    /// <summary>Streamer.bot's regex capture arguments.</summary>
    public static void SetCaptures(IDictionary<string, object?> args, System.Text.RegularExpressions.Match match)
    {
        args["match.count"] = match.Groups.Count;
        for (var i = 0; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            args[$"match[{i}]"] = group.Value;
            if (!int.TryParse(group.Name, out _)) args[group.Name] = group.Value;
        }
    }
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
/// Split text into inputs (type id "core.getInput", called "Get input" before Cuebot added Streamer.bot's dialog): splits a
/// text (default %rawInput%) into %input0%..%inputN% and %inputCount%. A blank separator splits
/// on whitespace. Stale %inputN% arguments beyond the new count are removed.
/// </summary>
public sealed class GetInputSubAction : ISubActionHandler
{
    public string TypeId => "core.getInput";
    public string Category => Categories.Logic;
    public string DisplayName => "Split text into inputs";
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

/// <summary>
/// Get Input (Streamer.bot semantics): opens a dialog with a title, a prompt and an optional starting text, and stores
/// what the user typed in the output argument (default %inputResult%, auto-typed when asked). %success% is true when
/// OK was pressed, false when the dialog was cancelled or closed, or when no dialog can be shown (headless).
/// </summary>
public sealed class GetInputDialogSubAction(UserPromptService prompts) : ISubActionHandler
{
    public string TypeId => "core.getInputDialog";
    public string Category => Categories.Logic;
    public string DisplayName => "Ask me for input (popup)";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("title", "Title") { DefaultValue = "Cuebot" },
        new("prompt", "Prompt") { DefaultValue = "Enter a value" },
        new("originalText", "Original text") { Help = "Starting text in the input box" },
        new("variable", "Output variable") { DefaultValue = "inputResult" },
        new("autoType", "Auto type output", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = p["variable"].Trim().Trim('%');
        if (name.Length == 0) name = "inputResult";
        if (!prompts.IsAvailable) context.Log.Warn(context.Action.Name, "Get input: no dialog can be shown here (headless); treating it as cancelled");
        var text = await prompts.AskAsync(new UserPromptRequest(p["title"], p["prompt"], p["originalText"]), ct).ConfigureAwait(false);
        context.Args["success"] = text is not null;
        if (text is not null) context.Args[name] = p.GetBool("autoType") ? Cuebot.Core.Variables.VariableValues.AutoType(text) : text;
        return SubActionResult.Next;
    }
}
