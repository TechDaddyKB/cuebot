using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.StreamApps.Common;

/// <summary>A sub-action built from a table entry. Type ids are stored in actions.json and must never change.</summary>
internal sealed class StreamAppSubAction(
    string typeId,
    string category,
    string displayName,
    IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues values, CancellationToken ct)
    {
        await run(context, values, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal static class Steps
{
    /// <summary>A three-way state choice; <paramref name="on"/>/<paramref name="off"/> are the labels shown (e.g. Visible/Hidden).</summary>
    public static ParameterDescriptor Switch(string on, string off, string key = "state", string label = "State", string? defaultValue = null) =>
        new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue ?? "Toggle", Choices = [on, off, "Toggle"] };

    public static SwitchMode ParseSwitch(string value) => value.Trim().ToLowerInvariant().Replace(" ", "") switch
    {
        "toggle" or "" => SwitchMode.Toggle,
        "on" or "true" or "1" or "start" or "visible" or "show" or "enabled" or "enable" or "muted" or "mute" or "monitoring" => SwitchMode.On,
        "off" or "false" or "0" or "stop" or "hidden" or "hide" or "disabled" or "disable" or "notmuted" or "unmuted" or "unmute" or "notmonitoring" => SwitchMode.Off,
        _ => throw new ArgumentException($"Unknown state '{value}'."),
    };

    public static bool Desired(SwitchMode mode, bool current) => mode switch
    {
        SwitchMode.On => true,
        SwitchMode.Off => false,
        _ => !current,
    };

    public static string Require(string value, string label) =>
        value.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException($"{label} is required.");

    public static double? OptionalNumber(string value, string label)
    {
        if (value.Trim().Length == 0) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ArgumentException($"{label} must be a number (got '{value}').");
    }

    public static void Store(ActionRunContext ctx, string argument, object? value)
    {
        if (argument.Trim().Length > 0) ctx.Args[argument.Trim()] = value;
    }

    public static EventTriggerType Trigger(string category, string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null, string? typeId = null) =>
        new(typeId ?? eventType, category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));

    public static string Text(this BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public static bool? Flag(this BotEvent e, string key) => e.Args.TryGetValue(key, out var v) ? v as bool? : null;

    /// <summary>Empty filter matches; otherwise case-insensitive equality with any comma-separated entry.</summary>
    public static bool MatchesList(string filter, params string[] values)
    {
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => string.Equals(w, v, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>"name=value" lines (or a JSON object) to an ordered dictionary; values are kept as text.</summary>
    public static Dictionary<string, string> ParsePairs(string text, Func<string, string>? resolve = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in ParsePairNodes(text, resolve))
            result[name] = value is null ? "null" : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        return result;
    }

    /// <summary>
    /// "name=value" lines or a JSON object, in order, keeping JSON values as typed (a number stays a number); line
    /// values are strings. With <paramref name="resolve"/> the text is read first and variables are then substituted
    /// in each name and text value, so a chat message with quotes, line breaks or "=" stays one value. A JSON object
    /// that is only valid once variables are in (e.g. <c>{"n":%amount%}</c>) is substituted as a whole first.
    /// </summary>
    public static List<(string Name, JsonNode? Value)> ParsePairNodes(string text, Func<string, string>? resolve = null)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{'))
        {
            JsonObject? obj = null;
            try
            {
                obj = JsonNode.Parse(trimmed) as JsonObject;
            }
            catch (JsonException) when (resolve is not null)
            {
                // Not JSON as typed; maybe once the variables are in.
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"The values are not a valid JSON object ({ex.Message}). Use name=value lines instead.", ex);
            }
            if (obj is null && resolve is not null) return ParsePairNodes(resolve(trimmed));
            if (obj is null) throw new ArgumentException("The values must be a JSON object ({...}) or name=value lines.");
            var pairs = new List<(string, JsonNode?)>();
            foreach (var (name, value) in obj)
                pairs.Add((resolve?.Invoke(name) ?? name, resolve is null ? value?.DeepClone() : ResolveNode(value, resolve)));
            return pairs;
        }
        var result = new List<(string, JsonNode?)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"Expected name=value, got '{line}'.");
            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (resolve is not null)
            {
                name = resolve(name).Trim();
                value = resolve(value);
                if (name.Length == 0) throw new ArgumentException($"The name in '{line}' is empty once its variables are filled in.");
            }
            result.RemoveAll(p => p.Item1 == name);
            result.Add((name, JsonValue.Create(value)));
        }
        return result;
    }

    private static JsonNode? ResolveNode(JsonNode? node, Func<string, string> resolve) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(resolve(v.GetValue<string>())),
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(resolve(p.Key), ResolveNode(p.Value, resolve)))),
        JsonArray a => new JsonArray(a.Select(i => ResolveNode(i, resolve)).ToArray()),
        _ => node.DeepClone(),
    };
}

internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Compact);

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

    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Null => "", _ => v.ToString() }
            : "";

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => v.GetDouble() != 0, _ => null }
            : null;

    public static double? Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>A JSON value as a plain CLR value for trigger arguments (objects/arrays stay JSON text).</summary>
    public static object? ToArg(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? (object)l : v.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.GetRawText(),
    };
}
