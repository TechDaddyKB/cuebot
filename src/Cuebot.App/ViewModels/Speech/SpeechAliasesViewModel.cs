using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Storage;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>One voice inside an alias: engine, voice (loaded from the engine) and weight.</summary>
public sealed partial class AliasVoiceViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;
    private readonly VoiceAliasEditorViewModel _owner;
    private int _loadVersion;

    public AliasVoiceViewModel(AliasVoice voice, VoiceAliasEditorViewModel owner, SpeechPageContext ctx)
    {
        Voice = voice;
        _owner = owner;
        _ctx = ctx;
        EngineChoices = ctx.EngineChoices();
        _ = LoadVoicesAsync();
    }

    public AliasVoice Voice { get; }

    public IReadOnlyList<Choice> EngineChoices { get; private set; }

    public Choice? Engine
    {
        get => SpeechPageContext.Find(EngineChoices, Voice.EngineId);
        set
        {
            if (value is null || string.Equals(value.Value, Voice.EngineId, StringComparison.OrdinalIgnoreCase)) return;
            Voice.EngineId = value.Value;
            Voice.VoiceId = "";
            Voice.Style = "";
            _owner.OnVoiceChanged(this);
            OnPropertyChanged();
            _ = LoadVoicesAsync();
        }
    }

    public IReadOnlyList<VoiceOption> VoiceOptions { get; private set; } = [];

    public VoiceOption? SelectedVoice
    {
        get => VoiceOptions.FirstOrDefault(v => v.Id == Voice.VoiceId);
        set
        {
            if (value is null || value.Id == Voice.VoiceId) return;
            Voice.VoiceId = value.Id;
            if (Voice.Style.Length > 0 && !value.Styles.Contains(Voice.Style, StringComparer.OrdinalIgnoreCase)) Voice.Style = "";
            _owner.OnVoiceChanged(this);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>Styles of the selected voice (Azure); empty for most engines.</summary>
    public IReadOnlyList<string> Styles => SelectedVoice?.Styles ?? [];

    public decimal Weight
    {
        get => Math.Max(1, Voice.Weight);
        set
        {
            var weight = SpeechText.Clamp(value, 1, 100);
            if (weight == Voice.Weight) return;
            Voice.Weight = weight;
            _owner.OnVoiceChanged(this);
            OnPropertyChanged();
        }
    }

    public bool ShowWeight => _owner.IsMultiVoice && _owner.Selection == VoiceSelection.Random;

    public bool CanRemove => _owner.IsMultiVoice;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadError = "";

    /// <summary>Explains an empty picker: a saved voice the engine does not list, or an unavailable engine.</summary>
    public string Status
    {
        get
        {
            if (IsLoading) return "Loading voices…";
            if (LoadError.Length > 0) return LoadError;
            var engine = _ctx.Voices.Engine(Voice.EngineId);
            if (engine is null) return Voice.EngineId.Length > 0 ? $"Unknown engine '{Voice.EngineId}'." : "Pick an engine.";
            if (!engine.IsAvailable) return engine.UnavailableReason ?? "Engine unavailable.";
            if (Voice.VoiceId.Length > 0 && SelectedVoice is null) return $"Saved voice '{Voice.VoiceId}' is not listed by the engine.";
            if (Voice.VoiceId.Length == 0) return "Pick a voice.";
            return "";
        }
    }

    public bool HasStatus => Status.Length > 0;

    public override string ToString() => $"{Voice.EngineId}/{Voice.VoiceId}";

    public void RefreshChoices()
    {
        EngineChoices = _ctx.EngineChoices();
        OnPropertyChanged(nameof(EngineChoices));
        OnPropertyChanged(nameof(Engine));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
    }

    public void RefreshLayout()
    {
        OnPropertyChanged(nameof(ShowWeight));
        OnPropertyChanged(nameof(CanRemove));
    }

    internal async Task LoadVoicesAsync()
    {
        var version = ++_loadVersion;
        LoadError = "";
        IsLoading = true;
        RaiseStatus();
        IReadOnlyList<VoiceOption> voices = [];
        try
        {
            voices = await _ctx.Voices.GetAsync(Voice.EngineId);
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) LoadError = $"Could not list voices: {ex.Message}";
        }
        if (version != _loadVersion) return;
        IsLoading = false;
        VoiceOptions = voices;
        OnPropertyChanged(nameof(VoiceOptions));
        OnPropertyChanged(nameof(SelectedVoice));
        OnPropertyChanged(nameof(Styles));
        RaiseStatus();
        _owner.OnStylesChanged();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
    }
}

/// <summary>Editor of one alias: name, voices, selection mode, device, volume/rate/pitch/style.</summary>
public sealed partial class VoiceAliasEditorViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public VoiceAliasEditorViewModel(VoiceAlias alias, SpeechPageContext ctx)
    {
        Alias = alias;
        _ctx = ctx;
        Devices = ctx.OutputDeviceChoices("Global output device");
        // The single-voice form (EngineId/VoiceId) shows as one row; it moves into Voices on the first voice edit.
        var voices = alias.Voices.Count > 0
            ? alias.Voices
            : [new AliasVoice { EngineId = alias.EngineId, VoiceId = alias.VoiceId }];
        Voices = new ObservableCollection<AliasVoiceViewModel>(voices.Select(v => new AliasVoiceViewModel(v, this, ctx)));
    }

    public VoiceAlias Alias { get; }

    public ObservableCollection<AliasVoiceViewModel> Voices { get; }

    public bool IsMultiVoice => Voices.Count > 1;

    public string Name
    {
        get => Alias.Name;
        set
        {
            var name = (value ?? "").Trim();
            if (name == Alias.Name) return;
            var old = Alias.Name;
            Alias.Name = name;
            _ctx.RenameAliasReferences(old, name);
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            _ctx.NotifyChoicesChanged();
        }
    }

    public string Summary =>
        Voices.Count == 1
            ? Voices[0].Voice.EngineId is { Length: > 0 } engine ? $"{engine} · {VoiceName(Voices[0])}" : "no voice"
            : $"{Voices.Count} voices · {(Alias.Selection == VoiceSelection.Random ? "random" : "in turn")}";

    public IReadOnlyList<VoiceSelection> Selections { get; } = Enum.GetValues<VoiceSelection>();

    public VoiceSelection Selection
    {
        get => Alias.Selection;
        set
        {
            if (value == Alias.Selection) return;
            Alias.Selection = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            foreach (var voice in Voices) voice.RefreshLayout();
        }
    }

    public IReadOnlyList<Choice> Devices { get; private set; }

    public Choice? Device
    {
        get => SpeechPageContext.Find(Devices, Alias.DeviceId);
        set
        {
            if (value is null || value.Value == Alias.DeviceId) return;
            Alias.DeviceId = value.Value;
            Save();
            OnPropertyChanged();
        }
    }

    public double Volume { get => Alias.Volume; set => SetInt(Alias.Volume, SpeechText.Clamp(value, 0, 100), v => Alias.Volume = v); }
    public double Rate { get => Alias.Rate; set => SetInt(Alias.Rate, SpeechText.Clamp(value, -10, 10), v => Alias.Rate = v); }
    public double Pitch { get => Alias.Pitch; set => SetInt(Alias.Pitch, SpeechText.Clamp(value, -10, 10), v => Alias.Pitch = v); }

    /// <summary>"None" plus every style the alias' voices support.</summary>
    public IReadOnlyList<Choice> Styles =>
    [
        new Choice("", "No style"),
        .. Voices.SelectMany(v => v.Styles).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(s => new Choice(s, s)),
    ];

    public bool HasStyles => Styles.Count > 1 || Alias.Style.Length > 0;

    public Choice? Style
    {
        get => SpeechPageContext.Find(Styles, Alias.Style) ?? (Alias.Style.Length > 0 ? null : Styles[0]);
        set
        {
            if (value is null || value.Value == Alias.Style) return;
            Alias.Style = value.Value;
            Save();
            OnPropertyChanged();
        }
    }

    public override string ToString() => Alias.Name;

    [RelayCommand]
    private void AddVoice()
    {
        EnsureVoiceList();
        var last = Voices.LastOrDefault()?.Voice;
        var voice = new AliasVoice { EngineId = last?.EngineId ?? FirstEngine() };
        Alias.Voices.Add(voice);
        Voices.Add(new AliasVoiceViewModel(voice, this, _ctx));
        AfterVoicesChanged();
    }

    [RelayCommand]
    private void RemoveVoice(AliasVoiceViewModel? row)
    {
        if (row is null || Voices.Count <= 1) return;
        EnsureVoiceList();
        Alias.Voices.Remove(row.Voice);
        Voices.Remove(row);
        AfterVoicesChanged();
    }

    internal string FirstEngine() =>
        (_ctx.Speech.Engines.FirstOrDefault(e => e.IsAvailable) ?? _ctx.Speech.Engines.FirstOrDefault())?.Id ?? "";

    internal void OnVoiceChanged(AliasVoiceViewModel row)
    {
        EnsureVoiceList();
        Save();
        OnPropertyChanged(nameof(Summary));
        OnStylesChanged();
    }

    internal void OnStylesChanged()
    {
        OnPropertyChanged(nameof(Styles));
        OnPropertyChanged(nameof(HasStyles));
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(Summary));
    }

    public void RefreshChoices()
    {
        foreach (var voice in Voices) voice.RefreshChoices();
    }

    public void RefreshDevices(IReadOnlyList<Choice> devices)
    {
        Devices = devices;
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(Device));
    }

    /// <summary>Reloads every voice list (after engine credentials changed).</summary>
    public void ReloadVoices(string engineId)
    {
        foreach (var voice in Voices.Where(v => string.Equals(v.Voice.EngineId, engineId, StringComparison.OrdinalIgnoreCase)))
            _ = voice.LoadVoicesAsync();
    }

    /// <summary>Moves the single-voice form into <see cref="VoiceAlias.Voices"/> and mirrors the first voice back.</summary>
    private void EnsureVoiceList()
    {
        if (Alias.Voices.Count == 0) Alias.Voices.AddRange(Voices.Select(v => v.Voice));
        var first = Alias.Voices[0];
        Alias.EngineId = first.EngineId; // older builds read the single-voice fields
        Alias.VoiceId = first.VoiceId;
    }

    private void AfterVoicesChanged()
    {
        EnsureVoiceList();
        Save();
        OnPropertyChanged(nameof(IsMultiVoice));
        foreach (var voice in Voices) voice.RefreshLayout();
        OnStylesChanged();
    }

    private static string VoiceName(AliasVoiceViewModel row) =>
        row.SelectedVoice?.Name ?? (row.Voice.VoiceId.Length > 0 ? row.Voice.VoiceId : "no voice");

    private void SetInt(int current, int value, Action<int> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value) return;
        set(value);
        Save();
        OnPropertyChanged(name);
    }

    private void Save() => _ctx.Save();
}

/// <summary>Voice aliases tab: the alias list, editor, default and highlight aliases and a voice test.</summary>
public sealed partial class SpeechAliasesViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public SpeechAliasesViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        Aliases = new ObservableCollection<VoiceAliasEditorViewModel>(ctx.Settings.Aliases.Select(a => new VoiceAliasEditorViewModel(a, ctx)));
        _selected = Aliases.FirstOrDefault();
        AliasChoices = ctx.AliasChoices("First alias");
        HighlightChoices = ctx.AliasChoices("Same as chat alias");
        ctx.ChoicesChanged += RefreshChoices;
        ctx.Voices.Invalidated += engineId =>
        {
            foreach (var alias in Aliases) alias.ReloadVoices(engineId);
        };
    }

    public ObservableCollection<VoiceAliasEditorViewModel> Aliases { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(DuplicateCommand), nameof(TestCommand))]
    private VoiceAliasEditorViewModel? _selected;

    public bool HasSelection => Selected is not null;

    public IReadOnlyList<Choice> AliasChoices { get; private set; }
    public IReadOnlyList<Choice> HighlightChoices { get; private set; }

    public Choice? DefaultAlias
    {
        get => SpeechPageContext.Find(AliasChoices, _ctx.Settings.DefaultAlias);
        set
        {
            if (value is null || value.Value == _ctx.Settings.DefaultAlias) return;
            _ctx.Settings.DefaultAlias = value.Value;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public Choice? HighlightAlias
    {
        get => SpeechPageContext.Find(HighlightChoices, _ctx.Settings.SpeakChat.HighlightAlias);
        set
        {
            if (value is null || value.Value == _ctx.Settings.SpeakChat.HighlightAlias) return;
            _ctx.Settings.SpeakChat.HighlightAlias = value.Value;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _testText = "Hello! This is how I sound.";
    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private void Add()
    {
        var alias = new VoiceAlias { Name = SpeechText.UniqueName("Voice", _ctx.Settings.Aliases.Select(a => a.Name)) };
        var engine = (_ctx.Speech.Engines.FirstOrDefault(e => e.IsAvailable) ?? _ctx.Speech.Engines.FirstOrDefault())?.Id ?? "";
        alias.Voices.Add(new AliasVoice { EngineId = engine });
        alias.EngineId = engine;
        AddAlias(alias);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (Selected is null) return;
        var copy = Json.Clone(Selected.Alias);
        copy.Name = SpeechText.UniqueName($"{Selected.Alias.Name} copy", _ctx.Settings.Aliases.Select(a => a.Name));
        AddAlias(copy);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } alias) return;
        var index = Aliases.IndexOf(alias);
        _ctx.Settings.Aliases.Remove(alias.Alias);
        _ctx.Save();
        Aliases.Remove(alias);
        Selected = Aliases.Count == 0 ? null : Aliases[Math.Clamp(index, 0, Aliases.Count - 1)];
        _ctx.NotifyChoicesChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Test()
    {
        if (Selected is not { } alias) return;
        Error = "";
        if (!_ctx.Speech.Enabled)
        {
            Error = "Speech is off. Turn it on in Overview to test voices.";
            return;
        }
        try
        {
            await _ctx.Speech.TestVoiceAsync(alias.Alias, TestText);
        }
        catch (OperationCanceledException)
        {
            // skipped or cleared
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void AddAlias(VoiceAlias alias)
    {
        _ctx.Settings.Aliases.Add(alias);
        _ctx.Save();
        var vm = new VoiceAliasEditorViewModel(alias, _ctx);
        Aliases.Add(vm);
        Selected = vm;
        _ctx.NotifyChoicesChanged();
    }

    private void RefreshChoices()
    {
        AliasChoices = _ctx.AliasChoices("First alias");
        HighlightChoices = _ctx.AliasChoices("Same as chat alias");
        OnPropertyChanged(nameof(AliasChoices));
        OnPropertyChanged(nameof(HighlightChoices));
        OnPropertyChanged(nameof(DefaultAlias));
        OnPropertyChanged(nameof(HighlightAlias));
        foreach (var alias in Aliases) alias.RefreshChoices();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        var devices = _ctx.OutputDeviceChoices("Global output device");
        foreach (var alias in Aliases) alias.RefreshDevices(devices);
    }
}
