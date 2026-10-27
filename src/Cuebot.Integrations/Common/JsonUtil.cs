using System.Globalization;
using System.Text.Json;

namespace Cuebot.Integrations.Common;

/// <summary>Tolerant readers for app JSON (field casing and types vary between app versions).</summary>
internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The first property of <paramref name="element"/> named any of <paramref name="names"/> (case-insensitive).</summary>
    public static JsonElement? Prop(this JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in element.EnumerateObject())
            foreach (var name in names)
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    public static string Str(this JsonElement element, params string[] names) => element.Prop(names) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.ToString(),
        }
        : "";

    public static bool? Bool(this JsonElement element, params string[] names) => element.Prop(names) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => null,
        }
        : null;

    public static double? Num(this JsonElement element, params string[] names) => element.Prop(names) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        }
        : null;

    public static IEnumerable<JsonElement> Array(this JsonElement element, params string[] names) =>
        element.Prop(names) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    public static JsonElement Obj(this JsonElement element, params string[] names) =>
        element.Prop(names) is { ValueKind: JsonValueKind.Object } o ? o : default;

    public static JsonElement? TryParse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Compact);
}
