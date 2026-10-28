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

/// <summary>
/// Get Random Number. "Between": an integer from min to max (inclusive) in the chosen argument (default %random%; the
/// Streamer.bot importer uses %randomNumber%). "Next Float": %randomFloat% in [0, 1) and %randomPercent% (0-100).
/// </summary>
public sealed class RandomNumberSubAction : ISubActionHandler
{
    public const string Between = "Between";
    public const string NextFloat = "Next Float";

    public string TypeId => "core.randomNumber";
    public string Category => Categories.Variables;
    public string DisplayName => "Random number";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("type", "Type", ParameterKind.Choice) { DefaultValue = Between, Choices = [Between, NextFloat] },
        new("name", "Store in argument") { DefaultValue = "random", Help = "Between only; Next Float sets %randomFloat% and %randomPercent%" },
        new("min", "Minimum", ParameterKind.Number) { DefaultValue = "1" },
        new("max", "Maximum (inclusive)", ParameterKind.Number) { DefaultValue = "100" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var random = context.Globals.Text.Random;
        if (string.Equals(p["type"].Trim(), NextFloat, StringComparison.OrdinalIgnoreCase))
        {
            var value = random.NextDouble();
            context.Args["randomFloat"] = value;
            context.Args["randomPercent"] = (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);
            return Task.FromResult(SubActionResult.Next);
        }
        var min = p.GetInt("min", 1);
        var max = Math.Max(min, p.GetInt("max", 100));
        var name = p["name"].Trim().Length > 0 ? p["name"].Trim() : "random";
        context.Args[name] = (int)random.NextInt64(min, (long)max + 1);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Streamer.bot "Pick Color": the colour is chosen when editing (#RRGGBB or #AARRGGBB); running the step fills
/// %name.color.a/r/g/b%, %name.html% (#RRGGBB), %name.htmlalpha% (#AARRGGBB) and %name.obs% (OBS's ABGR integer).
/// </summary>
public sealed class PickColorSubAction : ISubActionHandler
{
    public string TypeId => "core.pickColor";
    public string Category => Categories.Variables;
    public string DisplayName => "Pick color";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("color", "Color") { DefaultValue = "#FF9146FF", Help = "#RRGGBB or #AARRGGBB" },
        new("name", "Variable name") { DefaultValue = "color", Help = "Without %; e.g. color gives %color.html%" },
    ];

    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB (the # is optional).</summary>
    public static bool TryParse(string text, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255; r = g = b = 0;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return false;
        if (hex.Length == 8) a = (byte)(v >> 24);
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        if (!TryParse(p["color"], out var a, out var r, out var g, out var b)) throw new ArgumentException($"'{p["color"]}' is not a colour (use #RRGGBB)");
        var name = p["name"].Trim().Trim('%');
        if (name.Length == 0) name = "color";
        context.Args[name + ".color.a"] = (int)a;
        context.Args[name + ".color.r"] = (int)r;
        context.Args[name + ".color.g"] = (int)g;
        context.Args[name + ".color.b"] = (int)b;
        context.Args[name + ".html"] = $"#{r:X2}{g:X2}{b:X2}";
        context.Args[name + ".htmlalpha"] = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        context.Args[name + ".obs"] = (long)(((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r);
        return Task.FromResult(SubActionResult.Next);
    }
}
