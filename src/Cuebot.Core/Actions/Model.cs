namespace Cuebot.Core.Actions;

/// <summary>
/// An action is a flow tree: any enabled trigger starts it, then its sub-actions run in order.
/// A sub-action whose handler declares branches (e.g. If → True/False) owns a child list per branch;
/// the chosen branch runs before execution continues with the next sibling. The list editor and the
/// node graph editor are two views of this same structure.
/// </summary>
public sealed class ActionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New action";
    public string Group { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Only used when <see cref="Queue"/> is empty: when false (default) runs of this action wait for each other
    /// (one at a time); when true they overlap. Actions in a named queue follow that queue's Blocking setting instead.
    /// </summary>
    public bool Concurrent { get; set; }

    /// <summary>
    /// Name of the action queue runs wait in ("" = the built-in "Default" queue, non-blocking). JSON: "queue".
    /// Pausing a queue holds its runs as pending; a blocking queue runs one action at a time.
    /// </summary>
    public string Queue { get; set; } = "";

    /// <summary>Run only ONE random enabled top-level sub-action, chosen by <see cref="SubActionDefinition.Weight"/>. JSON: "randomAction".</summary>
    public bool RandomAction { get; set; }

    /// <summary>Run all enabled top-level sub-actions at the same time (each with its own copy of the arguments,
    /// merged back in order afterwards). Ignored when <see cref="RandomAction"/> is set. JSON: "concurrentSubActions".</summary>
    public bool ConcurrentSubActions { get; set; }

    /// <summary>Start even while the action's queue is paused. JSON: "alwaysRun".</summary>
    public bool AlwaysRun { get; set; }

    /// <summary>Keep runs out of the pending list and action history. JSON: "excludeFromHistory".</summary>
    public bool ExcludeFromHistory { get; set; }

    public List<TriggerDefinition> Triggers { get; set; } = [];
    public List<SubActionDefinition> SubActions { get; set; } = [];

    /// <summary>Canvas position of the action's entry node in the graph editor.</summary>
    public NodeLayout? Layout { get; set; }
}

public sealed class TriggerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Settings { get; set; } = [];
    public NodeLayout? Layout { get; set; }
}

public sealed class SubActionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>Child chains keyed by branch name, for handlers that declare branches.</summary>
    public Dictionary<string, List<SubActionDefinition>> Branches { get; set; } = [];

    /// <summary>
    /// Relative chance of being picked when the parent runs ONE random child (a Random action or a random Group).
    /// Default 1; 0 or less is never picked (unless every candidate is 0, then they are equally likely). JSON: "weight".
    /// </summary>
    public double Weight { get; set; } = 1;

    public NodeLayout? Layout { get; set; }

    public List<SubActionDefinition> Branch(string name)
    {
        if (!Branches.TryGetValue(name, out var list)) Branches[name] = list = [];
        return list;
    }
}

/// <summary>Position of a node on the graph canvas. Purely presentational.</summary>
public sealed record NodeLayout(double X, double Y);

public sealed class TimerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New timer";
    public int IntervalSeconds { get; set; } = 300;
    public bool Enabled { get; set; } = true;
}

/// <summary>Everything the user builds: actions and timers. Stored in actions.json.</summary>
public sealed class BotProfile
{
    public int Version { get; set; } = 1;
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<TimerDefinition> Timers { get; set; } = [];

    /// <summary>User-created action queues (the built-in "Default" queue is implicit). JSON: "queues".</summary>
    public List<QueueDefinition> Queues { get; set; } = [];
}

/// <summary>A named action queue. Paused state is runtime-only (every queue starts resumed).</summary>
public sealed class QueueDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New queue";

    /// <summary>When true, runs in this queue execute one at a time in arrival order. JSON: "blocking".</summary>
    public bool Blocking { get; set; }
}
