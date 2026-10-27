using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations;

/// <summary>
/// Lenient readers for service payloads: missing, null or oddly typed fields yield defaults (Streamlabs sends
/// amounts as strings in one event and numbers in the next).
/// </summary>
internal static class JsonReaders
{
    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static JsonElement? Child(this JsonElement? element, string name) => element is { } e ? e.Child(name) : null;

    public static string Str(this JsonElement element, string name) =>
        element.Child(name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Object or JsonValueKind.Array => "",
                _ => value.GetRawText(),
            }
            : "";

    public static string Str(this JsonElement? element, string name) => element is { } e ? e.Str(name) : "";

    /// <summary>The first non-empty of several fields.</summary>
    public static string First(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.Str(name) is { Length: > 0 } value) return value;
        return "";
    }

    public static double Number(this JsonElement element, string name) => element.Child(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s => ParseNumber(s.GetString()),
        _ => 0,
    };

    public static double Number(this JsonElement? element, string name) => element is { } e ? e.Number(name) : 0;

    public static bool Has(this JsonElement element, string name) => element.Child(name) is not null;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>"13.37", "1,000.50" and "5" all parse; anything else is 0.</summary>
    public static double ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var cleaned = text.Trim().Replace(",", "", StringComparison.Ordinal);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    public static JsonElement? TryParse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
