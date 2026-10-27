using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>
/// Tolerant readers over Streamer.bot JSON. Property names are matched case-insensitively and every getter takes
/// several candidate names, because the exact field names are not publicly documented and differ between versions.
/// Wrong-typed values read as "missing" instead of throwing.
/// </summary>
public static class SbJson
{
    public static JsonNode? Get(JsonObject? obj, params string[] names)
    {
        if (obj is null) return null;
        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var exact) && exact is not null) return exact;
            foreach (var (key, value) in obj)
                if (value is not null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    public static bool Has(JsonObject obj, string name) => Get(obj, name) is not null;

    public static string? Str(JsonObject? obj, params string[] names) => AsString(Get(obj, names));

    public static string? AsString(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        }
        : null;

    public static bool? Bool(JsonObject? obj, params string[] names) => Get(obj, names) switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString() != "0",
        JsonValue v when v.GetValueKind() == JsonValueKind.String && bool.TryParse(v.GetValue<string>(), out var b) => b,
        _ => null,
    };

    public static double? Num(JsonObject? obj, params string[] names) =>
        double.TryParse(Str(obj, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    public static int? Int(JsonObject? obj, params string[] names) =>
        Num(obj, names) is { } d ? (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue) : null;

    public static JsonArray? Arr(JsonObject? obj, params string[] names) => Get(obj, names) as JsonArray;

    public static JsonObject? Obj(JsonObject? obj, params string[] names) => Get(obj, names) as JsonObject;

    public static IEnumerable<JsonObject> Objects(JsonArray? array) => array?.OfType<JsonObject>() ?? [];

    /// <summary>Lower-case letters and digits only: "Send Message" / "send_message" / "SendMessage" → "sendmessage".</summary>
    public static string Normalize(string? key) =>
        key is null ? "" : new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Invariant text for a number ("1.5", "300").</summary>
    public static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
}
