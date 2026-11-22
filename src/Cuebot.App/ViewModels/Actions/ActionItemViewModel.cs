using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>Where a new or moved step goes: a step list and an index in it.</summary>
/// <param name="Layout">Where a new step goes on the flowchart canvas (null: placed automatically).</param>
public sealed record StepInsertTarget(StepListViewModel List, int Index, NodeLayout? Layout = null);

/// <summary>
/// Editing model for one action, shared by the list editor and the node graph editor.
/// All structural edits go through here so both views and actions.json stay in sync.
/// </summary>
public sealed partial class ActionItemViewModel : ObservableObject
{
    private readonly ProfileService _profile;

    public ActionItemViewModel(ActionDefinition definition, Registry registry, ProfileService profile)
    {
        Definition = definition;
        Registry = registry;
        _profile = profile;
        Triggers = new ObservableCollection<TriggerItemViewModel>(
            definition.Triggers.Select(t => new TriggerItemViewModel(t, registry.GetTrigger(t.Type), key => MarkChanged(false, key))));
        Steps = new StepListViewModel(definition.SubActions, this);
        RefreshFromProfile();
    }

    public ActionDefinition Definition { get; }
    public Registry Registry { get; }
    public ObservableCollection<TriggerItemViewModel> Triggers { get; }
    public StepListViewModel Steps { get; }

    /// <summary>Raised after any edit (structure, layout or parameters), so views can refresh.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised after each edit, before the profile publishes it, with a coalesce key: edits sharing a non-null key in
    /// quick succession (typing in one field) form one undo step. Inside <see cref="Batch"/> it is raised once at the end.
    /// </summary>
    public event Action<string?>? Edited;

    /// <summary>Raised when a view asks to add a step at a specific place (the page shows the type picker).</summary>
    public event Action<StepInsertTarget>? AddStepRequested;

    public event Action? AddTriggerRequested;

    [ObservableProperty] private EditorItemViewModel? _selected;

    partial void OnSelectedChanged(EditorItemViewModel? oldValue, EditorItemViewModel? newValue)
    {
        if (!_keepStepSelection)
        {
            // A plain selection replaces a multi-selection.
            foreach (var step in _selectedSteps) step.IsSelected = false;
            _selectedSteps.Clear();
            if (newValue is SubActionItemViewModel single) _selectedSteps.Add(single);
            _stepAnchor = newValue as SubActionItemViewModel;
        }
        if (oldValue is not null && !(oldValue is SubActionItemViewModel kept && _selectedSteps.Contains(kept))) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(SelectedTrigger));
        RaiseStepSelection();
    }

    // ---- Several sub-actions selected (always from one list, so every block operation is well defined) ----

    private readonly List<SubActionItemViewModel> _selectedSteps = [];
    private SubActionItemViewModel? _stepAnchor;
    private bool _keepStepSelection;

    /// <summary>The selected sub-actions in list order (one list only). A single selected step counts.</summary>
    public IReadOnlyList<SubActionItemViewModel> SelectedSteps =>
        _selectedSteps.Where(s => s.Parent.Items.Contains(s)).OrderBy(s => s.Parent.Items.IndexOf(s)).ToArray();

    /// <summary>Two or more sub-actions are selected: the details pane gives way to the block tools.</summary>
    public bool HasStepBlock => SelectedSteps.Count > 1;

    public string StepBlockSummary => $"{SelectedSteps.Count} sub-actions selected";

    private void RaiseStepSelection()
    {
        OnPropertyChanged(nameof(SelectedStep));
        OnPropertyChanged(nameof(SelectedSteps));
        OnPropertyChanged(nameof(HasStepBlock));
        OnPropertyChanged(nameof(StepBlockSummary));
    }

    private void SetStepSelection(IEnumerable<SubActionItemViewModel> steps, SubActionItemViewModel primary)
    {
        var next = steps.ToList();
        foreach (var step in _selectedSteps.Where(s => !next.Contains(s))) step.IsSelected = false;
        _selectedSteps.Clear();
        _selectedSteps.AddRange(next);
        foreach (var step in next) step.IsSelected = true;
        _keepStepSelection = true;
        try { Selected = primary; }
        finally { _keepStepSelection = false; }
        RaiseStepSelection();
    }

    /// <summary>Ctrl/Cmd+click: adds or removes a step. A step from another list starts a new selection there.</summary>
    public void ToggleStepSelection(SubActionItemViewModel step)
    {
        var current = SelectedSteps;
        if (current.Count == 0 || current[0].Parent != step.Parent)
        {
            Selected = step;
            return;
        }
        _stepAnchor = step;
        if (!current.Contains(step))
        {
            SetStepSelection(current.Append(step), step);
            return;
        }
        if (current.Count == 1) return;   // the last selected step stays selected
        var rest = current.Where(s => s != step).ToArray();
        SetStepSelection(rest, rest[^1]);
    }

    /// <summary>Shift+click: selects from the last clicked step to this one, within one list.</summary>
    public void SelectStepRange(SubActionItemViewModel step)
    {
        var anchor = _stepAnchor is { } a && a.Parent == step.Parent && a.Parent.Items.Contains(a) ? a : null;
        if (anchor is null)
        {
            Selected = step;
            return;
        }
        var items = step.Parent.Items;
        var (from, to) = (items.IndexOf(anchor), items.IndexOf(step));
        if (from > to) (from, to) = (to, from);
        SetStepSelection(items.Skip(from).Take(to - from + 1), step);
    }

    /// <summary>Ctrl+A: every step of the list the selection is in (the top-level list when nothing is selected).</summary>
    public void SelectAllSteps()
    {
        var list = Selected switch
        {
            SubActionItemViewModel step => step.Parent,
            BranchViewModel branch => branch.Steps,
            _ => Steps,
        };
        if (list.Items.Count > 0) SetStepSelection(list.Items, list.Items[^1]);
    }

    /// <summary>Back to one selected step (the flowchart and Esc work on one).</summary>
    public void CollapseStepSelection()
    {
        if (HasStepBlock && Selected is SubActionItemViewModel primary) SetStepSelection([primary], primary);
    }

    /// <summary>Selects these steps again after undo rebuilt the editor.</summary>
    public void SelectSteps(IReadOnlyCollection<Guid> ids)
    {
        var found = Steps.Descendants().Where(s => ids.Contains(s.Definition.Id)).ToArray();
        if (found.Length < 2 || found.Any(s => s.Parent != found[0].Parent)) return;
        SetStepSelection(found, found[^1]);
    }

    /// <summary>Turns the selected steps on or off as one undo step.</summary>
    public void SetSelectedStepsEnabled(bool enabled)
    {
        using (Batch())
        {
            foreach (var step in SelectedSteps) step.Enabled = enabled;
        }
    }

    /// <summary>Copies the selected steps right after the last of them, and selects the copies. One undo step.</summary>
    public IReadOnlyList<SubActionItemViewModel> DuplicateSelectedSteps()
    {
        var steps = SelectedSteps;
        if (steps.Count == 0) return [];
        var copies = steps.Select(s =>
        {
            var copy = Cuebot.Core.Storage.Json.Clone(s.Definition);
            EditorClip.NewIds(copy, clearLayout: true);
            return copy;
        }).ToArray();
        var list = steps[0].Parent;
        var added = InsertSteps(copies, new StepInsertTarget(list, list.Items.IndexOf(steps[^1]) + 1));
        if (added.Count > 1) SetStepSelection(added, added[^1]);
        return added;
    }

    /// <summary>Default name of the Group that <see cref="WrapSelectionInGroup"/> creates.</summary>
    public const string NewGroupName = "New group";

    /// <summary>Puts the selected steps, in order, into a new Group where the first of them was. One undo step.</summary>
    /// <returns>The new group, or null when nothing is selected (or no Group sub-action is registered).</returns>
    public SubActionItemViewModel? WrapSelectionInGroup()
    {
        var steps = SelectedSteps;
        if (steps.Count == 0 || Registry.GetSubAction(GroupSubAction.Id) is not { } groupHandler) return null;
        var list = steps[0].Parent;
        SubActionItemViewModel group;
        using (Batch())
        {
            var definition = new SubActionDefinition { Type = GroupSubAction.Id };
            foreach (var p in groupHandler.Parameters) definition.Parameters[p.Key] = p.DefaultValue;
            definition.Parameters["name"] = NewGroupName;
            foreach (var branch in SubActionBranches.For(groupHandler, definition)) definition.Branch(branch);
            group = list.Insert(list.Items.IndexOf(steps[0]), definition);
            var inside = group.Branches.First(b => b.Name == GroupSubAction.Steps).Steps;
            foreach (var step in steps)
            {
                step.Definition.Layout = null;
                inside.Adopt(step, inside.Items.Count);
            }
        }
        Selected = group;
        return group;
    }

    /// <summary>The selection when it is a trigger (shown under the trigger list).</summary>
    public TriggerItemViewModel? SelectedTrigger => Selected as TriggerItemViewModel;

    /// <summary>The selection when it is a sub-action or branch (shown under the sub-action list).</summary>
    public EditorItemViewModel? SelectedStep => Selected is TriggerItemViewModel || HasStepBlock ? null : Selected;

    /// <summary>Set by the page on the first action of each group in the list: the group's folder row.</summary>
    [ObservableProperty] private string? _groupHeader;

    [ObservableProperty] private int _groupCount;

    public string Name
    {
        get => Definition.Name;
        set => Set(Definition.Name, value, v => Definition.Name = v);
    }

    public string Group
    {
        get => Definition.Group;
        set => Set(Definition.Group, value, v => Definition.Group = v);
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set => Set(Definition.Enabled, value, v => Definition.Enabled = v);
    }

    /// <summary>Only used without a queue: runs of this action may overlap instead of waiting for each other.</summary>
    public bool Concurrent
    {
        get => Definition.Concurrent;
        set => Set(Definition.Concurrent, value, v => Definition.Concurrent = v);
    }

    /// <summary>Queue name as shown in the dropdown: "Default" for actions without a queue.</summary>
    public string? Queue
    {
        get => QueueService.Normalize(Definition.Queue);
        set
        {
            if (value is null) return;   // the dropdown clears its selection while its items are replaced
            var stored = string.Equals(QueueService.Normalize(value), QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase)
                ? ""
                : value.Trim();
            if (Definition.Queue == stored) return;
            Definition.Queue = stored;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueue));
            OnPropertyChanged(nameof(RunSummary));
            MarkChanged(structural: false);
        }
    }

    /// <summary>The action waits in a named queue, so "Allow concurrent runs" does not apply.</summary>
    public bool HasQueue => !string.IsNullOrWhiteSpace(Definition.Queue);

    /// <summary>"Default" followed by the profile's queues (and the action's own queue if it no longer exists).</summary>
    public IReadOnlyList<string> QueueOptions { get; private set; } = [];

    public bool RandomAction
    {
        get => Definition.RandomAction;
        set => Set(Definition.RandomAction, value, v => Definition.RandomAction = v);
    }

    public bool ConcurrentSubActions
    {
        get => Definition.ConcurrentSubActions;
        set => Set(Definition.ConcurrentSubActions, value, v => Definition.ConcurrentSubActions = v);
    }

    public bool AlwaysRun
    {
        get => Definition.AlwaysRun;
        set => Set(Definition.AlwaysRun, value, v => Definition.AlwaysRun = v);
    }

    public bool ExcludeFromHistory
    {
        get => Definition.ExcludeFromHistory;
        set => Set(Definition.ExcludeFromHistory, value, v => Definition.ExcludeFromHistory = v);
    }

    /// <summary>Short description of how the action runs, for the graph's entry node.</summary>
    public string RunSummary
    {
        get
        {
            var parts = new List<string> { HasQueue ? $"Queue: {Queue}" : Concurrent ? "Runs concurrently" : "Runs queued" };
            if (RandomAction) parts.Add("one random step");
            else if (ConcurrentSubActions) parts.Add("steps in parallel");
            if (AlwaysRun) parts.Add("always runs");
            return string.Join(" · ", parts);
        }
    }

    public string TriggerSummary => Triggers.Count switch
    {
        0 => "No triggers",
        1 => Triggers[0].Title,
        _ => $"{Triggers[0].Title} +{Triggers.Count - 1}",
    };

    private int _batchDepth;
    private bool _batchStructural;
    private bool _batchChanged;

    public void MarkChanged(bool structural = true, string? coalesceKey = null)
    {
        if (_batchDepth > 0)
        {
            _batchChanged = true;
            _batchStructural |= structural;
            return;
        }
        Edited?.Invoke(coalesceKey);
        _profile.NotifyChanged();
        if (structural) OnPropertyChanged(nameof(TriggerSummary));
        foreach (var step in Steps.Descendants()) step.RefreshDerived();
        Changed?.Invoke();
    }

    /// <summary>Groups several edits (a multi-step paste) into one change notification and one undo step.</summary>
    public IDisposable Batch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private sealed class BatchScope(ActionItemViewModel owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--owner._batchDepth > 0 || !owner._batchChanged) return;
            owner._batchChanged = false;
            var structural = owner._batchStructural;
            owner._batchStructural = false;
            owner.MarkChanged(structural);
        }
    }

    /// <summary>
    /// Re-reads values that can change outside this editor: Enabled (the "Set action state" sub-action edits the
    /// live profile) and the queue list (Queues page).
    /// </summary>
    public void RefreshFromProfile()
    {
        OnPropertyChanged(nameof(Enabled));
        var options = new List<string> { QueueService.DefaultQueue };
        foreach (var q in _profile.Profile.Queues)
        {
            if (!options.Contains(q.Name, StringComparer.OrdinalIgnoreCase)) options.Add(q.Name);
        }
        if (Queue is { } current && !options.Contains(current, StringComparer.OrdinalIgnoreCase)) options.Add(current);
        if (options.SequenceEqual(QueueOptions)) return;
        QueueOptions = options;
        OnPropertyChanged(nameof(QueueOptions));
        OnPropertyChanged(nameof(Queue));
    }

    public TriggerItemViewModel AddTrigger(ITriggerType type)
    {
        var definition = new TriggerDefinition { Type = type.TypeId };
        foreach (var p in type.Parameters) definition.Settings[p.Key] = p.DefaultValue;
        return AddTriggerDefinition(definition);
    }

    /// <summary>Adds an existing trigger definition (pasted or duplicated) at <paramref name="index"/> (default: the end).</summary>
    public TriggerItemViewModel AddTriggerDefinition(TriggerDefinition definition, int? index = null)
    {
        var at = Math.Clamp(index ?? Definition.Triggers.Count, 0, Definition.Triggers.Count);
        Definition.Triggers.Insert(at, definition);
        var item = new TriggerItemViewModel(definition, Registry.GetTrigger(definition.Type), key => MarkChanged(false, key));
        Triggers.Insert(at, item);
        MarkChanged();
        Selected = item;
        return item;
    }

    /// <summary>Inserts existing step definitions (pasted or duplicated) in order at the target, as one edit.</summary>
    public IReadOnlyList<SubActionItemViewModel> InsertSteps(IReadOnlyList<SubActionDefinition> steps, StepInsertTarget target)
    {
        var added = new List<SubActionItemViewModel>();
        using (Batch())
        {
            var index = Math.Clamp(target.Index, 0, target.List.Items.Count);
            foreach (var step in steps) added.Add(target.List.Insert(index++, step));
        }
        if (added.Count > 0) Selected = added[^1];
        return added;
    }

    /// <summary>Every selectable item: triggers, steps at any depth, and their branch headers.</summary>
    public IEnumerable<EditorItemViewModel> AllItems() =>
        Triggers.Cast<EditorItemViewModel>()
            .Concat(Steps.Descendants().SelectMany(s => s.Branches.Cast<EditorItemViewModel>().Prepend(s)));

    /// <summary>Id of the selected trigger or step (a branch reports its owner step and name), to restore selection after undo.</summary>
    public EditorItemKey? SelectedKey => Selected switch
    {
        TriggerItemViewModel t => new EditorItemKey(t.Definition.Id, null),
        SubActionItemViewModel s => new EditorItemKey(s.Definition.Id, null),
        BranchViewModel b => new EditorItemKey(b.Owner.Definition.Id, b.Name),
        _ => null,
    };

    public void Select(EditorItemKey? key)
    {
        if (key is not { } k) return;
        Selected = AllItems().FirstOrDefault(i => i switch
        {
            TriggerItemViewModel t => k.Branch is null && t.Definition.Id == k.Id,
            SubActionItemViewModel s => k.Branch is null && s.Definition.Id == k.Id,
            BranchViewModel b => b.Name == k.Branch && b.Owner.Definition.Id == k.Id,
            _ => false,
        });
    }

    public SubActionItemViewModel AddStep(ISubActionHandler handler, StepInsertTarget target)
    {
        var definition = new SubActionDefinition { Type = handler.TypeId };
        foreach (var p in handler.Parameters) definition.Parameters[p.Key] = p.DefaultValue;
        foreach (var branch in SubActionBranches.For(handler, definition)) definition.Branch(branch);
        definition.Layout = target.Layout;
        var item = target.List.Insert(target.Index, definition);
        Selected = item;
        return item;
    }

    /// <summary>Insert after the selected step, at the top of a selected branch, or at the end.</summary>
    public StepInsertTarget DefaultInsertTarget() => Selected switch
    {
        SubActionItemViewModel step when SelectedSteps is { Count: > 1 } block =>
            new StepInsertTarget(step.Parent, step.Parent.Items.IndexOf(block[^1]) + 1),
        SubActionItemViewModel step => new StepInsertTarget(step.Parent, step.Parent.Items.IndexOf(step) + 1),
        BranchViewModel branch => new StepInsertTarget(branch.Steps, branch.Steps.Items.Count),
        _ => new StepInsertTarget(Steps, Steps.Items.Count),
    };

    public void RequestAddStep(StepInsertTarget target) => AddStepRequested?.Invoke(target);

    public void RequestAddTrigger() => AddTriggerRequested?.Invoke();

    /// <summary>Moves a step to a new place, refusing to move a step into its own branches.</summary>
    public bool MoveStep(SubActionItemViewModel step, StepInsertTarget target)
    {
        if (step.OwnedLists().Contains(target.List)) return false;
        target.List.Adopt(step, target.Index);
        return true;
    }

    /// <summary>Name of the switched-off group that <see cref="Disconnect"/> puts cut-off steps in.</summary>
    public const string DisconnectedGroupName = "Disconnected steps";

    /// <summary>
    /// Cuts the wire into <paramref name="step"/>: it and every step after it in the same list leave the run order and
    /// go, in order, into a new switched-off Group ("Disconnected steps") at the end of that list. The steps are kept
    /// and shown, but never run, until they are dragged back onto a port or the group is switched on. One undo step.
    /// </summary>
    /// <param name="at">Where the group goes on the canvas (null: placed automatically).</param>
    /// <returns>The new group, or null when nothing could be cut (no Group sub-action registered).</returns>
    public SubActionItemViewModel? Disconnect(SubActionItemViewModel step, NodeLayout? at = null)
    {
        var list = step.Parent;
        var index = list.Items.IndexOf(step);
        if (index < 0 || Registry.GetSubAction(GroupSubAction.Id) is not { } groupHandler) return null;
        var cut = list.Items.Skip(index).ToArray();
        SubActionItemViewModel group;
        using (Batch())
        {
            var definition = new SubActionDefinition { Type = GroupSubAction.Id, Enabled = false, Layout = at };
            foreach (var p in groupHandler.Parameters) definition.Parameters[p.Key] = p.DefaultValue;
            definition.Parameters["name"] = DisconnectedGroupName;
            foreach (var branch in SubActionBranches.For(groupHandler, definition)) definition.Branch(branch);
            group = list.Insert(list.Items.Count, definition);
            var inside = group.Branches.First(b => b.Name == GroupSubAction.Steps).Steps;
            foreach (var s in cut)
            {
                // The group takes the first step's place on the canvas; the steps line up inside its frame.
                s.Definition.Layout = null;
                inside.Adopt(s, inside.Items.Count);
            }
        }
        Selected = group;
        return group;
    }

    public void DeleteSelected()
    {
        if (HasStepBlock)
        {
            using (Batch())
            {
                foreach (var step in SelectedSteps) step.Parent.Remove(step);
            }
            Selected = null;
            return;
        }
        switch (Selected)
        {
            case SubActionItemViewModel step:
                step.Parent.Remove(step);
                break;
            case TriggerItemViewModel trigger:
                Definition.Triggers.Remove(trigger.Definition);
                Triggers.Remove(trigger);
                MarkChanged();
                break;
            default:
                return;
        }
        Selected = null;
    }

    public void MoveSelected(int delta)
    {
        if (Selected is not SubActionItemViewModel step) return;
        if (!HasStepBlock)
        {
            step.Parent.Move(step, delta);
            return;
        }
        // One place per step, leading edge first; a step blocked by the list's end holds the ones behind it.
        var block = SelectedSteps;
        var list = block[0].Parent;
        var sign = Math.Sign(delta);
        using (Batch())
        {
            foreach (var s in sign < 0 ? block : block.Reverse())
            {
                var to = list.Items.IndexOf(s) + sign;
                if (to >= 0 && to < list.Items.Count && !block.Contains(list.Items[to])) list.Move(s, sign);
            }
        }
    }

    /// <summary>Moves the selected sub-action to the top or bottom of its list (Ctrl+Home / Ctrl+End).</summary>
    public void MoveSelectedToEdge(bool top)
    {
        if (Selected is not SubActionItemViewModel step) return;
        if (HasStepBlock)
        {
            var block = SelectedSteps;
            var list = block[0].Parent;
            using (Batch())
            {
                for (var i = 0; i < block.Count; i++)
                {
                    var s = top ? block[i] : block[block.Count - 1 - i];
                    var target = top ? i : list.Items.Count - 1 - i;
                    list.Move(s, target - list.Items.IndexOf(s));
                }
            }
            return;
        }
        var index = step.Parent.Items.IndexOf(step);
        if (index >= 0) step.Parent.Move(step, top ? -index : step.Parent.Items.Count - 1 - index);
    }

    /// <summary>Forget all node positions so the graph editor lays the action out automatically.</summary>
    public void ClearLayout()
    {
        Definition.Layout = null;
        foreach (var t in Definition.Triggers) t.Layout = null;
        foreach (var s in Steps.Descendants()) s.Definition.Layout = null;
        MarkChanged();
    }

    private void Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(RunSummary));
        // Typing a name or group is one undo step per pause; each toggle is its own step.
        MarkChanged(structural: false, coalesceKey: typeof(T) == typeof(string) ? name : null);
    }

    /// <summary>The action's name (screen readers read list items through this).</summary>
    public override string ToString() => Name;
}

/// <summary>Identifies a trigger or step (or a step's branch) across view model rebuilds.</summary>
public readonly record struct EditorItemKey(Guid Id, string? Branch);
