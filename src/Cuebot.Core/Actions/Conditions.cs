using System.Globalization;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Actions;

public static class Conditions
{
    public static readonly IReadOnlyList<string> Operators =
    [
        "equals", "not equals", "contains", "does not contain", "starts with", "ends with",
        "greater than", "less than", "is empty", "is not empty", "matches regex",
    ];

    /// <summary>Compares two already-resolved values. Numeric comparison is used when both sides parse.</summary>
    public static bool Evaluate(string left, string op, string right)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        return op.ToLowerInvariant() switch
        {
            "equals" => NumericOrText(left, right) == 0,
            "not equals" => NumericOrText(left, right) != 0,
            "contains" => left.Contains(right, cmp),
            "does not contain" => !left.Contains(right, cmp),
            "starts with" => left.StartsWith(right, cmp),
            "ends with" => left.EndsWith(right, cmp),
            "greater than" => NumericOrText(left, right) > 0,
            "less than" => NumericOrText(left, right) < 0,
            "is empty" => string.IsNullOrWhiteSpace(left),
            "is not empty" => !string.IsNullOrWhiteSpace(left),
            "matches regex" => Regex.IsMatch(left, right, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
            _ => throw new ArgumentException($"Unknown operator '{op}'"),
        };
    }

    private static int NumericOrText(string left, string right)
    {
        if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
        {
            return l.CompareTo(r);
        }
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
