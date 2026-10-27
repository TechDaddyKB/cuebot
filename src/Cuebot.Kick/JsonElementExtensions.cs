using System.Globalization;
using System.Text.Json;

namespace Cuebot.Kick;

/// <summary>Lenient reads of Kick JSON, where fields may be missing, null, or numbers sent as strings.</summary>
internal static class JsonElementExtensions
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v
            : null;

    public static JsonElement? Prop(this JsonElement? e, string name) => e is { } v ? v.Prop(name) : null;

    public static string Str(this JsonElement? e, string name) => e?.Prop(name) is { } v ? AsString(v) : "";

    public static string Str(this JsonElement e, string name) => e.Prop(name) is { } v ? AsString(v) : "";

    public static long Long(this JsonElement? e, string name) => e?.Prop(name) is { } v ? AsLong(v) : 0;

    public static long Long(this JsonElement e, string name) => e.Prop(name) is { } v ? AsLong(v) : 0;

    public static bool Bool(this JsonElement? e, string name) => e?.Prop(name) is { } v && AsBool(v);

    public static bool Bool(this JsonElement e, string name) => e.Prop(name) is { } v && AsBool(v);

    public static IEnumerable<JsonElement> Items(this JsonElement? e, string name) =>
        e?.Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) => ((JsonElement?)e).Items(name);

    public static string AsString(this JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => "",
    };

    private static long AsLong(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
        JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0,
        _ => 0,
    };

    private static bool AsBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
        JsonValueKind.Number => v.TryGetInt64(out var l) && l != 0,
        _ => false,
    };
}
