using System.Buffers;
using System.Globalization;
using Cuebot.Core.Variables;

namespace Cuebot.Core.Actions;

/// <summary>
/// Resolves text templates used by every sub-action parameter (except code).
/// <list type="bullet">
/// <item><c>%name%</c>: action argument (case-insensitive), else a built-in such as <c>%date%</c>, <c>%time%</c>,
/// <c>%unixtime%</c>, <c>%lf%</c>, <c>%random:1-6%</c> (see <see cref="BuiltInVariables"/>).</item>
/// <item><c>~name~</c>: global variable.</item>
/// <item><c>%name:format%</c> / <c>~name:format~</c>: .NET format specifier (<c>%amount:c2%</c>, <c>%n:N0%</c>,
/// <c>%date:yyyy-MM-dd%</c>, <c>%time:t%</c>), using <see cref="TextEnvironment.Culture"/> (invariant with "$" by
/// default); append <c>|culture</c> to override, e.g. <c>%amount:c2|de-DE%</c>. See <see cref="ValueFormatter"/>
/// for how text values are treated.</item>
/// <item><c>$function(...)$</c>: inline functions (<c>$math()$</c>, <c>$length()$</c>, <c>$parse()$</c>, ...; see
/// <see cref="TextFunctions"/>), evaluated after variables, innermost first.</item>
/// </list>
/// Unknown names are left untouched so literal percent signs survive, and substituted values are never parsed
/// again. Templates are parsed once and cached; text without markers is returned as-is without allocating.
/// </summary>
public static class VariableResolver
{
    private static readonly SearchValues<char> Markers = SearchValues.Create("%~$");

    /// <summary>
    /// Resolves with the environment of the <see cref="GlobalVariables"/> instance behind <paramref name="globals"/>
    /// (when it is <c>GlobalVariables.Get</c>), otherwise <see cref="TextEnvironment.Default"/>.
    /// </summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, object?> args, Func<string, string?>? globals = null) =>
        Resolve(template, args, globals, (globals?.Target as GlobalVariables)?.Text ?? TextEnvironment.Default);

    public static string Resolve(string template, IReadOnlyDictionary<string, object?> args, Func<string, string?>? globals,
        TextEnvironment environment)
    {
        if (string.IsNullOrEmpty(template) || !HasMarkers(template)) return template;
        var parsed = TextTemplate.Get(template);
        if (parsed.Literal is not null) return parsed.Literal;
        return TemplateRenderer.Render(parsed, new ResolveScope(args, globals, environment));
    }

    /// <summary>True when the text contains a character that could start a variable or function.</summary>
    public static bool HasMarkers(string text) => text.AsSpan().IndexOfAny(Markers) >= 0;

    /// <summary>Formats a value as it is inserted into text: invariant culture, booleans as "true"/"false".</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
