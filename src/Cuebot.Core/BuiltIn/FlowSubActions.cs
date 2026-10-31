using Cuebot.Core.Actions;
using Cuebot.Core.Logging;

namespace Cuebot.Core.BuiltIn;

/// <summary>Waits a fixed time, or with "random" a random time between the delay and "to" (inclusive). Sets %delayMs%.</summary>
public sealed class DelaySubAction(IRandomSource? random = null) : ISubActionHandler
{
    public string TypeId => "core.delay";
    public string Category => Categories.Core;
    public string DisplayName => "Delay";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("milliseconds", "Milliseconds", ParameterKind.Number) { DefaultValue = "1000" },
        new("random", "Random delay", ParameterKind.Boolean) { DefaultValue = "false", Help = "Wait a random time between Milliseconds and To" },
        new("to", "To (milliseconds)", ParameterKind.Number) { DefaultValue = "2000" },
    ];

    /// <summary>The delay to wait, in milliseconds.</summary>
    public static int Pick(ParameterValues p, IRandomSource random)
    {
        var from = Math.Max(0, p.GetInt("milliseconds"));
        if (!p.GetBool("random")) return from;
        var to = Math.Max(0, p.GetInt("to", from));
        var (lo, hi) = from <= to ? (from, to) : (to, from);
        return (int)Math.Min(hi, lo + Math.Floor(random.NextDouble() * ((long)hi - lo + 1)));
    }

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var ms = Pick(p, random ?? SystemRandomSource.Instance);
        context.Args["delayMs"] = ms;
        await Task.Delay(ms, ct).ConfigureAwait(false);
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
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("text", "Comment", ParameterKind.Multiline),
        new("color", "Colour") { Help = "Text colour in the editor, e.g. #E0A030 (blank = default)" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.Next);
}

/// <summary>
/// Releases every run waiting for the named local signal. Sets %signalWaiters% to how many were waiting. The signal
/// carries %signalSender% plus, optionally, all of this run's arguments and/or the listed "name=value" arguments
/// (the listed ones win). "Queue signal" keeps it for the next wait when nobody is waiting yet.
/// </summary>
public sealed class SendSignalSubAction(SignalService signals) : ISubActionHandler
{
    /// <summary>Run bookkeeping that never travels with a signal.</summary>
    private static readonly IReadOnlyList<string> RunArgs = ActionEngine.RunArgumentNames;

    public string TypeId => "core.sendSignal";
    public string Category => Categories.Core;
    public string DisplayName => "Send local signal";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Signal name"),
        new("includeArgs", "Include the action's variables", ParameterKind.Boolean) { DefaultValue = "false" },
        new("arguments", "Values to send", ParameterKind.Multiline) { Help = "One name=value per line; always sent" },
        new("queue", "Queue signal", ParameterKind.Boolean) { DefaultValue = "false", Help = "Keep it for a wait that has not started yet" },
    ];

    /// <summary>"name=value" lines (blank lines and lines without a name are skipped).</summary>
    public static IEnumerable<KeyValuePair<string, string>> ParseArguments(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            var name = (eq < 0 ? line : line[..eq]).Trim().Trim('%');
            if (name.Length == 0) continue;
            yield return new(name, eq < 0 ? "" : line[(eq + 1)..].Trim());
        }
    }

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (p.GetBool("includeArgs"))
        {
            foreach (var (key, value) in context.Args) payload[key] = value;
            foreach (var key in RunArgs) payload.Remove(key);
        }
        foreach (var (key, value) in ParseArguments(p["arguments"])) payload[key] = value;
        payload["signalSender"] = context.Action.Name;
        context.Args["signalWaiters"] = signals.Send(p["name"], payload, p.GetBool("queue"));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Waits until another run sends the named local signal (only signals sent after the wait starts count, unless queued).
/// Sets %signalReceived%, %signalTimeout%, %signalName% and %hasResult% (the signal carried arguments), and copies the
/// signal's arguments in; with "Overwrite variables" off, arguments this run already has are kept. On timeout it
/// continues, or stops the action if asked.
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
        new("overwrite", "Overwrite variables", ParameterKind.Boolean) { DefaultValue = "true", Help = "The signal's arguments replace this run's arguments of the same name" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var timeoutMs = p.GetInt("timeoutMs");
        var payload = await signals.WaitAsync(p["name"], timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : null, ct)
            .ConfigureAwait(false);
        context.Args["signalReceived"] = payload is not null;
        context.Args["signalTimeout"] = payload is null;
        if (payload is null) return p.GetBool("stopOnTimeout") ? SubActionResult.Stop : SubActionResult.Next;

        var overwrite = p.GetBool("overwrite", true);
        foreach (var (key, value) in payload)
        {
            if (overwrite || !context.Args.ContainsKey(key) || string.Equals(key, "signalSender", StringComparison.OrdinalIgnoreCase))
                context.Args[key] = value;
        }
        context.Args["signalName"] = p["name"];
        context.Args["hasResult"] = payload.Keys.Any(k => !string.Equals(k, "signalSender", StringComparison.OrdinalIgnoreCase));
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
    public string DisplayName => "Turn action on/off";

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
        new("name", "Save in variable") { DefaultValue = "actionEnabled" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var action = profile.Snapshot.FindAction(p["action"])
            ?? throw new InvalidOperationException($"No action named '{p["action"]}'");
        // Streamer.bot always sets %actionState%; the chosen name is kept for older Cuebot actions.
        context.Args["actionState"] = action.Enabled;
        if (p["name"].Trim().Length > 0) context.Args[p["name"].Trim()] = action.Enabled;
        return Task.FromResult(SubActionResult.Next);
    }
}
