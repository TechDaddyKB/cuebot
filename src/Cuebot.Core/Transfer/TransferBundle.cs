using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;

namespace Cuebot.Core.Transfer;

/// <summary>
/// A self-describing, versioned Cuebot export: selected actions (with their sub-actions and triggers), the queues
/// they use, commands, timers and optionally global variables, plus an author/description header.
/// Serialized with <see cref="Storage.Json.Options"/> (camelCase, enums as text). See <see cref="TransferCodec"/>
/// for the string and file forms.
/// </summary>
public sealed class TransferBundle
{
    /// <summary>Value of <see cref="Format"/>; anything else is not a Cuebot bundle.</summary>
    public const string FormatId = "cuebot.bundle";

    /// <summary>The bundle schema version this build writes. Readers accept this and older versions.</summary>
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public TransferHeader Header { get; set; } = new();
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<QueueDefinition> Queues { get; set; } = [];
    public List<CommandDefinition> Commands { get; set; } = [];
    public List<TimerDefinition> Timers { get; set; } = [];
    public List<TransferGlobal> Globals { get; set; } = [];

    public bool IsEmpty => Actions.Count + Queues.Count + Commands.Count + Timers.Count + Globals.Count == 0;
}

/// <summary>Who made a bundle and what it is for. Purely informational.</summary>
public sealed class TransferHeader
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The author's own version label for the bundle ("1.2"), not the schema version.</summary>
    public string BundleVersion { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The program that wrote the bundle, e.g. "Cuebot 0.1.0" or "Streamer.bot import".</summary>
    public string Generator { get; set; } = "";
}

/// <summary>A global variable in a bundle. The value keeps its JSON type (string, number, boolean or null).</summary>
public sealed class TransferGlobal
{
    public string Name { get; set; } = "";
    public JsonElement Value { get; set; }
    public bool Persisted { get; set; } = true;
}
