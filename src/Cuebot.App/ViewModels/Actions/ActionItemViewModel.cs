using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Core.Actions;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>Where a new or moved step goes: a step list and an index in it.</summary>
public sealed record StepInsertTarget(StepListViewModel List, int Index);

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
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(SelectedTrigger));
        OnPropertyChanged(nameof(SelectedStep));
    }

    /// <summary>The selection when it is a trigger (shown under the trigger list).</summary>
    public TriggerItemViewModel? SelectedTrigger => Selected as TriggerItemViewModel;

    /// <summary>The selection when it is a sub-action or branch (shown under the sub-action list).</summary>
    public EditorItemViewModel? SelectedStep => Selected is TriggerItemViewModel ? null : Selected;

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
        var item = target.List.Insert(target.Index, definition);
        Selected = item;
        return item;
    }

    /// <summary>Insert after the selected step, at the top of a selected branch, or at the end.</summary>
    public StepInsertTarget DefaultInsertTarget() => Selected switch
    {
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

    public void DeleteSelected()
    {
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
        if (Selected is SubActionItemViewModel step) step.Parent.Move(step, delta);
    }

    /// <summary>Moves the selected sub-action to the top or bottom of its list (Ctrl+Home / Ctrl+End).</summary>
    public void MoveSelectedToEdge(bool top)
    {
        if (Selected is not SubActionItemViewModel step) return;
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
}

/// <summary>Identifies a trigger or step (or a step's branch) across view model rebuilds.</summary>
public readonly record struct EditorItemKey(Guid Id, string? Branch);
