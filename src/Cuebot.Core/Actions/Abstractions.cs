using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Actions;

/// <summary>How a sub-action affects the enclosing loop, if any.</summary>
public enum LoopSignal
{
    /// <summary>Not a loop-control result.</summary>
    None,

    /// <summary>Leave the nearest enclosing loop (Loop Break).</summary>
    Break,

    /// <summary>Skip the rest of the nearest loop's body and start its next iteration (Loop Continue).</summary>
    Continue,
}

/// <summary>Outcome of a sub-action: continue, stop the action, control a loop, or run one of its branches.</summary>
public readonly record struct SubActionResult(bool Continue, string? Branch = null)
{
    /// <summary>
    /// Loop control (only meaningful when <see cref="Continue"/> is false). Loop signals propagate out of
    /// branches and groups to the nearest enclosing loop; outside any loop they end the action like Stop.
    /// </summary>
    public LoopSignal Loop { get; init; }

    public static SubActionResult Next => new(true);
    public static SubActionResult Stop => new(false);
    public static SubActionResult TakeBranch(string branch) => new(true, branch);
    public static SubActionResult BreakLoop => new(false) { Loop = LoopSignal.Break };
    public static SubActionResult ContinueLoop => new(false) { Loop = LoopSignal.Continue };

    /// <summary>The chain outcome this result produces when it has no branch to run.</summary>
    public FlowOutcome ToOutcome() => Continue
        ? FlowOutcome.Completed
        : Loop switch
        {
            LoopSignal.Break => FlowOutcome.Break,
            LoopSignal.Continue => FlowOutcome.Continue,
            _ => FlowOutcome.Stop,
        };
}

/// <summary>How a chain of sub-actions (an action body, a branch, a group) finished.</summary>
public enum FlowOutcome
{
    /// <summary>Every step ran; execution continues after the chain.</summary>
    Completed,

    /// <summary>A step stopped the whole action.</summary>
    Stop,

    /// <summary>A Loop Break is travelling up to the nearest loop.</summary>
    Break,

    /// <summary>A Loop Continue is travelling up to the nearest loop.</summary>
    Continue,
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

/// <summary>
/// Optional: a sub-action whose branch names depend on its parameters (e.g. Switch: one branch per case plus
/// "Default"). The engine and both editors should call <see cref="SubActionBranches.For"/>, which prefers this
/// list over <see cref="ISubActionHandler.Branches"/>. Names must be unique. Child lists of branches that
/// disappear (e.g. a deleted case) stay in actions.json but never run.
/// </summary>
public interface IDynamicBranchesProvider
{
    /// <param name="parameters">The step's raw (unresolved) parameter values; a missing key means its default.</param>
    IReadOnlyList<string> GetBranches(IReadOnlyDictionary<string, string> parameters);
}

/// <summary>
/// Optional: a sub-action that drives its own branches (groups, loops). The engine calls
/// <see cref="ExecuteFlowAsync"/> instead of <see cref="ISubActionHandler.ExecuteAsync"/>.
/// </summary>
public interface IFlowContainerHandler : ISubActionHandler
{
    /// <summary>Run the step; use <paramref name="step"/> to resolve parameters and run branches.</summary>
    /// <returns>How the step finished. Containers that are not loops return a branch's
    /// <see cref="FlowOutcome.Break"/>/<see cref="FlowOutcome.Continue"/> unchanged so it reaches the nearest loop.</returns>
    Task<FlowOutcome> ExecuteFlowAsync(ActionRunContext context, FlowStep step, CancellationToken ct);
}

public static class SubActionBranches
{
    /// <summary>The branches a step offers: dynamic ones when the handler provides them, else the declared ones.</summary>
    public static IReadOnlyList<string> For(ISubActionHandler handler, SubActionDefinition step) =>
        handler is IDynamicBranchesProvider dynamic ? dynamic.GetBranches(step.Parameters) : handler.Branches;
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

    /// <summary>A copy with its own argument dictionary, for steps that run concurrently.</summary>
    internal ActionRunContext Fork() =>
        new(Action, new Dictionary<string, object?>(Args, StringComparer.OrdinalIgnoreCase), Engine, Globals, Log, Depth);
}
