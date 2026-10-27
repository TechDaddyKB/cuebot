using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

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

