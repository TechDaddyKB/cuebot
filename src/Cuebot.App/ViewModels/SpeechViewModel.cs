using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.App.ViewModels.Speech;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels;

/// <summary>
/// Text-to-speech page (Speaker.bot parity): one tab per area. Every edit writes straight into the live
/// <see cref="SpeechSettings"/> and schedules a save; service events are marshaled to the UI thread.
/// </summary>
public sealed partial class SpeechViewModel : PageViewModel
{
    internal const int EnginesTab = 1;
    internal const int UsersTab = 3;

    private readonly SpeechPageContext _ctx;

    /// <param name="audioInput">Microphone access for the VoiceGate device list and level meter; optional.</param>
    public SpeechViewModel(SpeechService speech, IAudioInput? audioInput = null) : base("Speech")
    {
        _ctx = new SpeechPageContext(speech, audioInput);
        Overview = new SpeechOverviewViewModel(_ctx);
        Engines = new SpeechEnginesViewModel(_ctx);
        Aliases = new SpeechAliasesViewModel(_ctx);
        Users = new SpeechUsersViewModel(_ctx);
        Chat = new SpeechChatViewModel(_ctx);
        Filters = new SpeechFiltersViewModel(_ctx);
        Events = new SpeechEventsViewModel(_ctx);
        Gate = new SpeechGateViewModel(_ctx);
        speech.StateChanged += () => OnUi(Refresh);
    }

    public SpeechOverviewViewModel Overview { get; }
    public SpeechEnginesViewModel Engines { get; }
    public SpeechAliasesViewModel Aliases { get; }
    public SpeechUsersViewModel Users { get; }
    public SpeechChatViewModel Chat { get; }
    public SpeechFiltersViewModel Filters { get; }
    public SpeechEventsViewModel Events { get; }
    public SpeechGateViewModel Gate { get; }

    [ObservableProperty] private int _selectedTab;

    public string StatusText => Overview.StatusText;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == UsersTab) Users.Refresh(); // the directory changes as chatters speak
        if (value == EnginesTab) Engines.RefreshStatus();
    }

    private void Refresh()
    {
        Overview.Refresh();
        Events.Refresh();
        Gate.Refresh();
        OnPropertyChanged(nameof(StatusText));
    }

    public override string ToString() => Title;
}
