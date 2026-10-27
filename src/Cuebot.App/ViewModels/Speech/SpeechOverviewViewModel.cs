using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

public sealed record QueueItemViewModel(long Id, string Text, string Meta, string User)
{
    public bool HasUser => User.Length > 0;

    public static QueueItemViewModel From(QueuedSpeech q) =>
        new(q.Id, q.Text, Join(q.Alias.Length > 0 ? q.Alias : "default voice", q.User, q.Source, q.Priority != 0 ? $"priority {q.Priority}" : ""), q.User);

    internal static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

public sealed record HistoryItemViewModel(string Time, string Text, string Meta, bool Skipped)
{
    public static HistoryItemViewModel From(SpeechHistoryEntry e) =>
        new(e.Time.ToLocalTime().ToString("HH:mm:ss"), e.Text,
            QueueItemViewModel.Join(e.Alias, e.EngineId.Length > 0 ? $"{e.EngineId}/{e.VoiceId}" : "", e.User, e.Source, e.Skipped ? "skipped" : ""),
            e.Skipped);
}

/// <summary>Overview tab: switches, the queue, history, a test box, master volume and the global output device.</summary>
public sealed partial class SpeechOverviewViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public SpeechOverviewViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        OutputDevices = ctx.OutputDeviceChoices("System default");
        AliasChoices = ctx.AliasChoices("Default voice");
        ctx.ChoicesChanged += () =>
        {
            AliasChoices = ctx.AliasChoices("Default voice");
            OnPropertyChanged(nameof(AliasChoices));
            OnPropertyChanged(nameof(TestAlias));
        };
        Refresh();
    }

    private SpeechService Speech => _ctx.Speech;

    public bool Enabled
    {
        get => Speech.Enabled;
        set { Speech.Enabled = value; OnPropertyChanged(); }
    }

    public bool EventsEnabled
    {
        get => Speech.EventsEnabled;
        set { Speech.EventsEnabled = value; OnPropertyChanged(); }
    }

    public bool Paused
    {
        get => Speech.Paused;
        set { Speech.Paused = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public string CurrentText => Speech.CurrentText ?? "Nothing is playing.";

    public bool IsSpeaking => Speech.CurrentText is not null;

    public string StatusText =>
        !Speech.Enabled ? "Speech is off"
        : Speech.Paused ? "Paused"
        : Speech.VoiceGateHolding ? "Held by VoiceGate"
        : Speech.CurrentText is not null ? "Speaking"
        : "Idle";

    public IReadOnlyList<QueueItemViewModel> Queue { get; private set; } = [];

    public string QueueHeader => Queue.Count == 0 ? "Queue — empty" : $"Queue — {Queue.Count} waiting";

    public IReadOnlyList<HistoryItemViewModel> History { get; private set; } = [];

    public bool HasQueue => Queue.Count > 0;

    public bool HasHistory => History.Count > 0;

    public double MasterVolume
    {
        get => _ctx.Settings.MasterVolume;
        set
        {
            var v = SpeechText.Clamp(value, 0, 100);
            if (v == _ctx.Settings.MasterVolume) return;
            _ctx.Settings.MasterVolume = v;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice> OutputDevices { get; private set; }

    public Choice? OutputDevice
    {
        get => SpeechPageContext.Find(OutputDevices, _ctx.Settings.OutputDeviceId);
        set
        {
            if (value is null || value.Value == _ctx.Settings.OutputDeviceId) return;
            _ctx.Settings.OutputDeviceId = value.Value;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice> AliasChoices { get; private set; }

    private string _testAlias = "";

    public Choice? TestAlias
    {
        get => SpeechPageContext.Find(AliasChoices, _testAlias) ?? AliasChoices[0];
        set
        {
            if (value is null) return;
            _testAlias = value.Value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _testText = "Hello from Cuebot!";
    [ObservableProperty] private string _error = "";

    /// <summary>Re-reads the live state; called on the UI thread after <see cref="SpeechService.StateChanged"/>.</summary>
    public void Refresh()
    {
        Queue = Speech.Queue.Select(QueueItemViewModel.From).ToArray();
        History = Speech.History.Select(HistoryItemViewModel.From).ToArray();
        OnPropertyChanged(nameof(Queue));
        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(IsSpeaking));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Paused));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(EventsEnabled));
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        OutputDevices = _ctx.OutputDeviceChoices("System default");
        OnPropertyChanged(nameof(OutputDevices));
        OnPropertyChanged(nameof(OutputDevice));
    }

    [RelayCommand]
    private async Task Speak()
    {
        Error = "";
        if (!Speech.Enabled)
        {
            Error = "Speech is off. Turn it on to speak.";
            return;
        }
        try
        {
            await Speech.SpeakAsync(TestText, new SpeakOptions { Alias = _testAlias.Length > 0 ? _testAlias : null, Source = "test" });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Skip() => Speech.SkipCurrent();

    [RelayCommand]
    private void ClearQueue() => Speech.ClearQueue();

    /// <summary>Drops everything waiting from this item's user (and stops them if speaking).</summary>
    [RelayCommand]
    private void StopUser(QueueItemViewModel? item)
    {
        if (item is { HasUser: true }) Speech.StopUser(null, null, item.User);
    }
}
