using System.Globalization;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Variables;

/// <summary>
/// Applies a .NET format specifier to a variable value (<c>%amount:c2%</c>, <c>%n:N0%</c>, <c>%date:yyyy-MM-dd%</c>).
/// <list type="bullet">
/// <item>Numbers and dates use the format with the given culture.</item>
/// <item>Text that is a number (invariant, e.g. "5.5") is formatted as that number; text that is a date/time
/// (e.g. ISO 8601 "2026-10-27T14:00:00Z") is formatted as that date.</item>
/// <item>Other text and booleans ignore the format and are inserted unchanged.</item>
/// <item>An invalid format (e.g. <c>X</c> on a fractional number) fails, leaving the marker untouched.</item>
/// </list>
/// </summary>
public static class ValueFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryFormat(object? value, string? format, CultureInfo culture, out string result)
    {
        if (string.IsNullOrEmpty(format))
        {
            result = VariableResolver.Format(value);
            return true;
        }
        try
        {
            switch (value)
            {
                case null:
                    result = "";
                    return true;
                case string s when TryParse(s, out var parsed):
                    return TryFormat(parsed, format, culture, out result);
                case string or bool:
                    result = VariableResolver.Format(value);
                    return true;
                case IFormattable f:
                    result = f.ToString(format, culture);
                    return true;
                default:
                    result = VariableResolver.Format(value);
                    return true;
            }
        }
        catch (FormatException)
        {
            result = "";
            return false;
        }
    }

    private static bool TryParse(string text, out IFormattable parsed)
    {
        var t = text.Trim();
        if (t.Length > 0 && (char.IsAsciiDigit(t[0]) || t[0] is '-' or '+' or '.'))
        {
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, Inv, out var l))
            {
                parsed = l;
                return true;
            }
            if (decimal.TryParse(t, NumberStyles.Float, Inv, out var d))
            {
                parsed = d;
                return true;
            }
            if (double.TryParse(t, NumberStyles.Float, Inv, out var dbl))
            {
                parsed = dbl;
                return true;
            }
            if (t.Length >= 8 && DateTimeOffset.TryParse(t, Inv, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var date))
            {
                parsed = date;
                return true;
            }
        }
        parsed = 0;
        return false;
    }
}
