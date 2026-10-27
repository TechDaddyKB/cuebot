using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Core.Actions;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>Anything selectable in an action editor: a trigger, a step, or a branch header.</summary>
public abstract partial class EditorItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public abstract string Title { get; }
    public virtual string Summary => "";
    public virtual IReadOnlyList<ParameterViewModel> Parameters => [];
}

public sealed partial class TriggerItemViewModel : EditorItemViewModel
{
    private readonly Action _changed;

    public TriggerItemViewModel(TriggerDefinition definition, ITriggerType? type, Action changed)
    {
        Definition = definition;
        Type = type;
        _changed = changed;
        Parameters = type?.Parameters.Select(d => ParameterViewModel.Create(d, definition.Settings, OnParameterChanged)).ToArray() ?? [];
    }

    public TriggerDefinition Definition { get; }
    public ITriggerType? Type { get; }
    public override IReadOnlyList<ParameterViewModel> Parameters { get; }
    public override string Title => Type?.DisplayName ?? $"Unknown trigger ({Definition.Type})";
    public string Category => Type?.Category ?? "Missing";
    public override string Summary => Summaries.Describe(Type?.Parameters, Definition.Settings, withLabels: true);

    public bool Enabled
    {
        get => Definition.Enabled;
        set
        {
            if (Definition.Enabled == value) return;
            Definition.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    private void OnParameterChanged()
    {
        OnPropertyChanged(nameof(Summary));
        _changed();
    }
}

public sealed partial class SubActionItemViewModel : EditorItemViewModel
{
    private readonly ActionItemViewModel _owner;

    public SubActionItemViewModel(SubActionDefinition definition, ActionItemViewModel owner, StepListViewModel parent)
    {
        Definition = definition;
        _owner = owner;
        Parent = parent;
        Handler = owner.Registry.GetSubAction(definition.Type);
        Parameters = Handler?.Parameters.Select(d => ParameterViewModel.Create(d, definition.Parameters, OnParameterChanged)).ToArray() ?? [];
        Branches = new ObservableCollection<BranchViewModel>(
            (Handler?.Branches ?? []).Select(name => new BranchViewModel(this, name, new StepListViewModel(definition.Branch(name), owner))));
    }

    public SubActionDefinition Definition { get; }
    public ISubActionHandler? Handler { get; }
    public StepListViewModel Parent { get; internal set; }
    public override IReadOnlyList<ParameterViewModel> Parameters { get; }
    public ObservableCollection<BranchViewModel> Branches { get; }
    public bool HasBranches => Branches.Count > 0;
    public override string Title => Handler?.DisplayName ?? $"Unknown ({Definition.Type})";
    public string Category => Handler?.Category ?? "Missing";
    public override string Summary => Summaries.Describe(Handler?.Parameters, Definition.Parameters);

    public bool Enabled
    {
        get => Definition.Enabled;
        set
        {
            if (Definition.Enabled == value) return;
            Definition.Enabled = value;
            OnPropertyChanged();
            _owner.MarkChanged();
        }
    }

    /// <summary>Every step list nested under this step's branches, at any depth.</summary>
    public IEnumerable<StepListViewModel> OwnedLists() =>
        Branches.SelectMany(b => b.Steps.Items.SelectMany(s => s.OwnedLists()).Prepend(b.Steps));

    private void OnParameterChanged()
    {
        OnPropertyChanged(nameof(Summary));
        _owner.MarkChanged(structural: false);
    }
}

/// <summary>A named output of a branching step (e.g. If → True) holding its own chain.</summary>
public sealed class BranchViewModel(SubActionItemViewModel owner, string name, StepListViewModel steps) : EditorItemViewModel
{
    public SubActionItemViewModel Owner { get; } = owner;
    public string Name { get; } = name;
    public StepListViewModel Steps { get; } = steps;
    public override string Title => Name;
}

/// <summary>Keeps a list of step definitions and its view models in sync.</summary>
public sealed class StepListViewModel
{
    private readonly ActionItemViewModel _owner;

    public StepListViewModel(List<SubActionDefinition> model, ActionItemViewModel owner)
    {
        Model = model;
        _owner = owner;
        Items = new ObservableCollection<SubActionItemViewModel>(model.Select(d => new SubActionItemViewModel(d, owner, this)));
    }

    public List<SubActionDefinition> Model { get; }
    public ObservableCollection<SubActionItemViewModel> Items { get; }

    public SubActionItemViewModel Insert(int index, SubActionDefinition definition)
    {
        index = Math.Clamp(index, 0, Model.Count);
        Model.Insert(index, definition);
        var item = new SubActionItemViewModel(definition, _owner, this);
        Items.Insert(index, item);
        _owner.MarkChanged();
        return item;
    }

    public void Remove(SubActionItemViewModel item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return;
        Items.RemoveAt(index);
        Model.RemoveAt(index);
        _owner.MarkChanged();
    }

    public void Move(SubActionItemViewModel item, int delta)
    {
        var from = Items.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count) return;
        Items.Move(from, to);
        var def = Model[from];
        Model.RemoveAt(from);
        Model.Insert(to, def);
        _owner.MarkChanged();
    }

    /// <summary>Moves an existing step (with its branches) into this list at <paramref name="index"/>.</summary>
    public void Adopt(SubActionItemViewModel item, int index)
    {
        var source = item.Parent;
        var from = source.Items.IndexOf(item);
        if (from < 0) return;
        source.Items.RemoveAt(from);
        source.Model.RemoveAt(from);
        if (source == this && from < index) index--;
        index = Math.Clamp(index, 0, Model.Count);
        Model.Insert(index, item.Definition);
        Items.Insert(index, item);
        item.Parent = this;
        _owner.MarkChanged();
    }

    public IEnumerable<SubActionItemViewModel> Descendants() =>
        Items.SelectMany(i => new[] { i }.Concat(i.Branches.SelectMany(b => b.Steps.Descendants())));
}

internal static class Summaries
{
    public static string Describe(IReadOnlyList<ParameterDescriptor>? descriptors, Dictionary<string, string> values, bool withLabels = false)
    {
        if (descriptors is null) return "";
        var parts = descriptors
            .Where(d => d.Kind != ParameterKind.Code)
            .Select(d => (d, value: values.TryGetValue(d.Key, out var v) ? v : d.DefaultValue))
            .Where(x => !string.IsNullOrWhiteSpace(x.value))
            .Select(x => x.d.Kind == ParameterKind.Boolean ? (x.value == "true" ? x.d.Label : null) : withLabels ? $"{x.d.Label}: {x.value}" : x.value)
            .OfType<string>()
            .Select(v => v.ReplaceLineEndings(" "))
            .Take(3);
        var text = string.Join(" · ", parts);
        return text.Length > 80 ? text[..77] + "..." : text;
    }
}
