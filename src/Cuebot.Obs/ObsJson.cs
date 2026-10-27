using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>Lenient readers for OBS payloads, which omit fields freely.</summary>
internal static class ObsJson
{
    public static string? GetStringOrNull(this JsonElement data, string property) =>
        data.TryGet(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool? GetBoolOrNull(this JsonElement data, string property) =>
        data.TryGet(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    public static double? GetDoubleOrNull(this JsonElement data, string property) =>
        data.TryGet(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public static int? GetIntOrNull(this JsonElement data, string property) =>
        data.TryGet(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    /// <summary>The elements of an array property; empty when missing or not an array.</summary>
    public static IEnumerable<JsonElement> GetArray(this JsonElement data, string property) =>
        data.TryGet(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    /// <summary>Converts a JSON value to what action arguments hold: string, double, bool, null or raw JSON text.</summary>
    public static object? ToArgValue(this JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    private static bool TryGet(this JsonElement data, string property, out JsonElement value)
    {
        value = default;
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out value);
    }
}
