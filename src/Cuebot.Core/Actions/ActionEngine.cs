using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Actions;

/// <summary>A finished run (dashboard "recent runs"). <see cref="ActionHistory"/> has the full picture.</summary>
public sealed record ActionRunRecord(string ActionName, string Source, bool Success, TimeSpan Duration, string? Error)
{
    public Guid RunId { get; init; }
    public string Queue { get; init; } = QueueService.DefaultQueue;
    public ActionRunState State { get; init; }

    /// <summary>The action is marked "Exclude from history"; dashboards may want to hide it too.</summary>
    public bool ExcludedFromHistory { get; init; }

    public Guid ActionId { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>The run's arguments when it finished (a copy).</summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }
}

/// <summary>A run is starting (after any queue wait). <see cref="Args"/> is the live dictionary: copy it before leaving the handler.</summary>
public sealed record ActionRunStart(Guid RunId, ActionDefinition Action, string Source, int Depth, DateTimeOffset QueuedAt,
    DateTimeOffset StartedAt, IReadOnlyDictionary<string, object?> Args);

/// <summary>A sub-action is about to run. <see cref="Args"/> is the live dictionary: copy it before leaving the handler.</summary>
public sealed record SubActionStart(ActionDefinition Action, SubActionDefinition Step, ISubActionHandler Handler,
    IReadOnlyDictionary<string, object?> Args);

/// <summary>Matches events to triggers, queues runs and executes actions' flow trees.</summary>
public sealed class ActionEngine : IDisposable
{
    public const int MaxDepth = 16;
    private const string Source = "Actions";

    /// <summary>Arguments the engine adds to every run; Requeue drops them so the replay gets fresh ones.</summary>
    private static readonly string[] EngineArgs = ["actionName", "actionId", "runId", "runningActionId", "actionQueuedAt"];

    private readonly EventBus _bus;
    private readonly ProfileService _profile;
    private readonly Registry _registry;
    private readonly GlobalVariables _globals;
    private readonly AppLog _log;
    private readonly IRandomSource _random;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool _ownsQueues;
    private IDisposable? _subscription;

    public ActionEngine(EventBus bus, ProfileService profile, Registry registry, GlobalVariables globals, AppLog log,
        QueueService? queues = null, ActionHistory? history = null, IRandomSource? random = null)
    {
        _bus = bus;
        _profile = profile;
        _registry = registry;
        _globals = globals;
        _log = log;
        _ownsQueues = queues is null;
        Queues = queues ?? new QueueService(profile);
        History = history ?? new ActionHistory();
        _random = random ?? SystemRandomSource.Instance;
    }

    /// <summary>Raised when any run (including nested and excluded ones) finishes, on the run's thread.</summary>
    public event Action<ActionRunRecord>? RunCompleted;

    /// <summary>Raised when any run starts executing (after its queue), on the run's thread.</summary>
    public event Action<ActionRunStart>? RunStarted;

    /// <summary>Raised before each sub-action (including flow containers) runs, on the run's thread.</summary>
    public event Action<SubActionStart>? SubActionStarting;

    public QueueService Queues { get; }
    public ActionHistory History { get; }

    public void Start() => _subscription ??= _bus.Subscribe(OnEvent);

    /// <summary>Find actions triggered by the event and start them. Returns the started runs (for tests).</summary>
    public IReadOnlyList<Task<bool>> Dispatch(BotEvent e)
    {
        var matches = new List<TriggerMatch>();
        foreach (var action in _profile.Snapshot.Actions)
        {
            if (!action.Enabled) continue;
            foreach (var trigger in action.Triggers)
            {
                if (!trigger.Enabled) continue;
                var type = _registry.GetTrigger(trigger.Type);
                if (type is null || !type.EventTypes.Contains(e.Type)) continue;

                var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
                var settings = ParameterValues.From(type.Parameters, trigger.Settings);
                bool matched;
                try
                {
                    matched = type.Matches(e, trigger, settings, args);
                }
                catch (Exception ex)
                {
                    _log.Error(Source, $"Trigger '{type.DisplayName}' on '{action.Name}' failed", ex);
                    continue;
                }
                if (matched) matches.Add(new TriggerMatch(action, trigger, type, args, RangeTriggers.Width(type, settings)));
            }
        }

        var started = new List<Task<bool>>();
        var ran = new HashSet<Guid>();
        foreach (var m in RangeTriggers.MostSpecific(matches))
        {
            if (!ran.Add(m.Action.Id)) continue; // one run per event per action, even if several triggers match
            TriggerArgs.AddDerived(m.Args);
            AddTriggerArgs(m.Args, m.Type, e.Type);
            started.Add(RunAsync(m.Action, m.Args, source: m.Type.DisplayName));
        }
        return started;
    }

    public Task<bool> RunAsync(ActionDefinition action, IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken ct = default, string source = "Manual") =>
        RunAsync(action, args, depth: 0, source, ct);

    public ActionDefinition? FindAction(string name) => _profile.Snapshot.FindAction(name);

    public Task<bool> RunByNameAsync(string name, IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        var action = _profile.Snapshot.FindAction(name);
        if (action is null)
        {
            _log.Warn(Source, $"No action named '{name}'");
            return Task.FromResult(false);
        }
        return RunAsync(action, args, depth: 0, "Run action", ct);
    }

    /// <summary>
    /// "Test trigger": runs the action as if <paramref name="trigger"/> had fired with <paramref name="args"/>
    /// (adds triggerType/triggerName/eventType like a real event, plus isTest=true). The trigger's filter is not
    /// evaluated. Pass a null trigger to test with custom arguments only. The run goes through the action's queue.
    /// </summary>
    public Task<bool> TestTriggerAsync(ActionDefinition action, TriggerDefinition? trigger,
        IReadOnlyDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        var runArgs = args is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        var type = trigger is null ? null : _registry.GetTrigger(trigger.Type);
        TriggerArgs.AddDerived(runArgs);
        if (type is not null) AddTriggerArgs(runArgs, type, type.EventTypes.FirstOrDefault() ?? "");
        runArgs["isTest"] = true;
        return RunAsync(action, runArgs, depth: 0, $"Test: {type?.DisplayName ?? "custom arguments"}", ct);
    }

    /// <summary>
    /// Runs a past run's action again with the arguments it had when it was queued. Returns false when the run is
    /// unknown (evicted from history) or its action no longer exists.
    /// </summary>
    public Task<bool> RequeueAsync(Guid runId, CancellationToken ct = default)
    {
        var entry = History.Find(runId);
        var action = entry is null ? null : _profile.Snapshot.FindAction(entry.ActionId) ?? FindAction(entry.ActionName);
        if (entry is null || action is null)
        {
            _log.Warn(Source, entry is null ? "That run is no longer in the history" : $"No action named '{entry.ActionName}'");
            return Task.FromResult(false);
        }
        var args = new Dictionary<string, object?>(entry.ArgsWhenQueued, StringComparer.OrdinalIgnoreCase);
        foreach (var key in EngineArgs) args.Remove(key);
        return RunAsync(action, args, depth: 0, "Requeue", ct);
    }

    /// <summary>Cancels a pending or running run (e.g. "Break running" in the UI). Returns false if it already ended.</summary>
    public bool Cancel(Guid runId)
    {
        if (!_active.TryGetValue(runId, out var cts)) return false;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    internal async Task<bool> RunAsync(ActionDefinition action, IReadOnlyDictionary<string, object?>? args, int depth,
        string source, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"'{action.Name}' exceeded the maximum nesting depth of {MaxDepth}; is an action running itself?");
        }

        var runId = Guid.NewGuid();
        var queue = QueueService.Normalize(action.Queue);
        var queuedAt = DateTimeOffset.Now;
        var runArgs = args is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        runArgs["actionName"] = action.Name;
        runArgs["actionId"] = action.Id.ToString();
        runArgs["runId"] = runId.ToString();
        runArgs["runningActionId"] = runId.ToString(); // Streamer.bot name
        runArgs["actionQueuedAt"] = queuedAt;

        var tracked = !action.ExcludeFromHistory;
        if (tracked)
        {
            History.Add(new ActionRunEntry
            {
                RunId = runId,
                ActionId = action.Id,
                ActionName = action.Name,
                Queue = queue,
                Source = source,
                QueuedAt = queuedAt,
                Depth = depth,
                ArgsWhenQueued = Copy(runArgs),
            });
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        _active[runId] = linked;
        IDisposable? lease = null;
        var watch = new Stopwatch();
        string? error = null;
        var state = ActionRunState.Failed;
        DateTimeOffset? startedAtValue = null;
        try
        {
            if (depth == 0)
            {
                // Nested runs (Run action, scripts) bypass queues: waiting would deadlock a blocking queue.
                Guid? exclusive = string.IsNullOrWhiteSpace(action.Queue) && !action.Concurrent ? action.Id : null;
                lease = await Queues.EnterAsync(queue, action.AlwaysRun, exclusive, linked.Token).ConfigureAwait(false);
            }

            watch.Start();
            var startedAt = DateTimeOffset.Now;
            startedAtValue = startedAt;
            if (tracked)
            {
                History.Update(runId, e => e with { State = ActionRunState.Running, StartedAt = startedAt });
            }
            RunStarted?.Invoke(new ActionRunStart(runId, action, source, depth, queuedAt, startedAt, runArgs));
            var context = new ActionRunContext(action, runArgs, this, _globals, _log, depth);
            await RunBodyAsync(context, action, linked.Token).ConfigureAwait(false);
            state = ActionRunState.Completed;
        }
        catch (Exception ex) when (depth > 0)
        {
            // Nested runs propagate so the calling action fails too; only the outermost run logs.
            var cancelled = ex is OperationCanceledException;
            error = cancelled ? "cancelled" : ex.Message;
            state = cancelled ? ActionRunState.Cancelled : ActionRunState.Failed;
            throw;
        }
        catch (OperationCanceledException)
        {
            error = "cancelled";
            state = ActionRunState.Cancelled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error(Source, $"'{action.Name}' failed", ex);
        }
        finally
        {
            watch.Stop();
            lease?.Dispose();
            _active.TryRemove(runId, out _);
            var completedAt = DateTimeOffset.Now;
            var after = tracked || RunCompleted is not null ? Copy(runArgs) : null;
            if (tracked)
            {
                History.Update(runId, e => e with
                {
                    State = state,
                    CompletedAt = completedAt,
                    Duration = watch.Elapsed,
                    Error = error,
                    ArgsAfterRun = after ?? Copy(runArgs),
                });
            }
            RunCompleted?.Invoke(new ActionRunRecord(action.Name, source, state == ActionRunState.Completed, watch.Elapsed, error)
            {
                RunId = runId,
                Queue = queue,
                State = state,
                ExcludedFromHistory = !tracked,
                ActionId = action.Id,
                QueuedAt = queuedAt,
                StartedAt = startedAtValue,
                CompletedAt = completedAt,
                Arguments = after,
            });
        }
        return state == ActionRunState.Completed;
    }

    private async Task RunBodyAsync(ActionRunContext context, ActionDefinition action, CancellationToken ct)
    {
        // Stop, and loop signals outside any loop, simply end the action.
        if (action.RandomAction) await RunRandomAsync(context, action.SubActions, ct).ConfigureAwait(false);
        else if (action.ConcurrentSubActions) await RunConcurrentlyAsync(context, action.SubActions, ct).ConfigureAwait(false);
        else await RunChainAsync(context, action.SubActions, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a chain in order until it ends or a step stops it / signals a loop.</summary>
    internal async Task<FlowOutcome> RunChainAsync(ActionRunContext context, IReadOnlyList<SubActionDefinition> chain, CancellationToken ct)
    {
        foreach (var step in chain)
        {
            if (!step.Enabled) continue;
            var outcome = await RunStepAsync(context, step, ct).ConfigureAwait(false);
            if (outcome != FlowOutcome.Completed) return outcome;
        }
        return FlowOutcome.Completed;
    }

    /// <summary>Runs ONE enabled step of <paramref name="candidates"/>, picked by weight.</summary>
    internal async Task<FlowOutcome> RunRandomAsync(ActionRunContext context, IReadOnlyList<SubActionDefinition> candidates, CancellationToken ct)
    {
        var enabled = candidates.Where(s => s.Enabled).ToArray();
        var pick = WeightedRandom.Pick(enabled, s => s.Weight, _random);
        return pick is null ? FlowOutcome.Completed : await RunStepAsync(context, pick, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs every enabled step at once. Each gets its own copy of the arguments; afterwards the changes are merged
    /// back in step order (later steps win). A Stop or failure cancels the siblings that are still running.
    /// </summary>
    internal async Task<FlowOutcome> RunConcurrentlyAsync(ActionRunContext context, IReadOnlyList<SubActionDefinition> steps, CancellationToken ct)
    {
        var enabled = steps.Where(s => s.Enabled).ToArray();
        if (enabled.Length == 0) return FlowOutcome.Completed;

        var baseline = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forks = enabled.Select(_ => context.Fork()).ToArray();
        var tasks = enabled.Select((step, i) => RunForkAsync(forks[i], step)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Inspected per task below.
        }

        ct.ThrowIfCancellationRequested();
        var failed = tasks.FirstOrDefault(t => t.IsFaulted);
        if (failed is not null) ExceptionDispatchInfo.Capture(failed.Exception!.InnerException!).Throw();

        foreach (var fork in forks)
        {
            foreach (var (key, value) in fork.Args)
            {
                if (!baseline.TryGetValue(key, out var old) || !Equals(old, value)) context.Args[key] = value;
            }
            foreach (var key in baseline.Keys)
            {
                if (!fork.Args.ContainsKey(key)) context.Args.Remove(key);
            }
        }

        var outcomes = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToArray();
        if (tasks.Any(t => t.IsCanceled) || outcomes.Contains(FlowOutcome.Stop)) return FlowOutcome.Stop;
        if (outcomes.Contains(FlowOutcome.Break)) return FlowOutcome.Break;
        return outcomes.Contains(FlowOutcome.Continue) ? FlowOutcome.Continue : FlowOutcome.Completed;

        async Task<FlowOutcome> RunForkAsync(ActionRunContext fork, SubActionDefinition step)
        {
            await Task.Yield();
            try
            {
                var outcome = await RunStepAsync(fork, step, siblings.Token).ConfigureAwait(false);
                if (outcome == FlowOutcome.Stop) await siblings.CancelAsync().ConfigureAwait(false);
                return outcome;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await siblings.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<FlowOutcome> RunStepAsync(ActionRunContext context, SubActionDefinition step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var handler = _registry.GetSubAction(step.Type)
            ?? throw new InvalidOperationException($"Unknown sub-action type '{step.Type}' (is its module installed?)");
        SubActionStarting?.Invoke(new SubActionStart(context.Action, step, handler, context.Args));

        if (handler is IFlowContainerHandler container)
        {
            try
            {
                return await container.ExecuteFlowAsync(context, new FlowStep(this, context, handler, step), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SubActionFailedException)
            {
                throw new SubActionFailedException($"{handler.DisplayName}: {ex.Message}", ex);
            }
        }

        SubActionResult result;
        try
        {
            var values = ResolveParameters(handler, step, context);
            result = await handler.ExecuteAsync(context, values, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SubActionFailedException($"{handler.DisplayName}: {ex.Message}", ex);
        }

        if (!result.Continue) return result.ToOutcome();
        if (result.Branch is not null && step.Branches.TryGetValue(result.Branch, out var branch))
        {
            return await RunChainAsync(context, branch, ct).ConfigureAwait(false);
        }
        return FlowOutcome.Completed;
    }

    internal static ParameterValues ResolveParameters(ISubActionHandler handler, SubActionDefinition step, ActionRunContext context)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in handler.Parameters)
        {
            var raw = step.Parameters.TryGetValue(d.Key, out var v) ? v : d.DefaultValue;
            resolved[d.Key] = d.Kind == ParameterKind.Code ? raw : context.Resolve(raw);
        }
        return new ParameterValues(resolved);
    }

    private static void AddTriggerArgs(Dictionary<string, object?> args, ITriggerType type, string eventType)
    {
        args["triggerType"] = type.TypeId;
        args["triggerName"] = type.DisplayName;
        args["eventType"] = eventType;
    }

    private static Dictionary<string, object?> Copy(Dictionary<string, object?> args) =>
        new(args, StringComparer.OrdinalIgnoreCase);

    private void OnEvent(BotEvent e) => Dispatch(e);

    public void Dispose()
    {
        _subscription?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_ownsQueues) Queues.Dispose();
    }

    /// <summary>A sub-action failed; carries "Display name: message" and is not re-wrapped by enclosing containers.</summary>
    private sealed class SubActionFailedException(string message, Exception inner) : InvalidOperationException(message, inner);
}
