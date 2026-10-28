using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;

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
    private readonly Action<string?> _changed;

    /// <param name="changed">Called after an edit with an undo coalesce key (null: never merged with other edits).</param>
    public TriggerItemViewModel(TriggerDefinition definition, ITriggerType? type, Action<string?> changed)
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
            _changed(null);
        }
    }

    /// <summary>The trigger has a min/max range, so Streamer.bot's "Always Run" option applies.</summary>
    public bool IsRangeTrigger => Type is not null && RangeTriggers.IsRangeTrigger(Type);

    /// <summary>Run even when a narrower range trigger of the same type also matches.</summary>
    public bool AlwaysRun
    {
        get => Definition.AlwaysRun;
        set
        {
            if (Definition.AlwaysRun == value) return;
            Definition.AlwaysRun = value;
            OnPropertyChanged();
            _changed(null);
        }
    }

    private void OnParameterChanged()
    {
        OnPropertyChanged(nameof(Summary));
        _changed("trigger:" + Definition.Id);
    }
}

public sealed partial class SubActionItemViewModel : EditorItemViewModel
{
    /// <summary>Colour of a Group whose "color" parameter is blank or not a colour.</summary>
    public static readonly Color DefaultGroupColor = Color.Parse("#7C8496");

    private readonly ActionItemViewModel _owner;

    public SubActionItemViewModel(SubActionDefinition definition, ActionItemViewModel owner, StepListViewModel parent)
    {
        Definition = definition;
        _owner = owner;
        Parent = parent;
        Handler = owner.Registry.GetSubAction(definition.Type);
        Parameters = Handler?.Parameters.Select(d => ParameterViewModel.Create(d, definition.Parameters, OnParameterChanged)).ToArray() ?? [];
        Branches = new ObservableCollection<BranchViewModel>(
            CurrentBranchNames().Select(name => new BranchViewModel(this, name, new StepListViewModel(definition.Branch(name), owner, this))));
    }

    /// <summary>Declared branches, or for dynamic handlers (e.g. Switch cases) the ones its parameters define now.</summary>
    private IReadOnlyList<string> CurrentBranchNames() =>
        Handler is null ? [] : SubActionBranches.For(Handler, Definition);

    /// <summary>
    /// Re-syncs branch view models after a parameter edit changed a dynamic handler's branches. Child steps of a
    /// branch that disappears stay in the JSON (they reappear if the case comes back) but are no longer shown.
    /// </summary>
    private bool RefreshBranches()
    {
        var names = CurrentBranchNames();
        if (names.SequenceEqual(Branches.Select(b => b.Name))) return false;
        Branches.Clear();
        foreach (var name in names)
            Branches.Add(new BranchViewModel(this, name, new StepListViewModel(Definition.Branch(name), _owner, this)));
        OnPropertyChanged(nameof(HasBranches));
        return true;
    }

    public SubActionDefinition Definition { get; }
    public ISubActionHandler? Handler { get; }
    public StepListViewModel Parent { get; internal set; }
    public override IReadOnlyList<ParameterViewModel> Parameters { get; }
    public ObservableCollection<BranchViewModel> Branches { get; }
    public bool HasBranches => Branches.Count > 0;
    public override string Title => IsGroup ? GroupName : Handler?.DisplayName ?? $"Unknown ({Definition.Type})";
    public string Category => IsGroup ? (IsRandomGroup ? "Group · one at random" : "Group") : Handler?.Category ?? "Missing";

    public override string Summary
    {
        get
        {
            if (!IsGroup) return Summaries.Describe(Handler?.Parameters, Definition.Parameters);
            var count = Branches.Sum(b => b.Steps.Items.Count);
            return IsRandomGroup ? $"Runs one of {count} sub-action(s), picked by weight" : $"{count} sub-action(s), run in order";
        }
    }

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

    // ---- Group containers ----

    /// <summary>A "Group" sub-action: shown as a named container tinted by its colour.</summary>
    public bool IsGroup => Definition.Type == GroupSubAction.Id;

    public string GroupName =>
        Definition.Parameters.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : "Group";

    /// <summary>A group with "random" on runs one enabled child, picked by weight.</summary>
    public bool IsRandomGroup =>
        IsGroup && Definition.Parameters.TryGetValue("random", out var r) && string.Equals(r, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The group's editor colour (its "color" parameter), or a neutral default.</summary>
    public Color GroupColor =>
        Definition.Parameters.TryGetValue("color", out var c) && !string.IsNullOrWhiteSpace(c) && Color.TryParse(c.Trim(), out var parsed)
            ? parsed
            : DefaultGroupColor;

    public IBrush ContainerBackground => IsGroup ? new SolidColorBrush(GroupColor, 0.14) : Brushes.Transparent;
    public IBrush ContainerBorderBrush => IsGroup ? new SolidColorBrush(GroupColor) : Brushes.Transparent;
    public Thickness ContainerBorderThickness => IsGroup ? new Thickness(3, 0, 0, 0) : default;
    public Thickness ContainerPadding => IsGroup ? new Thickness(2, 2, 4, 4) : default;
    public Thickness ContainerMargin => IsGroup ? new Thickness(0, 2, 0, 4) : default;

    // ---- Weight (random picks) ----

    /// <summary>True when the parent runs ONE random child (Random action, or a random Group), so weight matters.</summary>
    public bool ShowsWeight => Parent.PicksRandomChild;

    /// <summary>Relative chance of being picked. 0 means never (unless every candidate is 0).</summary>
    public decimal? Weight
    {
        get => (decimal)Definition.Weight;
        set
        {
            if (value is not { } v) return;
            var weight = (double)Math.Max(0, v);
            if (Definition.Weight.Equals(weight)) return;
            Definition.Weight = weight;
            OnPropertyChanged();
            _owner.MarkChanged(structural: false, coalesceKey: "weight:" + Definition.Id);
        }
    }

    /// <summary>"Weight 2 · 40%": the chance among the enabled sub-actions of the same list.</summary>
    public string WeightLabel
    {
        get
        {
            if (!ShowsWeight) return "";
            var weight = Definition.Weight.ToString("0.##", CultureInfo.InvariantCulture);
            if (!Enabled) return $"Weight {weight} · disabled";
            var candidates = Parent.Items.Where(i => i.Enabled).ToArray();
            var total = candidates.Sum(i => Math.Max(0, i.Definition.Weight));
            var chance = total <= 0 ? 1.0 / candidates.Length : Math.Max(0, Definition.Weight) / total;
            return string.Create(CultureInfo.InvariantCulture, $"Weight {weight} · {chance * 100:0.#}%");
        }
    }

    /// <summary>Re-reads values that depend on siblings or the parent (weights, random mode, group summary).</summary>
    internal void RefreshDerived()
    {
        OnPropertyChanged(nameof(ShowsWeight));
        OnPropertyChanged(nameof(WeightLabel));
        if (IsGroup) OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Every step list nested under this step's branches, at any depth.</summary>
    public IEnumerable<StepListViewModel> OwnedLists() =>
        Branches.SelectMany(b => b.Steps.Items.SelectMany(s => s.OwnedLists()).Prepend(b.Steps));

    private void OnParameterChanged()
    {
        OnPropertyChanged(nameof(Summary));
        if (IsGroup)
        {
            foreach (var name in (string[])[nameof(Title), nameof(Category), nameof(GroupName), nameof(IsRandomGroup), nameof(GroupColor),
                         nameof(ContainerBackground), nameof(ContainerBorderBrush)])
                OnPropertyChanged(name);
        }
        var structural = RefreshBranches();
        _owner.MarkChanged(structural, structural ? null : "step:" + Definition.Id);
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

    public StepListViewModel(List<SubActionDefinition> model, ActionItemViewModel owner, SubActionItemViewModel? ownerStep = null)
    {
        Model = model;
        _owner = owner;
        OwnerStep = ownerStep;
        Items = new ObservableCollection<SubActionItemViewModel>(model.Select(d => new SubActionItemViewModel(d, owner, this)));
    }

    public List<SubActionDefinition> Model { get; }
    public ObservableCollection<SubActionItemViewModel> Items { get; }

    /// <summary>The step whose branch this list is; null for the action's top-level list.</summary>
    public SubActionItemViewModel? OwnerStep { get; }

    /// <summary>The engine runs ONE child of this list, picked by weight (Random action, or a random Group).</summary>
    public bool PicksRandomChild => OwnerStep is null ? _owner.RandomAction : OwnerStep.IsRandomGroup;

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
