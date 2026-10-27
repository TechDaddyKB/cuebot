using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Variables;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>One choice in the test overlay: an action's trigger, or custom arguments only (null trigger).</summary>
public sealed record TestTriggerOption(string Display, TriggerDefinition? Trigger);

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
    [ObservableProperty] private string _arguments = "user=TestUser\nmessage=Hello from a test";
    [ObservableProperty] private string _error = "";

    public ObservableCollection<TestTriggerOption> Options { get; } = [];

    public void Open(ActionItemViewModel action, Func<TestTriggerOption, IReadOnlyDictionary<string, object?>, Task> run)
    {
        ActionName = action.Name;
        Options.Clear();
        foreach (var t in action.Triggers) Options.Add(new TestTriggerOption($"{t.Category} › {t.Title}", t.Definition));
        Options.Add(new TestTriggerOption("Custom arguments only", null));
        Selected = Options[0];
        Error = "";
        _run = run;
        IsOpen = true;
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
