using System.Globalization;
using System.Text.Json;

namespace Cuebot.Rumble;

/// <summary>
/// Lenient readers for Rumble JSON payloads: missing, null or oddly typed fields yield defaults, because Rumble
/// sends ids as strings in one place and numbers in another.
/// </summary>
internal static class JsonElementExtensions
{
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static string Str(this JsonElement element, string name) =>
        element.Child(name) is { } value
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";

    public static long Long(this JsonElement element, string name) => element.Child(name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.String } s when long.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    public static int Int(this JsonElement element, string name) => (int)Math.Clamp(element.Long(name), int.MinValue, int.MaxValue);

    public static bool Bool(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.True };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>
    /// A list of strings (badge slugs, login salts). Rumble documents arrays; some responses use an object whose
    /// values are the strings, so both are accepted.
    /// </summary>
    public static IReadOnlyList<string> Strings(this JsonElement element, string name) => element.Child(name) switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Where(IsString).Select(i => i.GetString()!).ToArray(),
        { ValueKind: JsonValueKind.Object } map => map.EnumerateObject().Select(p => p.Value).Where(IsString).Select(v => v.GetString()!).ToArray(),
        _ => [],
    };

    /// <summary>Rumble timestamps: ISO 8601 with an offset written either "+00:00" or "+0000".</summary>
    public static DateTimeOffset Time(this JsonElement element, string name) => ParseTime(element.Str(name));

    public static DateTimeOffset ParseTime(string text)
    {
        if (text.Length == 0) return DateTimeOffset.MinValue;
        // "+0000" → "+00:00" so one set of formats covers both spellings.
        if (text.Length > 5 && text[^5] is '+' or '-' && text[^4..].All(char.IsAsciiDigit)) text = text[..^2] + ":" + text[^2..];
        return DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)
            ? time
            : DateTimeOffset.MinValue;
    }

    private static bool IsString(JsonElement element) => element.ValueKind == JsonValueKind.String;
}
