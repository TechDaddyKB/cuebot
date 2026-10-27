using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels;

public sealed record EngineStatus(string Id, string Name, bool IsAvailable, string Detail);

public sealed partial class VoiceAliasViewModel : ObservableObject
{
    private readonly SpeechService _speech;

    public VoiceAliasViewModel(VoiceAlias alias, SpeechService speech)
    {
        Alias = alias;
        _speech = speech;
        _ = LoadVoicesAsync();
    }

    public VoiceAlias Alias { get; }
    public IReadOnlyList<string> EngineIds => _speech.Engines.Select(e => e.Id).ToArray();
    public ObservableCollection<VoiceInfo> Voices { get; } = [];

    public string Name
    {
        get => Alias.Name;
        set { Alias.Name = value; Save(); }
    }

    public string? EngineId
    {
        get => Alias.EngineId;
        set
        {
            if (value is null || value == Alias.EngineId) return;
            Alias.EngineId = value;
            Alias.VoiceId = "";
            Save();
            _ = LoadVoicesAsync();
        }
    }

    public VoiceInfo? Voice
    {
        get => Voices.FirstOrDefault(v => v.Id == Alias.VoiceId);
        set
        {
            if (value is null) return;
            Alias.VoiceId = value.Id;
            Save();
        }
    }

    public double Rate
    {
        get => Alias.Rate;
        set { Alias.Rate = (int)Math.Round(value); Save(); }
    }

    public double Volume
    {
        get => Alias.Volume;
        set { Alias.Volume = (int)Math.Round(value); Save(); }
    }

    [ObservableProperty] private string _voiceError = "";

    private void Save()
    {
        _speech.SaveSettings();
        OnPropertyChanged(string.Empty);
    }

    private async Task LoadVoicesAsync()
    {
        VoiceError = "";
        Voices.Clear();
        var engine = _speech.Engines.FirstOrDefault(e => e.Id == Alias.EngineId);
        if (engine is null) return;
        try
        {
            foreach (var voice in await engine.GetVoicesAsync(CancellationToken.None)) Voices.Add(voice);
        }
        catch (Exception ex)
        {
            VoiceError = ex.Message;
        }
        OnPropertyChanged(nameof(Voice));
    }
}

/// <summary>Text-to-speech: engines, voice aliases, speak-chat options and the playback queue.</summary>
public sealed partial class SpeechViewModel : PageViewModel
{
    private readonly SpeechService _speech;

    public SpeechViewModel(SpeechService speech) : base("Speech")
    {
        _speech = speech;
        Aliases = new ObservableCollection<VoiceAliasViewModel>(speech.Settings.Aliases.Select(a => new VoiceAliasViewModel(a, speech)));
        speech.StateChanged += () => OnUi(() =>
        {
            OnPropertyChanged(nameof(QueueLength));
            OnPropertyChanged(nameof(CurrentText));
            OnPropertyChanged(nameof(Paused));
        });
        SelectedAlias = Aliases.FirstOrDefault();
    }

    public IReadOnlyList<EngineStatus> Engines => _speech.Engines
        .Select(e => new EngineStatus(e.Id, e.DisplayName, e.IsAvailable, e.IsAvailable ? "Available" : e.UnavailableReason ?? "Unavailable"))
        .ToArray();

    public ObservableCollection<VoiceAliasViewModel> Aliases { get; }
    public IReadOnlyList<string> AliasNames => Aliases.Select(a => a.Name).ToArray();

    [ObservableProperty] private VoiceAliasViewModel? _selectedAlias;
    [ObservableProperty] private string _testText = "Hello from Cuebot!";
    [ObservableProperty] private string _error = "";

    public int QueueLength => _speech.QueueLength;
    public string CurrentText => _speech.CurrentText ?? "—";

    public bool Paused
    {
        get => _speech.Paused;
        set { _speech.Paused = value; OnPropertyChanged(); }
    }

    public string ElevenLabsApiKey
    {
        get => _speech.Settings.ElevenLabsApiKey;
        set { _speech.Settings.ElevenLabsApiKey = value.Trim(); Save(); OnPropertyChanged(nameof(Engines)); }
    }

    public double MasterVolume
    {
        get => _speech.Settings.MasterVolume;
        set { _speech.Settings.MasterVolume = (int)Math.Round(value); Save(); }
    }

    // Speak chat
    private SpeakChatSettings Chat => _speech.Settings.SpeakChat;
    public bool SpeakChatEnabled { get => Chat.Enabled; set { Chat.Enabled = value; Save(); } }
    public string? SpeakChatAlias { get => Chat.Alias; set { Chat.Alias = value ?? ""; Save(); } }
    public string SpeakChatTemplate { get => Chat.Template; set { Chat.Template = value; Save(); } }
    public bool IgnoreCommands { get => Chat.IgnoreCommands; set { Chat.IgnoreCommands = value; Save(); } }
    public bool SkipLinks { get => Chat.SkipLinks; set { Chat.SkipLinks = value; Save(); } }
    public decimal MaxLength { get => Chat.MaxLength; set { Chat.MaxLength = (int)value; Save(); } }

    public string IgnoredUsers
    {
        get => string.Join(", ", Chat.IgnoredUsers);
        set
        {
            Chat.IgnoredUsers = value.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Save();
        }
    }

    public string Replacements
    {
        get => string.Join(Environment.NewLine, _speech.Settings.Replacements.Select(r => $"{(r.IsRegex ? "re:" : "")}{r.Find} => {r.Replace}"));
        set
        {
            _speech.Settings.Replacements = ParseReplacements(value).ToList();
            Save();
        }
    }

    /// <summary>One rule per line: "find => replace"; prefix "re:" for a regular expression.</summary>
    internal static IEnumerable<WordReplacement> ParseReplacements(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts[0].Length == 0) continue;
            var isRegex = parts[0].StartsWith("re:", StringComparison.OrdinalIgnoreCase);
            yield return new WordReplacement
            {
                Find = isRegex ? parts[0][3..] : parts[0],
                Replace = parts.Length > 1 ? parts[1] : "",
                IsRegex = isRegex,
            };
        }
    }

    private void Save() => _speech.SaveSettings();

    [RelayCommand]
    private void AddAlias()
    {
        var engine = _speech.Engines.FirstOrDefault(e => e.IsAvailable) ?? _speech.Engines.FirstOrDefault();
        var alias = new VoiceAlias { Name = $"Voice {Aliases.Count + 1}", EngineId = engine?.Id ?? "" };
        _speech.Settings.Aliases.Add(alias);
        Save();
        var vm = new VoiceAliasViewModel(alias, _speech);
        Aliases.Add(vm);
        SelectedAlias = vm;
        OnPropertyChanged(nameof(AliasNames));
    }

    [RelayCommand]
    private void RemoveAlias()
    {
        if (SelectedAlias is not { } alias) return;
        _speech.Settings.Aliases.Remove(alias.Alias);
        Save();
        Aliases.Remove(alias);
        SelectedAlias = Aliases.FirstOrDefault();
        OnPropertyChanged(nameof(AliasNames));
    }

    [RelayCommand]
    private async Task TestAlias()
    {
        if (SelectedAlias is not { } alias) return;
        Error = "";
        try { await _speech.TestVoiceAsync(alias.Alias, TestText); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task Speak()
    {
        Error = "";
        try { await _speech.SpeakAsync(TestText, SelectedAlias?.Name); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void Skip() => _speech.SkipCurrent();

    [RelayCommand]
    private void ClearQueue() => _speech.ClearQueue();
}
