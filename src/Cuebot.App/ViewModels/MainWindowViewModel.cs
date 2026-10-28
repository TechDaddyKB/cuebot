using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels.Actions;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Obs;
using Cuebot.Scripting;
using Cuebot.Speech;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// Sidebar sections, in order, and the page titles each one holds (in order). To place a new page, add its
    /// <see cref="PageViewModel.Title"/> to a section here; a page that is not listed lands in <see cref="FallbackGroup"/>.
    /// </summary>
    public static readonly IReadOnlyList<(string Group, string[] Titles)> NavLayout =
    [
        ("Main", ["Dashboard", "Chat", "Actions", "Commands", "History"]),
        ("Automation", ["Queues", "Timers", "Users & Groups", "Variables", "Watchers", "Hotkeys", "Quotes", "Credits", "Points"]),
        ("Platforms", ["Twitch", "Rumble", "YouTube", "Kick"]),
        ("Apps", ["OBS", "Speech", "Donations", "Integrations", "Webhooks"]),
        ("System", ["Import / Export", "Scripting", "Servers", "Support", "Settings"]),
    ];

    /// <summary>Section for pages missing from <see cref="NavLayout"/> (shown before System).</summary>
    public const string FallbackGroup = "More";

    private bool _syncingSelection;
    private readonly AppHost _host;
    private readonly IShellHost _shell;

    public MainWindowViewModel(AppHost host, IShellHost? shell = null)
    {
        var shellHost = shell ?? new DetachedShellHost(host.Settings);
        _host = host;
        _shell = shellHost;
        Settings = new SettingsViewModel(host, shellHost);
        Support = new Cuebot.App.ViewModels.Support.SupportViewModel(Cuebot.App.ViewModels.Support.SupportServices.From(host, shellHost));
        var log = host.Get<AppLog>();
        Dashboard = new DashboardViewModel(log, host.Get<ActionEngine>(), host.GetAll<IConnectionStatus>());
        var actions = new ActionsViewModel(host.Get<ProfileService>(), host.Get<Registry>(), host.Get<ActionEngine>());
        var queues = new QueuesViewModel(host.Get<ProfileService>(), host.Get<ActionEngine>());
        var history = new History.HistoryViewModel(host.Get<ActionEngine>(), host.Get<EventBus>(), host.Get<Registry>());
        Pages =
        [
            Dashboard,
            new ChatViewModel(host.Get<Cuebot.Core.Chat.ChatHub>(), Cuebot.App.ViewModels.Chat.ChatPageServices.From(host)),
            actions,
            new CommandsViewModel(host.Get<Cuebot.Core.Commands.CommandStore>(), host.Get<Cuebot.Core.Users.GroupStore>()),
            history,
            queues,
            new TimersViewModel(host.Get<ProfileService>(), host.Get<TimerService>()),
            new UsersViewModel(host.Get<Cuebot.Core.Users.UserRegistry>(), host.Get<Cuebot.Core.Users.GroupStore>()),
            new VariablesViewModel(host.Get<Cuebot.Core.Variables.VariableViewer>()),
            new WatchersViewModel(host.Get<Cuebot.Core.SystemTools.WatcherService>()),
            new TwitchViewModel(host.Get<TwitchService>()),
            new RumbleViewModel(host.Get<Cuebot.Rumble.RumbleService>()),
            new Cuebot.App.ViewModels.YouTube.YouTubeViewModel(host.Get<Cuebot.YouTube.YouTubeService>()),
            new Cuebot.App.ViewModels.Kick.KickViewModel(host.Get<Cuebot.Kick.KickService>()),
            new Cuebot.App.ViewModels.Donations.DonationsViewModel(host.Get<Cuebot.Donations.DonationServices>()),
            new Cuebot.App.ViewModels.Integrations.IntegrationsViewModel(
                host.Get<Cuebot.Integrations.VTubeStudio.VTubeStudioService>(), host.Get<Cuebot.Integrations.Voicemod.VoicemodService>(),
                host.Get<Cuebot.Integrations.HeartRate.Pulsoid.PulsoidService>(), host.Get<Cuebot.Integrations.HeartRate.HypeRate.HypeRateService>(),
                host.Get<Cuebot.Integrations.StreamLoots.StreamLootsService>(), host.Get<Cuebot.Integrations.CrowdControl.CrowdControlService>()),
            new Cuebot.App.ViewModels.Webhooks.WebhooksPageViewModel(
                new Cuebot.App.ViewModels.Webhooks.WebhookServicesViewModel(host.Get<Cuebot.Webhooks.WebhookServices>()),
                new Cuebot.App.ViewModels.Webhooks.WebhooksViewModel(host.Get<Cuebot.Relay.RelayClient>(), host.Get<Cuebot.Core.Desktop.Clipboard.IClipboardService>())),
            new Extras.QuotesViewModel(host.Get<Cuebot.Core.Quotes.QuoteStore>()),
            new Extras.CreditsViewModel(host.Get<Cuebot.Core.Credits.CreditsCollector>()),
            new Extras.PointsViewModel(host.Get<Cuebot.Core.Points.PointsService>()),
            new Extras.HotkeysViewModel(host.Get<Cuebot.Core.Desktop.Hotkeys.HotkeyService>(),
                host.Get<Cuebot.Core.Storage.JsonFile<Cuebot.Core.Desktop.Notifications.DesktopSettings>>(),
                () => host.Get<ProfileService>().Snapshot.Actions.Select(a => a.Name)),
            new Cuebot.App.ViewModels.StreamApps.StreamAppsPageViewModel(new ObsViewModel(host.Get<ObsService>()),
                new Cuebot.App.ViewModels.StreamApps.StreamAppsViewModel(host.Get<Cuebot.StreamApps.Meld.MeldStudioService>(),
                    host.Get<Cuebot.StreamApps.PolyPop.PolyPopService>())),
            new SpeechViewModel(host.Get<SpeechService>(), host.Get<IAudioInput>()),
            new Transfer.ImportExportViewModel(host.Get<Cuebot.Core.Transfer.TransferService>(), host.Get<Cuebot.Core.Actions.ProfileService>(),
                host.Get<Cuebot.Core.Commands.CommandStore>(), host.Get<Cuebot.Core.Actions.GlobalVariables>(),
                host.Get<Cuebot.Core.Desktop.Clipboard.IClipboardService>()),
            new ScriptingViewModel(host.Get<PythonRuntime>(), host.Get<Cuebot.Core.Scripting.ScriptApi>()),
            new Cuebot.App.ViewModels.Servers.ServersViewModel(host.Get<Cuebot.Server.ServerManager>()),
            Support,
            Settings,
        ];

        // Cross-page links from the History page.
        history.OpenQueuesRequested += () => SelectedPage = queues;
        Support.CrashBanner.ReportRequested += () => SelectedPage = Support;
        history.OpenActionRequested += id =>
        {
            if (actions.SelectAction(id)) SelectedPage = actions;
            else history.Status = "That action no longer exists.";
        };
        history.Triggers.CreateActionRequested += type =>
        {
            actions.CreateActionFromTrigger(type);
            SelectedPage = actions;
        };

        NavGroups = BuildGroups(Pages);
        _selectedPage = Dashboard;
        SyncGroupSelection(Dashboard);

        PageViewModel Page(string title) => Pages.First(p => p.Title == title);
        ShortcutCommands = new Dictionary<string, System.Windows.Input.ICommand>
        {
            ["save"] = new RelayCommand(SaveAll),
            ["help"] = new RelayCommand(() => SelectedPage = Support),
            ["testTrigger"] = new RelayCommand(() => actions.TestTriggerCommand.Execute(null),
                () => SelectedPage == actions && actions.HasSelection && !actions.Test.IsOpen),
            ["import"] = new RelayCommand(() => SelectedPage = Page("Import / Export"), NotTyping),
            ["export"] = new RelayCommand(() => SelectedPage = Page("Import / Export"), NotTyping),
            ["chat"] = new RelayCommand(() => SelectedPage = Page("Chat"), NotTyping),
            ["variables"] = new RelayCommand(() => SelectedPage = Page("Variables"), NotTyping),
            ["logs"] = new RelayCommand(() => _shell.OpenFolder(_host.Get<AppPaths>().Logs), NotTyping),
        };
    }

    /// <summary>
    /// The window shortcuts' commands by <see cref="AppShortcut.Id"/> (<see cref="AppShortcuts.Window"/>); the window
    /// turns them into KeyBindings.
    /// </summary>
    public IReadOnlyDictionary<string, System.Windows.Input.ICommand> ShortcutCommands { get; }

    /// <summary>
    /// True while a text field has focus (set by the window). The Ctrl+Alt shortcuts then stay out of the way: on many
    /// layouts AltGr arrives as Ctrl+Alt (AltGr+E types €), so a KeyBinding would swallow the character.
    /// </summary>
    public Func<bool> IsTypingText { get; set; } = () => false;

    private bool NotTyping() => !IsTypingText();

    /// <summary>Result of the last Ctrl+S (also shown as an in-app toast when available).</summary>
    [ObservableProperty] private string _saveStatus = "";

    /// <summary>Ctrl+S: writes every pending change to disk now (stores also save on their own within a second).</summary>
    public void SaveAll()
    {
        var log = _host.Get<AppLog>();
        try
        {
            _host.Get<ProfileService>().Flush();
            _host.Get<Cuebot.Core.Users.GroupStore>().Flush();
            _host.Get<Cuebot.Core.Users.UserRegistry>().Flush();
            SaveStatus = _host.Settings.TrySave()
                ? $"Saved at {DateTime.Now:HH:mm:ss}"
                : "Saved, but the app settings could not be written (see the log)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            SaveStatus = "Save failed: " + ex.Message;
            log.Error("Cuebot", "Save failed", ex);
        }
        log.Info("Cuebot", SaveStatus);
        var toast = _host.Get<Cuebot.Core.Desktop.Notifications.IToastPresenter>();
        if (toast.IsAvailable)
            toast.Show(new Cuebot.Core.Desktop.Notifications.DesktopNotification("Cuebot", SaveStatus) { DurationSeconds = 2 }, () => { });
    }

    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Problem reports, crash banner and update checks.</summary>
    public Cuebot.App.ViewModels.Support.SupportViewModel Support { get; }

    /// <summary>Every page, flat (registration order).</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The sidebar: collapsible sections of pages.</summary>
    public IReadOnlyList<NavGroupViewModel> NavGroups { get; }

    [ObservableProperty] private PageViewModel _selectedPage;

    partial void OnSelectedPageChanged(PageViewModel value) => SyncGroupSelection(value);

    /// <summary>Shows a page and expands its section.</summary>
    public void Navigate(PageViewModel page) => SelectedPage = page;

    public NavGroupViewModel? GroupOf(PageViewModel page) => NavGroups.FirstOrDefault(g => g.Pages.Contains(page));

    private IReadOnlyList<NavGroupViewModel> BuildGroups(IReadOnlyList<PageViewModel> pages)
    {
        var groups = new List<NavGroupViewModel>();
        var placed = new HashSet<PageViewModel>();
        foreach (var (name, titles) in NavLayout)
        {
            var members = titles.Select(t => pages.FirstOrDefault(p => p.Title == t)).OfType<PageViewModel>().ToList();
            placed.UnionWith(members);
            if (members.Count > 0) groups.Add(new NavGroupViewModel(this, name, members));
        }
        var rest = pages.Where(p => !placed.Contains(p)).ToList();
        if (rest.Count > 0)
        {
            var at = groups.FindIndex(g => g.Name == "System");
            groups.Insert(at < 0 ? groups.Count : at, new NavGroupViewModel(this, FallbackGroup, rest));
        }
        return groups;
    }

    private void SyncGroupSelection(PageViewModel page)
    {
        _syncingSelection = true;
        try
        {
            foreach (var group in NavGroups)
            {
                var contains = group.Pages.Contains(page);
                group.Selected = contains ? page : null;
                if (contains) group.IsExpanded = true;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    internal void OnGroupSelected(PageViewModel page)
    {
        if (!_syncingSelection) SelectedPage = page;
    }
}

/// <summary>A collapsible sidebar section. Its list box selection mirrors the window's selected page.</summary>
public sealed partial class NavGroupViewModel(MainWindowViewModel owner, string name, IReadOnlyList<PageViewModel> pages) : ObservableObject
{
    public string Name { get; } = name;
    public string Header => Name.ToUpperInvariant();
    public IReadOnlyList<PageViewModel> Pages { get; } = pages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded = true;

    [ObservableProperty] private PageViewModel? _selected;

    public string Chevron => IsExpanded ? "▾" : "▸";

    partial void OnSelectedChanged(PageViewModel? value)
    {
        if (value is not null) owner.OnGroupSelected(value);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
