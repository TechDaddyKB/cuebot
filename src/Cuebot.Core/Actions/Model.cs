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

    /// <summary>When false (default) runs of this action are queued one at a time.</summary>
    public bool Concurrent { get; set; }

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
}
