using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>The Actions page: action list, and the selected action in list or node-graph view.</summary>
public sealed partial class ActionsViewModel : PageViewModel
{
    private readonly ProfileService _profile;
    private readonly Registry _registry;
    private readonly ActionEngine _engine;
    private readonly List<ActionItemViewModel> _all;

    public ActionsViewModel(ProfileService profile, Registry registry, ActionEngine engine) : base("Actions")
    {
        _profile = profile;
        _registry = registry;
        _engine = engine;
        _all = profile.Profile.Actions.Select(Wrap).ToList();
        ApplyFilter();
        SelectedAction = Actions.FirstOrDefault();
        profile.Changed += () => OnUi(OnProfileChanged);
    }

    public ObservableCollection<ActionItemViewModel> Actions { get; } = [];
    public TypePickerViewModel Picker { get; } = new();
    public TestTriggerViewModel Test { get; } = new();

    /// <summary>
    /// The live profile can change outside this page: "Set action state" toggles Enabled, the Queues page adds or
    /// renames queues. Re-read what each action shows.
    /// </summary>
    private void OnProfileChanged()
    {
        foreach (var action in _all) action.RefreshFromProfile();
    }

    [ObservableProperty] private string _filter = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ActionItemViewModel? _selectedAction;
    [ObservableProperty] private bool _isGraphView;
    [ObservableProperty] private string _lastRunResult = "";

    public bool HasSelection => SelectedAction is not null;

    partial void OnFilterChanged(string value) => ApplyFilter();

    private ActionItemViewModel Wrap(ActionDefinition definition)
    {
        var item = new ActionItemViewModel(definition, _registry, _profile);
        item.AddStepRequested += target => PickStep(item, target);
        item.AddTriggerRequested += () => PickTrigger(item);
        return item;
    }

    private void ApplyFilter()
    {
        var selected = SelectedAction;
        Actions.Clear();
        foreach (var action in _all
                     .Where(a => Filter.Length == 0 || a.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                                    || a.Group.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            Actions.Add(action);
        }
        SelectedAction = selected is not null && Actions.Contains(selected) ? selected : null;
    }

    private void Add(ActionDefinition definition)
    {
        _profile.Profile.Actions.Add(definition);
        _profile.NotifyChanged();
        var item = Wrap(definition);
        _all.Add(item);
        Filter = "";
        ApplyFilter();
        SelectedAction = item;
    }

    [RelayCommand]
    private void NewAction()
    {
        var name = "New action";
        for (var i = 2; _all.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"New action {i}";
        Add(new ActionDefinition { Name = name });
    }

    [RelayCommand]
    private void DuplicateAction()
    {
        if (SelectedAction is null) return;
        var copy = Json.Clone(SelectedAction.Definition);
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        foreach (var t in copy.Triggers) t.Id = Guid.NewGuid();
        Add(copy);
    }

    [RelayCommand]
    private void DeleteAction()
    {
        if (SelectedAction is not { } action) return;
        _profile.Profile.Actions.Remove(action.Definition);
        _profile.NotifyChanged();
        _all.Remove(action);
        SelectedAction = null;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RunAction()
    {
        if (SelectedAction is not { } action) return;
        LastRunResult = $"Running '{action.Name}'...";
        // Run the engine's snapshot copy, so the run reflects saved state like a real trigger would.
        var snapshot = _profile.Snapshot.Actions.FirstOrDefault(a => a.Id == action.Definition.Id) ?? action.Definition;
        bool ok;
        try { ok = await _engine.RunAsync(snapshot); }
        catch (OperationCanceledException) { ok = false; }
        LastRunResult = ok ? $"'{action.Name}' completed" : $"'{action.Name}' failed — see Dashboard log";
    }

    /// <summary>Opens the "Test trigger" overlay for the selected action.</summary>
    [RelayCommand]
    private void TestTrigger()
    {
        if (SelectedAction is not { } action) return;
        Test.Open(action, async (option, args) =>
        {
            var snapshot = _profile.Snapshot.Actions.FirstOrDefault(a => a.Id == action.Definition.Id) ?? action.Definition;
            var trigger = option.Trigger is null ? null : snapshot.Triggers.FirstOrDefault(t => t.Id == option.Trigger.Id) ?? option.Trigger;
            LastRunResult = $"Testing '{action.Name}'...";
            bool ok;
            try { ok = await _engine.TestTriggerAsync(snapshot, trigger, args); }
            catch (OperationCanceledException) { ok = false; }
            LastRunResult = ok ? $"Test of '{action.Name}' completed" : $"Test of '{action.Name}' failed or was cancelled — see Dashboard log";
        });
    }

    [RelayCommand]
    private void AddTrigger()
    {
        if (SelectedAction is { } action) PickTrigger(action);
    }

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedAction is { } action) PickStep(action, action.DefaultInsertTarget());
    }

    [RelayCommand]
    private void DeleteItem() => SelectedAction?.DeleteSelected();

    [RelayCommand]
    private void MoveUp() => SelectedAction?.MoveSelected(-1);

    [RelayCommand]
    private void MoveDown() => SelectedAction?.MoveSelected(1);

    [RelayCommand]
    private void AutoArrange() => SelectedAction?.ClearLayout();

    [RelayCommand]
    private void Select(EditorItemViewModel item)
    {
        if (SelectedAction is not null) SelectedAction.Selected = item;
    }

    private void PickTrigger(ActionItemViewModel action) =>
        Picker.Open("Add trigger",
            _registry.Triggers.Select(t => new TypeOption(t.Category, t.DisplayName, t)),
            option => action.AddTrigger((ITriggerType)option.Payload));

    private void PickStep(ActionItemViewModel action, StepInsertTarget target) =>
        Picker.Open("Add sub-action",
            _registry.SubActions.Select(h => new TypeOption(h.Category, h.DisplayName, h)),
            option => action.AddStep((ISubActionHandler)option.Payload, target));
}
