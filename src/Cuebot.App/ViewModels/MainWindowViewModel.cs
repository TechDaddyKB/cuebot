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
    /// The sections of the icon rail, in order, and the page titles each one holds (in order). To place a new page, add
    /// its <see cref="PageViewModel.Title"/> to a section here; a page that is not listed lands in
    /// <see cref="FallbackGroup"/>. Icons are Lucide names (Styles/Icons.axaml).
    /// </summary>
    public static readonly IReadOnlyList<NavSection> NavLayout =
    [
        new("Home", "Home", "house", ["Dashboard", "Assistant"]),
        new("Automation", "Automation", "zap", ["Actions", "Commands", "Timers", "Queues", "Variables", "Watchers", "Hotkeys", "Scripting"]),
        new("Chat & Events", "Chat", "message-square", ["Chat", "History", "Users & Groups", "Points", Extras.GiveawaysStoreViewModel.PageTitle, "Quotes", "Credits"]),
        new("Audio", "Audio", "audio-lines", ["Speech", MediaRequestsViewModel.PageTitle, "Voice Control"]),
        new("Decks & Input", "Input", "layout-grid", ["Decks", "Stream Deck", "Input"]),
        new("Platforms", "Platforms", "radio", ["Twitch", "YouTube", "Kick", "Rumble"]),
        new("Overlays & Widgets", "Overlays", "layers", [OverlaysViewModel.PageTitle]),
        new("Integrations", "Apps", "plug", ["Stream apps", "Apps & devices", "Donations", "Webhooks", "Servers"]),
        new("Settings", "Settings", "settings", ["Settings", "Import / Export", "Support"], Bottom: true),
    ];

    /// <summary>Section for pages missing from <see cref="NavLayout"/> (placed before the bottom sections).</summary>
    public const string FallbackGroup = "More";

    /// <summary>The line under each page's title in the page header.</summary>
    public static readonly IReadOnlyDictionary<string, string> PageDescriptions = new Dictionary<string, string>
    {
        ["Dashboard"] = "What is connected, and what just happened.",
        ["Assistant"] = "Ask an AI assistant to help you set Cuebot up.",
        ["Actions"] = "Create and manage actions, their triggers and sub-actions.",
        ["Commands"] = "Chat commands and the actions they run.",
        ["Timers"] = "Run actions on an interval or a schedule.",
        ["Queues"] = "Run actions one after another.",
        ["Variables"] = "Global and per-viewer values your actions use.",
        ["Watchers"] = "React to files, folders and programs.",
        ["Hotkeys"] = "Keyboard shortcuts that run actions.",
        ["Scripting"] = "C# and Python for anything custom.",
        ["Chat"] = "Read and answer chat across platforms.",
        ["History"] = "What ran, and what triggered it.",
        ["Users & Groups"] = "Viewers Cuebot knows, and the groups you put them in.",
        ["Points"] = "Your channel currency.",
        [Extras.GiveawaysStoreViewModel.PageTitle] = "Giveaways and a store viewers spend their points in.",
        ["Quotes"] = "Quotes saved from your streams.",
        ["Credits"] = "End-of-stream credits.",
        ["Speech"] = "Text-to-speech voices, queue and filters.",
        [MediaRequestsViewModel.PageTitle] = "YouTube videos viewers request in chat, played on your overlay.",
        ["Decks"] = "Web control decks for your phone or tablet.",
        ["Input"] = "MIDI devices and game controllers.",
        ["Stream Deck"] = "Elgato Stream Deck keys, through the plugin or USB.",
        ["Voice Control"] = "Spoken commands that fire triggers.",
        ["Twitch"] = "Your Twitch account, channel and settings.",
        ["YouTube"] = "Your YouTube channel, chat and settings.",
        ["Kick"] = "Your Kick account, channel and settings.",
        ["Rumble"] = "Your Rumble channel and chat.",
        [OverlaysViewModel.PageTitle] = "Overlays for OBS browser sources, with widgets, alerts and the designer.",
        ["Stream apps"] = "OBS Studio, Streamlabs Desktop and other streaming apps.",
        ["Apps & devices"] = "VTube Studio, lights, mixers, Stream Deck and other apps.",
        ["Donations"] = "Tips and charity services.",
        ["Webhooks"] = "Events from web services, through your relay.",
        ["Servers"] = "Local servers for decks, docks, other apps and AI tools.",
        ["Settings"] = "Window, appearance, backups and more.",
        ["Import / Export"] = "Bring in Streamer.bot data, or share your own.",
        ["Support"] = "Report a problem and check for updates.",
    };

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
        history.Triggers.Seen += Dashboard.AddEvent;
        Pages =
        [
            Dashboard,
            new ChatViewModel(host.Get<Cuebot.Core.Chat.ChatHub>(), Cuebot.App.ViewModels.Chat.ChatPageServices.From(host)),
            actions,
            new CommandsViewModel(host.Get<Cuebot.Core.Commands.CommandStore>(), host.Get<Cuebot.Core.Users.GroupStore>()),
            history,
            new Cuebot.App.ViewModels.Assistant.AssistantViewModel(host.Get<Cuebot.Assistant.AssistantService>()),
            queues,
            new TimersViewModel(host.Get<ProfileService>(), host.Get<TimerService>()),
            new UsersViewModel(host.Get<Cuebot.Core.Users.UserRegistry>(), host.Get<Cuebot.Core.Users.GroupStore>(), host.Get<Cuebot.Core.Users.ViewerData>()),
            new VariablesViewModel(host.Get<Cuebot.Core.Variables.VariableViewer>()),
            new WatchersViewModel(host.Get<Cuebot.Core.SystemTools.WatcherService>()),
            new TwitchViewModel(host.Get<TwitchService>()),
            new RumbleViewModel(host.Get<Cuebot.Rumble.RumbleService>()),
            new Cuebot.App.ViewModels.YouTube.YouTubeViewModel(host.Get<Cuebot.YouTube.YouTubeService>()),
            new Cuebot.App.ViewModels.Kick.KickViewModel(host.Get<Cuebot.Kick.KickService>()),
            new Cuebot.App.ViewModels.Donations.DonationsViewModel(host.Get<Cuebot.Donations.DonationServices>()),
            new Cuebot.App.ViewModels.VoiceControl.VoiceControlViewModel(host.Get<Cuebot.VoiceControl.VoiceControlService>()),
            new InputPageViewModel(
                new Cuebot.App.ViewModels.Midi.MidiViewModel(host.Get<Cuebot.Midi.MidiService>()),
                new Cuebot.App.ViewModels.Controllers.ControllersViewModel(host.Get<Cuebot.Controllers.ControllerService>())),
            new Cuebot.App.ViewModels.Integrations.IntegrationsPageViewModel(
                new Cuebot.App.ViewModels.Integrations.IntegrationsViewModel(
                    host.Get<Cuebot.Integrations.VTubeStudio.VTubeStudioService>(), host.Get<Cuebot.Integrations.Voicemod.VoicemodService>(),
                    host.Get<Cuebot.Integrations.HeartRate.Pulsoid.PulsoidService>(), host.Get<Cuebot.Integrations.HeartRate.HypeRate.HypeRateService>(),
                    host.Get<Cuebot.Integrations.StreamLoots.StreamLootsService>(), host.Get<Cuebot.Integrations.CrowdControl.CrowdControlService>()),
                new Cuebot.App.ViewModels.Elgato.ElgatoViewModel(host.Get<Cuebot.Elgato.Lights.ElgatoLightsService>(),
                    host.Get<Cuebot.Elgato.CameraHub.CameraHubService>()),
                new Cuebot.App.ViewModels.WaveLink.WaveLinkViewModel(host.Get<Cuebot.WaveLink.WaveLinkService>()),
                new Cuebot.App.ViewModels.GoXlr.GoXlrViewModel(host.Get<Cuebot.GoXlr.GoXlrService>())),
            new Cuebot.App.ViewModels.StreamDeck.StreamDeckViewModel(host.Get<Cuebot.StreamDeck.StreamDeckHub>(),
                host.Get<Cuebot.Server.ServerManager>(), usb: host.Get<Cuebot.StreamDeck.Hid.StreamDeckHidService>()),
            new Cuebot.App.ViewModels.Webhooks.WebhooksPageViewModel(
                new Cuebot.App.ViewModels.Webhooks.WebhookServicesViewModel(host.Get<Cuebot.Webhooks.WebhookServices>()),
                new Cuebot.App.ViewModels.Webhooks.WebhooksViewModel(host.Get<Cuebot.Relay.RelayClient>(), host.Get<Cuebot.Core.Desktop.Clipboard.IClipboardService>())),
            new Extras.QuotesViewModel(host.Get<Cuebot.Core.Quotes.QuoteStore>()),
            new Extras.CreditsViewModel(host.Get<Cuebot.Core.Credits.CreditsCollector>()),
            new Extras.PointsViewModel(host.Get<Cuebot.Core.Points.PointsService>()),
            new Extras.GiveawaysStoreViewModel(host.Get<Cuebot.Core.Giveaways.Giveaways>(), host.Get<Cuebot.Core.Store.PointsStore>()),
            new Extras.DecksViewModel(host.Get<Cuebot.Core.Decks.DeckService>(),
                () => host.Get<Cuebot.Server.ServerManager>().DockUrls[0].Url.Replace("/feed/chat", "", StringComparison.Ordinal),
                () => host.Get<Cuebot.Server.ServerManager>().Settings.Http.Enabled,
                () => host.Get<ProfileService>().Snapshot.Actions.Select(a => a.Name)),
            new Extras.HotkeysViewModel(host.Get<Cuebot.Core.Desktop.Hotkeys.HotkeyService>(),
                host.Get<Cuebot.Core.Storage.JsonFile<Cuebot.Core.Desktop.Notifications.DesktopSettings>>(),
                () => host.Get<ProfileService>().Snapshot.Actions.Select(a => a.Name)),
            new Cuebot.App.ViewModels.StreamApps.StreamAppsPageViewModel(new ObsViewModel(host.Get<ObsService>()),
                new Cuebot.App.ViewModels.StreamApps.StreamAppsViewModel(host.Get<Cuebot.StreamApps.Meld.MeldStudioService>(),
                    host.Get<Cuebot.StreamApps.PolyPop.PolyPopService>()))
            {
                Tits = new Cuebot.App.ViewModels.StreamApps.TitsViewModel(host.Get<Cuebot.StreamApps.Tits.TitsService>()),
                Lumia = new Cuebot.App.ViewModels.StreamApps.LumiaViewModel(host.Get<Cuebot.StreamApps.Lumia.LumiaService>()),
                Obsbot = new Cuebot.App.ViewModels.StreamApps.ObsbotViewModel(host.Get<Cuebot.StreamApps.Obsbot.ObsbotService>()),
                Mtion = new Cuebot.App.ViewModels.StreamApps.MtionViewModel(host.Get<Cuebot.StreamApps.Mtion.MtionService>()),
            },
            new SpeechViewModel(host.Get<SpeechService>(), host.Get<IAudioInput>()),
            new MediaRequestsViewModel(host.Get<Cuebot.Core.Media.MediaRequests>()),
            new Transfer.ImportExportViewModel(host.Get<Cuebot.Core.Transfer.TransferService>(), host.Get<Cuebot.Core.Actions.ProfileService>(),
                host.Get<Cuebot.Core.Commands.CommandStore>(), host.Get<Cuebot.Core.Actions.GlobalVariables>(),
                host.Get<Cuebot.Core.Desktop.Clipboard.IClipboardService>()),
            new ScriptingViewModel(host.Get<PythonRuntime>(), host.Get<Cuebot.Core.Scripting.ScriptApi>()),
            new OverlaysViewModel(host.Get<Cuebot.Core.Overlays.OverlayStore>(), host.Get<Cuebot.Server.ServerManager>(),
                host.Get<Cuebot.Core.Overlays.Widgets.WidgetLibrary>(), host.Get<Cuebot.Core.Overlays.OverlayAlertQueue>(),
                new ChatPollPanelViewModel(host.Get<Cuebot.Core.Polls.ChatPolls>(), host.Get<Cuebot.Core.Polls.PollTracker>()),
                host.Get<Cuebot.Donations.StreamElements.StreamElementsService>()),
            new Cuebot.App.ViewModels.Servers.ServersViewModel(host.Get<Cuebot.Server.ServerManager>(),
                new Cuebot.App.ViewModels.Servers.RemoteInstancesViewModel(host.Get<Cuebot.Remote.RemoteHub>())),
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
        _isSectionListOpen = !host.Settings.Settings.General.CollapseSectionList;
        Status = new StatusBarViewModel(host, Dashboard);
        Status.ProblemsRequested += () => SelectedPage = Support;
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
            ["jump"] = new RelayCommand(() => JumpRequested?.Invoke()),
            ["fullScreen"] = new RelayCommand(() => FullScreenRequested?.Invoke()),
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

    /// <summary>The sections: the icon rail shows them, the section list shows the current one's pages.</summary>
    public IReadOnlyList<NavGroupViewModel> NavGroups { get; }

    public IEnumerable<NavGroupViewModel> RailTop => NavGroups.Where(g => !g.IsBottom);
    public IEnumerable<NavGroupViewModel> RailBottom => NavGroups.Where(g => g.IsBottom);

    /// <summary>The bottom bar: connections, counts, uptime, warnings.</summary>
    public StatusBarViewModel Status { get; }

    public string Version => BackupService.Version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDescription))]
    private PageViewModel _selectedPage;

    /// <summary>The section of the selected page.</summary>
    [ObservableProperty] private NavGroupViewModel? _currentGroup;

    public string PageDescription => PageDescriptions.GetValueOrDefault(SelectedPage.Title, "");

    /// <summary>The section list next to the rail; closing it leaves the rail only (remembered in app.json).</summary>
    [ObservableProperty] private bool _isSectionListOpen;

    partial void OnIsSectionListOpenChanged(bool value)
    {
        _host.Settings.Settings.General.CollapseSectionList = !value;
        _host.Settings.TrySave();
    }

    [RelayCommand]
    private void ToggleSectionList() => IsSectionListOpen = !IsSectionListOpen;

    /// <summary>Rail click: the section's last page (its first page the first time).</summary>
    [RelayCommand]
    private void SelectGroup(NavGroupViewModel? group)
    {
        if (group is not null) SelectedPage = group.LastPage ?? group.Pages[0];
    }

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        oldValue?.OnLeft();
        SyncGroupSelection(newValue);
    }

    /// <summary>Shows a page (and its section).</summary>
    public void Navigate(PageViewModel page) => SelectedPage = page;

    /// <summary>Page names for the jump box (Ctrl+K), in rail order.</summary>
    public IReadOnlyList<string> JumpTargets => NavGroups.SelectMany(g => g.Pages).Select(p => p.Title).ToArray();

    /// <summary>Ctrl+K: the window focuses the jump box.</summary>
    public event Action? JumpRequested;

    /// <summary>F11: the window turns full screen on or off.</summary>
    public event Action? FullScreenRequested;

    /// <summary>
    /// Opens the page with this title (case-insensitive), or else the first page in the jump list whose title contains
    /// it (what the jump box's list shows first); false when there is none.
    /// </summary>
    public bool JumpTo(string? title)
    {
        var text = title?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        var partial = JumpTargets.FirstOrDefault(t => t.Contains(text, StringComparison.OrdinalIgnoreCase));
        var page = Pages.FirstOrDefault(p => string.Equals(p.Title, text, StringComparison.OrdinalIgnoreCase))
                   ?? Pages.FirstOrDefault(p => p.Title == partial);
        if (page is null) return false;
        SelectedPage = page;
        return true;
    }

    /// <summary>"by Tech Daddy Digital" in the top bar opens Settings (its About tab).</summary>
    [RelayCommand]
    private void OpenAbout()
    {
        SelectedPage = Settings;
        Settings.ShowAbout();
    }

    public NavGroupViewModel? GroupOf(PageViewModel page) => NavGroups.FirstOrDefault(g => g.Pages.Contains(page));

    private IReadOnlyList<NavGroupViewModel> BuildGroups(IReadOnlyList<PageViewModel> pages)
    {
        var groups = new List<NavGroupViewModel>();
        var placed = new HashSet<PageViewModel>();
        foreach (var section in NavLayout)
        {
            var members = section.Titles.Select(t => pages.FirstOrDefault(p => p.Title == t)).OfType<PageViewModel>().ToList();
            placed.UnionWith(members);
            if (members.Count > 0) groups.Add(new NavGroupViewModel(this, section, members));
        }
        var rest = pages.Where(p => !placed.Contains(p)).ToList();
        if (rest.Count > 0)
        {
            var at = groups.FindIndex(g => g.IsBottom);
            groups.Insert(at < 0 ? groups.Count : at,
                new NavGroupViewModel(this, new NavSection(FallbackGroup, FallbackGroup, "ellipsis", []), rest));
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
                group.IsCurrent = contains;
                if (!contains) continue;
                group.LastPage = page;
                CurrentGroup = group;
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

/// <summary>A rail section and its pages (one row of <see cref="MainWindowViewModel.NavLayout"/>).</summary>
public sealed record NavSection(string Name, string Label, string Icon, string[] Titles, bool Bottom = false);

/// <summary>A section of the icon rail. Its list box selection mirrors the window's selected page.</summary>
public sealed partial class NavGroupViewModel(MainWindowViewModel owner, NavSection section, IReadOnlyList<PageViewModel> pages) : ObservableObject
{
    public string Name { get; } = section.Name;

    /// <summary>Short label under the rail icon.</summary>
    public string Label { get; } = section.Label;

    /// <summary>Lucide icon name.</summary>
    public string Icon { get; } = section.Icon;

    /// <summary>Shown at the bottom of the rail (Settings).</summary>
    public bool IsBottom { get; } = section.Bottom;

    public IReadOnlyList<PageViewModel> Pages { get; } = pages;

    [ObservableProperty] private PageViewModel? _selected;

    /// <summary>True for the selected page's section (highlighted in the rail).</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>The page shown when the section is picked in the rail.</summary>
    public PageViewModel? LastPage { get; set; }

    partial void OnSelectedChanged(PageViewModel? value)
    {
        if (value is not null) owner.OnGroupSelected(value);
    }
}
