using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Integrations.Common;

/// <summary>A sub-action built from a table entry. Type ids are stored in actions.json and must never change.</summary>
internal sealed class IntegrationSubAction(
    string typeId,
    string category,
    string displayName,
    IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues values, CancellationToken ct)
    {
        await run(context, values, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>On / Off / Toggle for app switches.</summary>
public enum SwitchMode { Toggle, On, Off }

internal static class Steps
{
    public static ParameterDescriptor Switch(string key = "state", string label = "State", SwitchMode defaultValue = SwitchMode.Toggle) =>
        new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue.ToString(), Choices = Enum.GetNames<SwitchMode>() };

    public static SwitchMode ParseSwitch(string value) =>
        Enum.TryParse<SwitchMode>(value.Trim(), ignoreCase: true, out var mode) ? mode
        : value.Trim().ToLowerInvariant() switch
        {
            "true" or "enable" or "enabled" or "1" => SwitchMode.On,
            "false" or "disable" or "disabled" or "0" => SwitchMode.Off,
            _ => throw new ArgumentException($"State must be Toggle, On or Off (got '{value}')."),
        };

    /// <summary>The target state for <paramref name="mode"/> given the <paramref name="current"/> one.</summary>
    public static bool Desired(SwitchMode mode, bool current) => mode switch
    {
        SwitchMode.On => true,
        SwitchMode.Off => false,
        _ => !current,
    };

    public static string Require(string value, string label) =>
        value.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException($"{label} is required.");

    public static double? OptionalNumber(string value, string label)
    {
        if (value.Trim().Length == 0) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ArgumentException($"{label} must be a number (got '{value}').");
    }

    public static double Number(string value, string label, double fallback) => OptionalNumber(value, label) ?? fallback;

    public static ParameterDescriptor Result(string defaultArgument, string label = "Store result in argument") =>
        new("resultArgument", label) { DefaultValue = defaultArgument };

    public static void Store(ActionRunContext ctx, string argument, object? value)
    {
        if (argument.Trim().Length > 0) ctx.Args[argument.Trim()] = value;
    }

    /// <summary>A trigger for one event type, optionally filtered by its settings.</summary>
    public static EventTriggerType Trigger(string category, string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null, string? typeId = null) =>
        new(typeId ?? eventType, category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));

    public static string Text(this BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public static bool? Flag(this BotEvent e, string key) => e.Args.TryGetValue(key, out var v) ? v as bool? : null;

    /// <summary>Empty filter matches; otherwise case-insensitive equality with any comma-separated entry.</summary>
    public static bool MatchesList(string filter, params string[] values)
    {
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => string.Equals(w, v, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>"Any" / "On" / "Off" state filter for toggled-events.</summary>
    public static ParameterDescriptor StateFilter(string label = "State") =>
        new("stateFilter", label, ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "On", "Off"] };

    public static bool MatchesState(BotEvent e, ParameterValues s, string arg = "enabled") => s["stateFilter"] switch
    {
        "On" => e.Flag(arg) == true,
        "Off" => e.Flag(arg) == false,
        _ => true,
    };
}
