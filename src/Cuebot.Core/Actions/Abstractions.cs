using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Actions;

/// <summary>Outcome of a sub-action: continue, stop the action, or run one of its branches.</summary>
public readonly record struct SubActionResult(bool Continue, string? Branch = null)
{
    public static SubActionResult Next => new(true);
    public static SubActionResult Stop => new(false);
    public static SubActionResult TakeBranch(string branch) => new(true, branch);
}

/// <summary>A kind of step an action can perform. Registered through DI as a singleton.</summary>
public interface ISubActionHandler
{
    /// <summary>Stable id stored in actions.json, e.g. "twitch.sendMessage".</summary>
    string TypeId { get; }
    string Category { get; }
    string DisplayName { get; }
    IReadOnlyList<ParameterDescriptor> Parameters { get; }

    /// <summary>Named outputs (e.g. "True", "False"); empty for plain steps.</summary>
    IReadOnlyList<string> Branches => [];

    /// <param name="parameters">Values with %arg% and ~global~ variables already substituted (except Code).</param>
    Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct);
}

/// <summary>A kind of trigger: which events it listens to and how its settings filter them.</summary>
public interface ITriggerType
{
    string TypeId { get; }
    string Category { get; }
    string DisplayName { get; }
    IReadOnlyCollection<string> EventTypes { get; }
    IReadOnlyList<ParameterDescriptor> Parameters { get; }

    /// <summary>Decide whether the event starts the action. May add extra arguments to <paramref name="args"/>.</summary>
    bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args);
}

/// <summary>Trigger type for the common case "this event, optionally filtered".</summary>
public sealed class EventTriggerType(
    string typeId,
    string category,
    string displayName,
    string eventType,
    IReadOnlyList<ParameterDescriptor>? parameters = null,
    Func<BotEvent, ParameterValues, IDictionary<string, object?>, bool>? filter = null) : ITriggerType
{
    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => displayName;
    public IReadOnlyCollection<string> EventTypes { get; } = [eventType];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = parameters ?? [];

    public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args) =>
        filter?.Invoke(e, settings, args) ?? true;
}

/// <summary>State of one action run, shared by all of its sub-actions.</summary>
public sealed class ActionRunContext
{
    internal ActionRunContext(ActionDefinition action, Dictionary<string, object?> args, ActionEngine engine,
        GlobalVariables globals, AppLog log, int depth)
    {
        Action = action;
        Args = args;
        Engine = engine;
        Globals = globals;
        Log = log;
        Depth = depth;
    }

    public ActionDefinition Action { get; }

    /// <summary>Case-insensitive arguments: trigger data plus anything sub-actions add.</summary>
    public Dictionary<string, object?> Args { get; }

    public ActionEngine Engine { get; }
    public GlobalVariables Globals { get; }
    public AppLog Log { get; }

    /// <summary>Nesting level when actions run other actions; guards against infinite recursion.</summary>
    public int Depth { get; }

    public string Resolve(string template) => VariableResolver.Resolve(template, Args, Globals.Get);
}
