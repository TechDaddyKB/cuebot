using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.App.ViewModels.Actions;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Obs;
using Cuebot.Scripting;
using Cuebot.Speech;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(AppHost host)
    {
        var log = host.Get<AppLog>();
        Dashboard = new DashboardViewModel(log, host.Get<ActionEngine>(), host.GetAll<IConnectionStatus>());
        Pages =
        [
            Dashboard,
            new ChatViewModel(host.Get<Cuebot.Core.Chat.ChatHub>()),
            new ActionsViewModel(host.Get<ProfileService>(), host.Get<Registry>(), host.Get<ActionEngine>()),
            new TimersViewModel(host.Get<ProfileService>()),
            new VariablesViewModel(host.Get<GlobalVariables>()),
            new TwitchViewModel(host.Get<TwitchService>()),
            new RumbleViewModel(host.Get<Cuebot.Rumble.RumbleService>()),
            new ObsViewModel(host.Get<ObsService>()),
            new SpeechViewModel(host.Get<SpeechService>()),
            new ScriptingViewModel(host.Get<PythonRuntime>(), host.Get<Cuebot.Core.Scripting.ScriptApi>()),
        ];
        _selectedPage = Dashboard;
    }

    public DashboardViewModel Dashboard { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selectedPage;
}
