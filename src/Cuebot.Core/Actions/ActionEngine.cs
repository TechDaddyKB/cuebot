using System.Collections.Concurrent;
using System.Diagnostics;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Actions;

public sealed record ActionRunRecord(string ActionName, string Source, bool Success, TimeSpan Duration, string? Error);

/// <summary>Matches events to triggers and executes actions' flow trees.</summary>
public sealed class ActionEngine : IDisposable
{
    public const int MaxDepth = 16;
    private const string Source = "Actions";

    private readonly EventBus _bus;
    private readonly ProfileService _profile;
    private readonly Registry _registry;
    private readonly GlobalVariables _globals;
    private readonly AppLog _log;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _queues = new();
    private readonly CancellationTokenSource _shutdown = new();
    private IDisposable? _subscription;

    public ActionEngine(EventBus bus, ProfileService profile, Registry registry, GlobalVariables globals, AppLog log)
    {
        _bus = bus;
        _profile = profile;
        _registry = registry;
        _globals = globals;
        _log = log;
    }

    public event Action<ActionRunRecord>? RunCompleted;

    public void Start() => _subscription ??= _bus.Subscribe(OnEvent);

    /// <summary>Find actions triggered by the event and start them. Returns the started runs (for tests).</summary>
    public IReadOnlyList<Task<bool>> Dispatch(BotEvent e)
    {
        var started = new List<Task<bool>>();
        foreach (var action in _profile.Snapshot.Actions)
        {
            if (!action.Enabled) continue;
            foreach (var trigger in action.Triggers)
            {
                if (!trigger.Enabled) continue;
                var type = _registry.GetTrigger(trigger.Type);
                if (type is null || !type.EventTypes.Contains(e.Type)) continue;

                var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
                bool matched;
                try
                {
                    matched = type.Matches(e, trigger, ParameterValues.From(type.Parameters, trigger.Settings), args);
                }
                catch (Exception ex)
                {
                    _log.Error(Source, $"Trigger '{type.DisplayName}' on '{action.Name}' failed", ex);
                    continue;
                }
                if (!matched) continue;

                args["triggerType"] = type.TypeId;
                args["triggerName"] = type.DisplayName;
                args["eventType"] = e.Type;
                started.Add(RunAsync(action, args, source: type.DisplayName));
                break; // one run per event per action, even if several triggers match
            }
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

    internal async Task<bool> RunAsync(ActionDefinition action, IReadOnlyDictionary<string, object?>? args, int depth,
        string source, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"'{action.Name}' exceeded the maximum nesting depth of {MaxDepth}; is an action running itself?");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        SemaphoreSlim? queue = null;
        if (!action.Concurrent && depth == 0)
        {
            queue = _queues.GetOrAdd(action.Id, _ => new SemaphoreSlim(1, 1));
            await queue.WaitAsync(linked.Token).ConfigureAwait(false);
        }

        var watch = Stopwatch.StartNew();
        string? error = null;
        var success = false;
        try
        {
            var runArgs = args is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
            runArgs["actionName"] = action.Name;
            runArgs["actionId"] = action.Id.ToString();
            runArgs["runId"] = Guid.NewGuid().ToString();

            var context = new ActionRunContext(action, runArgs, this, _globals, _log, depth);
            await RunChainAsync(context, action.SubActions, linked.Token).ConfigureAwait(false);
            success = true;
        }
        catch (Exception ex) when (depth > 0)
        {
            // Nested runs propagate so the calling action fails too; only the outermost run logs.
            error = ex.Message;
            throw;
        }
        catch (OperationCanceledException)
        {
            error = "cancelled";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error(Source, $"'{action.Name}' failed", ex);
        }
        finally
        {
            queue?.Release();
            RunCompleted?.Invoke(new ActionRunRecord(action.Name, source, success, watch.Elapsed, error));
        }
        return success;
    }

    /// <summary>Runs a chain. Returns false when a sub-action asked to stop the whole action.</summary>
    private async Task<bool> RunChainAsync(ActionRunContext context, IReadOnlyList<SubActionDefinition> chain, CancellationToken ct)
    {
        foreach (var step in chain)
        {
            ct.ThrowIfCancellationRequested();
            if (!step.Enabled) continue;

            var handler = _registry.GetSubAction(step.Type)
                ?? throw new InvalidOperationException($"Unknown sub-action type '{step.Type}' (is its module installed?)");

            var values = ResolveParameters(handler, step, context);
            SubActionResult result;
            try
            {
                result = await handler.ExecuteAsync(context, values, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{handler.DisplayName}: {ex.Message}", ex);
            }

            if (!result.Continue) return false;
            if (result.Branch is not null && step.Branches.TryGetValue(result.Branch, out var branch))
            {
                if (!await RunChainAsync(context, branch, ct).ConfigureAwait(false)) return false;
            }
        }
        return true;
    }

    private static ParameterValues ResolveParameters(ISubActionHandler handler, SubActionDefinition step, ActionRunContext context)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in handler.Parameters)
        {
            var raw = step.Parameters.TryGetValue(d.Key, out var v) ? v : d.DefaultValue;
            resolved[d.Key] = d.Kind == ParameterKind.Code ? raw : context.Resolve(raw);
        }
        return new ParameterValues(resolved);
    }

    private void OnEvent(BotEvent e) => Dispatch(e);

    public void Dispose()
    {
        _subscription?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
