using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Elgato.Common;

/// <summary>Off / On / Toggle style switches (labels differ per sub-action, values parse leniently).</summary>
public enum SwitchMode { Toggle, On, Off }

/// <summary>A sub-action built from a table entry. Type ids are stored in actions.json and must never change.</summary>
internal sealed class ElgatoSubAction(
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

internal static class Steps
{
    public static ParameterDescriptor Switch(string on, string off, string key = "state", string label = "State", string? defaultValue = null) =>
        new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue ?? "Toggle", Choices = [on, off, "Toggle"] };

    public static ParameterDescriptor Adjustment() =>
        new("adjustment", "Add to current value", ParameterKind.Boolean) { DefaultValue = "false", Help = "Add the value to the current setting (use a negative value to lower it)" };

    public static SwitchMode ParseSwitch(string value) => value.Trim().ToLowerInvariant().Replace(" ", "") switch
    {
        "toggle" or "" => SwitchMode.Toggle,
        "on" or "true" or "1" or "enabled" or "enable" or "start" => SwitchMode.On,
        "off" or "false" or "0" or "disabled" or "disable" or "stop" => SwitchMode.Off,
        _ => throw new ArgumentException($"Unknown state '{value}'."),
    };

    public static bool Desired(SwitchMode mode, bool current) => mode switch
    {
        SwitchMode.On => true,
        SwitchMode.Off => false,
        _ => !current,
    };

    public static double Number(string value, string label) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ArgumentException($"{label} must be a number (got '{value}').");

    public static bool Bool(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    public static EventTriggerType Trigger(string category, string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));

    public static string Text(this BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    /// <summary>Empty filter matches; otherwise case-insensitive equality with any comma-separated entry.</summary>
    public static bool MatchesList(string filter, params string[] values)
    {
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => string.Equals(w, v, StringComparison.OrdinalIgnoreCase)));
    }
}
