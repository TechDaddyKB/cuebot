using System.Text.Json;
using Cuebot.Core.Actions;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

public sealed partial class CphProxy
{
    // ---- Actions --------------------------------------------------------------------------------------------------

    public bool ActionExists(string actionName) => Profile().Snapshot.FindAction(actionName) is not null;

    public List<ActionData> GetActions() => Profile().Snapshot.Actions.Select(a => new ActionData
    {
        Id = a.Id,
        Name = a.Name,
        Group = a.Group,
        Enabled = a.Enabled,
        SubActionCount = a.SubActions.Count,
        Queue = a.Queue,
    }).ToList();

    public void EnableAction(string actionName) => SetActionEnabled(actionName, true);
    public void DisableAction(string actionName) => SetActionEnabled(actionName, false);
    public void EnableActionById(string actionId) => SetActionEnabled(ActionById(actionId).Name, true);
    public void DisableActionById(string actionId) => SetActionEnabled(ActionById(actionId).Name, false);

    /// <summary>
    /// Runs an action. With <paramref name="runImmediately"/> (default) it runs now as a nested run with this run's
    /// arguments and the call waits for it; otherwise it is queued and the call returns at once.
    /// </summary>
    public bool RunAction(string actionName, bool runImmediately = true)
    {
        var action = Profile().Snapshot.FindAction(actionName);
        if (action is null)
        {
            LogWarn($"RunAction: no action named '{actionName}'");
            return false;
        }
        return Run(action, runImmediately);
    }

    public bool RunActionById(string actionId, bool runImmediately = true)
    {
        var action = Guid.TryParse(actionId, out var id) ? Profile().Snapshot.FindAction(id) : null;
        if (action is null)
        {
            LogWarn($"RunActionById: no action with id '{actionId}'");
            return false;
        }
        return Run(action, runImmediately);
    }

    private bool Run(ActionDefinition action, bool runImmediately)
    {
        if (runImmediately && _context is not null && HasFunction("actions.run"))
        {
            // Nested like the "Run action" sub-action: bypasses queues (no deadlock) and counts towards the depth limit.
            var result = Call("actions.run", new { name = action.Name });
            return result.ValueKind == JsonValueKind.True;
        }
        var engine = Need(_services.Engine);
        var args = _context is null ? null : new Dictionary<string, object?>(_context.Args, StringComparer.OrdinalIgnoreCase);
        var run = engine.RunAsync(action, args, CancellationToken.None, $"CPH in '{ScriptName}'");
        if (!runImmediately) return true;
        return Sync(run);
    }

    private void SetActionEnabled(string name, bool enabled, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (!Sync(Profile(method).SetActionEnabledAsync(name, enabled))) LogWarn($"{method}: no action named '{name}'");
    }

    private ActionDefinition ActionById(string actionId) =>
        (Guid.TryParse(actionId, out var id) ? Profile().Snapshot.FindAction(id) : null)
        ?? throw new ArgumentException($"No action with id '{actionId}'", nameof(actionId));

    private ProfileService Profile([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Need(_services.Profile, method);

    // ---- Action queues --------------------------------------------------------------------------------------------

    public void PauseActionQueue(string queueName) => Queues().Pause(queueName);

    public void ResumeActionQueue(string queueName, bool clear = false)
    {
        var queues = Queues();
        if (clear) queues.Clear(queueName);
        queues.Resume(queueName);
    }

    public void PauseAllActionQueues() => Queues().PauseAll();
    /// <summary>Resumes every queue; with <paramref name="clear"/> their waiting runs are dropped first.</summary>
    public void ResumeAllActionQueues(bool clear = false)
    {
        var queues = Queues();
        if (clear)
        {
            foreach (var queue in queues.Queues) queues.Clear(queue.Name);
        }
        queues.ResumeAll();
    }

    private QueueService Queues([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Need(_services.Engine, method).Queues;
}
