using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>
/// The Actions page: action list, and the selected action in list or node-graph view.
/// <para>
/// Copy/paste: actions, sub-actions (with branches and group children) and triggers go to an in-app clipboard as
/// model JSON (<see cref="EditorClip"/>), and the same text to the system clipboard when the view connects one.
/// Pasting assigns new ids. Undo/redo: each action keeps its own bounded history of JSON snapshots for the whole
/// session (switching actions keeps it; deleting the action drops it), see <see cref="EditHistory"/>.
/// </para>
/// </summary>
public sealed partial class ActionsViewModel : PageViewModel
{
    private readonly ProfileService _profile;
    private readonly Registry _registry;
    private readonly ActionEngine _engine;
    private readonly List<ActionItemViewModel> _all;
    private readonly Dictionary<Guid, EditHistory> _histories = [];
    private EditorClip? _clip;
    private bool _restoring;

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
    /// The live profile can change outside this page: "Set action state" toggles Enabled, the Queues page adds or
    /// renames queues. Re-read what each action shows.
    /// </summary>
    private void OnProfileChanged()
    {
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
    private ActionItemViewModel? _selectedAction;
    [ObservableProperty] private bool _isGraphView;
    [ObservableProperty] private string _lastRunResult = "";

    /// <summary>Result of the last copy, paste, duplicate, undo or redo.</summary>
    [ObservableProperty] private string _editStatus = "";

    public bool HasSelection => SelectedAction is not null;

    public bool CanUndo => SelectedAction is { } a && History(a).CanUndo;
    public bool CanRedo => SelectedAction is { } a && History(a).CanRedo;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedActionChanged(ActionItemViewModel? value)
    {
        if (value is not null)
        {
            var history = History(value);
            var now = Snapshot(value.Definition);
            if (now != history.Current) AdoptOutsideChange(history, now);
        }
        RaiseUndoState();
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
        OnPropertyChanged(nameof(ActionCount));
    }

    /// <summary>"12 actions" (or "3 of 12" while searching).</summary>
    public string ActionCount => Actions.Count == _all.Count ? Plural(_all.Count, "action") : $"{Actions.Count} of {_all.Count}";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private ActionItemViewModel Add(ActionDefinition definition)
    {
        _profile.Profile.Actions.Add(definition);
        _profile.NotifyChanged();
        var item = Wrap(definition);
        _all.Add(item);
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
        var copy = Json.Clone(SelectedAction.Definition);
        EditorClip.NewIds(copy);
        copy.Name = UniqueName(copy.Name);
        Add(copy);
        EditStatus = $"Duplicated as '{copy.Name}'.";
    }

    [RelayCommand]
    private void DeleteAction()
    {
        if (SelectedAction is not { } action) return;
        _profile.Profile.Actions.Remove(action.Definition);
        _profile.NotifyChanged();
        _all.Remove(action);
        _histories.Remove(action.Definition.Id);
        SelectedAction = null;
        ApplyFilter();
    }

    private Guid? _deleteArmedFor;
    private DateTime _deleteArmedAt;

    /// <summary>
    /// The Delete key in the action list: the first press asks, a second press within 5 seconds deletes the action
    /// (an action cannot be undone once deleted).
    /// </summary>
    public bool DeleteActionWithConfirm()
    {
        if (SelectedAction is not { } action) return false;
        var now = Clock();
        if (_deleteArmedFor == action.Definition.Id && now - _deleteArmedAt < TimeSpan.FromSeconds(5))
        {
            _deleteArmedFor = null;
            DeleteAction();
            EditStatus = $"Deleted '{action.Name}'.";
            return true;
        }
        _deleteArmedFor = action.Definition.Id;
        _deleteArmedAt = now;
        EditStatus = $"Press Delete again to delete the action '{action.Name}'.";
        return false;
    }

    /// <summary>Ctrl+O: turns the selected action (from the action list) or the selected trigger/sub-action on or off.</summary>
    public void ToggleEnabled(bool wholeAction)
    {
        if (SelectedAction is not { } action) return;
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
    private void MoveToTop() => SelectedAction?.MoveSelectedToEdge(top: true);

    [RelayCommand]
    private void MoveToBottom() => SelectedAction?.MoveSelectedToEdge(top: false);

    [RelayCommand]
    private void AutoArrange() => SelectedAction?.ClearLayout();

    [RelayCommand]
    private void Select(EditorItemViewModel item)
    {
        if (SelectedAction is not null) SelectedAction.Selected = item;
    }

    // ---- Copy / paste / duplicate ----

    /// <summary>
    /// Copies the selected trigger or sub-action (a branch copies its sub-actions), or the whole action when
    /// <paramref name="wholeAction"/> is set or nothing inside the action is selected.
    /// </summary>
    public async Task<EditorClip?> CopySelectionAsync(bool wholeAction)
    {
        if (SelectedAction is not { } action) return null;
        var clip = wholeAction ? EditorClip.Of(action.Definition) : action.Selected switch
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
                foreach (var a in fresh.Actions)
                {
                    a.Name = UniqueName(a.Name);
                    Add(a);
                }
                EditStatus = $"Pasted {fresh.Describe()}.";
                return true;
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

    /// <summary>Duplicates the selected sub-action (with its children) or trigger next to it, else the whole action.</summary>
    [RelayCommand]
    private void DuplicateItem()
    {
        if (SelectedAction is not { } action) return;
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
        if (SelectedAction is not { } action || History(action).Undo() is not { } state) return;
        Restore(action, state);
        EditStatus = "Undone.";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
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
        RaiseUndoState();
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
