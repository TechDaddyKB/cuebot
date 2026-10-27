using System.Globalization;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Actions;

/// <summary>
/// Substitutes %argument% with action arguments and ~global~ with global variables.
/// Unknown names are left untouched so literal percent signs survive.
/// </summary>
public static partial class VariableResolver
{
    [GeneratedRegex(@"%([A-Za-z0-9_.]+)%")]
    private static partial Regex ArgPattern();

    [GeneratedRegex(@"~([A-Za-z0-9_.]+)~")]
    private static partial Regex GlobalPattern();

    public static string Resolve(string template, IReadOnlyDictionary<string, object?> args, Func<string, string?>? globals = null)
    {
        if (string.IsNullOrEmpty(template)) return template;
        var result = template;
        if (result.Contains('%'))
        {
            result = ArgPattern().Replace(result, m =>
                TryGet(args, m.Groups[1].Value, out var value) ? Format(value) : m.Value);
        }
        if (globals is not null && result.Contains('~'))
        {
            result = GlobalPattern().Replace(result, m => globals(m.Groups[1].Value) ?? m.Value);
        }
        return result;
    }

    public static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool TryGet(IReadOnlyDictionary<string, object?> args, string key, out object? value)
    {
        if (args.TryGetValue(key, out value)) return true;
        // Fall back to a case-insensitive scan for dictionaries created with the default comparer.
        foreach (var pair in args)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        return false;
    }
}
