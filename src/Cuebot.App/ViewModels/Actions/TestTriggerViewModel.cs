using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Variables;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>
/// One choice in the test overlay: an action's trigger, or custom arguments only (null trigger). <see cref="Sample"/> is
/// what the arguments box starts with for it: a made-up value for each of the trigger's variables.
/// </summary>
public sealed record TestTriggerOption(string Display, TriggerDefinition? Trigger, string Sample = TestTriggerViewModel.DefaultArguments);

/// <summary>
/// "Test trigger…" overlay: pick a trigger, type key=value arguments (one per line) and run the action as if the
/// trigger had fired. Values are auto-typed like variables ("5" becomes a number).
/// </summary>
public sealed partial class TestTriggerViewModel : ObservableObject
{
    private Func<TestTriggerOption, IReadOnlyDictionary<string, object?>, Task>? _run;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _actionName = "";
    [ObservableProperty] private TestTriggerOption? _selected;
    public const string DefaultArguments = "user=TestUser\nmessage=Hello from a test";

    [ObservableProperty] private string _arguments = DefaultArguments;
    [ObservableProperty] private string _error = "";

    public ObservableCollection<TestTriggerOption> Options { get; } = [];

    public void Open(ActionItemViewModel action, Func<TestTriggerOption, IReadOnlyDictionary<string, object?>, Task> run)
    {
        ActionName = action.Name;
        Options.Clear();
        foreach (var t in action.Triggers)
            Options.Add(new TestTriggerOption($"{t.Category} › {t.Title}", t.Definition, TriggerSamples.Arguments(t.VariableHelp, t.Definition.Settings)));
        Options.Add(new TestTriggerOption("Custom arguments only", null));
        Selected = null;   // so the first option counts as a change and fills the box
        Selected = Options[0];
        Error = "";
        _run = run;
        IsOpen = true;
    }

    // Each trigger starts with its own sample values (a cheer gets bits=100); edits are kept until another is chosen.
    partial void OnSelectedChanged(TestTriggerOption? value)
    {
        if (value is not null) Arguments = value.Sample;
    }

    /// <summary>Parses "key=value" lines. Blank lines and lines starting with # are ignored.</summary>
    public static IReadOnlyDictionary<string, object?> ParseArguments(string text, out string? error)
    {
        error = null;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            var key = eq < 0 ? "" : line[..eq].Trim();
            if (key.Length == 0)
            {
                error = $"Line {lineNumber}: expected name=value";
                continue;
            }
            args[key] = VariableValues.AutoType(line[(eq + 1)..].Trim());
        }
        return args;
    }

    [RelayCommand]
    private async Task Run()
    {
        if (Selected is not { } option || _run is not { } run) return;
        var args = ParseArguments(Arguments, out var error);
        if (error is not null)
        {
            Error = error;
            return;
        }
        IsOpen = false;
        _run = null;
        await run(option, args);
    }

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        _run = null;
    }
}

/// <summary>
/// Made-up values for a trigger's variables, so "Test trigger" runs an action the way a real event would: a cheer gets
/// bits, a Kick gift gets kicks, a Super Chat gets an amount. A number is raised to the trigger's own minimum
/// (minBits for bits, and so on) so the test passes the trigger's filter.
/// </summary>
public static class TriggerSamples
{
    public static string Arguments(IReadOnlyDictionary<string, string> variables, IReadOnlyDictionary<string, string> settings)
    {
        if (variables.Count == 0) return TestTriggerViewModel.DefaultArguments;
        var lines = variables.Select(v => $"{v.Key}={Value(v.Key, v.Value, settings)}");
        return string.Join("\n", lines);
    }

    internal static string Value(string name, string meaning, IReadOnlyDictionary<string, string> settings)
    {
        var n = name.ToLowerInvariant();
        var m = meaning.ToLowerInvariant();
        string sample;
        if (n.StartsWith("is", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2]) || n.StartsWith("has", StringComparison.Ordinal)) sample = "False";
        else if (n.EndsWith("id", StringComparison.Ordinal)) sample = "12345";
        else if (n.EndsWith("username", StringComparison.Ordinal) || n == "login") sample = "testuser";   // logins are lowercase
        else if (n is "user" or "displayname" or "gifter" or "moderator" or "raider" or "topcontributor") sample = "TestUser";
        else if (n is "message" or "text" or "userinput" or "comment") sample = "Hello from a test";
        else if (n == "currency") sample = "USD";
        else if (n.Contains("text", StringComparison.Ordinal) && n.Contains("amount", StringComparison.Ordinal) || n.StartsWith("formatted", StringComparison.Ordinal)) sample = "$5.00";
        else if (n is "bits" or "kicks" or "points" or "rewardcost") sample = "100";
        else if (n is "amount" or "total" or "tip" || n.EndsWith("amount", StringComparison.Ordinal)) sample = "5";
        else if (n is "tier") sample = "1000";
        else if (n is "viewers" or "count" or "months" or "level" or "streak" || n.EndsWith("count", StringComparison.Ordinal)
                 || n.EndsWith("months", StringComparison.Ordinal) || m.StartsWith("how many", StringComparison.Ordinal) || m.StartsWith("number of", StringComparison.Ordinal)) sample = "3";
        else if (n.Contains("url", StringComparison.Ordinal) || n.Contains("link", StringComparison.Ordinal)) sample = "https://example.com";
        else sample = "Test";

        // The trigger's own lower limit (minBits, minKicks, minAmount…) wins over a smaller sample.
        if (double.TryParse(sample, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            foreach (var (key, raw) in settings)
            {
                if (!key.StartsWith("min", StringComparison.OrdinalIgnoreCase)) continue;
                var limited = key[3..];
                if (!string.Equals(limited, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(limited + "s", name, StringComparison.OrdinalIgnoreCase)) continue;
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var min) && min > number)
                    sample = min.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return sample;
    }
}
