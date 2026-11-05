namespace Cuebot.Core.Actions;

/// <summary>
/// Keeps what mod portal members set away from steps that run code, programs or files (<see cref="StepRisk.Never"/>;
/// docs/MOD_PORTAL_PLAN.md, section 5). Once a step a mod added or changed (<see cref="SubActionDefinition.EditedBy"/>)
/// has run, the run carries <see cref="ArgName"/> (also into actions it runs), and Never steps in it refuse to run.
/// Variables a mod's step set are marked in <see cref="GlobalVariables"/>; a Never step whose settings name one refuses
/// too. Code reads variables by name in its text, so a code step that mentions a marked name is refused the same way.
/// </summary>
public static class ModInfluence
{
    /// <summary>The run argument that says a mod's step ran earlier in this run (or in the run that started it).</summary>
    public const string ArgName = "__modInfluence";

    private static readonly HashSet<string> VariableWriters = new(StringComparer.Ordinal) { "core.setGlobal", "core.setUserVariable" };

    public static bool IsIn(IReadOnlyDictionary<string, object?> args) => args.TryGetValue(ArgName, out var v) && v is true;

    internal static void BeforeStep(ActionRunContext context, ISubActionHandler handler, SubActionDefinition step)
    {
        if (step.EditedBy is not null) context.Args[ArgName] = true;
        if (StepRiskCatalog.ForStep(handler.TypeId) != StepRisk.Never) return;
        if (IsIn(context.Args))
        {
            throw new ActionEngine.SubActionFailedException(
                $"{handler.DisplayName} was blocked: a mod changed an earlier step of this run, and steps that run code, programs or files never use what mods set.");
        }
        var marked = context.Globals.SetByMod();
        if (marked.Count == 0) return;
        foreach (var value in step.Parameters.Values)
        {
            foreach (var name in marked)
            {
                if (value.Contains(name, StringComparison.OrdinalIgnoreCase))
                    throw new ActionEngine.SubActionFailedException($"{handler.DisplayName} was blocked: it uses the variable '{name}', which a mod set.");
            }
        }
    }

    internal static void AfterStep(ActionRunContext context, ISubActionHandler handler, SubActionDefinition step, ParameterValues values)
    {
        if (step.EditedBy is not null) context.Args[ArgName] = true;   // a mod's Set argument cannot clear it
        if (VariableWriters.Contains(handler.TypeId) && values["name"] is { Length: > 0 } name)
            context.Globals.MarkSetByMod(name, step.EditedBy is not null);
    }
}
