using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cuebot.Core.SystemTools;

/// <summary>
/// A small JSONPath subset for picking one value: <c>$.a.b[0].c</c>, <c>$['key with spaces']</c>, <c>items[-1]</c>.
/// The leading <c>$</c> is optional. Wildcards, filters and slices are not supported.
/// </summary>
public static class JsonPath
{
    /// <summary>A parsed path step: a property name, or an array index (negative counts from the end).</summary>
    public readonly record struct Segment(string? Property, int Index)
    {
        public bool IsIndex => Property is null;
        public override string ToString() => IsIndex ? $"[{Index}]" : $".{Property}";
    }

    /// <exception cref="FormatException">The path is malformed.</exception>
    public static IReadOnlyList<Segment> Parse(string path)
    {
        var segments = new List<Segment>();
        var text = (path ?? "").Trim();
        var i = 0;
        if (text.StartsWith('$')) i = 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '.')
            {
                i++;
                segments.Add(new Segment(ReadName(text, ref i), 0));
            }
            else if (c == '[')
            {
                i++;
                segments.Add(ReadBracket(text, ref i));
            }
            else if (segments.Count == 0)
            {
                segments.Add(new Segment(ReadName(text, ref i), 0)); // "a.b" without "$."
            }
            else
            {
                throw new FormatException($"Unexpected '{c}' at position {i + 1} in JSON path.");
            }
        }
        return segments;
    }

    private static string ReadName(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && text[i] != '.' && text[i] != '[') i++;
        if (i == start) throw new FormatException($"Missing property name at position {start + 1} in JSON path.");
        return text[start..i].Trim();
    }

    private static Segment ReadBracket(string text, ref int i)
    {
        if (i < text.Length && text[i] is '\'' or '"')
        {
            var quote = text[i++];
            var name = new StringBuilder();
            while (i < text.Length && text[i] != quote)
            {
                if (text[i] == '\\' && i + 1 < text.Length) i++;
                name.Append(text[i++]);
            }
            if (i + 1 >= text.Length || text[i + 1] != ']') throw new FormatException("Unterminated ['name'] in JSON path.");
            i += 2;
            return new Segment(name.ToString(), 0);
        }
        var end = text.IndexOf(']', i);
        if (end < 0) throw new FormatException("Missing ']' in JSON path.");
        var raw = text[i..end].Trim();
        i = end + 1;
        if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            throw new FormatException($"'[{raw}]' is not an array index.");
        return new Segment(null, index);
    }

    /// <summary>Finds the value at <paramref name="path"/>; false when any step is missing.</summary>
    /// <exception cref="FormatException">The path is malformed.</exception>
    public static bool TrySelect(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in Parse(path))
        {
            if (segment.IsIndex)
            {
                if (value.ValueKind != JsonValueKind.Array) return false;
                var length = value.GetArrayLength();
                var index = segment.Index < 0 ? length + segment.Index : segment.Index;
                if (index < 0 || index >= length) return false;
                value = value[index];
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Object || !TryGetProperty(value, segment.Property!, out value)) return false;
            }
        }
        return true;
    }

    /// <summary>Exact property match first, then case-insensitive.</summary>
    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The value as an argument: strings unquoted, integers as long, other numbers as double, booleans as bool,
    /// null as an empty string, objects and arrays as JSON text.
    /// </summary>
    public static object ToArgument(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    /// <summary>Elements of an array or properties of an object; null for other values.</summary>
    public static int? Count(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength(),
        JsonValueKind.Object => value.EnumerateObject().Count(),
        _ => null,
    };
}
