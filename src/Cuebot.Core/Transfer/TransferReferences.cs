using Cuebot.Core.Actions;

namespace Cuebot.Core.Transfer;

/// <summary>Kinds of item a bundle carries.</summary>
public enum TransferKind { Action, Queue, Command, Timer, Global }

/// <summary>
/// Where actions point at other items. Cuebot links by name (or, for commands, name or id), so these are the
/// parameter/setting keys that hold such names. Extend <see cref="SubActionRefs"/> / <see cref="TriggerRefs"/> when a
/// module adds a step or trigger that names another action, command or timer.
/// </summary>
public static class TransferReferences
{
    /// <summary>Sub-action type → (parameter key, kind of item it names).</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, TransferKind Kind)> SubActionRefs =
        new Dictionary<string, (string, TransferKind)>(StringComparer.OrdinalIgnoreCase)
        {
            ["core.runAction"] = ("action", TransferKind.Action),
            ["core.setActionState"] = ("action", TransferKind.Action),
            ["core.getActionState"] = ("action", TransferKind.Action),
            ["timers.getState"] = ("timer", TransferKind.Timer),
            ["timers.setState"] = ("timer", TransferKind.Timer),
            ["commands.getState"] = ("command", TransferKind.Command),
            ["commands.setState"] = ("command", TransferKind.Command),
            ["commands.resetCounters"] = ("command", TransferKind.Command),
        };

    /// <summary>Trigger type → (setting key, kind of item it names).</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, TransferKind Kind)> TriggerRefs =
        new Dictionary<string, (string, TransferKind)>(StringComparer.OrdinalIgnoreCase)
        {
            ["command.triggered"] = ("command", TransferKind.Command),
            ["command.cooldown"] = ("command", TransferKind.Command),
            ["core.timer"] = ("timer", TransferKind.Timer),
        };

    /// <summary>A literal reference (not a %variable% or ~global~ that is only known when the action runs).</summary>
    public static bool IsLiteral(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOf('%') < 0 && value.IndexOf('~') < 0;

    /// <summary>Every literal reference in an action, including its queue.</summary>
    public static IEnumerable<(TransferKind Kind, string Value)> Find(ActionDefinition action)
    {
        if (!string.IsNullOrWhiteSpace(action.Queue)) yield return (TransferKind.Queue, action.Queue.Trim());
        foreach (var trigger in action.Triggers)
            if (TriggerRefs.TryGetValue(trigger.Type, out var r) && trigger.Settings.TryGetValue(r.Key, out var v) && IsLiteral(v))
                yield return (r.Kind, v.Trim());
        foreach (var step in Walk(action.SubActions))
            if (SubActionRefs.TryGetValue(step.Type, out var r) && step.Parameters.TryGetValue(r.Key, out var v) && IsLiteral(v))
                yield return (r.Kind, v.Trim());
    }

    /// <summary>
    /// Rewrites literal references in place: <paramref name="map"/> returns the replacement for (kind, old value),
    /// or null to leave it.
    /// </summary>
    public static void Rewrite(ActionDefinition action, Func<TransferKind, string, string?> map)
    {
        if (!string.IsNullOrWhiteSpace(action.Queue) && map(TransferKind.Queue, action.Queue.Trim()) is { } q) action.Queue = q;
        foreach (var trigger in action.Triggers)
            if (TriggerRefs.TryGetValue(trigger.Type, out var r) && trigger.Settings.TryGetValue(r.Key, out var v) && IsLiteral(v)
                && map(r.Kind, v.Trim()) is { } nv)
                trigger.Settings[r.Key] = nv;
        foreach (var step in Walk(action.SubActions))
            if (SubActionRefs.TryGetValue(step.Type, out var r) && step.Parameters.TryGetValue(r.Key, out var v) && IsLiteral(v)
                && map(r.Kind, v.Trim()) is { } nv)
                step.Parameters[r.Key] = nv;
    }

    /// <summary>Every sub-action, depth first, including branch children.</summary>
    public static IEnumerable<SubActionDefinition> Walk(IEnumerable<SubActionDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var branch in step.Branches.Values)
                foreach (var child in Walk(branch))
                    yield return child;
        }
    }

    /// <summary>Gives an action, its triggers and every sub-action fresh ids (used when importing as a copy).</summary>
    public static void RenewIds(ActionDefinition action, Guid id)
    {
        action.Id = id;
        foreach (var trigger in action.Triggers) trigger.Id = Guid.NewGuid();
        foreach (var step in Walk(action.SubActions)) step.Id = Guid.NewGuid();
    }
}
