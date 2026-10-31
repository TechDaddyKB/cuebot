using System.Collections.Frozen;
using System.Text.Json;

namespace Cuebot.Core.Actions;

/// <summary>Plain-language help for one trigger or sub-action type.</summary>
/// <param name="Description">What it does, in a sentence or two a beginner understands.</param>
/// <param name="Fields">Help for each setting, by parameter key.</param>
/// <param name="Variables">The %variables% it gives the action, by name (without the % signs).</param>
public sealed record HelpEntry(
    string Description,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>
/// The help text shown in the action editor, the API catalogue and the documentation site. It lives in
/// <c>src/Cuebot.Core/Help/*.json</c> (embedded), one file per area, so the wording can be reviewed in one place
/// without touching module code. A field without an entry falls back to its descriptor's own
/// <see cref="ParameterDescriptor.Help"/>.
/// </summary>
public static class ActionHelp
{
    private const string ResourcePrefix = "Cuebot.Core.Help.";

    private static readonly Lazy<(FrozenDictionary<string, HelpEntry> Triggers, FrozenDictionary<string, HelpEntry> SubActions)> Entries =
        new(Load);

    public static HelpEntry? Trigger(string typeId) => Entries.Value.Triggers.GetValueOrDefault(typeId);

    public static HelpEntry? SubAction(string typeId) => Entries.Value.SubActions.GetValueOrDefault(typeId);

    public static IReadOnlyCollection<string> TriggerIds => Entries.Value.Triggers.Keys;

    public static IReadOnlyCollection<string> SubActionIds => Entries.Value.SubActions.Keys;

    public static string? Describe(ITriggerType type) => Trigger(type.TypeId)?.Description;

    public static string? Describe(ISubActionHandler handler) => SubAction(handler.TypeId)?.Description;

    /// <summary>The help for one setting: the help file's wording, else the module's own.</summary>
    public static string? Field(HelpEntry? entry, ParameterDescriptor descriptor) =>
        entry?.Fields.GetValueOrDefault(descriptor.Key) is { Length: > 0 } text ? text : descriptor.Help;

    /// <summary>The descriptors with the help file's wording applied, for editors and catalogues.</summary>
    public static IReadOnlyList<ParameterDescriptor> WithHelp(HelpEntry? entry, IReadOnlyList<ParameterDescriptor> parameters) =>
        entry is null ? parameters : parameters.Select(d => d with { Help = Field(entry, d) }).ToArray();

    private static (FrozenDictionary<string, HelpEntry>, FrozenDictionary<string, HelpEntry>) Load()
    {
        var triggers = new Dictionary<string, HelpEntry>(StringComparer.OrdinalIgnoreCase);
        var subActions = new Dictionary<string, HelpEntry>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(ActionHelp).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var file = JsonSerializer.Deserialize<HelpFile>(stream, Options)
                ?? throw new InvalidDataException($"Help file {name} is empty");
            Merge(file.Triggers, triggers, name);
            Merge(file.SubActions, subActions, name);
        }
        return (triggers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), subActions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static void Merge(Dictionary<string, HelpFileEntry>? source, Dictionary<string, HelpEntry> target, string file)
    {
        if (source is null) return;
        foreach (var (id, e) in source)
        {
            if (!target.TryAdd(id, new HelpEntry(e.Description ?? "", e.Fields ?? [], e.Variables ?? [])))
                throw new InvalidDataException($"Help for {id} appears twice (again in {file})");
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record HelpFile(Dictionary<string, HelpFileEntry>? Triggers, Dictionary<string, HelpFileEntry>? SubActions);

    private sealed record HelpFileEntry(string? Description, Dictionary<string, string>? Fields, Dictionary<string, string>? Variables);
}
