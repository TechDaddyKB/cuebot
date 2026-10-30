using System.Globalization;

namespace Cuebot.Core.Actions;

public enum ParameterKind { Text, Multiline, Number, Boolean, Choice, Code }

/// <summary>
/// Describes one setting of a trigger or sub-action. The UI renders editors from these descriptors,
/// so modules never ship their own editor views.
/// </summary>
public sealed record ParameterDescriptor(string Key, string Label, ParameterKind Kind = ParameterKind.Text)
{
    public string DefaultValue { get; init; } = "";

    /// <summary>Fixed options for <see cref="ParameterKind.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Live suggestions for text fields (e.g. OBS scene names, voice aliases).</summary>
    public Func<IEnumerable<string>>? Suggestions { get; init; }

    /// <summary>Syntax for <see cref="ParameterKind.Code"/>: "csharp" or "python".</summary>
    public string? Language { get; init; }

    public string? Help { get; init; }

    /// <summary>A live message shown above the editor, such as "Python is not installed [Install Python]".</summary>
    public IParameterNoticeSource? Notice { get; init; }
}

/// <summary>A message shown with a parameter's editor, with an optional button that runs <see cref="Run"/>.</summary>
public sealed record ParameterNotice(string Text, string? ButtonText = null, Func<Task>? Run = null);

/// <summary>Supplies a parameter's current notice and says when it changes (possibly on a background thread).</summary>
public interface IParameterNoticeSource
{
    /// <summary>The notice to show now, or null for none.</summary>
    ParameterNotice? Current { get; }

    event Action? Changed;
}

/// <summary>Read access to parameter values with descriptor defaults and typed parsing.</summary>
public sealed class ParameterValues
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public ParameterValues(IReadOnlyDictionary<string, string> values) => _values = values;

    public static ParameterValues From(IReadOnlyList<ParameterDescriptor> descriptors, IReadOnlyDictionary<string, string> raw)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in descriptors) values[d.Key] = raw.TryGetValue(d.Key, out var v) ? v : d.DefaultValue;
        return new ParameterValues(values);
    }

    public string this[string key] => GetString(key);

    public string GetString(string key) => _values.TryGetValue(key, out var v) ? v : "";

    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        bool.TryParse(GetString(key), out var v) ? v : fallback;
}
