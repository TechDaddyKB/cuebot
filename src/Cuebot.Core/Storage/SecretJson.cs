using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cuebot.Core.Storage;

/// <summary>
/// Serializes settings with their <see cref="SecretAttribute"/> properties replaced by references into the folder's
/// <see cref="SecretStore"/>, and resolves the references when reading. Settings files written before secrets were
/// split out still load; <see cref="Deserialize{T}"/> reports them so the caller saves them in the new form.
/// </summary>
public static class SecretJson
{
    public const string Prefix = "cuebot-secret:";

    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    public static string Serialize<T>(T value, string path)
    {
        var node = JsonSerializer.SerializeToNode(value, Json.Options);
        var found = new Dictionary<string, (JsonObject Parent, string Name, string Value)>(StringComparer.Ordinal);
        Collect(value, node, "", found);
        if (found.Count > 0 || HasStore(path))
        {
            var ids = Store(path).Update(Path.GetFileName(path), found.ToDictionary(f => f.Key, f => f.Value.Value, StringComparer.Ordinal));
            foreach (var (pointer, (parent, name, _)) in found) parent[name] = Prefix + ids[pointer];
        }
        return node?.ToJsonString(Json.Options) ?? "null";
    }

    /// <summary>Reads settings, filling in secrets. <paramref name="needsSave"/> is set when a secret was still stored in the file itself.</summary>
    public static T? Deserialize<T>(string json, string path, out bool needsSave)
    {
        needsSave = false;
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var file = Path.GetFileName(path);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        Resolve(node, "", path, file, resolved);
        var value = node.Deserialize<T>(Json.Options);
        var found = new Dictionary<string, (JsonObject, string, string)>(StringComparer.Ordinal);
        Collect(value, node, "", found);
        needsSave = found.Keys.Any(p => !resolved.Contains(p));
        return value;
    }

    private static bool HasStore(string path) => File.Exists(Path.Combine(Folder(path), SecretStore.FileName));

    private static SecretStore Store(string path) => SecretStore.For(Folder(path));

    private static string Folder(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;

    private static void Resolve(JsonNode? node, string pointer, string path, string file, HashSet<string> resolved)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    var childPointer = pointer + "/" + Escape(name);
                    if (child is JsonValue v && v.TryGetValue(out string? s) && s.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        obj[name] = Store(path).Get(file, childPointer, s[Prefix.Length..]) ?? "";
                        resolved.Add(childPointer);
                    }
                    else Resolve(child, childPointer, path, file, resolved);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) Resolve(array[i], pointer + "/" + i, path, file, resolved);
                break;
        }
    }

    // Walks the object and its JSON together, collecting non-empty secret values by JSON pointer.
    private static void Collect(object? value, JsonNode? node, string pointer, Dictionary<string, (JsonObject, string, string)> found)
    {
        if (value is null || node is null || IsLeaf(value.GetType())) return;
        switch (node)
        {
            case JsonArray array when value is IEnumerable items and not IDictionary:
                var i = 0;
                foreach (var item in items)
                {
                    if (i >= array.Count) break;
                    Collect(item, array[i], pointer + "/" + i, found);
                    i++;
                }
                break;
            case JsonObject obj when value is IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (obj[key] is { } child) Collect(entry.Value, child, pointer + "/" + Escape(key), found);
                }
                break;
            case JsonObject obj:
                foreach (var member in MembersOf(value.GetType()))
                {
                    if (obj[member.JsonName] is not { } child) continue;
                    var childPointer = pointer + "/" + Escape(member.JsonName);
                    var memberValue = member.Property.GetValue(value);
                    if (member.IsSecret)
                    {
                        if (memberValue is string { Length: > 0 } secret) found[childPointer] = (obj, member.JsonName, secret);
                    }
                    else Collect(memberValue, child, childPointer, found);
                }
                break;
        }
    }

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
        || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri)
        || Nullable.GetUnderlyingType(type) is not null;

    private static Member[] MembersOf(Type type) => Members.GetOrAdd(type, static t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null
                        && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always)
            .Select(p => new Member(p,
                p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? Json.Options.PropertyNamingPolicy?.ConvertName(p.Name) ?? p.Name,
                p.GetCustomAttribute<SecretAttribute>() is not null && p.PropertyType == typeof(string)))
            .ToArray());

    private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");

    private sealed record Member(PropertyInfo Property, string JsonName, bool IsSecret);
}
