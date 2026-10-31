using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Core.BuiltIn;

/// <summary>State choices shared by the "Set ... state" sub-actions (Streamer.bot: Enabled / Disabled / Toggle).</summary>
public static class StateChoices
{
    public const string Enable = "Enable";
    public const string Disable = "Disable";
    public const string Toggle = "Toggle";
    public static readonly IReadOnlyList<string> All = [Enable, Disable, Toggle];

    /// <summary>The new state for <paramref name="choice"/> given the current one ("Enabled"/"Disabled" also accepted).</summary>
    public static bool Apply(string choice, bool current) => choice.Trim().ToLowerInvariant() switch
    {
        "disable" or "disabled" or "off" or "false" => false,
        "toggle" => !current,
        _ => true,
    };
}

/// <summary>Finds steps and triggers inside an action by id or by their 1-based position in the editor.</summary>
public static class ActionPaths
{
    /// <summary>Every sub-action of the action, depth first, in the order the list editor shows them.</summary>
    public static IEnumerable<SubActionDefinition> Flatten(IEnumerable<SubActionDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var branch in step.Branches.Values)
            foreach (var child in Flatten(branch))
                yield return child;
        }
    }

    /// <summary>
    /// The step named by <paramref name="reference"/>: its id, or its position (numbered from 1, depth first). A
    /// position may be followed by a label, as the suggestions show it ("3. Write to log").
    /// </summary>
    public static SubActionDefinition? FindStep(ActionDefinition action, string reference)
    {
        var all = Flatten(action.SubActions).ToList();
        if (Guid.TryParse(reference.Trim(), out var id)) return all.FirstOrDefault(s => s.Id == id);
        return LeadingNumber(reference) is { } n && n >= 1 && n <= all.Count ? all[n - 1] : null;
    }

    /// <summary>The trigger named by its id, its position (from 1, optionally followed by a label) or its type.</summary>
    public static TriggerDefinition? FindTrigger(ActionDefinition action, string reference, Registry? registry = null)
    {
        var text = reference.Trim();
        if (Guid.TryParse(text, out var id)) return action.Triggers.FirstOrDefault(t => t.Id == id);
        if (LeadingNumber(text) is { } n) return n >= 1 && n <= action.Triggers.Count ? action.Triggers[n - 1] : null;
        return action.Triggers.FirstOrDefault(t => string.Equals(t.Type, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(registry?.GetTrigger(t.Type)?.DisplayName, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"3", "3.", "3. Write to log" → 3.</summary>
    public static int? LeadingNumber(string text)
    {
        var t = text.Trim();
        var end = 0;
        while (end < t.Length && char.IsAsciiDigit(t[end])) end++;
        if (end == 0) return null;
        if (end < t.Length && t[end] is not ('.' or ':' or ' ' or ')')) return null;
        return int.TryParse(t[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>Suggestions for a step field: "n. Display name" for every step of every action.</summary>
    public static IEnumerable<string> StepSuggestions(ProfileService profile, Registry? registry) =>
        profile.Snapshot.Actions.SelectMany(a => Flatten(a.SubActions)
            .Select((s, i) => $"{i + 1}. {registry?.GetSubAction(s.Type)?.DisplayName ?? s.Type}"));

    public static IEnumerable<string> TriggerSuggestions(ProfileService profile, Registry? registry) =>
        profile.Snapshot.Actions.SelectMany(a => a.Triggers
            .Select((t, i) => $"{i + 1}. {registry?.GetTrigger(t.Type)?.DisplayName ?? t.Type}"));

    public static ActionDefinition RequireAction(ProfileService profile, string name) =>
        profile.Snapshot.FindAction(name.Trim()) ??
        (Guid.TryParse(name.Trim(), out var id) ? profile.Snapshot.FindAction(id) : null) ??
        throw new InvalidOperationException($"No action named '{name}'");

    /// <summary>The live (editable) copy of a snapshot action.</summary>
    public static ActionDefinition? Live(BotProfile profile, Guid id) => profile.Actions.FirstOrDefault(a => a.Id == id);
}

/// <summary>Queue choices: the existing queues plus "All".</summary>
internal static class QueueChoices
{
    public const string All = "All";

    public static IEnumerable<string> Suggestions(ProfileService profile) =>
        new[] { All, QueueService.DefaultQueue }.Concat(profile.Snapshot.Queues.Select(q => q.Name));

    public static IEnumerable<string> Targets(QueueService queues, string queue) =>
        string.Equals(queue.Trim(), All, StringComparison.OrdinalIgnoreCase)
            ? queues.Queues.Select(q => q.Name)
            : [QueueService.Normalize(queue)];
}

/// <summary>Removes every pending run from a queue (or from all of them). Running actions finish. Sets %queueCleared%.</summary>
public sealed class ClearActionQueueSubAction(QueueService queues, ProfileService profile) : ISubActionHandler
{
    public string TypeId => "core.clearActionQueue";
    public string Category => Categories.Core;
    public string DisplayName => "Clear action queue";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("queue", "Queue") { DefaultValue = QueueChoices.All, Suggestions = () => QueueChoices.Suggestions(profile), Help = "A queue name, or All" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["queueCleared"] = QueueChoices.Targets(queues, p["queue"]).Sum(queues.Clear);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Turns a queue's blocking option on, off or toggles it (saved, like editing the queue).</summary>
public sealed class SetActionQueueBlockingSubAction(ProfileService profile) : ISubActionHandler
{
    public string TypeId => "core.setActionQueueBlocking";
    public string Category => Categories.Core;
    public string DisplayName => "Set queue to one at a time";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("queue", "Queue") { Suggestions = () => profile.Snapshot.Queues.Select(q => q.Name) },
        new("state", "Run one at a time", ParameterKind.Choice) { DefaultValue = StateChoices.Enable, Choices = StateChoices.All },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = QueueService.Normalize(p["queue"]);
        if (string.Equals(name, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Default queue never blocks; create a named queue instead.");
        if (profile.Snapshot.FindQueue(name) is null) throw new InvalidOperationException($"No action queue named '{name}'");
        var state = p["state"];
        await profile.EditAsync(pr =>
        {
            var q = pr.Queues.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (q is not null) q.Blocking = StateChoices.Apply(state, q.Blocking);
        }).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Pauses or resumes a queue (or all of them), optionally clearing its pending runs.</summary>
public sealed class SetActionQueuePauseSubAction(QueueService queues, ProfileService profile) : ISubActionHandler
{
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string Toggle = "Toggle";

    public string TypeId => "core.setActionQueuePause";
    public string Category => Categories.Core;
    public string DisplayName => "Set action queue pause state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("queue", "Queue") { DefaultValue = QueueChoices.All, Suggestions = () => QueueChoices.Suggestions(profile), Help = "A queue name, or All" },
        new("state", "State", ParameterKind.Choice) { DefaultValue = Pause, Choices = [Pause, Resume, Toggle] },
        new("clear", "Also remove pending runs", ParameterKind.Boolean) { DefaultValue = "false" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        foreach (var name in QueueChoices.Targets(queues, p["queue"]).ToArray())
        {
            var pause = p["state"].Trim().ToLowerInvariant() switch
            {
                "resume" => false,
                "toggle" => !queues.IsPaused(name),
                _ => true,
            };
            if (p.GetBool("clear")) queues.Clear(name);
            if (pause) queues.Pause(name);
            else queues.Resume(name);
        }
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Enables, disables or toggles one trigger of an action (saved).</summary>
public sealed class SetTriggerStateSubAction(ProfileService profile, Lazy<Registry>? registry = null) : ISubActionHandler
{
    public string TypeId => "core.setTriggerState";
    public string Category => Categories.Core;
    public string DisplayName => "Turn trigger on/off";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("action", "Action") { Suggestions = () => profile.Snapshot.Actions.Select(a => a.Name) },
        new("trigger", "Trigger")
        {
            Suggestions = () => ActionPaths.TriggerSuggestions(profile, registry?.Value),
            Help = "Its position in the action's trigger list (1 = first), its id, or its type name",
        },
        new("state", "State", ParameterKind.Choice) { DefaultValue = StateChoices.Enable, Choices = StateChoices.All },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var action = ActionPaths.RequireAction(profile, p["action"]);
        var trigger = ActionPaths.FindTrigger(action, p["trigger"], registry?.Value)
            ?? throw new InvalidOperationException($"'{action.Name}' has no trigger '{p["trigger"]}'");
        var state = p["state"];
        await profile.EditAsync(pr =>
        {
            var live = ActionPaths.Live(pr, action.Id)?.Triggers.FirstOrDefault(t => t.Id == trigger.Id);
            if (live is not null) live.Enabled = StateChoices.Apply(state, live.Enabled);
        }).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Enables, disables or toggles one sub-action of an action (saved).</summary>
public sealed class SetSubActionStateSubAction(ProfileService profile, Lazy<Registry>? registry = null) : ISubActionHandler
{
    public string TypeId => "core.setSubActionState";
    public string Category => Categories.Core;
    public string DisplayName => "Turn sub-action on/off";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("action", "Action") { Suggestions = () => profile.Snapshot.Actions.Select(a => a.Name) },
        new("subAction", "Sub-action")
        {
            Suggestions = () => ActionPaths.StepSuggestions(profile, registry?.Value),
            Help = "Its position in the action (1 = first, counting steps inside groups and branches) or its id",
        },
        new("state", "State", ParameterKind.Choice) { DefaultValue = StateChoices.Enable, Choices = StateChoices.All },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var action = ActionPaths.RequireAction(profile, p["action"]);
        var step = ActionPaths.FindStep(action, p["subAction"])
            ?? throw new InvalidOperationException($"'{action.Name}' has no sub-action '{p["subAction"]}'");
        var state = p["state"];
        await profile.EditAsync(pr =>
        {
            if (ActionPaths.Live(pr, action.Id) is { } live &&
                ActionPaths.Flatten(live.SubActions).FirstOrDefault(s => s.Id == step.Id) is { } target)
            {
                target.Enabled = StateChoices.Apply(state, target.Enabled);
            }
        }).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Changes the random weight of one sub-action (used by random actions and random groups; saved).</summary>
public sealed class SetSubActionWeightSubAction(ProfileService profile, Lazy<Registry>? registry = null) : ISubActionHandler
{
    public string TypeId => "core.setSubActionWeight";
    public string Category => Categories.Core;
    public string DisplayName => "Set sub-action weight";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("action", "Action") { Suggestions = () => profile.Snapshot.Actions.Select(a => a.Name) },
        new("subAction", "Sub-action")
        {
            Suggestions = () => ActionPaths.StepSuggestions(profile, registry?.Value),
            Help = "Its position in the action (1 = first, counting steps inside groups and branches) or its id",
        },
        new("weight", "Weight", ParameterKind.Number) { DefaultValue = "1", Help = "Relative chance; only used when the action or group runs one random step" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var action = ActionPaths.RequireAction(profile, p["action"]);
        var step = ActionPaths.FindStep(action, p["subAction"])
            ?? throw new InvalidOperationException($"'{action.Name}' has no sub-action '{p["subAction"]}'");
        if (!double.TryParse(p["weight"], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) || double.IsNaN(weight))
            throw new ArgumentException($"'{p["weight"]}' is not a number");
        weight = Math.Max(0, weight);
        await profile.EditAsync(pr =>
        {
            if (ActionPaths.Live(pr, action.Id) is { } live &&
                ActionPaths.Flatten(live.SubActions).FirstOrDefault(s => s.Id == step.Id) is { } target)
            {
                target.Weight = weight;
            }
        }).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Action group helpers (the group is <see cref="ActionDefinition.Group"/>; "" is ungrouped).</summary>
internal static class ActionGroups
{
    public static IEnumerable<string> Names(ProfileService profile) =>
        profile.Snapshot.Actions.Select(a => a.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static bool InGroup(ActionDefinition a, string group) =>
        string.Equals(a.Group.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the enabled state of an action group: %actionGroupState% (0 all disabled, 1 all enabled, 2 mixed) and the ids
/// of its enabled and disabled actions in %actionsEnabled% / %actionsDisabled% (lists).
/// </summary>
public sealed class GetActionGroupStateSubAction(ProfileService profile) : ISubActionHandler
{
    public string TypeId => "core.getActionGroupState";
    public string Category => Categories.Core;
    public string DisplayName => "Get action group state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
        [new("group", "Group") { Suggestions = () => ActionGroups.Names(profile) }];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var actions = profile.Snapshot.Actions.Where(a => ActionGroups.InGroup(a, p["group"])).ToArray();
        var enabled = actions.Where(a => a.Enabled).Select(a => a.Id.ToString()).ToList();
        var disabled = actions.Where(a => !a.Enabled).Select(a => a.Id.ToString()).ToList();
        context.Args["actionGroupState"] = enabled.Count == 0 ? 0 : disabled.Count == 0 ? 1 : 2;
        context.Args["actionsEnabled"] = enabled;
        context.Args["actionsDisabled"] = disabled;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Enables, disables or toggles every action of a group (saved). Toggle flips each action on its own.</summary>
public sealed class SetActionGroupStateSubAction(ProfileService profile) : ISubActionHandler
{
    public string TypeId => "core.setActionGroupState";
    public string Category => Categories.Core;
    public string DisplayName => "Set action group state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("group", "Group") { Suggestions = () => ActionGroups.Names(profile) },
        new("state", "State", ParameterKind.Choice) { DefaultValue = StateChoices.Enable, Choices = StateChoices.All },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var group = p["group"];
        var state = p["state"];
        await profile.EditAsync(pr =>
        {
            foreach (var a in pr.Actions.Where(a => ActionGroups.InGroup(a, group))) a.Enabled = StateChoices.Apply(state, a.Enabled);
        }).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>
/// Fires a custom event: actions with a "Custom code event" trigger for that name run (Streamer.bot "Trigger Custom
/// Event"; the same event <c>CPH.TriggerEvent</c> sends). With "Use arguments" the new runs get a copy of this run's.
/// </summary>
public sealed class TriggerCustomEventSubAction(EventBus bus) : ISubActionHandler
{
    /// <summary>The event the "Custom code event" trigger listens to (owned by the scripting module).</summary>
    public const string EventType = "custom.codeEvent";

    private static readonly IReadOnlyList<string> RunArgs = ActionEngine.RunArgumentNames;

    public string TypeId => "core.triggerCustomEvent";
    public string Category => Categories.Core;
    public string DisplayName => "Trigger custom event";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("eventName", "Event name"),
        new("useArgs", "Use variables", ParameterKind.Boolean) { DefaultValue = "false", Help = "Pass this run's arguments to the actions it triggers" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = p["eventName"].Trim();
        if (name.Length == 0) throw new ArgumentException("No event name given.");
        var data = p.GetBool("useArgs")
            ? new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in RunArgs) data.Remove(key);
        data["eventName"] = name;
        data["customEvent.event"] = name;   // Streamer.bot's Custom trigger names the event here (null in its Test Trigger)
        bus.Publish(new BotEvent(EventType, data));
        return Task.FromResult(SubActionResult.Next);
    }
}
