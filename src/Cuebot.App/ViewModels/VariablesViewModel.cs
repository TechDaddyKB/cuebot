using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;

namespace Cuebot.App.ViewModels;

public sealed record GlobalVariableRow(string Name, string Value, bool Persisted);

/// <summary>Inspect and edit global variables (~name~).</summary>
public sealed partial class VariablesViewModel : PageViewModel
{
    private readonly GlobalVariables _globals;

    public VariablesViewModel(GlobalVariables globals) : base("Variables")
    {
        _globals = globals;
        globals.Changed += _ => OnUi(Refresh);
        Refresh();
    }

    public ObservableCollection<GlobalVariableRow> Variables { get; } = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private bool _newPersisted = true;

    private void Refresh()
    {
        Variables.Clear();
        foreach (var (name, value) in _globals.All().OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            Variables.Add(new GlobalVariableRow(name, value, _globals.IsPersisted(name)));
    }

    [RelayCommand]
    private void SetVariable()
    {
        if (string.IsNullOrWhiteSpace(NewName)) return;
        _globals.Set(NewName.Trim(), NewValue, NewPersisted);
        NewName = NewValue = "";
    }

    [RelayCommand]
    private void Edit(GlobalVariableRow row)
    {
        NewName = row.Name;
        NewValue = row.Value;
        NewPersisted = row.Persisted;
    }

    [RelayCommand]
    private void Remove(GlobalVariableRow row) => _globals.Remove(row.Name);
}
