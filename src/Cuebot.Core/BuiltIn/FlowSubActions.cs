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

