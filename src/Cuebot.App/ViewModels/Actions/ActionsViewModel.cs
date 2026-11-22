using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.Core.Actions;
using Cuebot.Core.Storage;
using Cuebot.Scripting.Export;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>
/// The Actions page: action list, and the selected action in list or node-graph view.
/// <para>
/// Copy/paste: actions, sub-actions (with branches and group children) and triggers go to an in-app clipboard as
/// model JSON (<see cref="EditorClip"/>), and the same text to the system clipboard when the view connects one.
/// Pasting assigns new ids. Undo/redo: each action keeps its own bounded history of JSON snapshots for the whole
/// session (switching actions keeps it), see <see cref="EditHistory"/>. Deleted actions go on a short stack, with their
/// history, so "Undo delete" (or Ctrl+Z with no action selected) brings them back.
/// </para>
/// </summary>
public sealed partial class ActionsViewModel : PageViewModel
{
    private readonly ProfileService _profile;
    private readonly Registry _registry;
    private readonly ActionEngine _engine;
    private readonly AppSettingsStore? _settings;
    private readonly List<ActionItemViewModel> _all;
    private readonly Dictionary<Guid, EditHistory> _histories = [];
    private EditorClip? _clip;
    private bool _restoring;

    /// <param name="settings">Where editor preferences (flowchart grid snapping) are remembered; null keeps them in memory.</param>
    public ActionsViewModel(ProfileService profile, Registry registry, ActionEngine engine, AppSettingsStore? settings = null) : base("Actions")
    {
        _profile = profile;
        _registry = registry;
        _engine = engine;
        _settings = settings;
        _snapToGrid = settings?.Settings.General.FlowchartSnapToGrid ?? true;
        _all = profile.Profile.Actions.Select(Wrap).ToList();
        ApplyFilter();
        SelectedAction = Actions.FirstOrDefault();
        profile.Changed += () => OnUi(OnProfileChanged);
    }

    public ObservableCollection<ActionItemViewModel> Actions { get; } = [];
    public TypePickerViewModel Picker { get; } = new();
    public TestTriggerViewModel Test { get; } = new();

    /// <summary>Writes text to the system clipboard; set by the view (null in tests or without a window).</summary>
    public Func<string, Task>? WriteSystemClipboard { get; set; }

    /// <summary>Reads text from the system clipboard; set by the view.</summary>
    public Func<Task<string?>>? ReadSystemClipboard { get; set; }

    /// <summary>Edits with the same coalesce key inside this window become one undo step.</summary>
    public TimeSpan UndoCoalesceWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Time source for undo coalescing (tests replace it).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>What the in-app clipboard holds (null when nothing was copied in this session).</summary>
    public EditorClip? Clip => _clip;

    /// <summary>
    /// The live profile can change outside this page: the assistant, the API and imports add, replace or delete actions,
    /// "Set action state" toggles Enabled, the Queues page adds or renames queues. Re-sync the list, then re-read what
    /// each action shows.
    /// </summary>
    private void OnProfileChanged()
    {
        SyncWithProfile();
        foreach (var action in _all) action.RefreshFromProfile();
        // Outside changes to the edited action become the new baseline instead of an undo step.
        if (!_restoring && SelectedAction is { } selected && _histories.TryGetValue(selected.Definition.Id, out var history))
        {
            var now = Snapshot(selected.Definition);
            if (now != history.Current) AdoptOutsideChange(history, now);
        }
    }

    [ObservableProperty] private string _filter = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowsEditor))]
    private ActionItemViewModel? _selectedAction;
    [ObservableProperty] private bool _isGraphView;

    // The flowchart works on one box at a time, so it must not act on a block it does not show.
    partial void OnIsGraphViewChanged(bool value)
    {
        if (value) SelectedAction?.CollapseStepSelection();
    }

    /// <summary>Flowchart boxes snap to the dot grid when dragged or added (remembered in app.json).</summary>
    [ObservableProperty] private bool _snapToGrid;

    partial void OnSnapToGridChanged(bool value)
    {
        if (_settings is null) return;
        _settings.Settings.General.FlowchartSnapToGrid = value;
        _settings.TrySave();
    }
    [ObservableProperty] private string _lastRunResult = "";

    /// <summary>Result of the last copy, paste, duplicate, undo or redo.</summary>
    [ObservableProperty] private string _editStatus = "";

    public bool HasSelection => SelectedAction is not null;

    /// <summary>Leaving the page closes the test-trigger and type-picker dialogs, which would otherwise wait hidden.</summary>
    public override void OnLeft()
    {
        if (Test.IsOpen) Test.CancelCommand.Execute(null);
        if (Picker.IsOpen) Picker.CancelCommand.Execute(null);
    }

    public bool CanUndo => IsBulkSelection ? _bulkUndo.Count > 0 : SelectedAction is { } a && History(a).CanUndo;
    public bool CanRedo => IsBulkSelection ? _bulkRedo.Count > 0 : SelectedAction is { } a && History(a).CanRedo;

    // ---- Several actions selected ----

    private IReadOnlyList<ActionItemViewModel> _selectedActions = [];

    /// <summary>One bulk edit: each changed action's id and the state that edit left it in.</summary>
    private sealed record BulkStep(IReadOnlyList<(Guid Id, string State)> Actions);

    private readonly Stack<BulkStep> _bulkUndo = new();
    private readonly Stack<BulkStep> _bulkRedo = new();

    /// <summary>Asks the view to select these rows (after a bulk duplicate, paste, undo or restore).</summary>
    public event Action<IReadOnlyList<ActionItemViewModel>>? ActionSelectionRequested;

    /// <summary>The selected actions in list order.</summary>
    public IReadOnlyList<ActionItemViewModel> SelectedActions => _selectedActions;

    /// <summary>Two or more actions are selected: the bulk panel replaces the editor.</summary>
    public bool IsBulkSelection => _selectedActions.Count > 1;

    public bool ShowsEditor => HasSelection && !IsBulkSelection;

    public string BulkTitle => $"{_selectedActions.Count} actions selected";
    public string CopyActionHeader => IsBulkSelection ? $"Copy {_selectedActions.Count} actions" : "Copy action";
    public string DuplicateActionHeader => IsBulkSelection ? $"Duplicate {_selectedActions.Count} actions" : "Duplicate action";
    public string DeleteActionHeader => IsBulkSelection ? $"Delete {_selectedActions.Count} actions" : "Delete action";

    /// <summary>The group the bulk panel moves the selection to (empty: no group).</summary>
    [ObservableProperty] private string _bulkGroup = "";

    /// <summary>The groups in use, as suggestions for <see cref="BulkGroup"/>.</summary>
    public IReadOnlyList<string> GroupNames => _all.Select(a => a.Group).Where(g => g.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Takes the list's selection (the view calls this whenever it changes).</summary>
    public void SetSelectedActions(IEnumerable<ActionItemViewModel> items)
    {
        var wanted = items.ToHashSet();
        var next = Actions.Where(wanted.Contains).ToArray();
        if (next.SequenceEqual(_selectedActions)) return;
        var wasBulk = IsBulkSelection;
        _selectedActions = next;
        OnPropertyChanged(nameof(SelectedActions));
        OnPropertyChanged(nameof(IsBulkSelection));
        OnPropertyChanged(nameof(ShowsEditor));
        OnPropertyChanged(nameof(BulkTitle));
        OnPropertyChanged(nameof(CopyActionHeader));
        OnPropertyChanged(nameof(DuplicateActionHeader));
        OnPropertyChanged(nameof(DeleteActionHeader));
        if (IsBulkSelection && !wasBulk) OnPropertyChanged(nameof(GroupNames));
        RaiseUndoState();
    }

    /// <summary>Selects these actions, here and in the list.</summary>
    private void SelectActions(IReadOnlyList<ActionItemViewModel> items)
    {
        if (items.Count == 0) return;
        if (SelectedAction is null || !items.Contains(SelectedAction)) SelectedAction = items[0];
        SetSelectedActions(items);
        ActionSelectionRequested?.Invoke(_selectedActions);
    }

    /// <summary>What the action-level commands work on: the whole selection, or the one selected action.</summary>
    private IReadOnlyList<ActionItemViewModel> ActionTargets() =>
        IsBulkSelection ? _selectedActions : SelectedAction is { } action ? [action] : [];

    /// <summary>Applies one change to every selected action, as one undo step.</summary>
    private void BulkEdit(Action<ActionItemViewModel> edit, Func<int, string> status)
    {
        var changed = new List<(Guid, string)>();
        var targets = ActionTargets();
        foreach (var item in targets)
        {
            var history = History(item);   // made before the edit, so it holds the state to go back to
            var before = Snapshot(item.Definition);
            if (before != history.Current) AdoptOutsideChange(history, before);
            using (item.Batch()) edit(item);
            var after = Snapshot(item.Definition);
            if (after != before) changed.Add((item.Definition.Id, after));
        }
        if (changed.Count > 0)
        {
            _bulkUndo.Push(new BulkStep(changed));
            _bulkRedo.Clear();
        }
        ApplyFilter();   // a new group sorts differently
        SelectActions(targets.Where(Actions.Contains).ToArray());
        RaiseUndoState();
        EditStatus = status(changed.Count);
    }

    [RelayCommand]
    private void BulkTurnOn() => BulkEdit(a => a.Enabled = true, n => $"Turned on {Plural(n, "action")}.");

    [RelayCommand]
    private void BulkTurnOff() => BulkEdit(a => a.Enabled = false, n => $"Turned off {Plural(n, "action")}.");

    [RelayCommand]
    private void BulkMoveToGroup()
    {
        var group = BulkGroup.Trim();
        BulkEdit(a => a.Group = group, n => group.Length == 0
            ? $"Took {Plural(n, "action")} out of their group."
            : $"Moved {Plural(n, "action")} to '{group}'.");
        OnPropertyChanged(nameof(GroupNames));
    }

    /// <summary>Takes back the last bulk edit on every action it changed, unless that action was edited since.</summary>
    private void StepBulk(Stack<BulkStep> from, Stack<BulkStep> to, Func<EditHistory, string?> move, string done)
    {
        if (!from.TryPop(out var step)) return;
        var reversed = new List<(Guid, string)>();
        var touched = new List<Guid>();
        var skipped = 0;
        foreach (var (id, state) in step.Actions)
        {
            if (_all.FirstOrDefault(a => a.Definition.Id == id) is not { } item || History(item).Current != state)
            {
                skipped++;
                continue;
            }
            if (move(History(item)) is not { } restored) continue;
            Restore(item, restored);
            reversed.Add((id, restored));
            touched.Add(id);
        }
        if (reversed.Count > 0) to.Push(new BulkStep(reversed));
        SelectActions(Actions.Where(a => touched.Contains(a.Definition.Id)).ToArray());
        RaiseUndoState();
        EditStatus = skipped == 0 ? $"{done} {Plural(reversed.Count, "action")}."
            : $"{done} {Plural(reversed.Count, "action")}; {skipped} changed since and stayed as they are.";
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedActionChanged(ActionItemViewModel? value)
    {
        if (value is not null)
        {
            var history = History(value);
            var now = Snapshot(value.Definition);
            if (now != history.Current) AdoptOutsideChange(history, now);
        }
        // A row outside the multi-selection replaces it; a row inside it is just the new primary.
        if (value is null || !_selectedActions.Contains(value)) SetSelectedActions(value is null ? [] : [value]);
        RaiseUndoState();
    }

    /// <summary>
    /// Makes the list match the profile's actions: new ones get an editor, deleted ones leave, and an action the API
    /// replaced with a new object (same id) gets a fresh editor that keeps its place, selection and undo history.
    /// </summary>
    private void SyncWithProfile()
    {
        var live = _profile.Profile.Actions;
        var present = new HashSet<ActionDefinition>(live, ReferenceEqualityComparer.Instance);
        var changed = false;
        ActionItemViewModel? reselect = null;
        for (var i = _all.Count - 1; i >= 0; i--)
        {
            var item = _all[i];
            if (present.Contains(item.Definition)) continue;
            changed = true;
            if (live.FirstOrDefault(a => a.Id == item.Definition.Id) is { } replacement)
            {
                _all[i] = Wrap(replacement);
                if (ReferenceEquals(SelectedAction, item)) reselect = _all[i];
            }
            else
            {
                _all.RemoveAt(i);
                _histories.Remove(item.Definition.Id);
            }
        }
        var wrapped = new HashSet<ActionDefinition>(_all.Select(a => a.Definition), ReferenceEqualityComparer.Instance);
        foreach (var definition in live.Where(d => !wrapped.Contains(d)))
        {
            _all.Add(Wrap(definition));
            changed = true;
        }
        if (!changed) return;
        ApplyFilter();
        if (reselect is not null && Actions.Contains(reselect)) SelectedAction = reselect;
    }

    private ActionItemViewModel Wrap(ActionDefinition definition)
    {
        var item = new ActionItemViewModel(definition, _registry, _profile);
        item.AddStepRequested += target => PickStep(item, target);
        item.AddTriggerRequested += () => PickTrigger(item);
        item.Edited += key => RecordEdit(item, key);
        return item;
    }

    private void ApplyFilter()
    {
        var selected = SelectedAction;
        // Sync instead of clear + refill: the list keeps its selection, so the editor never flickers to "no action".
        Actions.SyncTo(_all
            .Where(a => Filter.Length == 0 || a.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                           || a.Group.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        SelectedAction = selected is not null && Actions.Contains(selected) ? selected : null;
        string? group = null;
        foreach (var action in Actions)
        {
            var first = group is null || !string.Equals(group, action.Group, StringComparison.OrdinalIgnoreCase);
            group = action.Group;
            action.GroupHeader = first ? (action.Group.Length == 0 ? "No group" : action.Group) : null;
            action.GroupCount = first ? Actions.Count(a => string.Equals(a.Group, action.Group, StringComparison.OrdinalIgnoreCase)) : 0;
        }
        SetSelectedActions(_selectedActions);   // drops rows the search hid or a delete removed, and keeps list order
        OnPropertyChanged(nameof(ActionCount));
    }

    /// <summary>"12 actions" (or "3 of 12" while searching).</summary>
    public string ActionCount => Actions.Count == _all.Count ? Plural(_all.Count, "action") : $"{Actions.Count} of {_all.Count}";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private ActionItemViewModel Add(ActionDefinition definition)
    {
        // The editor goes in the list first, so the change notification finds it there instead of adding a second one.
        var item = Wrap(definition);
        _all.Add(item);
        _profile.Profile.Actions.Add(definition);
        _profile.NotifyChanged();
        if (Filter.Length > 0) Filter = "";
        else ApplyFilter();
        SelectedAction = item;
        return item;
    }

    private string UniqueName(string name)
    {
        bool Taken(string n) => _all.Any(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        var copy = $"{name} (copy)";
        for (var i = 2; Taken(copy); i++) copy = $"{name} (copy {i})";
        return copy;
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
        if (IsBulkSelection)
        {
            var copies = AddAll(_selectedActions.Select(a =>
            {
                var clone = Json.Clone(a.Definition);
                EditorClip.NewIds(clone);
                return clone;
            }).ToArray());
            EditStatus = $"Duplicated {Plural(copies.Count, "action")}.";
            return;
        }
        var copy = Json.Clone(SelectedAction.Definition);
        EditorClip.NewIds(copy);
        copy.Name = UniqueName(copy.Name);
        Add(copy);
        EditStatus = $"Duplicated as '{copy.Name}'.";
    }

    /// <summary>Adds several actions under unique names, publishes once, and selects them.</summary>
    private IReadOnlyList<ActionItemViewModel> AddAll(IReadOnlyList<ActionDefinition> definitions)
    {
        var added = new List<ActionItemViewModel>();
        foreach (var definition in definitions)
        {
            definition.Name = UniqueName(definition.Name);
            var item = Wrap(definition);
            _all.Add(item);
            _profile.Profile.Actions.Add(definition);
            added.Add(item);
        }
        _profile.NotifyChanged();
        if (Filter.Length > 0) Filter = "";
        else ApplyFilter();
        SelectedAction = added[0];
        SelectActions(added);
        return added;
    }

    /// <summary>
    /// "Export to code": a copy of the selected action (same triggers and options) whose steps are replaced by one
    /// C# or Python code step that does the same. The original is switched off so the triggers don't run both.
    /// </summary>
    /// <param name="language">"csharp" or "python".</param>
    [RelayCommand]
    private void ExportToCode(string language)
    {
        if (SelectedAction is not { } source) return;
        var python = string.Equals(language, "python", StringComparison.OrdinalIgnoreCase);
        var export = ActionCodeExporter.Export(source.Definition, _registry, python ? CodeLanguage.Python : CodeLanguage.CSharp,
            _profile.Profile.Actions);

        var copy = Json.Clone(source.Definition);
        EditorClip.NewIds(copy);
        copy.Name = UniqueName($"{source.Name} ({(python ? "Python" : "C#")})");
        copy.RandomAction = false;
        copy.ConcurrentSubActions = false;
        copy.Layout = null;
        copy.Enabled = true;
        var step = new SubActionDefinition { Type = python ? "code.python" : "code.csharp" };
        if (_registry.GetSubAction(step.Type) is { } handler)
        {
            foreach (var p in handler.Parameters) step.Parameters[p.Key] = p.DefaultValue;
        }
        step.Parameters["code"] = export.Code;
        // Delays and waits that were steps now run inside the script; no time limit, like the steps had.
        step.Parameters["timeoutSeconds"] = "0";
        if (step.Parameters.ContainsKey("description")) step.Parameters["description"] = $"Exported from the action '{source.Name}'.";
        copy.SubActions = [step];

        var wasEnabled = source.Enabled;
        source.Enabled = false;
        Add(copy);
        var notes = export.Warnings.Count switch
        {
            0 => "",
            1 => " One note about it is at the top of the code.",
            var n => $" {n} notes about it are at the top of the code.",
        };
        EditStatus = $"Exported to '{copy.Name}'." + (wasEnabled ? $" '{source.Name}' is now off so it doesn't run twice." : "") + notes;
    }

    /// <summary>How many deleted actions "Undo delete" can bring back.</summary>
    public const int DeletedKept = 20;

    /// <summary>One press of Delete: the actions it removed, each with its old place and undo history.</summary>
    private readonly List<(ActionDefinition Definition, int Index, EditHistory? History)[]> _deleted = [];

    /// <summary>"Deleted 'Name'." while deleted actions can be brought back, else null (the notice is hidden).</summary>
    public string? DeletedNotice => _deleted.Count == 0 ? null
        : _deleted[^1].Length == 1 ? $"Deleted '{_deleted[^1][0].Definition.Name}'." : $"Deleted {_deleted[^1].Length} actions.";

    public bool CanRestoreDeleted => _deleted.Count > 0;

    [RelayCommand]
    private void DeleteAction()
    {
        var targets = ActionTargets();
        if (targets.Count == 0) return;
        // Oldest place first, so bringing them back in this order puts each where it was.
        var batch = targets
            .Select(a => (a.Definition, Index: _profile.Profile.Actions.IndexOf(a.Definition), History: _histories.GetValueOrDefault(a.Definition.Id)))
            .OrderBy(d => d.Index)
            .ToArray();
        foreach (var item in targets)
        {
            _profile.Profile.Actions.Remove(item.Definition);
            _all.Remove(item);
            _histories.Remove(item.Definition.Id);
        }
        _profile.NotifyChanged();
        _deleted.Add(batch);
        if (_deleted.Count > DeletedKept) _deleted.RemoveAt(0);
        SelectedAction = null;
        ApplyFilter();
        RaiseDeletedState();
        EditStatus = batch.Length == 1 ? $"Deleted '{batch[0].Definition.Name}'." : $"Deleted {batch.Length} actions.";
    }

    /// <summary>Brings back the most recently deleted action, in its old place, with its undo history.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreDeleted))]
    private void RestoreDeleted()
    {
        if (_deleted.Count == 0) return;
        var batch = _deleted[^1];
        _deleted.RemoveAt(_deleted.Count - 1);
        var restored = new List<ActionItemViewModel>();
        foreach (var (definition, index, history) in batch)
        {
            if (_profile.Profile.Actions.Any(a => a.Id == definition.Id)) continue;   // already back (an import or the API added it again)
            if (history is not null) _histories[definition.Id] = history;
            var item = Wrap(definition);
            _all.Add(item);
            _profile.Profile.Actions.Insert(Math.Clamp(index, 0, _profile.Profile.Actions.Count), definition);
            restored.Add(item);
        }
        if (restored.Count == 0)
        {
            RaiseDeletedState();
            return;
        }
        _profile.NotifyChanged();
        if (Filter.Length > 0) Filter = "";
        else ApplyFilter();
        SelectedAction = restored[0];
        SelectActions(restored);
        RaiseDeletedState();
        EditStatus = restored.Count == 1 ? $"Brought back '{restored[0].Name}'." : $"Brought back {restored.Count} actions.";
    }

    /// <summary>Hides the "Deleted …" notice; the deleted actions are gone for good.</summary>
    [RelayCommand]
    private void ForgetDeleted()
    {
        _deleted.Clear();
        RaiseDeletedState();
    }

    private void RaiseDeletedState()
    {
        OnPropertyChanged(nameof(DeletedNotice));
        OnPropertyChanged(nameof(CanRestoreDeleted));
        RestoreDeletedCommand.NotifyCanExecuteChanged();
    }

    private Guid? _deleteArmedFor;
    private DateTime _deleteArmedAt;
    private int _deleteArmedCount;

    /// <summary>
    /// The Delete key in the action list: the first press asks, a second press within 5 seconds deletes the action
    /// (it can still be brought back with Undo delete).
    /// </summary>
    public bool DeleteActionWithConfirm()
    {
        if (SelectedAction is not { } action) return false;
        var now = Clock();
        var count = ActionTargets().Count;
        if (_deleteArmedFor == action.Definition.Id && _deleteArmedCount == count && now - _deleteArmedAt < TimeSpan.FromSeconds(5))
        {
            _deleteArmedFor = null;
            DeleteAction();
            return true;
        }
        _deleteArmedFor = action.Definition.Id;
        _deleteArmedCount = count;
        _deleteArmedAt = now;
        EditStatus = count > 1 ? $"Press Delete again to delete {count} actions." : $"Press Delete again to delete the action '{action.Name}'.";
        return false;
    }

    /// <summary>Ctrl+O: turns the selected action (from the action list) or the selected trigger/sub-action on or off.</summary>
    public void ToggleEnabled(bool wholeAction)
    {
        if (SelectedAction is not { } action) return;
        if (IsBulkSelection)
        {
            // Mixed selection: first press turns everything on, like a tri-state checkbox.
            if (_selectedActions.Any(a => !a.Enabled)) BulkTurnOn();
            else BulkTurnOff();
            return;
        }
        if (!wholeAction && action.HasStepBlock)
        {
            var on = action.SelectedSteps.Any(s => !s.Enabled);
            action.SetSelectedStepsEnabled(on);
            EditStatus = $"{action.SelectedSteps.Count} sub-actions turned {(on ? "on" : "off")}.";
            return;
        }
        switch (wholeAction ? null : action.Selected)
        {
            case TriggerItemViewModel trigger:
                trigger.Enabled = !trigger.Enabled;
                EditStatus = trigger.Enabled ? "Trigger turned on." : "Trigger turned off.";
                break;
            case SubActionItemViewModel step:
                step.Enabled = !step.Enabled;
                EditStatus = step.Enabled ? "Sub-action turned on." : "Sub-action turned off.";
                break;
            default:
                action.Enabled = !action.Enabled;
                EditStatus = action.Enabled ? $"'{action.Name}' turned on." : $"'{action.Name}' turned off.";
                break;
        }
    }

    /// <summary>Creates an action started by <paramref name="type"/> (default settings) and selects it (Trigger Viewer).</summary>
    public ActionItemViewModel CreateActionFromTrigger(ITriggerType type)
    {
        var action = new ActionDefinition { Name = UniqueName(type.DisplayName) };
        var trigger = new TriggerDefinition { Type = type.TypeId };
        foreach (var p in type.Parameters) trigger.Settings[p.Key] = p.DefaultValue;
        action.Triggers.Add(trigger);
        var item = Add(action);
        EditStatus = $"Created '{action.Name}' from the {type.DisplayName} trigger.";
        return item;
    }

    /// <summary>Selects an action by id (clearing the search if it hides it). Returns false when it no longer exists.</summary>
    public bool SelectAction(Guid id)
    {
        if (_all.FirstOrDefault(a => a.Definition.Id == id) is not { } item) return false;
        if (!Actions.Contains(item)) Filter = "";
        SelectedAction = item;
        return true;
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
        if (IsBulkSelection || SelectedAction is not { } action) return;
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
    private void DeleteItem()
    {
        if (IsBulkSelection || SelectedAction is not { } action) return;   // the editor is hidden: it has no selection to act on
        var count = action.SelectedSteps.Count;
        action.DeleteSelected();
        if (count > 1) EditStatus = $"Deleted {count} sub-actions.";
    }

    [RelayCommand]
    private void MoveUp() { if (!IsBulkSelection) SelectedAction?.MoveSelected(-1); }

    [RelayCommand]
    private void MoveDown() { if (!IsBulkSelection) SelectedAction?.MoveSelected(1); }

    [RelayCommand]
    private void MoveToTop() { if (!IsBulkSelection) SelectedAction?.MoveSelectedToEdge(top: true); }

    [RelayCommand]
    private void MoveToBottom() { if (!IsBulkSelection) SelectedAction?.MoveSelectedToEdge(top: false); }

    [RelayCommand]
    private void StepsTurnOn() => SetStepsEnabled(true);

    [RelayCommand]
    private void StepsTurnOff() => SetStepsEnabled(false);

    private void SetStepsEnabled(bool enabled)
    {
        if (IsBulkSelection || SelectedAction is not { } action) return;
        action.SetSelectedStepsEnabled(enabled);
        EditStatus = $"{Plural(action.SelectedSteps.Count, "sub-action")} turned {(enabled ? "on" : "off")}.";
    }

    /// <summary>Ctrl+G: puts the selected sub-actions into a new Group.</summary>
    [RelayCommand]
    private void GroupSteps()
    {
        if (IsBulkSelection || SelectedAction is not { } action) return;
        var count = action.SelectedSteps.Count;
        if (action.WrapSelectionInGroup() is null)
        {
            EditStatus = "Select the sub-actions to put in a group first.";
            return;
        }
        EditStatus = $"Put {Plural(count, "sub-action")} in a new group.";
    }

    [RelayCommand]
    private void AutoArrange() => SelectedAction?.ClearLayout();

    [RelayCommand]
    private void Select(EditorItemViewModel item)
    {
        if (SelectedAction is not { } action) return;
        // A plain click on the block's primary step changes no property, but still means "just this one".
        if (ReferenceEquals(action.Selected, item)) action.CollapseStepSelection();
        else action.Selected = item;
    }

    // ---- Copy / paste / duplicate ----

    /// <summary>
    /// Copies the selected trigger or sub-action (a branch copies its sub-actions), or the whole action when
    /// <paramref name="wholeAction"/> is set or nothing inside the action is selected.
    /// </summary>
    public async Task<EditorClip?> CopySelectionAsync(bool wholeAction)
    {
        if (SelectedAction is not { } action) return null;
        var clip = IsBulkSelection
            ? new EditorClip { Kind = ClipKind.Actions, Actions = _selectedActions.Select(a => Json.Clone(a.Definition)).ToList() }
            : wholeAction ? EditorClip.Of(action.Definition)
            : action.HasStepBlock ? new EditorClip { Kind = ClipKind.SubActions, SubActions = action.SelectedSteps.Select(s => Json.Clone(s.Definition)).ToList() }
            : action.Selected switch
        {
            TriggerItemViewModel t => EditorClip.Of(t.Definition),
            SubActionItemViewModel s => EditorClip.Of(s.Definition),
            BranchViewModel { Steps.Model.Count: > 0 } b => new EditorClip { Kind = ClipKind.SubActions, SubActions = Json.Clone(b.Steps.Model) },
            BranchViewModel => null,
            _ => EditorClip.Of(action.Definition),
        };
        if (clip is null)
        {
            EditStatus = "That branch is empty; nothing to copy.";
            return null;
        }
        _clip = clip;
        OnPropertyChanged(nameof(Clip));
        EditStatus = $"Copied {clip.Describe()}.";
        if (WriteSystemClipboard is { } write)
        {
            try { await write(clip.ToJson()); }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or TimeoutException)
            {
                EditStatus = $"Copied {clip.Describe()} (inside Cuebot only: the system clipboard is unavailable).";
            }
        }
        return clip;
    }

    [RelayCommand]
    private Task CopyAction() => CopySelectionAsync(wholeAction: true);

    [RelayCommand]
    private Task CopyItem() => CopySelectionAsync(wholeAction: false);

    /// <summary>Pastes what another Cuebot (or this one) put on the system clipboard, else the in-app clipboard.</summary>
    [RelayCommand]
    private async Task Paste()
    {
        EditorClip? clip = null;
        if (ReadSystemClipboard is { } read)
        {
            try { clip = EditorClip.TryParse(await read()); }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or TimeoutException)
            {
                // Fall back to the in-app clipboard.
            }
        }
        clip ??= _clip;
        if (clip is null)
        {
            EditStatus = "Nothing to paste: copy an action, sub-action or trigger first.";
            return;
        }
        PasteClip(clip);
    }

    /// <summary>
    /// Pastes a clip with new ids: actions are added to the list; sub-actions go where "+ Sub-action" would put them
    /// (after the selected step, or into the selected branch); triggers go after the selected trigger.
    /// </summary>
    public bool PasteClip(EditorClip clip)
    {
        var fresh = clip.CloneWithNewIds();
        switch (fresh.Kind)
        {
            case ClipKind.Actions:
                if (fresh.Actions.Count > 1) AddAll(fresh.Actions);
                else
                {
                    fresh.Actions[0].Name = UniqueName(fresh.Actions[0].Name);
                    Add(fresh.Actions[0]);
                }
                EditStatus = $"Pasted {fresh.Describe()}.";
                return true;
            case ClipKind.SubActions or ClipKind.Triggers when IsBulkSelection:
                EditStatus = $"Select one action to paste {fresh.Describe()} into.";
                return false;
            case ClipKind.SubActions when SelectedAction is { } action:
                action.InsertSteps(fresh.SubActions, action.DefaultInsertTarget());
                EditStatus = $"Pasted {fresh.Describe()} into '{action.Name}'.";
                return true;
            case ClipKind.Triggers when SelectedAction is { } action:
                var index = action.Selected is TriggerItemViewModel t ? action.Triggers.IndexOf(t) + 1 : action.Triggers.Count;
                using (action.Batch())
                {
                    foreach (var trigger in fresh.Triggers) action.AddTriggerDefinition(trigger, index++);
                }
                EditStatus = $"Pasted {fresh.Describe()} into '{action.Name}'.";
                return true;
            default:
                EditStatus = $"Select an action to paste {fresh.Describe()} into.";
                return false;
        }
    }

    /// <summary>
    /// Duplicates the selected sub-action (with its children) or trigger next to it; with nothing selected in the
    /// editor, the whole action. A selected branch is part of its sub-action, so it is not copied on its own.
    /// </summary>
    [RelayCommand]
    private void DuplicateItem()
    {
        if (SelectedAction is not { } action) return;
        if (IsBulkSelection)
        {
            DuplicateAction();
            return;
        }
        if (action.HasStepBlock)
        {
            EditStatus = $"Duplicated {action.DuplicateSelectedSteps().Count} sub-actions.";
            return;
        }
        switch (action.Selected)
        {
            case SubActionItemViewModel step:
                var copy = Json.Clone(step.Definition);
                EditorClip.NewIds(copy, clearLayout: true);
                action.InsertSteps([copy], new StepInsertTarget(step.Parent, step.Parent.Items.IndexOf(step) + 1));
                EditStatus = $"Duplicated '{step.Title}'.";
                break;
            case TriggerItemViewModel trigger:
                var t = Json.Clone(trigger.Definition);
                EditorClip.NewIds(t, clearLayout: true);
                action.AddTriggerDefinition(t, action.Triggers.IndexOf(trigger) + 1);
                EditStatus = $"Duplicated trigger '{trigger.Title}'.";
                break;
            case BranchViewModel:
                EditStatus = "Select a sub-action or trigger to duplicate (a branch is duplicated with its sub-action).";
                break;
            default:
                DuplicateAction();
                break;
        }
    }

    // ---- Undo / redo ----

    private static string Snapshot(ActionDefinition definition) => JsonSerializer.Serialize(definition, Json.Options);

    /// <summary>
    /// The action changed outside the editor ("Set action state" toggles Enabled, the Queues page renames a queue):
    /// make that the baseline and carry the changed top-level settings into every undo/redo snapshot, so undoing an
    /// unrelated edit never reverts them.
    /// </summary>
    private static void AdoptOutsideChange(EditHistory history, string now)
    {
        if (JsonNode.Parse(history.Current) is not JsonObject before || JsonNode.Parse(now) is not JsonObject after)
        {
            history.Rebase(now);
            return;
        }
        var changed = after.Where(p => !JsonNode.DeepEquals(p.Value, before[p.Key]))
            .Select(p => (p.Key, Value: p.Value?.ToJsonString()))
            .ToArray();
        history.Rebase(now, changed.Length == 0 ? null : stored =>
        {
            if (JsonNode.Parse(stored) is not JsonObject snapshot) return stored;
            foreach (var (key, value) in changed) snapshot[key] = value is null ? null : JsonNode.Parse(value);
            return snapshot.ToJsonString(Json.Options);
        });
    }

    private EditHistory History(ActionItemViewModel item)
    {
        var id = item.Definition.Id;
        if (!_histories.TryGetValue(id, out var history))
            _histories[id] = history = new EditHistory(Snapshot(item.Definition));
        history.CoalesceWindow = UndoCoalesceWindow;
        return history;
    }

    private void RecordEdit(ActionItemViewModel item, string? coalesceKey)
    {
        if (!_all.Contains(item)) return; // a replaced editor (after undo) that something still edits
        History(item).Record(Snapshot(item.Definition), coalesceKey, Clock());
        if (item == SelectedAction) RaiseUndoState();
    }

    private void RaiseUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (IsBulkSelection)
        {
            StepBulk(_bulkUndo, _bulkRedo, h => h.Undo(), "Undone on");
            return;
        }
        if (SelectedAction is not { } action || History(action).Undo() is not { } state) return;
        Restore(action, state);
        EditStatus = "Undone.";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (IsBulkSelection)
        {
            StepBulk(_bulkRedo, _bulkUndo, h => h.Redo(), "Redone on");
            return;
        }
        if (SelectedAction is not { } action || History(action).Redo() is not { } state) return;
        Restore(action, state);
        EditStatus = "Redone.";
    }

    /// <summary>Replaces the action with a snapshot: the definition in the profile and a fresh editor for it.</summary>
    private void Restore(ActionItemViewModel item, string json)
    {
        var restored = JsonSerializer.Deserialize<ActionDefinition>(json, Json.Options);
        var index = _profile.Profile.Actions.IndexOf(item.Definition);
        var at = _all.IndexOf(item);
        if (restored is null || index < 0 || at < 0) return;
        var selectedKey = item.SelectedKey;
        var blockIds = item.HasStepBlock ? item.SelectedSteps.Select(s => s.Definition.Id).ToArray() : [];
        _profile.Profile.Actions[index] = restored;
        var replacement = Wrap(restored);
        _all[at] = replacement;
        History(replacement).Rebase(Snapshot(restored));
        // Put the new editor next to the old one and select it before removing the old one, so the selection never
        // passes through null (the editor panel would blank for a frame).
        var shown = Actions.IndexOf(item);
        Actions.Insert(shown < 0 ? Actions.Count : shown, replacement);
        SelectedAction = replacement;
        Actions.Remove(item);
        _restoring = true;
        try { _profile.NotifyChanged(); }
        finally { _restoring = false; }
        ApplyFilter(); // the restored name or group may sort (or filter) differently
        if (!Actions.Contains(replacement))
        {
            Filter = "";
            SelectedAction = replacement;
        }
        replacement.Select(selectedKey);
        replacement.SelectSteps(blockIds);
        RaiseUndoState();
    }

    private void PickTrigger(ActionItemViewModel action) =>
        Picker.Open("Add trigger",
            _registry.Triggers.Select(t => new TypeOption(t.Category, t.DisplayName, t, ActionHelp.Describe(t) ?? "")),
            option => action.AddTrigger((ITriggerType)option.Payload));

    private void PickStep(ActionItemViewModel action, StepInsertTarget target) =>
        Picker.Open("Add sub-action",
            _registry.SubActions.Select(h => new TypeOption(h.Category, h.DisplayName, h, ActionHelp.Describe(h) ?? "")),
            option => action.AddStep((ISubActionHandler)option.Payload, target));
}
