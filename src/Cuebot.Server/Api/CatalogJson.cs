using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Server.Api;

/// <summary>Trigger, sub-action and script function catalogue as JSON, with JSON Schemas built from the descriptors.</summary>
public static class CatalogJson
{
    private const int MaxSuggestions = 50;

    public static JsonObject Parameter(ParameterDescriptor d, bool withSuggestions = true)
    {
        var json = new JsonObject
        {
            ["key"] = d.Key,
            ["label"] = d.Label,
            ["kind"] = Kind(d.Kind),
            ["default"] = d.DefaultValue,
        };
        if (d.Choices.Count > 0) json["choices"] = Strings(d.Choices);
        if (d.Language is { } language) json["language"] = language;
        if (d.Help is { } help) json["help"] = help;
        if (withSuggestions && Suggestions(d) is { Count: > 0 } suggestions) json["suggestions"] = Strings(suggestions);
        return json;
    }

    public static string Kind(ParameterKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Live suggestions (OBS scenes, command names...), capped; a failing provider yields none.</summary>
    private static List<string> Suggestions(ParameterDescriptor d)
    {
        if (d.Suggestions is null) return [];
        try
        {
            return d.Suggestions().Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).Take(MaxSuggestions).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// JSON Schema (2020-12) for a parameter dictionary. Every stored value is a string; numbers and booleans are
    /// written as text, and any value may contain %variables%, ~globals~ or $functions()$ (resolved at run time).
    /// </summary>
    public static JsonObject ParametersSchema(IReadOnlyList<ParameterDescriptor> parameters)
    {
        var properties = new JsonObject();
        foreach (var d in parameters)
        {
            var description = new List<string>();
            if (!string.IsNullOrEmpty(d.Help)) description.Add(d.Help!);
            switch (d.Kind)
            {
                case ParameterKind.Number: description.Add("A number written as text (or a %variable%)."); break;
                case ParameterKind.Boolean: description.Add("\"true\" or \"false\"."); break;
                case ParameterKind.Multiline: description.Add("Multi-line text."); break;
                case ParameterKind.Code: description.Add($"Source code ({d.Language ?? "text"}); variables are not substituted."); break;
                case ParameterKind.Choice: description.Add("One of the listed values."); break;
            }
            var property = new JsonObject { ["type"] = "string", ["title"] = d.Label };
            if (description.Count > 0) property["description"] = string.Join(" ", description);
            if (d.DefaultValue.Length > 0) property["default"] = d.DefaultValue;
            if (d.Kind == ParameterKind.Choice && d.Choices.Count > 0) property["enum"] = Strings(d.Choices);
            if (d.Kind == ParameterKind.Boolean) property["enum"] = Strings(["true", "false"]);
            properties[d.Key] = property;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    public static JsonObject SubAction(ISubActionHandler h, bool detailed)
    {
        var json = new JsonObject
        {
            ["typeId"] = h.TypeId,
            ["name"] = h.DisplayName,
            ["category"] = h.Category,
        };
        var help = ActionHelp.SubAction(h.TypeId);
        if (help is { Description.Length: > 0 }) json["description"] = help.Description;
        var parameters = ActionHelp.WithHelp(help, h.Parameters);
        if (h.Branches.Count > 0) json["branches"] = Strings(h.Branches);
        if (h is IDynamicBranchesProvider) json["dynamicBranches"] = true;
        if (h is IFlowContainerHandler) json["container"] = true;
        json["parameters"] = new JsonArray([.. parameters.Select(p => (JsonNode)Parameter(p, detailed))]);
        if (detailed) json["parametersSchema"] = ParametersSchema(parameters);
        if (help is { Variables.Count: > 0 }) json["variables"] = Variables(help.Variables);
        return json;
    }

    public static JsonObject Trigger(ITriggerType t, bool detailed)
    {
        var help = ActionHelp.Trigger(t.TypeId);
        var parameters = ActionHelp.WithHelp(help, t.Parameters);
        var json = new JsonObject
        {
            ["typeId"] = t.TypeId,
            ["name"] = t.DisplayName,
            ["category"] = t.Category,
        };
        if (help is { Description.Length: > 0 }) json["description"] = help.Description;
        json["eventTypes"] = Strings(t.EventTypes);
        json["parameters"] = new JsonArray([.. parameters.Select(p => (JsonNode)Parameter(p, detailed))]);
        if (detailed) json["settingsSchema"] = ParametersSchema(parameters);
        if (help is { Variables.Count: > 0 }) json["variables"] = Variables(help.Variables);
        return json;
    }

    /// <summary>The %variables% a trigger or sub-action provides, as name → meaning.</summary>
    private static JsonObject Variables(IReadOnlyDictionary<string, string> variables) =>
        new(variables.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    public static JsonObject ScriptFunction(ScriptFunction f) => new()
    {
        ["name"] = f.Name,
        ["description"] = f.Description,
        ["parameters"] = Strings(f.ParameterNames),
    };

    /// <summary>Case-insensitive filter on category (exact) and search words (all must appear in type id, name or category).</summary>
    public static bool Matches(string typeId, string name, string category, string? categoryFilter, string? search)
    {
        if (!string.IsNullOrWhiteSpace(categoryFilter) && !string.Equals(category, categoryFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var haystack = $"{typeId} {name} {category}";
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>Why a parameter value does not fit its descriptor, or null. Empty values and templates always fit.</summary>
    public static string? CheckValue(ParameterDescriptor d, string? value)
    {
        if (string.IsNullOrEmpty(value) || VariableResolver.HasMarkers(value)) return null;
        return d.Kind switch
        {
            ParameterKind.Number when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) =>
                $"'{value}' is not a number",
            ParameterKind.Boolean when !bool.TryParse(value, out _) => $"'{value}' is not true or false",
            ParameterKind.Choice when d.Choices.Count > 0 && !d.Choices.Contains(value, StringComparer.OrdinalIgnoreCase) =>
                $"'{value}' is not one of: {string.Join(", ", d.Choices)}",
            _ => null,
        };
    }
}
