using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cuebot.App.ViewModels.Actions;

public sealed record TypeOption(string Category, string Name, object Payload, string Description = "")
{
    public string Display => $"{Category} › {Name}";
    public bool HasDescription => Description.Length > 0;
}

/// <summary>Searchable picker for trigger and sub-action types, shown as an overlay.</summary>
public sealed partial class TypePickerViewModel : ObservableObject
{
    private IReadOnlyList<TypeOption> _all = [];
    private Action<TypeOption>? _onPick;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedName), nameof(SelectedDescription), nameof(HasSelectedDescription))]
    private TypeOption? _selected;

    /// <summary>The highlighted option's name and plain-language description, shown under the list.</summary>
    public string SelectedName => Selected?.Name ?? "";
    public string SelectedDescription => Selected?.Description ?? "";
    public bool HasSelectedDescription => Selected?.HasDescription == true;

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
        // Names match first; options that only mention the words in their description follow.
        var named = _all.Where(o => terms.All(t => o.Display.Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        var described = terms.Length == 0 ? [] : _all.Except(named)
            .Where(o => terms.All(t => o.Display.Contains(t, StringComparison.OrdinalIgnoreCase) || o.Description.Contains(t, StringComparison.OrdinalIgnoreCase)));
        foreach (var option in named.Concat(described)) Options.Add(option);
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
