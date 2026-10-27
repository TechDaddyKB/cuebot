using System.Diagnostics;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;

namespace Cuebot.Core.BuiltIn;

internal static class Categories
{
    public const string Core = "Core";
    public const string Logic = "Logic";
    public const string Variables = "Variables";
    public const string System = "System";
}

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

public sealed class SetArgumentSubAction : ISubActionHandler
{
    public string TypeId => "core.setArgument";
    public string Category => Categories.Variables;
    public string DisplayName => "Set argument";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Argument name") { Help = "Use it later as %name%" },
        new("value", "Value"),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args[p["name"]] = p["value"];
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetGlobalSubAction : ISubActionHandler
{
    public string TypeId => "core.setGlobal";
    public string Category => Categories.Variables;
    public string DisplayName => "Set global variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Variable name") { Help = "Use it anywhere as ~name~" },
        new("value", "Value"),
        new("persisted", "Keep after restart", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Globals.Set(p["name"], p["value"], p.GetBool("persisted", true));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class RandomNumberSubAction : ISubActionHandler
{
    public string TypeId => "core.randomNumber";
    public string Category => Categories.Variables;
    public string DisplayName => "Random number";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Store in argument") { DefaultValue = "random" },
        new("min", "Minimum", ParameterKind.Number) { DefaultValue = "1" },
        new("max", "Maximum (inclusive)", ParameterKind.Number) { DefaultValue = "100" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var min = p.GetInt("min", 1);
        var max = Math.Max(min, p.GetInt("max", 100));
        context.Args[p["name"]] = Random.Shared.Next(min, max + 1);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class IfSubAction : ISubActionHandler
{
    public const string True = "True";
    public const string False = "False";

    public string TypeId => "core.if";
    public string Category => Categories.Logic;
    public string DisplayName => "If";
    public IReadOnlyList<string> Branches { get; } = [True, False];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("left", "Value") { DefaultValue = "%message%" },
        new("operator", "Operator", ParameterKind.Choice) { DefaultValue = "equals", Choices = Conditions.Operators },
        new("right", "Compare to"),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.TakeBranch(Conditions.Evaluate(p["left"], p["operator"], p["right"]) ? True : False));
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

public sealed class RunProgramSubAction : ISubActionHandler
{
    public string TypeId => "core.runProgram";
    public string Category => Categories.System;
    public string DisplayName => "Run program";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("path", "Program or file"),
        new("arguments", "Arguments"),
        new("workingDirectory", "Working directory"),
        new("wait", "Wait for exit", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var info = new ProcessStartInfo(p["path"], p["arguments"]) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(p["workingDirectory"])) info.WorkingDirectory = p["workingDirectory"];
        using var process = Process.Start(info);
        if (process is not null && p.GetBool("wait"))
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            context.Args["exitCode"] = process.ExitCode;
        }
        return SubActionResult.Next;
    }
}
