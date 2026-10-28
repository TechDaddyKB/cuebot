using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>What a mapping function can ask the importer for while converting one item.</summary>
public interface ISbMapContext
{
    /// <summary>The Cuebot action being built (for messages).</summary>
    string ActionName { get; }

    /// <summary>Converts a nested list of Streamer.bot sub-actions (children of a group or an if/else branch).</summary>
    List<SubActionDefinition> MapChildren(JsonArray? children);

    /// <summary>Name of the imported action with this Streamer.bot id (or the text itself when unknown).</summary>
    string ActionRef(string? idOrName);

    /// <summary>The Cuebot reference (new id as text) for a Streamer.bot command id, or the text itself.</summary>
    string CommandRef(string? idOrName);

    /// <summary>Name of the imported timer with this Streamer.bot id (or the text itself).</summary>
    string TimerRef(string? idOrName);

    /// <summary>Name of the imported action queue with this Streamer.bot id; empty (and reported) when it is not in the export.</summary>
    string QueueRef(string? idOrName);

    /// <summary>
    /// The name <c>CPH.ExecuteMethod</c> finds a Streamer.bot "Execute C# Code" sub-action by: its code name, else the
    /// name of the action holding it. Falls back to the text itself.
    /// </summary>
    string CodeRef(string? subActionId);

    void Warn(string message);
}

/// <summary>Converts one Streamer.bot sub-action object into a Cuebot step.</summary>
public delegate SubActionDefinition SbSubActionMap(JsonObject source, ISbMapContext context);

/// <summary>Converts one Streamer.bot trigger object into a Cuebot trigger.</summary>
public delegate TriggerDefinition SbTriggerMap(JsonObject source, ISbMapContext context);

/// <summary>
/// One mapping rule. A Streamer.bot item matches when its type key (the "type" value, or the class name in a
/// "$type" field, compared after <see cref="SbJson.Normalize"/>) is one of <see cref="TypeKeys"/>, or a user
/// alias points at <see cref="Name"/>. Failing that, the importer may infer the rule from the item's fields: every
/// field of one of the <see cref="Signatures"/> must be present and none of <see cref="Excludes"/>.
/// </summary>
public abstract record SbRule(string Name, string TargetType, IReadOnlyList<string> TypeKeys)
{
    public IReadOnlyList<IReadOnlyList<string>> Signatures { get; init; } = [];
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// True when the Streamer.bot name of a sub-action code is only likely or unsure: Streamer.bot 1.0.7 confirmed the
    /// code and its fields, but which of several look-alike sub-actions it is was worked out from its fields, the
    /// order Streamer.bot added them in and public exports (see <see cref="StreamerBotMappingTable.TentativeSubActionCodes"/>
    /// and docs/streamerbot-import-codes.md). The import report asks the user to check such items. Trigger codes are
    /// all verified.
    /// </summary>
    public bool Tentative { get; init; }
}

public sealed record SbSubActionRule(string Name, string TargetType, IReadOnlyList<string> TypeKeys, SbSubActionMap Map)
    : SbRule(Name, TargetType, TypeKeys);

public sealed record SbTriggerRule(string Name, string TargetType, IReadOnlyList<string> TypeKeys, SbTriggerMap Map)
    : SbRule(Name, TargetType, TypeKeys);

/// <summary>
/// The pluggable Streamer.bot → Cuebot mapping table. <see cref="Default"/> holds the built-in rules (see the table
/// in docs and <see cref="Describe"/>). Streamer.bot stores sub-action and trigger types as numeric codes that are
/// not publicly documented. The defaults hold the codes worked out from public exports (docs/streamerbot-import-codes.md)
/// and also match descriptive type names, CLR class names and field shapes; users add or override codes with
/// <see cref="AddAliases"/> or a JSON file (<c>{"subActions": {"&lt;code&gt;": "SendMessage"}, "triggers": {"&lt;code&gt;": "Follow"}}</c>).
/// </summary>
public sealed partial class StreamerBotMappingTable
{
    private readonly List<SbSubActionRule> _subActions = [];
    private readonly List<SbTriggerRule> _triggers = [];
    private readonly Dictionary<string, string> _subActionAliases = [];
    private readonly Dictionary<string, string> _triggerAliases = [];

    public IReadOnlyList<SbSubActionRule> SubActions => _subActions;
    public IReadOnlyList<SbTriggerRule> Triggers => _triggers;

    public StreamerBotMappingTable Add(SbSubActionRule rule)
    {
        _subActions.RemoveAll(r => r.Name == rule.Name);
        _subActions.Add(rule);
        return this;
    }

    public StreamerBotMappingTable Add(SbTriggerRule rule)
    {
        _triggers.RemoveAll(r => r.Name == rule.Name);
        _triggers.Add(rule);
        return this;
    }

    /// <summary>Maps extra type keys (e.g. numeric codes) to rule names.</summary>
    public StreamerBotMappingTable AddAliases(IReadOnlyDictionary<string, string>? subActions, IReadOnlyDictionary<string, string>? triggers)
    {
        foreach (var (key, rule) in subActions ?? new Dictionary<string, string>()) _subActionAliases[Normalize(key)] = rule;
        foreach (var (key, rule) in triggers ?? new Dictionary<string, string>()) _triggerAliases[Normalize(key)] = rule;
        return this;
    }

    /// <summary>Reads aliases from JSON (<c>{"subActions": {...}, "triggers": {...}}</c>). Bad JSON throws <see cref="TransferFormatException"/>.</summary>
    public StreamerBotMappingTable AddAliasesJson(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new TransferFormatException("Mapping file must be a JSON object.");
            static Dictionary<string, string> Read(JsonObject? o) =>
                o?.Where(kv => AsString(kv.Value) is not null).ToDictionary(kv => kv.Key, kv => AsString(kv.Value)!) ?? [];
            return AddAliases(Read(Obj(root, "subActions")), Read(Obj(root, "triggers")));
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException("Mapping file is not valid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>Finds the rule for a sub-action. <paramref name="inferred"/> is true when only the field shape matched.</summary>
    public SbSubActionRule? FindSubAction(JsonObject source, out bool inferred) => Find(_subActions, _subActionAliases, source, out inferred);

    public SbTriggerRule? FindTrigger(JsonObject source, out bool inferred) => Find(_triggers, _triggerAliases, source, out inferred);

    /// <summary>The type key used for matching and reports: the "type" value, else the "$type" class name.</summary>
    public static string TypeKeyOf(JsonObject source)
    {
        var type = Str(source, "type", "subActionType", "triggerType", "kind");
        if (!string.IsNullOrWhiteSpace(type)) return type.Trim();
        return ClassName(source) ?? "";
    }

    private static string? ClassName(JsonObject source)
    {
        if (Str(source, "$type") is not { Length: > 0 } clr) return null;
        var name = clr.Split(',')[0].Trim();
        name = name[(name.LastIndexOf('.') + 1)..];
        name = name[(name.LastIndexOf('+') + 1)..];
        foreach (var suffix in new[] { "SubAction", "Trigger", "Action" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    private static T? Find<T>(List<T> rules, Dictionary<string, string> aliases, JsonObject source, out bool inferred) where T : SbRule
    {
        inferred = false;
        foreach (var key in new[] { Str(source, "type", "subActionType", "triggerType", "kind"), ClassName(source) })
        {
            var norm = Normalize(key);
            if (norm.Length == 0) continue;
            if (aliases.TryGetValue(norm, out var alias) && rules.FirstOrDefault(r => r.Name == alias) is { } aliased) return aliased;
            if (rules.FirstOrDefault(r => r.TypeKeys.Any(k => Normalize(k) == norm)) is { } byKey) return byKey;
        }
        var best = rules
            .SelectMany(r => r.Signatures.Select(sig => (Rule: r, Sig: sig)))
            .Where(x => x.Sig.Count > 0 && x.Sig.All(f => Has(source, f)) && !x.Rule.Excludes.Any(f => Has(source, f)))
            .OrderByDescending(x => x.Sig.Count)
            .FirstOrDefault();
        inferred = best.Rule is not null;
        return best.Rule;
    }

    /// <summary>"Name → target (type keys)" lines for documentation and the UI.</summary>
    public IEnumerable<string> Describe() =>
        _subActions.Select(r => $"Sub-action {r.Name} → {r.TargetType}")
            .Concat(_triggers.Select(r => $"Trigger {r.Name} → {r.TargetType}"));

    /// <summary>The built-in rules (see StreamerBotRules.cs and docs/streamerbot-import-codes.md).</summary>
    public static StreamerBotMappingTable Default { get; } = CreateDefault();
}

/// <summary>Thrown by a mapping when the item matched a rule but cannot be converted; the importer keeps a placeholder.</summary>
public sealed class SbMappingException(string message) : Exception(message);
