using Cuebot.Core.Actions;
using Cuebot.Core.Commands;

namespace Cuebot.Core.Transfer;

/// <summary>How an imported item clashes with what is already in the profile.</summary>
public enum ConflictKind
{
    None,

    /// <summary>An existing item has the same id (usually the same item exported earlier).</summary>
    SameId,

    /// <summary>An existing item has the same name but a different id.</summary>
    SameName,
}

/// <summary>What to do with an item that conflicts. Items without a conflict are always added as they are.</summary>
public enum ImportMode
{
    /// <summary>Replace the existing item (it keeps its id, so other references to it still work).</summary>
    Overwrite,

    /// <summary>Add a copy with a new id and, if the name is taken, a new name; references in the import follow it.</summary>
    AddAsNew,

    /// <summary>Keep the existing item and drop the imported one.</summary>
    Skip,
}

/// <summary>One item in an import preview. <see cref="Include"/> and <see cref="Mode"/> are the user's choices.</summary>
public sealed class ImportItem
{
    public required TransferKind Kind { get; init; }

    /// <summary>Id in the bundle (null for global variables, which are keyed by name).</summary>
    public Guid? SourceId { get; init; }

    public required string Name { get; init; }
    public string Group { get; init; } = "";
    public ConflictKind Conflict { get; init; }

    /// <summary>The existing item this one clashes with.</summary>
    public Guid? ConflictTargetId { get; init; }
    public string ConflictTargetName { get; init; } = "";

    public bool Include { get; set; } = true;
    public ImportMode Mode { get; set; } = ImportMode.Overwrite;

    /// <summary>Read-only summary lines for the preview tree (triggers, steps).</summary>
    public List<string> Details { get; } = [];

    /// <summary>Things to check for this item (missing references, scripts).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>The action contains C# or Python script steps.</summary>
    public bool ContainsCode { get; init; }

    /// <summary>Disabled placeholder steps standing in for Streamer.bot items Cuebot could not map.</summary>
    public int PlaceholderCount { get; init; }

    public bool HasConflict => Conflict != ConflictKind.None;
}

/// <summary>A Streamer.bot item that could not be mapped and was kept as a disabled placeholder step.</summary>
public sealed record ImportPlaceholder(string ActionName, string ItemKind, string OriginalType, string Json);

/// <summary>What an import mapped, kept as placeholders and wants the user to check.</summary>
public sealed class ImportReport
{
    /// <summary>One line per mapped item ("Send Message → twitch.sendMessage").</summary>
    public List<string> Mapped { get; } = [];

    public List<ImportPlaceholder> Placeholders { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>An import ready to review: the bundle, one <see cref="ImportItem"/> per bundle item, and the report.</summary>
public sealed class ImportPlan
{
    public required PayloadKind Source { get; init; }
    public required TransferBundle Bundle { get; init; }
    public List<ImportItem> Items { get; } = [];
    public ImportReport Report { get; init; } = new();

    public IEnumerable<ImportItem> Of(TransferKind kind) => Items.Where(i => i.Kind == kind);
}

/// <summary>The profile edits an import resolves to (pure data; <see cref="TransferService"/> applies it).</summary>
public sealed class ImportChanges
{
    public List<ActionDefinition> Actions { get; } = [];
    public List<QueueDefinition> Queues { get; } = [];
    public List<CommandDefinition> Commands { get; } = [];
    public List<TimerDefinition> Timers { get; } = [];
    public List<TransferGlobal> Globals { get; } = [];
    public int Added { get; set; }
    public int Overwritten { get; set; }
    public int Skipped { get; set; }

    /// <summary>Old name → new name for every item renamed by "add as new".</summary>
    public List<string> Renamed { get; } = [];

    public string Summary =>
        $"{Added} added, {Overwritten} overwritten, {Skipped} skipped" + (Renamed.Count > 0 ? $", {Renamed.Count} renamed" : "");
}
