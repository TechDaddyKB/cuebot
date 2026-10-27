using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Transfer;

/// <summary>The profile an import goes into (read-only copies).</summary>
public sealed record TransferTarget(
    IReadOnlyList<ActionDefinition> Actions,
    IReadOnlyList<QueueDefinition> Queues,
    IReadOnlyList<CommandDefinition> Commands,
    IReadOnlyList<TimerDefinition> Timers,
    IReadOnlyCollection<string> GlobalNames)
{
    public static TransferTarget Empty { get; } = new([], [], [], [], []);

    public static TransferTarget From(ProfileSnapshot profile, IReadOnlyList<CommandDefinition> commands, IEnumerable<string> globals) =>
        new(profile.Actions, profile.Queues, commands, profile.Timers, globals.ToArray());
}

/// <summary>
/// Turns a bundle into a reviewable <see cref="ImportPlan"/> and a plan into <see cref="ImportChanges"/>. Pure: it
/// never touches the live profile and never runs anything.
/// </summary>
public static class BundleImporter
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static ImportPlan Preview(TransferBundle bundle, TransferTarget target, PayloadKind source = PayloadKind.Cuebot,
        ImportReport? report = null)
    {
        var plan = new ImportPlan { Source = source, Bundle = bundle, Report = report ?? new ImportReport() };

        foreach (var queue in bundle.Queues)
            plan.Items.Add(Item(TransferKind.Queue, queue.Id, queue.Name, "", target.Queues.Select(q => (q.Id, q.Name))));

        foreach (var action in bundle.Actions)
        {
            var steps = TransferReferences.Walk(action.SubActions).ToList();
            var item = Item(TransferKind.Action, action.Id, action.Name, action.Group, target.Actions.Select(a => (a.Id, a.Name)),
                containsCode: steps.Any(s => s.Type.StartsWith("code.", StringComparison.OrdinalIgnoreCase)),
                placeholders: steps.Count(IsPlaceholder));
            foreach (var trigger in action.Triggers)
                item.Details.Add($"Trigger: {trigger.Type}{Describe(trigger.Settings)}{(trigger.Enabled ? "" : " (disabled)")}");
            foreach (var step in steps)
                item.Details.Add($"Step: {(IsPlaceholder(step) ? "placeholder " + step.Parameters.GetValueOrDefault(PlaceholderTypeKey) : step.Type)}{(step.Enabled ? "" : " (disabled)")}");
            if (item.ContainsCode) item.Warnings.Add("Contains script code. Read it before you enable or run this action.");
            if (item.PlaceholderCount > 0) item.Warnings.Add($"{item.PlaceholderCount} step(s) could not be converted and were kept as disabled placeholders.");
            foreach (var (kind, value) in TransferReferences.Find(action).Distinct())
                if (!Resolves(kind, value, bundle, target))
                    item.Warnings.Add($"Uses {kind.ToString().ToLowerInvariant()} '{value}', which is neither in the import nor in your profile.");
            plan.Items.Add(item);
        }

        foreach (var command in bundle.Commands)
        {
            var item = Item(TransferKind.Command, command.Id, command.Name, command.Group, target.Commands.Select(c => (c.Id, c.Name)));
            item.Details.Add("Commands: " + string.Join(", ", command.Commands));
            plan.Items.Add(item);
        }

        foreach (var timer in bundle.Timers)
        {
            var item = Item(TransferKind.Timer, timer.Id, timer.Name, "", target.Timers.Select(t => (t.Id, t.Name)));
            item.Details.Add(timer.Cron is { Length: > 0 } cron ? "Cron: " + cron : $"Every {timer.IntervalSeconds} s");
            plan.Items.Add(item);
        }

        foreach (var global in bundle.Globals)
        {
            var exists = target.GlobalNames.Contains(global.Name, Names);
            var item = new ImportItem
            {
                Kind = TransferKind.Global, Name = global.Name,
                Conflict = exists ? ConflictKind.SameName : ConflictKind.None, ConflictTargetName = exists ? global.Name : "",
            };
            item.Details.Add($"Value: {global.Value.ToString()}{(global.Persisted ? "" : " (temporary)")}");
            plan.Items.Add(item);
        }
        return plan;
    }

    /// <summary>Works out ids, names and rewritten references for the included items of <paramref name="plan"/>.</summary>
    public static ImportChanges Resolve(ImportPlan plan, TransferTarget target)
    {
        var changes = new ImportChanges();
        var bundle = plan.Bundle;
        // (kind, old id or old name) → new value; ids and names are both keys so either form of reference is remapped.
        var map = new Dictionary<(TransferKind, string), string>(new KeyComparer());
        var taken = new Dictionary<TransferKind, HashSet<string>>
        {
            [TransferKind.Action] = target.Actions.Select(a => a.Name).ToHashSet(Names),
            [TransferKind.Queue] = target.Queues.Select(q => q.Name).ToHashSet(Names),
            [TransferKind.Command] = target.Commands.Select(c => c.Name).ToHashSet(Names),
            [TransferKind.Timer] = target.Timers.Select(t => t.Name).ToHashSet(Names),
        };

        (Guid Id, string Name)? Decide(ImportItem item, Guid sourceId, string name)
        {
            if (!item.Include || (item.HasConflict && item.Mode == ImportMode.Skip))
            {
                changes.Skipped++;
                // References to a skipped item fall back to the existing one it clashed with.
                if (item.ConflictTargetId is { } existing)
                {
                    map[(item.Kind, sourceId.ToString())] = existing.ToString();
                    map[(item.Kind, name)] = item.ConflictTargetName;
                }
                return null;
            }
            Guid id;
            var finalName = name;
            if (!item.HasConflict)
            {
                id = sourceId;
                changes.Added++;
            }
            else if (item.Mode == ImportMode.Overwrite)
            {
                id = item.ConflictTargetId ?? sourceId;
                changes.Overwritten++;
            }
            else
            {
                id = Guid.NewGuid();
                finalName = UniqueName(name, taken[item.Kind]);
                if (!Names.Equals(finalName, name)) changes.Renamed.Add($"{name} → {finalName}");
                changes.Added++;
            }
            taken[item.Kind].Add(finalName);
            map[(item.Kind, sourceId.ToString())] = id.ToString();
            map[(item.Kind, name)] = finalName;
            return (id, finalName);
        }

        ImportItem ItemFor(TransferKind kind, Guid id) => plan.Items.First(i => i.Kind == kind && i.SourceId == id);

        foreach (var queue in bundle.Queues)
            if (Decide(ItemFor(TransferKind.Queue, queue.Id), queue.Id, queue.Name) is { } d)
            {
                var copy = Json.Clone(queue);
                (copy.Id, copy.Name) = d;
                changes.Queues.Add(copy);
            }

        foreach (var command in bundle.Commands)
            if (Decide(ItemFor(TransferKind.Command, command.Id), command.Id, command.Name) is { } d)
            {
                var copy = Json.Clone(command);
                (copy.Id, copy.Name) = d;
                changes.Commands.Add(copy);
            }

        foreach (var timer in bundle.Timers)
            if (Decide(ItemFor(TransferKind.Timer, timer.Id), timer.Id, timer.Name) is { } d)
            {
                var copy = Json.Clone(timer);
                (copy.Id, copy.Name) = d;
                changes.Timers.Add(copy);
            }

        // Names of every action first, so references between imported actions resolve whatever the order.
        var pending = new List<ActionDefinition>();
        foreach (var action in bundle.Actions)
        {
            var item = ItemFor(TransferKind.Action, action.Id);
            if (Decide(item, action.Id, action.Name) is not { } d) continue;
            var copy = Json.Clone(action);
            copy.Name = d.Name;
            if (item.HasConflict && item.Mode == ImportMode.AddAsNew) TransferReferences.RenewIds(copy, d.Id);
            else copy.Id = d.Id;
            pending.Add(copy);
        }
        foreach (var copy in pending)
        {
            TransferReferences.Rewrite(copy, (kind, value) => map.GetValueOrDefault((kind, value)));
            changes.Actions.Add(copy);
        }

        foreach (var global in bundle.Globals)
        {
            var item = plan.Items.First(i => i.Kind == TransferKind.Global && Names.Equals(i.Name, global.Name));
            if (!item.Include || (item.HasConflict && item.Mode != ImportMode.Overwrite))
            {
                changes.Skipped++;
                continue;
            }
            if (item.HasConflict) changes.Overwritten++;
            else changes.Added++;
            changes.Globals.Add(global);
        }
        return changes;
    }

    /// <summary>"Name", or "Name (2)", "Name (3)", … whichever is free.</summary>
    public static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Parameter key holding the original Streamer.bot type on a placeholder step.</summary>
    public const string PlaceholderTypeKey = "sourceType";

    /// <summary>Parameter key marking a placeholder step (value: the importer that made it).</summary>
    public const string PlaceholderMarkerKey = "importPlaceholder";

    public static bool IsPlaceholder(SubActionDefinition step) => step.Parameters.ContainsKey(PlaceholderMarkerKey);

    private static ImportItem Item(TransferKind kind, Guid id, string name, string group, IEnumerable<(Guid Id, string Name)> existing,
        bool containsCode = false, int placeholders = 0)
    {
        var list = existing.ToList();
        var byId = list.FirstOrDefault(e => e.Id == id);
        var byName = list.FirstOrDefault(e => Names.Equals(e.Name, name));
        var (conflict, target) = byId.Name is not null ? (ConflictKind.SameId, byId)
            : byName.Name is not null ? (ConflictKind.SameName, byName)
            : (ConflictKind.None, default);
        return new ImportItem
        {
            Kind = kind, SourceId = id, Name = name, Group = group, Conflict = conflict,
            ConflictTargetId = conflict == ConflictKind.None ? null : target.Id, ConflictTargetName = target.Name ?? "",
            ContainsCode = containsCode, PlaceholderCount = placeholders,
        };
    }

    private static bool Resolves(TransferKind kind, string value, TransferBundle bundle, TransferTarget target)
    {
        bool Match(Guid id, string name) =>
            Names.Equals(name, value) || (Guid.TryParse(value, out var g) && g == id);
        return kind switch
        {
            TransferKind.Action => bundle.Actions.Any(a => Match(a.Id, a.Name)) || target.Actions.Any(a => Match(a.Id, a.Name)),
            TransferKind.Queue => Names.Equals(value, "Default")
                                  || bundle.Queues.Any(q => Match(q.Id, q.Name)) || target.Queues.Any(q => Match(q.Id, q.Name)),
            TransferKind.Command => bundle.Commands.Any(c => Match(c.Id, c.Name)) || target.Commands.Any(c => Match(c.Id, c.Name)),
            TransferKind.Timer => bundle.Timers.Any(t => Match(t.Id, t.Name)) || target.Timers.Any(t => Match(t.Id, t.Name)),
            _ => true,
        };
    }

    private static string Describe(Dictionary<string, string> settings)
    {
        var shown = settings.Where(kv => kv.Value.Length > 0).Select(kv => $"{kv.Key}={kv.Value}").ToList();
        return shown.Count == 0 ? "" : " (" + string.Join(", ", shown) + ")";
    }

    private sealed class KeyComparer : IEqualityComparer<(TransferKind, string)>
    {
        public bool Equals((TransferKind, string) x, (TransferKind, string) y) => x.Item1 == y.Item1 && Names.Equals(x.Item2, y.Item2);
        public int GetHashCode((TransferKind, string) obj) => HashCode.Combine(obj.Item1, Names.GetHashCode(obj.Item2));
    }
}
