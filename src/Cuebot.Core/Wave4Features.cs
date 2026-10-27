using System.Globalization;
using Cuebot.Core.Credits;
using Cuebot.Core.Desktop;
using Cuebot.Core.Points;
using Cuebot.Core.Quotes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core;

/// <summary>Wave-4 core service extras: quotes, credits, points/currency, global hotkeys, notifications, clipboard.</summary>
public static class Wave4Features
{
    public static IServiceCollection AddWave4Features(this IServiceCollection services) => services
        .AddQuoteFeatures()
        .AddCreditsFeatures()
        .AddPointsFeatures()
        .AddDesktopFeatures();
}

/// <summary>Sub-action / trigger categories of the wave-4 areas (System is shared with the built-in system steps).</summary>
internal static class ExtrasCategories
{
    public const string Quotes = "Quotes";
    public const string Credits = "Credits";
    public const string Points = "Points";
    public const string System = BuiltIn.Categories.System;
}

/// <summary>Small text helpers shared by the wave-4 sub-actions.</summary>
internal static class ExtrasText
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Trims; an unresolved "%arg%" placeholder (the argument did not exist) counts as empty.</summary>
    public static string Clean(string? text)
    {
        var t = text?.Trim() ?? "";
        return t.Length > 2 && t[0] == '%' && t[^1] == '%' && t.AsSpan(1, t.Length - 2).IndexOf('%') < 0 ? "" : t;
    }

    /// <summary>Lower-camel identifier → "PascalCase" suffix for argument names (follows → Follows).</summary>
    public static string Pascal(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];

    /// <summary>First whole number found in a text ("raided with 25 viewers" → 25, "$4.50" → 4.5).</summary>
    public static double? FirstNumber(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i])) { start = i; break; }
        }
        if (start < 0) return null;
        var end = start;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is ',' or '.')) end++;
        var number = text[start..end].TrimEnd('.', ',');
        // "1,234.50" → 1234.50; "4,50" (decimal comma) → 4.50
        if (number.Contains('.')) number = number.Replace(",", "");
        else if (number.Count(c => c == ',') == 1 && number.Length - number.IndexOf(',') - 1 is 1 or 2) number = number.Replace(',', '.');
        else number = number.Replace(",", "");
        return double.TryParse(number, NumberStyles.Float, Inv, out var value) ? value : null;
    }
}
