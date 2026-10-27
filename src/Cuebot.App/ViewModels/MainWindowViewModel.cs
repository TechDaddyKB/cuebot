using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.App.Shell;
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
    public MainWindowViewModel(AppHost host, IShellHost? shell = null)
    {
        Settings = new SettingsViewModel(host, shell ?? new DetachedShellHost(host.Settings));
        var log = host.Get<AppLog>();
        Dashboard = new DashboardViewModel(log, host.Get<ActionEngine>(), host.GetAll<IConnectionStatus>());
        Pages =
        [
            Dashboard,
            new ChatViewModel(host.Get<Cuebot.Core.Chat.ChatHub>(), Cuebot.App.ViewModels.Chat.ChatPageServices.From(host)),
            new ActionsViewModel(host.Get<ProfileService>(), host.Get<Registry>(), host.Get<ActionEngine>()),
            new CommandsViewModel(host.Get<Cuebot.Core.Commands.CommandStore>(), host.Get<Cuebot.Core.Users.GroupStore>()),
            new QueuesViewModel(host.Get<ProfileService>(), host.Get<ActionEngine>()),
            new TimersViewModel(host.Get<ProfileService>(), host.Get<TimerService>()),
            new UsersViewModel(host.Get<Cuebot.Core.Users.UserRegistry>(), host.Get<Cuebot.Core.Users.GroupStore>()),
            new VariablesViewModel(host.Get<Cuebot.Core.Variables.VariableViewer>()),
            new WatchersViewModel(host.Get<Cuebot.Core.SystemTools.WatcherService>()),
            new TwitchViewModel(host.Get<TwitchService>()),
            new RumbleViewModel(host.Get<Cuebot.Rumble.RumbleService>()),
            new Cuebot.App.ViewModels.YouTube.YouTubeViewModel(host.Get<Cuebot.YouTube.YouTubeService>()),
            new Cuebot.App.ViewModels.Kick.KickViewModel(host.Get<Cuebot.Kick.KickService>()),
            new ObsViewModel(host.Get<ObsService>()),
            new SpeechViewModel(host.Get<SpeechService>(), host.Get<IAudioInput>()),
            new ScriptingViewModel(host.Get<PythonRuntime>(), host.Get<Cuebot.Core.Scripting.ScriptApi>()),
            Settings,
        ];
        _selectedPage = Dashboard;
    }

    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selectedPage;
}
