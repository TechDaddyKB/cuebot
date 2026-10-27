using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>One weighted message of an event template.</summary>
public sealed partial class EventMessageViewModel : ObservableObject
{
    private readonly EventKindViewModel _owner;

    public EventMessageViewModel(EventMessage message, EventKindViewModel owner)
    {
        Message = message;
        _owner = owner;
    }

    public EventMessage Message { get; }

    public string Text { get => Message.Text; set => Set(Message.Text, value ?? "", v => Message.Text = v); }
    public decimal Weight { get => Math.Max(1, Message.Weight); set => Set(Message.Weight, SpeechText.Clamp(value, 1, 100), v => Message.Weight = v); }
    public bool Enabled { get => Message.Enabled; set => Set(Message.Enabled, value, v => Message.Enabled = v); }

    private void Set<T>(T current, T value, Action<T> set, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        _owner.Materialize();
        set(value);
        _owner.Save();
        OnPropertyChanged(name);
    }
}

/// <summary>
/// The template of one activity kind. Until edited it shows the built-in default without storing it, so later
/// default improvements still reach users who never customized that kind.
/// </summary>
public sealed partial class EventKindViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public EventKindViewModel(string kind, string label, string defaultText, bool defaultEnabled, SpeechPageContext ctx,
        IReadOnlyList<Choice> aliasChoices)
    {
        Kind = kind;
        Label = label;
        _defaultText = defaultText;
        _defaultEnabled = defaultEnabled;
        _ctx = ctx;
        AliasChoices = aliasChoices;
        Template = Find() ?? CreateDefault();
        Messages = new ObservableCollection<EventMessageViewModel>(Template.Messages.Select(m => new EventMessageViewModel(m, this)));
    }

    private readonly string _defaultText;
    private readonly bool _defaultEnabled;

    public string Kind { get; }
    public string Label { get; }
    public EventTemplate Template { get; private set; }
    public ObservableCollection<EventMessageViewModel> Messages { get; }

    public bool IsCustomized => Find() is not null;

    public string Summary => !Template.Enabled ? "off" : IsCustomized ? $"{Messages.Count} message(s)" : "default";

    public bool Enabled
    {
        get => Template.Enabled;
        set
        {
            if (value == Template.Enabled) return;
            Materialize();
            Template.Enabled = value;
            Save();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice> AliasChoices { get; private set; }

    public Choice? Alias
    {
        get => SpeechPageContext.Find(AliasChoices, Template.Alias);
        set
        {
            if (value is null || value.Value == Template.Alias) return;
            Materialize();
            Template.Alias = value.Value;
            Save();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void AddMessage()
    {
        Materialize();
        var message = new EventMessage { Text = Messages.Count == 0 ? _defaultText : "" };
        Template.Messages.Add(message);
        Messages.Add(new EventMessageViewModel(message, this));
        Save();
    }

    [RelayCommand]
    private void RemoveMessage(EventMessageViewModel? message)
    {
        if (message is null) return;
        Materialize();
        Template.Messages.Remove(message.Message);
        Messages.Remove(message);
        Save();
    }

    /// <summary>Forgets the customization and goes back to the built-in message.</summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        if (Find() is { } stored) _ctx.Settings.Events.Templates.Remove(stored);
        Template = CreateDefault();
        Messages.Clear();
        foreach (var m in Template.Messages) Messages.Add(new EventMessageViewModel(m, this));
        Save();
        OnPropertyChanged(string.Empty);
    }

    public void RefreshChoices(IReadOnlyList<Choice> aliasChoices)
    {
        AliasChoices = aliasChoices;
        OnPropertyChanged(nameof(AliasChoices));
        OnPropertyChanged(nameof(Alias));
    }

    /// <summary>Stores the shown template in settings (once) so edits persist.</summary>
    internal void Materialize()
    {
        if (Find() is not null) return;
        _ctx.Settings.Events.Templates.Add(Template);
    }

    internal void Save()
    {
        _ctx.Save();
        OnPropertyChanged(nameof(IsCustomized));
        OnPropertyChanged(nameof(Summary));
    }

    private EventTemplate? Find() =>
        _ctx.Settings.Events.Templates.FirstOrDefault(t => string.Equals(t.Kind, Kind, StringComparison.OrdinalIgnoreCase));

    private EventTemplate CreateDefault() => new()
    {
        Kind = Kind,
        Enabled = _defaultEnabled,
        Messages = [new EventMessage { Text = _defaultText }],
    };
}

/// <summary>Events tab: speaking follows, subs, raids, etc.</summary>
public sealed partial class SpeechEventsViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public SpeechEventsViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        AliasChoices = ctx.AliasChoices("Default voice");
        KindAliasChoices = ctx.AliasChoices("Events alias");
        FilterProfiles = ctx.FilterProfileChoices();
        Kinds = SpeechDefaults.EventKinds
            .Select(k => new EventKindViewModel(k.Kind, k.Label, k.Text, k.Enabled, ctx, KindAliasChoices)).ToArray();
        _selectedKind = Kinds[0];
        ctx.ChoicesChanged += () =>
        {
            AliasChoices = ctx.AliasChoices("Default voice");
            KindAliasChoices = ctx.AliasChoices("Events alias");
            FilterProfiles = ctx.FilterProfileChoices();
            foreach (var kind in Kinds) kind.RefreshChoices(KindAliasChoices);
            OnPropertyChanged(nameof(AliasChoices));
            OnPropertyChanged(nameof(FilterProfiles));
            OnPropertyChanged(nameof(Alias));
            OnPropertyChanged(nameof(FilterProfile));
        };
    }

    private EventSpeechSettings Events => _ctx.Settings.Events;

    public IReadOnlyList<Choice> AliasChoices { get; private set; }
    public IReadOnlyList<Choice> KindAliasChoices { get; private set; }
    public IReadOnlyList<Choice> FilterProfiles { get; private set; }

    public IReadOnlyList<EventKindViewModel> Kinds { get; }

    [ObservableProperty] private EventKindViewModel? _selectedKind;

    public bool Enabled
    {
        get => _ctx.Speech.EventsEnabled;
        set { _ctx.Speech.EventsEnabled = value; OnPropertyChanged(); }
    }

    public Choice? Alias
    {
        get => SpeechPageContext.Find(AliasChoices, Events.Alias);
        set { if (value is not null) Set(Events.Alias, value.Value, v => Events.Alias = v); }
    }

    public Choice? FilterProfile
    {
        get => SpeechPageContext.Find(FilterProfiles, Events.FilterProfile);
        set { if (value is not null) Set(Events.FilterProfile, value.Value, v => Events.FilterProfile = v); }
    }

    public decimal MinRaidViewers { get => Events.MinRaidViewers; set => Set(Events.MinRaidViewers, SpeechText.Clamp(value, 0, 1_000_000), v => Events.MinRaidViewers = v); }
    public decimal MinCheerBits { get => Events.MinCheerBits; set => Set(Events.MinCheerBits, SpeechText.Clamp(value, 0, 10_000_000), v => Events.MinCheerBits = v); }
    public decimal MinTipAmount { get => (decimal)Events.MinTipAmount; set => Set(Events.MinTipAmount, (double)Math.Max(0, value), v => Events.MinTipAmount = v); }
    public bool SuppressDuplicateFollows { get => Events.SuppressDuplicateFollows; set => Set(Events.SuppressDuplicateFollows, value, v => Events.SuppressDuplicateFollows = v); }

    public string Variables { get; } = SpeechDefaults.Variables(SpeechDefaults.EventVariables);

    /// <summary>Forgets the announced follows of this session so they are spoken again.</summary>
    [RelayCommand]
    private void ResetSession() => _ctx.Speech.ResetSession();

    /// <summary>Re-reads the events switch after it changed elsewhere (chat command, sub-action).</summary>
    public void Refresh() => OnPropertyChanged(nameof(Enabled));

    private void Set<T>(T current, T value, Action<T> set, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        set(value);
        _ctx.Save();
        OnPropertyChanged(name);
    }
}
