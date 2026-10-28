using Cuebot.Core.Actions;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Finds the "Execute C# code" step holding a CPHInline class by name, for <c>CPH.ExecuteMethod</c> and the
/// "Execute C# method" sub-action: first a step whose "Code name" matches, else the first CPHInline code of the
/// action with that name (names ignore case).
/// </summary>
public static class InlineCodeFinder
{
    public static SubActionDefinition? Find(IEnumerable<ActionDefinition> actions, string codeName)
    {
        var name = codeName.Trim();
        if (name.Length == 0) return null;
        var list = actions as IReadOnlyCollection<ActionDefinition> ?? actions.ToList();
        return list.SelectMany(a => InlineCode(a.SubActions)).FirstOrDefault(s =>
                   s.Parameters.TryGetValue(CSharpCodeSubAction.NameParameter, out var n)
                   && string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase))
               ?? list.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                   .SelectMany(a => InlineCode(a.SubActions)).FirstOrDefault();
    }

    /// <summary>The names other code can call into: every code name, in order, without repeats.</summary>
    public static IEnumerable<string> Names(IEnumerable<ActionDefinition> actions) =>
        actions.SelectMany(a => InlineCode(a.SubActions))
            .Select(s => s.Parameters.GetValueOrDefault(CSharpCodeSubAction.NameParameter)?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every C# step holding a CPHInline class, branches included, in order.</summary>
    public static IEnumerable<SubActionDefinition> InlineCode(IEnumerable<SubActionDefinition> steps)
    {
        foreach (var step in steps)
        {
            if (step.Type == CSharpCodeSubAction.Id && step.Parameters.TryGetValue("code", out var code) && CphInlineHost.IsInlineClass(code))
                yield return step;
            foreach (var nested in step.Branches.Values.SelectMany(InlineCode)) yield return nested;
        }
    }
}
