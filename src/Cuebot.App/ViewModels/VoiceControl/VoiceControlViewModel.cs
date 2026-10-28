using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.VoiceControl;
using Cuebot.VoiceControl.Engines;

namespace Cuebot.App.ViewModels.VoiceControl;

/// <summary>Voice Control page: commands, settings (engine, model download, microphone) and the live dictation log.</summary>
public sealed partial class VoiceControlViewModel : PageViewModel
{
    private const int LogRows = 200;
    private readonly VoiceControlService _voice;
    private readonly DispatcherTimer _meter;

    public VoiceControlViewModel(VoiceControlService voice) : base("Voice Control")
    {
        _voice = voice;
        Commands = new ObservableCollection<VoiceCommandRowViewModel>(voice.Commands.Select(c => new VoiceCommandRowViewModel(this, c)));
        Engines = voice.Engines.Select(e => new VoiceEngineOption(e)).ToArray();
        Models = WhisperModelCatalog.All.Select(m => new WhisperModelRowViewModel(this, m)).ToArray();
        Log = new ObservableCollection<DictationRowViewModel>(voice.DictationLog.Reverse().Select(d => new DictationRowViewModel(d)));
        RefreshDevices();

        voice.StatusChanged += () => OnUi(RefreshStatus);
        voice.CommandsChanged += () => OnUi(SyncCommands);
        voice.Dictated += entry => OnUi(() =>
        {
            Log.Insert(0, new DictationRowViewModel(entry));
            while (Log.Count > LogRows) Log.RemoveAt(Log.Count - 1);
        });
        voice.Hearing += text => OnUi(() => Hearing = text);
        voice.Models.Changed += _ => OnUi(() =>
        {
            foreach (var model in Models) model.Refresh();
            OnPropertyChanged(nameof(ModelReady));
            OnPropertyChanged(nameof(ModelHint));
        });

        _meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _meter.Tick += (_, _) => LevelDb = _voice.IsListening ? _voice.LevelDb : -96;
        _meter.Start();
    }

    internal VoiceControlService Service => _voice;
    private VoiceControlSettings S => _voice.Settings;

    // ---- Status -------------------------------------------------------------------------------------------------

    public string State => _voice.State.ToString();
    public string StatusMessage => _voice.StatusMessage;
    public bool IsListening => _voice.IsListening;
    public bool HasMicrophone => _voice.HasMicrophone;

    [ObservableProperty] private double _levelDb = -96;
    [ObservableProperty] private string _hearing = "";
    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private Task Start() => RunAsync(() => _voice.StartListeningAsync());

    [RelayCommand]
    private Task Stop() => RunAsync(() => _voice.StopListeningAsync());

    internal async Task RunAsync(Func<Task> action)
    {
        Error = "";
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsListening));
    }

    // ---- Commands -----------------------------------------------------------------------------------------------

    public ObservableCollection<VoiceCommandRowViewModel> Commands { get; }

    public bool HasNoCommands => Commands.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private VoiceCommandRowViewModel? _selected;

    public bool HasSelection => Selected is not null;

    public static IReadOnlyList<string> Locations { get; } = Enum.GetNames<CommandLocation>();

    [RelayCommand]
    private void AddCommand()
    {
        var command = _voice.AddCommand("New command", "");
        Selected = Commands.FirstOrDefault(c => c.Command == command);
    }

    [RelayCommand]
    private void DuplicateCommand()
    {
        if (Selected?.Command is not { } source) return;
        var copy = _voice.AddCommand(source.Name, source.Phrase, source.Location);
        copy.StopAfter = source.StopAfter;
        copy.OverrideGlobal = source.OverrideGlobal;
        copy.ConfidenceThreshold = source.ConfidenceThreshold;
        copy.Enabled = source.Enabled;
        _voice.CommandEdited();
        Selected = Commands.FirstOrDefault(c => c.Command == copy);
    }

    [RelayCommand]
    private void RemoveCommand()
    {
        if (Selected?.Command is not { } command) return;
        var index = Commands.IndexOf(Selected);
        _voice.RemoveCommand(command);
        Selected = Commands.Count == 0 ? null : Commands[Math.Clamp(index, 0, Commands.Count - 1)];
    }

    [RelayCommand] private void MoveUp() => Move(-1);
    [RelayCommand] private void MoveDown() => Move(1);
    [RelayCommand] private void MoveToTop() => Move(int.MinValue / 2);
    [RelayCommand] private void MoveToBottom() => Move(int.MaxValue / 2);

    private void Move(int by)
    {
        if (Selected is not { } row) return;
        var index = Commands.IndexOf(row);
        _voice.MoveCommand(row.Command, (int)Math.Clamp((long)index + by, 0, Commands.Count - 1));
        Selected = Commands.FirstOrDefault(c => c.Command == row.Command);
    }

    private void SyncCommands()
    {
        var selected = Selected?.Command;
        var commands = _voice.Commands;
        if (!commands.SequenceEqual(Commands.Select(c => c.Command)))
        {
            Commands.Clear();
            foreach (var command in commands) Commands.Add(new VoiceCommandRowViewModel(this, command));
        }
        else
        {
            foreach (var row in Commands) row.Refresh();
        }
        Selected = Commands.FirstOrDefault(c => c.Command == selected);
        OnPropertyChanged(nameof(HasNoCommands));
    }

    // ---- Settings -----------------------------------------------------------------------------------------------

    public bool AutoStart
    {
        get => S.AutoStart;
        set { S.AutoStart = value; _voice.Save(); OnPropertyChanged(); }
    }

    public bool LogDictation
    {
        get => S.LogDictation;
        set { S.LogDictation = value; _voice.Save(); OnPropertyChanged(); }
    }

    public decimal? ConfidenceThreshold
    {
        get => S.ConfidenceThreshold;
        set { S.ConfidenceThreshold = (int)Math.Clamp(value ?? 50, 0, 100); _voice.Save(); OnPropertyChanged(); }
    }

    public decimal? MatchTolerance
    {
        get => S.MatchTolerance;
        set { S.MatchTolerance = (int)Math.Clamp(value ?? 20, 0, 50); _voice.Save(); OnPropertyChanged(); }
    }

    public IReadOnlyList<VoiceEngineOption> Engines { get; }

    public VoiceEngineOption? Engine
    {
        get => Engines.FirstOrDefault(e => e.Engine == _voice.CurrentEngine);
        set
        {
            if (value is null || value.Engine == _voice.CurrentEngine) return;
            S.Engine = value.Engine.Id;
            _voice.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWhisper));
            OnPropertyChanged(nameof(Locales));
            OnPropertyChanged(nameof(EngineHint));
            ApplyAfterChange();
        }
    }

    public bool IsWhisper => _voice.CurrentEngine.NeedsModel;

    public string EngineHint => _voice.CurrentEngine.UnavailableReason ?? (IsWhisper
        ? "Runs locally on your CPU (or a GPU runtime if installed); nothing is sent anywhere. Needs a model download once."
        : "Uses the speech recognition built into Windows. Exact commands get grammar matching (high confidence).");

    /// <summary>"" = the system language, plus what the engine offers.</summary>
    public IReadOnlyList<string> Locales => ["", .. _voice.CurrentEngine.GetLocales()];

    public string Locale
    {
        get => S.Locale;
        set
        {
            var locale = (value ?? "").Trim();
            if (locale == S.Locale) return;
            S.Locale = locale;
            _voice.Save();
            OnPropertyChanged();
            ApplyAfterChange();
        }
    }

    public string LocaleHint => $"Empty = the system language ({CultureInfo.CurrentUICulture.Name}). Whisper also accepts \"auto\" to detect the language.";

    public ObservableCollection<string> InputDevices { get; } = [];

    public string InputDevice
    {
        get => string.IsNullOrWhiteSpace(S.InputDevice) ? DefaultDevice : S.InputDevice;
        set
        {
            var device = value is null || value == DefaultDevice ? "" : value;
            if (device == S.InputDevice) return;
            _voice.SetInputDevice(device);
            OnPropertyChanged();
            RefreshStatus();
        }
    }

    private const string DefaultDevice = "Default";

    [RelayCommand]
    private void RefreshDevices()
    {
        var current = InputDevice;
        InputDevices.Clear();
        InputDevices.Add(DefaultDevice);
        foreach (var device in _voice.InputDevices) InputDevices.Add(device.Name);
        if (!InputDevices.Contains(current)) InputDevices.Add(current);
        OnPropertyChanged(nameof(InputDevice));
    }

    // ---- Whisper ------------------------------------------------------------------------------------------------

    public IReadOnlyList<WhisperModelRowViewModel> Models { get; }

    public WhisperModelRowViewModel? Model
    {
        get => Models.FirstOrDefault(m => string.Equals(m.Model.Id, S.Whisper.Model, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || value.Model.Id == S.Whisper.Model) return;
            S.Whisper.Model = value.Model.Id;
            _voice.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelReady));
            OnPropertyChanged(nameof(ModelHint));
            ApplyAfterChange();
        }
    }

    public bool ModelReady => Model is { } m && _voice.Models.IsDownloaded(m.Model);

    public string ModelHint => Model is not { } m ? "Pick a model."
        : ModelReady ? $"{m.Model.Notes}. Downloaded ({m.Model.SizeText}) in {_voice.Models.Directory}."
        : $"{m.Model.Notes}. {m.Model.SizeText} to download before Whisper can listen.";

    public bool UseGpu
    {
        get => S.Whisper.UseGpu;
        set { S.Whisper.UseGpu = value; _voice.Save(); OnPropertyChanged(); ApplyAfterChange(); }
    }

    public bool InterimResults
    {
        get => S.Whisper.InterimResults;
        set { S.Whisper.InterimResults = value; _voice.Save(); OnPropertyChanged(); _voice.CommandEdited(); }
    }

    public bool PromptWithCommands
    {
        get => S.Whisper.PromptWithCommands;
        set { S.Whisper.PromptWithCommands = value; _voice.Save(); OnPropertyChanged(); }
    }

    /// <summary>Engine, model or language changes need a restart of the engine; done automatically while listening.</summary>
    private void ApplyAfterChange()
    {
        if (_voice.IsListening) _ = RunAsync(() => _voice.RestartAsync());
    }

    // ---- Log ----------------------------------------------------------------------------------------------------

    public ObservableCollection<DictationRowViewModel> Log { get; }

    [RelayCommand]
    private void ClearLog() => Log.Clear();
}

/// <summary>One command, edited in place; every change is saved and reaches the running engine.</summary>
public sealed class VoiceCommandRowViewModel(VoiceControlViewModel owner, VoiceCommand command) : ObservableObject
{
    internal VoiceCommand Command { get; } = command;

    public string Name
    {
        get => Command.Name;
        set => Edit(() => Command.Name = value);
    }

    public bool Enabled
    {
        get => Command.Enabled;
        set => Edit(() => Command.Enabled = value);
    }

    public string Phrase
    {
        get => Command.Phrase;
        set => Edit(() => Command.Phrase = value);
    }

    public string Location
    {
        get => Command.Location.ToString();
        set => Edit(() => Command.Location = Enum.TryParse<CommandLocation>(value, out var l) ? l : CommandLocation.Exact);
    }

    public bool StopAfter
    {
        get => Command.StopAfter;
        set => Edit(() => Command.StopAfter = value);
    }

    public bool OverrideGlobal
    {
        get => Command.OverrideGlobal;
        set => Edit(() => Command.OverrideGlobal = value);
    }

    public decimal? ConfidenceThreshold
    {
        get => Command.ConfidenceThreshold;
        set => Edit(() => Command.ConfidenceThreshold = (int)Math.Clamp(value ?? 0, 0, 100));
    }

    public string Summary => $"{Command.Location}{(Command.StopAfter ? " · stop after" : "")}{(Command.OverrideGlobal ? $" · ≥ {Command.ConfidenceThreshold}%" : "")}";

    private void Edit(Action change)
    {
        change();
        owner.Service.CommandEdited();
    }

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class VoiceEngineOption(ISpeechRecognizerEngine engine)
{
    internal ISpeechRecognizerEngine Engine { get; } = engine;
    public string Name => Engine.IsAvailable ? Engine.DisplayName : $"{Engine.DisplayName} (unavailable)";
    public override string ToString() => Name;
}

/// <summary>One Whisper model in the picker: size, notes and download state.</summary>
public sealed partial class WhisperModelRowViewModel(VoiceControlViewModel owner, WhisperModel model) : ObservableObject
{
    internal WhisperModel Model { get; } = model;

    public string Name => $"{Model.DisplayName} · {Model.SizeText}{(IsDownloaded ? " · downloaded" : "")}";
    public string Notes => Model.Notes;
    public bool IsDownloaded => owner.Service.Models.IsDownloaded(Model);
    public bool IsDownloading => owner.Service.Models.IsDownloading(Model);
    public bool CanDownload => !IsDownloaded && !IsDownloading;
    public double Progress => (owner.Service.Models.Progress(Model)?.Fraction ?? (IsDownloaded ? 1 : 0)) * 100;

    public string ProgressText => owner.Service.Models.Progress(Model) is { } p
        ? $"{p.BytesReceived / 1_000_000.0:0.0} / {p.TotalBytes / 1_000_000.0:0.0} MB"
        : "";

    public override string ToString() => Name;

    [RelayCommand]
    internal Task DownloadAsync() => owner.RunAsync(() => owner.Service.Models.DownloadAsync(Model));

    [RelayCommand]
    private void Cancel() => owner.Service.Models.CancelDownload(Model);

    [RelayCommand]
    private void Delete()
    {
        try { owner.Service.Models.Delete(Model); }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class DictationRowViewModel(DictationEntry entry)
{
    public string Time => entry.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string Text => entry.Text;
    public string Confidence => $"{entry.ConfidencePercent}%";
    public string Outcome => entry.Ignored ? "below threshold" : entry.Commands.Count > 0 ? "→ " + string.Join(", ", entry.Commands) : "";
}
