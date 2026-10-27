using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cuebot.App.ViewModels.Actions;

public sealed record TypeOption(string Category, string Name, object Payload)
{
    public string Display => $"{Category} › {Name}";
}

/// <summary>Searchable picker for trigger and sub-action types, shown as an overlay.</summary>
public sealed partial class TypePickerViewModel : ObservableObject
{
    private IReadOnlyList<TypeOption> _all = [];
    private Action<TypeOption>? _onPick;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private TypeOption? _selected;

    public ObservableCollection<TypeOption> Options { get; } = [];

    public void Open(string title, IEnumerable<TypeOption> options, Action<TypeOption> onPick)
    {
        Title = title;
        _all = options.ToArray();
        _onPick = onPick;
        Search = "";
        ApplyFilter();
        IsOpen = true;
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Options.Clear();
        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var option in _all.Where(o => terms.All(t => o.Display.Contains(t, StringComparison.OrdinalIgnoreCase))))
            Options.Add(option);
        Selected = Options.FirstOrDefault();
    }

    [RelayCommand]
    private void Confirm()
    {
        if (Selected is null) return;
        IsOpen = false;
        _onPick?.Invoke(Selected);
        _onPick = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        _onPick = null;
    }
}
