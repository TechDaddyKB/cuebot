using Cuebot.Core.Actions;
using Cuebot.Core.Variables;

namespace Cuebot.Core.BuiltIn;

public sealed class SetArgumentSubAction : ISubActionHandler
{
    public string TypeId => "core.setArgument";
    public string Category => Categories.Variables;
    public string DisplayName => "Set argument";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Argument name") { Help = "Use it later as %name%" },
        new("value", "Value") { Help = "Supports %args%, ~globals~ and inline functions such as $math(%count% + 1)$" },
        new("autoType", "Auto type", ParameterKind.Boolean) { DefaultValue = "false", Help = "Store numbers and true/false as typed values" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var value = p["value"];
        context.Args[VariableNames.Require(p["name"], "Argument name")] = p.GetBool("autoType") ? VariableValues.AutoType(value) : value;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Get a global variable into an argument.</summary>
public sealed class GetGlobalSubAction : ISubActionHandler
{
    public string TypeId => "core.getGlobal";
    public string Category => Categories.Variables;
    public string DisplayName => "Get global variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Variable name"),
        new("destination", "Store in argument") { Help = "Defaults to the variable name; use it as %argument%" },
        new("defaultValue", "Default value") { Help = "Used when the variable does not exist; empty leaves the argument unset" },
        new("autoType", "Auto type", ParameterKind.Boolean) { DefaultValue = "true", Help = "Numbers stay numbers" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = VariableNames.Require(p["name"]);
        var destination = p["destination"].Trim().Length > 0 ? VariableNames.Require(p["destination"], "Argument name") : name;
        var autoType = p.GetBool("autoType", true);
        if (context.Globals.TryGetValue(name, out var value))
        {
            context.Args[destination] = autoType && value is string s ? VariableValues.AutoType(s) : value;
        }
        else if (p["defaultValue"].Length > 0)
        {
            context.Args[destination] = autoType ? VariableValues.AutoType(p["defaultValue"]) : p["defaultValue"];
        }
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Set, increment or decrement a global variable (~name~).</summary>
public sealed class SetGlobalSubAction : ISubActionHandler
{
    public string TypeId => "core.setGlobal";
    public string Category => Categories.Variables;
    public string DisplayName => "Set global variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Variable name") { Help = "Use it anywhere as ~name~" },
        new("mode", "Mode", ParameterKind.Choice) { DefaultValue = SetModes.Value, Choices = SetModes.All },
        new("value", "Value") { Help = "For Increment/Decrement: the amount (empty = 1)" },
        new("persisted", "Keep after restart", ParameterKind.Boolean) { DefaultValue = "true" },
        new("autoType", "Auto type", ParameterKind.Boolean) { DefaultValue = "false", Help = "Store numbers and true/false as typed values" },
        new("storeIn", "Also store in argument") { Help = "Optional argument that receives the new value" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = VariableNames.Require(p["name"]);
        var persist = p.GetBool("persisted", true);
        object? value;
        if (SetModes.IsIncrement(p["mode"], out var sign))
        {
            value = context.Globals.Increment(name, sign * SetModes.Amount(p["value"]), persist);
        }
        else
        {
            value = p.GetBool("autoType") ? VariableValues.AutoType(p["value"]) : p["value"];
            context.Globals.SetValue(name, value, persist);
        }
        if (p["storeIn"].Trim().Length > 0) context.Args[VariableNames.Require(p["storeIn"], "Argument name")] = value;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeleteGlobalSubAction : ISubActionHandler
{
    public string TypeId => "core.deleteGlobal";
    public string Category => Categories.Variables;
    public string DisplayName => "Delete global variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("name", "Variable name")];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Globals.Remove(VariableNames.Require(p["name"]));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class ClearTemporaryGlobalsSubAction : ISubActionHandler
{
    public string TypeId => "core.clearTemporaryGlobals";
    public string Category => Categories.Variables;
    public string DisplayName => "Clear temporary global variables";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Globals.ClearTemporary();
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
        context.Args[p["name"]] = (int)context.Globals.Text.Random.NextInt64(min, (long)max + 1);
        return Task.FromResult(SubActionResult.Next);
    }
}
