using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.Core.Actions;

namespace Cuebot.App.ViewModels.Actions;

/// <summary>Editor model for one trigger setting or sub-action parameter, writing through to the definition.</summary>
public abstract class ParameterViewModel : ObservableObject
{
    private readonly Dictionary<string, string> _store;
    private readonly Action _changed;

    protected ParameterViewModel(ParameterDescriptor descriptor, Dictionary<string, string> store, Action changed)
    {
        Descriptor = descriptor;
        _store = store;
        _changed = changed;
    }

    public ParameterDescriptor Descriptor { get; }
    public string Label => Descriptor.Label;
    public string? Help => Descriptor.Help;
    public bool HasHelp => !string.IsNullOrEmpty(Descriptor.Help);

    protected string RawValue
    {
        get => _store.TryGetValue(Descriptor.Key, out var v) ? v : Descriptor.DefaultValue;
        set
        {
            if (RawValue == value) return;
            _store[Descriptor.Key] = value;
            OnPropertyChanged(string.Empty);
            _changed();
        }
    }

    public static ParameterViewModel Create(ParameterDescriptor d, Dictionary<string, string> store, Action changed) => d.Kind switch
    {
        ParameterKind.Boolean => new BoolParameterViewModel(d, store, changed),
        ParameterKind.Choice => new ChoiceParameterViewModel(d, store, changed),
        ParameterKind.Multiline => new MultilineParameterViewModel(d, store, changed),
        ParameterKind.Code => new CodeParameterViewModel(d, store, changed),
        _ => new TextParameterViewModel(d, store, changed),
    };
}

public sealed class TextParameterViewModel(ParameterDescriptor d, Dictionary<string, string> store, Action changed)
    : ParameterViewModel(d, store, changed)
{
    public string Value { get => RawValue; set => RawValue = value; }

    public IReadOnlyList<string> Suggestions { get; } = d.Suggestions?.Invoke().Distinct().ToArray() ?? [];

    public bool HasSuggestions => Descriptor.Suggestions is not null;

    public string Placeholder => Descriptor.Kind == ParameterKind.Number ? "Number or %variable%" : "";
}

public sealed class MultilineParameterViewModel(ParameterDescriptor d, Dictionary<string, string> store, Action changed)
    : ParameterViewModel(d, store, changed)
{
    public string Value { get => RawValue; set => RawValue = value; }
}

public sealed class CodeParameterViewModel(ParameterDescriptor d, Dictionary<string, string> store, Action changed)
    : ParameterViewModel(d, store, changed)
{
    public string Value { get => RawValue; set => RawValue = value; }
    public string Language => Descriptor.Language ?? "csharp";
}

public sealed class BoolParameterViewModel(ParameterDescriptor d, Dictionary<string, string> store, Action changed)
    : ParameterViewModel(d, store, changed)
{
    public bool IsChecked
    {
        get => bool.TryParse(RawValue, out var b) && b;
        set => RawValue = value ? "true" : "false";
    }
}

public sealed class ChoiceParameterViewModel(ParameterDescriptor d, Dictionary<string, string> store, Action changed)
    : ParameterViewModel(d, store, changed)
{
    public IReadOnlyList<string> Choices => Descriptor.Choices;

    public string? Selected
    {
        get => RawValue;
        set => RawValue = value ?? Descriptor.DefaultValue;
    }
}
