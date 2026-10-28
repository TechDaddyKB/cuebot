using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.GoXlr.Common;

/// <summary>A sub-action built from a table entry. Type ids are stored in actions.json and must never change.</summary>
internal sealed class GoXlrSubAction(
    string typeId,
    string displayName,
    IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => GoXlrTriggers.Category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues values, CancellationToken ct)
    {
        await run(context, values, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>A trigger listening to one event type with an optional filter.</summary>
internal sealed class GoXlrTrigger(
    string typeId,
    string displayName,
    IReadOnlyList<ParameterDescriptor>? parameters = null,
    Func<BotEvent, ParameterValues, bool>? filter = null) : ITriggerType
{
    public string TypeId => typeId;
    public string Category => GoXlrTriggers.Category;
    public string DisplayName => displayName;
    public IReadOnlyCollection<string> EventTypes { get; } = [typeId];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = parameters ?? [];

    public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args) =>
        filter?.Invoke(e, settings) ?? true;
}

internal static class Steps
{
    public static SwitchMode ParseSwitch(string value) => value.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "") switch
    {
        "toggle" or "" => SwitchMode.Toggle,
        "on" or "true" or "1" or "enabled" or "enable" or "yes" => SwitchMode.On,
        "off" or "false" or "0" or "disabled" or "disable" or "no" => SwitchMode.Off,
        _ => throw new ArgumentException($"Unknown state '{value}' (expected On, Off or Toggle)."),
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

    public static bool IsTrue(string v) => v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || v.Trim() == "1";

    public static string Text(this BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public static bool? Flag(this BotEvent e, string key) => e.Args.TryGetValue(key, out var v) ? v as bool? : null;

    /// <summary>Empty filter matches; otherwise any comma-separated entry equal to a value, ignoring case, spaces and dashes.</summary>
    public static bool MatchesList(string filter, params string[] values)
    {
        static string Key(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => values.Any(v => v.Length > 0 && Key(w) == Key(v)));
    }
}
