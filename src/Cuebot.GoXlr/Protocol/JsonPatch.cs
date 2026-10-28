using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.GoXlr.Protocol;

/// <summary>The patch could not be applied (a path is missing, a test failed, or the operation is malformed).</summary>
internal sealed class JsonPatchException(string message) : Exception(message);

/// <summary>
/// RFC 6902 JSON Patch (add, remove, replace, move, copy, test) over <see cref="JsonNode"/> with RFC 6901 JSON Pointers.
/// The GoXLR Utility sends the output of the json-patch crate's <c>diff</c>, which uses add, remove and replace.
/// </summary>
internal static class JsonPatch
{
    /// <summary>Applies <paramref name="patch"/> (a JSON array of operations) and returns the new document (the root may be replaced).</summary>
    public static JsonNode? Apply(JsonNode? document, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Array) throw new JsonPatchException("a patch must be an array of operations");
        foreach (var op in patch.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object) throw new JsonPatchException("each operation must be an object");
            var name = op.TryGetProperty("op", out var o) ? o.GetString() : null;
            var path = op.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (name is null || path is null) throw new JsonPatchException("an operation needs 'op' and 'path'");
            JsonNode? Value() => op.TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : throw new JsonPatchException($"'{name}' needs a value");
            string From() => op.TryGetProperty("from", out var f) && f.GetString() is { } s ? s : throw new JsonPatchException($"'{name}' needs 'from'");
            switch (name)
            {
                case "add":
                    document = Add(document, path, Value());
                    break;
                case "remove":
                    document = Remove(document, path, out _);
                    break;
                case "replace":
                    document = Remove(document, path, out _);
                    document = Add(document, path, Value());
                    break;
                case "move":
                    {
                        var from = From();
                        if (path.StartsWith(from + "/", StringComparison.Ordinal)) throw new JsonPatchException("cannot move a value into itself");
                        document = Remove(document, from, out var moved);
                        document = Add(document, path, moved);
                        break;
                    }
                case "copy":
                    document = Add(document, path, Get(document, From())?.DeepClone());
                    break;
                case "test":
                    if (!JsonNode.DeepEquals(Get(document, path), Value())) throw new JsonPatchException($"test failed at {path}");
                    break;
                default:
                    throw new JsonPatchException($"unknown operation '{name}'");
            }
        }
        return document;
    }

    /// <summary>"/a/b~1c/0" → ["a", "b/c", "0"]; "" → [] (the whole document).</summary>
    public static string[] Tokens(string pointer)
    {
        if (pointer.Length == 0) return [];
        if (pointer[0] != '/') throw new JsonPatchException($"'{pointer}' is not a JSON pointer");
        return pointer[1..].Split('/').Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>The value at <paramref name="pointer"/>, or null when there is none.</summary>
    public static JsonNode? Get(JsonNode? document, string pointer)
    {
        var node = document;
        foreach (var token in Tokens(pointer))
        {
            node = node switch
            {
                JsonObject obj => obj.TryGetPropertyValue(token, out var child) ? child : throw new JsonPatchException($"no '{token}' in {pointer}"),
                JsonArray arr => arr[Index(arr, token, pointer, allowEnd: false)],
                _ => throw new JsonPatchException($"cannot descend into {pointer}"),
            };
        }
        return node;
    }

    private static (JsonNode Parent, string Last) Parent(JsonNode? document, string pointer)
    {
        var tokens = Tokens(pointer);
        var parentPointer = string.Concat(tokens[..^1].Select(t => "/" + t.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
        return (Get(document, parentPointer) ?? throw new JsonPatchException($"no parent for {pointer}"), tokens[^1]);
    }

    private static JsonNode? Add(JsonNode? document, string pointer, JsonNode? value)
    {
        if (pointer.Length == 0) return value;
        var (parent, last) = Parent(document, pointer);
        switch (parent)
        {
            case JsonObject obj:
                obj[last] = value;
                break;
            case JsonArray arr:
                var index = Index(arr, last, pointer, allowEnd: true);
                if (index == arr.Count) arr.Add(value);
                else arr.Insert(index, value);
                break;
            default:
                throw new JsonPatchException($"cannot add at {pointer}");
        }
        return document;
    }

    private static JsonNode? Remove(JsonNode? document, string pointer, out JsonNode? removed)
    {
        if (pointer.Length == 0)
        {
            removed = document;
            return null;
        }
        var (parent, last) = Parent(document, pointer);
        switch (parent)
        {
            case JsonObject obj:
                if (!obj.TryGetPropertyValue(last, out removed)) throw new JsonPatchException($"nothing to remove at {pointer}");
                obj.Remove(last);
                break;
            case JsonArray arr:
                var index = Index(arr, last, pointer, allowEnd: false);
                removed = arr[index];
                arr.RemoveAt(index);
                break;
            default:
                throw new JsonPatchException($"cannot remove at {pointer}");
        }
        return document;
    }

    private static int Index(JsonArray arr, string token, string pointer, bool allowEnd)
    {
        if (token == "-" && allowEnd) return arr.Count;
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0') || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
            throw new JsonPatchException($"'{token}' is not an array index in {pointer}");
        if (i > arr.Count || (!allowEnd && i == arr.Count)) throw new JsonPatchException($"index {i} is out of range in {pointer}");
        return i;
    }
}
