using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Variables;

/// <summary>
/// Typing rules shared by arguments, global and user variables. Stored values are always one of
/// <c>null</c>, <see cref="string"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="double"/> or <see cref="bool"/>.
/// </summary>
public static class VariableValues
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// "Auto type": text that is exactly an integer becomes <see cref="long"/>, a plain decimal number becomes
    /// <see cref="decimal"/> (keeping its scale, so "1.50" stays "1.50"), "true"/"false" become <see cref="bool"/>.
    /// Anything else, including numbers with leading zeros ("007"), surrounding spaces, thousands separators or
    /// exponents, stays text.
    /// </summary>
    public static object? AutoType(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (IsPlainNumber(text, out var hasPoint))
        {
            if (!hasPoint && long.TryParse(text, NumberStyles.AllowLeadingSign, Inv, out var l)) return l;
            if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var d)) return d;
        }
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return text;
    }

    private static bool IsPlainNumber(string text, out bool hasPoint)
    {
        hasPoint = false;
        var i = text[0] == '-' ? 1 : 0;
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        var digitsBefore = i - start;
        if (digitsBefore == 0) return false;
        if (digitsBefore > 1 && text[start] == '0') return false; // "007" is an id, not a number
        if (i == text.Length) return true;
        if (text[i] != '.') return false;
        hasPoint = true;
        var afterPoint = ++i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        return i > afterPoint && i == text.Length;
    }

    /// <summary>Reads any numeric value (or numeric text, invariant culture) as a decimal.</summary>
    public static bool TryGetNumber(object? value, out decimal number)
    {
        switch (value)
        {
            case long l:
                number = l;
                return true;
            case int i:
                number = i;
                return true;
            case decimal d:
                number = d;
                return true;
            case double or float:
                var dbl = Convert.ToDouble(value, Inv);
                if (double.IsFinite(dbl) && Math.Abs(dbl) < 7.9e27)
                {
                    number = (decimal)dbl;
                    return true;
                }
                break;
            case string s:
                if (decimal.TryParse(s.Trim(), NumberStyles.Float, Inv, out number)) return true;
                break;
            case short or byte or sbyte or ushort or uint or ulong:
                number = Convert.ToDecimal(value, Inv);
                return true;
        }
        number = 0;
        return false;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to a stored value (missing or empty counts as 0). Integers stay
    /// <see cref="long"/>; anything with a fractional part becomes <see cref="decimal"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current value is not a number.</exception>
    public static object Add(object? current, decimal amount)
    {
        decimal start = 0;
        var empty = current is null || (current is string s && s.Trim().Length == 0);
        if (!empty && !TryGetNumber(current, out start))
        {
            throw new InvalidOperationException($"'{VariableResolver.Format(current)}' is not a number");
        }
        var sum = start + amount;
        var integral = (empty || IsIntegral(current)) && amount == decimal.Truncate(amount) && amount.Scale == 0;
        if (integral && sum >= long.MinValue && sum <= long.MaxValue) return (long)sum;
        return sum;
    }

    private static bool IsIntegral(object? value) => value switch
    {
        long or int => true,
        string s => long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, Inv, out _),
        decimal d => d.Scale == 0,
        double d => d == Math.Truncate(d),
        _ => false,
    };

    /// <summary>Converts a value from a JSON file or a script into a stored value.</summary>
    public static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l
            : element.TryGetDecimal(out var d) ? d
            : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };

    /// <summary>Normalizes an arbitrary CLR value into one of the stored types.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null or string or long or decimal or double or bool => value,
        JsonElement e => FromJson(e),
        int or short or byte or sbyte or ushort or uint => Convert.ToInt64(value, Inv),
        ulong u => u <= long.MaxValue ? (long)u : (decimal)u,
        float f => (double)f,
        _ => VariableResolver.Format(value),
    };

    /// <summary>Human-readable type name for viewers ("text", "integer", "decimal", "boolean", "empty").</summary>
    public static string TypeName(object? value) => value switch
    {
        null => "empty",
        long or int => "integer",
        decimal or double or float => "decimal",
        bool => "boolean",
        _ => "text",
    };
}
