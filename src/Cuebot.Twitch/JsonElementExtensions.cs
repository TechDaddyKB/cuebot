using System.Text.Json;

namespace Cuebot.Twitch;

/// <summary>Lenient readers for Twitch JSON payloads: missing or null fields yield defaults.</summary>
internal static class JsonElementExtensions
{
    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static string Str(this JsonElement element, string name) =>
        element.Child(name) is { } value
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";

    public static int Int(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : 0;

    public static bool Bool(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.True };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IReadOnlyList<string> Strings(this JsonElement element, string name) =>
        element.Items(name).Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToArray();
}
