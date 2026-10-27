using Cuebot.Core.Actions;
using Cuebot.Core.Logging;

namespace Cuebot.Core.BuiltIn;

public sealed class DelaySubAction : ISubActionHandler
{
    public string TypeId => "core.delay";
    public string Category => Categories.Core;
    public string DisplayName => "Delay";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("milliseconds", "Milliseconds", ParameterKind.Number) { DefaultValue = "1000" }];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Task.Delay(Math.Max(0, p.GetInt("milliseconds")), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

public sealed class LogSubAction : ISubActionHandler
{
    public string TypeId => "core.log";
    public string Category => Categories.Core;
    public string DisplayName => "Write to log";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("message", "Message", ParameterKind.Multiline),
        new("level", "Level", ParameterKind.Choice) { DefaultValue = "Info", Choices = ["Debug", "Info", "Warning", "Error"] },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var level = Enum.TryParse<LogLevel>(p["level"], out var l) ? l : LogLevel.Info;
        context.Log.Write(level, context.Action.Name, p["message"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class StopSubAction : ISubActionHandler
{
    public string TypeId => "core.stop";
    public string Category => Categories.Logic;
    public string DisplayName => "Stop action";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.Stop);
}

public sealed class RunActionSubAction(ProfileService profile) : ISubActionHandler
{
    public string TypeId => "core.runAction";
    public string Category => Categories.Logic;
    public string DisplayName => "Run action";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("action", "Action") { Suggestions = () => profile.Snapshot.Actions.Select(a => a.Name) },
        new("wait", "Wait for it to finish", ParameterKind.Boolean) { DefaultValue = "true" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var target = profile.Snapshot.FindAction(p["action"])
            ?? throw new InvalidOperationException($"No action named '{p["action"]}'");
        // The child receives a copy of the current arguments.
        var run = context.Engine.RunAsync(target, context.Args, context.Depth + 1, $"Run from '{context.Action.Name}'", ct);
        if (p.GetBool("wait", true)) await run.ConfigureAwait(false);
        return SubActionResult.Next;
    }
}


/// <summary>
/// A named, coloured, nestable container. Its children live in the "Steps" branch and run in order, or, with
/// "random" on, ONE enabled child runs, picked by <see cref="SubActionDefinition.Weight"/>. Loop signals from
/// children pass through to the enclosing loop.
/// </summary>
public sealed class GroupSubAction : IFlowContainerHandler
{
    public const string Id = "core.group";
    public const string Steps = "Steps";

    public string TypeId => Id;
    public string Category => Categories.Core;
    public string DisplayName => "Group";
    public IReadOnlyList<string> Branches { get; } = [Steps];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Name") { DefaultValue = "Group" },
        new("random", "Run one random step", ParameterKind.Boolean) { DefaultValue = "false" },
        new("color", "Colour") { Help = "Editor colour, e.g. #3A7BD5 (blank = default)" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct) =>
        throw new NotSupportedException("Group runs through ExecuteFlowAsync");

    public Task<FlowOutcome> ExecuteFlowAsync(ActionRunContext context, FlowStep step, CancellationToken ct) =>
        step.ResolveParameters().GetBool("random")
            ? step.RunRandomChildAsync(Steps, ct)
            : step.RunBranchAsync(Steps, ct);
}

/// <summary>A note in the step list; does nothing.</summary>
public sealed class CommentSubAction : ISubActionHandler
{
    public const string Id = "core.comment";

    public string TypeId => Id;
    public string Category => Categories.Core;
    public string DisplayName => "Comment";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("text", "Comment", ParameterKind.Multiline)];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.Next);
}

/// <summary>Releases every run waiting for the named local signal. Sets %signalWaiters% to how many were waiting.</summary>
public sealed class SendSignalSubAction(SignalService signals) : ISubActionHandler
{
    public string TypeId => "core.sendSignal";
    public string Category => Categories.Core;
    public string DisplayName => "Send local signal";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("name", "Signal name")];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["signalWaiters"] = signals.Send(p["name"],
            new Dictionary<string, object?> { ["signalSender"] = context.Action.Name });
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Waits until another run sends the named local signal (only signals sent after the wait starts count).
/// Sets %signalReceived% (true/false) and %signalSender%. On timeout it continues, or stops the action if asked.
/// </summary>
public sealed class WaitForSignalSubAction(SignalService signals) : ISubActionHandler
{
    public string TypeId => "core.waitForSignal";
    public string Category => Categories.Core;
    public string DisplayName => "Wait for local signal";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Signal name"),
        new("timeoutMs", "Timeout (ms, 0 = wait forever)", ParameterKind.Number) { DefaultValue = "0" },
        new("stopOnTimeout", "Stop the action on timeout", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var timeoutMs = p.GetInt("timeoutMs");
        var payload = await signals.WaitAsync(p["name"], timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : null, ct)
            .ConfigureAwait(false);
        context.Args["signalReceived"] = payload is not null;
        if (payload is null) return p.GetBool("stopOnTimeout") ? SubActionResult.Stop : SubActionResult.Next;
        foreach (var (key, value) in payload) context.Args[key] = value;
        return SubActionResult.Next;
    }
}

/// <summary>Enables, disables or toggles an action by name (persisted, like editing it).</summary>
public sealed class SetActionStateSubAction(ProfileService profile) : ISubActionHandler
{
    public const string Enable = "Enable";
    public const string Disable = "Disable";
    public const string Toggle = "Toggle";

    public string TypeId => "core.setActionState";
    public string Category => Categories.Core;
    public string DisplayName => "Set action state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("action", "Action") { Suggestions = () => profile.Snapshot.Actions.Select(a => a.Name) },
        new("state", "State", ParameterKind.Choice) { DefaultValue = Enable, Choices = [Enable, Disable, Toggle] },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = p["action"];
        var current = profile.Snapshot.FindAction(name)
            ?? throw new InvalidOperationException($"No action named '{name}'");
        var enabled = p["state"] switch
        {
            Disable => false,
            Toggle => !current.Enabled,
            _ => true,
        };
        await profile.SetActionEnabledAsync(name, enabled).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Stores whether an action is enabled (true/false) in an argument.</summary>
public sealed class GetActionStateSubAction(ProfileService profile) : ISubActionHandler
{
    public string TypeId => "core.getActionState";
    public string Category => Categories.Core;
    public string DisplayName => "Get action state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("action", "Action") { Suggestions = () => profile.Snapshot.Actions.Select(a => a.Name) },
        new("name", "Store in argument") { DefaultValue = "actionEnabled" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var action = profile.Snapshot.FindAction(p["action"])
            ?? throw new InvalidOperationException($"No action named '{p["action"]}'");
        context.Args[p["name"]] = action.Enabled;
        return Task.FromResult(SubActionResult.Next);
    }
}
