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
            definition.Triggers.Select(t => new TriggerItemViewModel(t, registry.GetTrigger(t.Type), () => MarkChanged(false))));
        Steps = new StepListViewModel(definition.SubActions, this);
    }

    public ActionDefinition Definition { get; }
    public Registry Registry { get; }
    public ObservableCollection<TriggerItemViewModel> Triggers { get; }
    public StepListViewModel Steps { get; }

    /// <summary>Raised after any edit (structure, layout or parameters), so views can refresh.</summary>
    public event Action? Changed;

    /// <summary>Raised when a view asks to add a step at a specific place (the page shows the type picker).</summary>
    public event Action<StepInsertTarget>? AddStepRequested;

    public event Action? AddTriggerRequested;

    [ObservableProperty] private EditorItemViewModel? _selected;

    partial void OnSelectedChanged(EditorItemViewModel? oldValue, EditorItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

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

    public bool Concurrent
    {
        get => Definition.Concurrent;
        set => Set(Definition.Concurrent, value, v => Definition.Concurrent = v);
    }

    public string TriggerSummary => Triggers.Count switch
    {
        0 => "No triggers",
        1 => Triggers[0].Title,
        _ => $"{Triggers[0].Title} +{Triggers.Count - 1}",
    };

    public void MarkChanged(bool structural = true)
    {
        _profile.NotifyChanged();
        if (structural) OnPropertyChanged(nameof(TriggerSummary));
        Changed?.Invoke();
    }

    public TriggerItemViewModel AddTrigger(ITriggerType type)
    {
        var definition = new TriggerDefinition { Type = type.TypeId };
        foreach (var p in type.Parameters) definition.Settings[p.Key] = p.DefaultValue;
        Definition.Triggers.Add(definition);
        var item = new TriggerItemViewModel(definition, type, () => MarkChanged(false));
        Triggers.Add(item);
        MarkChanged();
        Selected = item;
        return item;
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
        MarkChanged(structural: false);
    }
}
