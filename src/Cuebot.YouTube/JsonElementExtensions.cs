using System.Globalization;
using System.Text.Json;

namespace Cuebot.YouTube;

/// <summary>Lenient reads of Google JSON: missing or mistyped members read as empty values.</summary>
internal static class JsonElementExtensions
{
    private static readonly string[] ThumbnailSizes = ["high", "medium", "default"];

    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static JsonElement At(this JsonElement e, params string[] names)
    {
        foreach (var name in names) e = e.Prop(name);
        return e;
    }

    public static bool Exists(this JsonElement e) => e.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;

    public static string Str(this JsonElement e, string name) => e.Prop(name).AsString();

    public static string AsString(this JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => "",
    };

    /// <summary>Reads numbers that Google sends either as JSON numbers or as strings (e.g. "amountMicros": "5000000").</summary>
    public static long Long(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.Number => (long)v.GetDouble(),
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => 0,
        };
    }

    public static long? NullableLong(this JsonElement e, string name) => e.Str(name).Length > 0 ? e.Long(name) : null;

    public static int Int(this JsonElement e, string name) => (int)Math.Clamp(e.Long(name), int.MinValue, int.MaxValue);

    public static bool Bool(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        return v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }

    public static DateTimeOffset? Time(this JsonElement e, string name) =>
        DateTimeOffset.TryParse(e.Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name = "items") =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IReadOnlyList<string> Strings(this JsonElement e, string name) =>
        e.Items(name).Select(i => i.AsString()).Where(s => s.Length > 0).ToArray();

    /// <summary>Best thumbnail URL of a snippet's "thumbnails" object.</summary>
    public static string Thumbnail(this JsonElement snippet)
    {
        var thumbs = snippet.Prop("thumbnails");
        foreach (var size in ThumbnailSizes)
            if (thumbs.At(size, "url").AsString() is { Length: > 0 } url) return url;
        return "";
    }

    public static JsonElement Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
