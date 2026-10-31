using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Transfer;

/// <summary>What to put in an export.</summary>
public sealed class ExportSelection
{
    public HashSet<Guid> ActionIds { get; } = [];
    public HashSet<Guid> CommandIds { get; } = [];
    public HashSet<Guid> TimerIds { get; } = [];

    /// <summary>Global variables to include (by name).</summary>
    public HashSet<string> GlobalNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Also include what the selected actions use: actions they run (transitively), their queues, and the commands and
    /// timers their triggers and steps name. Default on, so an import works on its own.
    /// </summary>
    public bool IncludeDependencies { get; set; } = true;

    public TransferHeader Header { get; set; } = new();
}

/// <summary>Builds a <see cref="TransferBundle"/> from the current profile. Pure: copies, never edits.</summary>
public static class BundleExporter
{
    public static TransferBundle Create(ProfileSnapshot profile, IReadOnlyList<CommandDefinition> commands,
        IReadOnlyList<GlobalVariableInfo> globals, ExportSelection selection)
    {
        var actions = profile.Actions.Where(a => selection.ActionIds.Contains(a.Id)).ToList();
        var commandIds = new HashSet<Guid>(selection.CommandIds);
        var timerIds = new HashSet<Guid>(selection.TimerIds);
        var queues = new List<QueueDefinition>();

        if (selection.IncludeDependencies)
        {
            var seen = actions.Select(a => a.Id).ToHashSet();
            for (var i = 0; i < actions.Count; i++)
            {
                foreach (var (kind, value) in TransferReferences.Find(actions[i]))
                {
                    switch (kind)
                    {
                        case TransferKind.Action:
                            if (profile.FindAction(value) is { } target && seen.Add(target.Id)) actions.Add(target);
                            break;
                        case TransferKind.Queue:
                            if (profile.FindQueue(value) is { } queue && queues.All(q => q.Id != queue.Id)) queues.Add(queue);
                            break;
                        case TransferKind.Command:
                            if (FindCommand(commands, value) is { } command) commandIds.Add(command.Id);
                            break;
                        case TransferKind.Timer:
                            if (FindTimer(profile.Timers, value) is { } timer) timerIds.Add(timer.Id);
                            break;
                    }
                }
            }
        }

        var header = Json.Clone(selection.Header);
        if (header.Generator.Length == 0) header.Generator = "Cuebot " + AppVersion.Current;
        return new TransferBundle
        {
            Header = header,
            Actions = actions.Select(Json.Clone).ToList(),
            Queues = queues.Select(Json.Clone).ToList(),
            Commands = commands.Where(c => commandIds.Contains(c.Id)).Select(Json.Clone).ToList(),
            Timers = profile.Timers.Where(t => timerIds.Contains(t.Id)).Select(Json.Clone).ToList(),
            Globals = globals.Where(g => selection.GlobalNames.Contains(g.Name))
                .Select(g => new TransferGlobal { Name = g.Name, Value = JsonSerializer.SerializeToElement(g.Value, Json.Options), Persisted = g.Persisted })
                .ToList(),
        };
    }

    internal static CommandDefinition? FindCommand(IEnumerable<CommandDefinition> commands, string idOrName) =>
        Guid.TryParse(idOrName, out var id)
            ? commands.FirstOrDefault(c => c.Id == id)
            : commands.FirstOrDefault(c => string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    internal static TimerDefinition? FindTimer(IEnumerable<TimerDefinition> timers, string idOrName) =>
        Guid.TryParse(idOrName, out var id)
            ? timers.FirstOrDefault(t => t.Id == id)
            : timers.FirstOrDefault(t => string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase));
}
