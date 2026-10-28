using System.Globalization;
using System.Text;

namespace Cuebot.Core.Actions;

/// <summary>One trigger that matched an event, before the range rule picks which ones run.</summary>
internal sealed record TriggerMatch(ActionDefinition Action, TriggerDefinition Trigger, ITriggerType Type,
    Dictionary<string, object?> Args, double? RangeWidth);

/// <summary>
/// Streamer.bot's range-trigger rule. A range trigger is a trigger type with a numeric "minX"/"maxX" setting pair (bits,
/// viewers, amount). When several triggers of the same type match one event, only those with the narrowest range run
/// ("exactly 100 bits" wins over "any amount"); a trigger marked <see cref="TriggerDefinition.AlwaysRun"/> runs anyway.
/// </summary>
public static class RangeTriggers
{
    /// <summary>The (min, max) setting keys of a range trigger type, or null when it has none.</summary>
    public static (string Min, string Max)? RangeKeys(ITriggerType type)
    {
        var numbers = type.Parameters.Where(p => p.Kind == ParameterKind.Number).Select(p => p.Key).ToArray();
        foreach (var min in numbers)
        {
            if (!min.StartsWith("min", StringComparison.OrdinalIgnoreCase)) continue;
            var max = numbers.FirstOrDefault(k => k.StartsWith("max", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(k[3..], min[3..], StringComparison.OrdinalIgnoreCase));
            if (max is not null) return (min, max);
        }
        return null;
    }

    public static bool IsRangeTrigger(ITriggerType type) => RangeKeys(type) is not null;

    /// <summary>
    /// How wide the trigger's range is: max - min, where a blank minimum is unbounded and a blank, zero or below-minimum
    /// maximum means "no maximum". Null for triggers that are not range triggers.
    /// </summary>
    public static double? Width(ITriggerType type, ParameterValues settings)
    {
        if (RangeKeys(type) is not { } keys) return null;
        double? min = Number(settings[keys.Min]);
        double? max = Number(settings[keys.Max]);
        if (max is { } hi && (hi <= 0 || (min is { } lo && hi < lo))) max = null;
        if (min is null || max is null) return double.PositiveInfinity;
        return max.Value - min.Value;
    }

    /// <summary>Drops matches that a narrower match of the same range trigger type overrides.</summary>
    internal static IEnumerable<TriggerMatch> MostSpecific(IReadOnlyList<TriggerMatch> matches)
    {
        var narrowest = matches.Where(m => m.RangeWidth is not null)
            .GroupBy(m => m.Type.TypeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(m => m.RangeWidth!.Value), StringComparer.OrdinalIgnoreCase);
        return matches.Where(m => m.RangeWidth is not { } width || m.Trigger.AlwaysRun || width <= narrowest[m.Type.TypeId]);
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Arguments the engine derives from trigger arguments for every run.</summary>
public static class TriggerArgs
{
    /// <summary>
    /// Adds Streamer.bot's escaped and URL-encoded copies of the user's input when the trigger supplied it and did
    /// not set them itself: %rawInputEscaped%, %rawInputUrlEncoded%, and for each %input#% also %inputEscaped#%,
    /// %input#Escaped% and %inputUrlEncoded#%.
    /// </summary>
    public static void AddDerived(IDictionary<string, object?> args)
    {
        if (args.TryGetValue("rawInput", out var raw) && raw is not null)
        {
            var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
            args.TryAdd("rawInputEscaped", Escape(text));
            args.TryAdd("rawInputUrlEncoded", Uri.EscapeDataString(text));
        }
        for (var i = 0; args.TryGetValue($"input{i}", out var word); i++)
        {
            var text = Convert.ToString(word, CultureInfo.InvariantCulture) ?? "";
            args.TryAdd($"inputEscaped{i}", Escape(text));
            args.TryAdd($"input{i}Escaped", Escape(text));
            args.TryAdd($"inputUrlEncoded{i}", Uri.EscapeDataString(text));
        }
    }

    /// <summary>
    /// Streamer.bot's "escaped" text: regular-expression metacharacters get a backslash
    /// (<c>https://streamer.bot/$$</c> → <c>https://streamer\.bot/\$\$</c>); spaces are left alone.
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '+' or '?' or '|' or '{' or '[' or '(' or ')' or '^' or '$' or '.' or '#') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
