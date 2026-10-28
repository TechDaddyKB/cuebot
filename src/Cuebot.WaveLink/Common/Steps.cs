using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.WaveLink.Common;

/// <summary>A sub-action built from a table entry. Type ids are stored in actions.json and must never change.</summary>
internal sealed class WaveLinkSubAction(
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

/// <summary>A trigger listening to one or more event types with an optional filter (that may add arguments).</summary>
internal sealed class WaveLinkTrigger(
    string typeId,
    string category,
    string displayName,
    IReadOnlyCollection<string> eventTypes,
    IReadOnlyList<ParameterDescriptor>? parameters = null,
    Func<BotEvent, ParameterValues, IDictionary<string, object?>, bool>? filter = null) : ITriggerType
{
    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => displayName;
    public IReadOnlyCollection<string> EventTypes => eventTypes;
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = parameters ?? [];

    public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args) =>
        filter?.Invoke(e, settings, args) ?? true;
}

internal static class Steps
{
    /// <summary>A three-way state choice; <paramref name="on"/>/<paramref name="off"/> are the labels shown (e.g. Muted/Not Muted).</summary>
    public static ParameterDescriptor Switch(string on, string off, string key = "state", string label = "State", string? defaultValue = null) =>
        new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue ?? on, Choices = [on, off, "Toggle"] };

    public static SwitchMode ParseSwitch(string value) => value.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "") switch
    {
        "toggle" or "" => SwitchMode.Toggle,
        "on" or "true" or "1" or "enabled" or "enable" or "muted" or "mute" or "bypassed" or "bypass" or "locked" or "lock" or "active" => SwitchMode.On,
        "off" or "false" or "0" or "disabled" or "disable" or "notmuted" or "unmuted" or "unmute" or "notbypassed" or "unlocked" or "unlock"
            or "inactive" => SwitchMode.Off,
        _ => throw new ArgumentException($"Unknown state '{value}'."),
    };

    public static bool Desired(SwitchMode mode, bool current) => mode switch
    {
        SwitchMode.On => true,
        SwitchMode.Off => false,
        _ => !current,
    };

    public static string Require(string value, string label) =>
        value.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException($"{label} is required.");

    public static double Number(string value, string label, double? fallback = null)
    {
        if (value.Trim().Length == 0)
            return fallback ?? throw new ArgumentException($"{label} is required.");
        return double.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ArgumentException($"{label} must be a number (got '{value}').");
    }

    public static string Text(this BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public static bool? Flag(this BotEvent e, string key) => e.Args.TryGetValue(key, out var v) ? v as bool? : null;

    /// <summary>Empty filter matches; otherwise case-insensitive equality with any comma-separated entry.</summary>
    public static bool MatchesList(string filter, params string[] values)
    {
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => string.Equals(w, v, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>0-100 percent from a 0-1 level, rounded to a tenth.</summary>
    public static double Percent(double level) => Math.Round(Math.Clamp(level, 0, 1) * 100, 1);
}

internal static class JsonUtil
{
    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Null => "", _ => v.ToString() }
            : "";

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => v.GetDouble() != 0, _ => null }
            : null;

    public static double? Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static bool Has(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
}
