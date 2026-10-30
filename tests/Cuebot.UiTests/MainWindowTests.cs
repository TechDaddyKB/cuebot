using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.Views;
using Cuebot.Core;

namespace Cuebot.UiTests;

/// <summary>
/// Renders every page headlessly with the real composition root, fails on binding errors,
/// and saves screenshots to tests/Cuebot.UiTests/screenshots for visual review.
/// </summary>
public sealed class MainWindowTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-").FullName;
    private readonly AppHost _host;

    public MainWindowTests()
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        Directory.CreateDirectory(ScreenshotDir);
    }

    private (Window Window, MainWindowViewModel ViewModel) Open()
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        Pump();
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, name + ".png"));
    }

    [AvaloniaFact]
    public void Every_page_renders_without_binding_errors()
    {
        TestApp.BindingErrors.Clear();
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        var (window, vm) = Open();
        try
        {
            Assert.Equal(
                ["Dashboard", "Chat", "Actions", "Commands", "History", "Assistant", "Queues", "Timers", "Users & Groups", "Variables", "Watchers",
                 "Twitch", "Rumble", "YouTube", "Kick", "Donations", "Voice Control", "Input", "Apps & devices", "Stream Deck", "Webhooks", "Quotes", "Credits", "Points", "Decks", "Hotkeys", "Stream apps", "Speech", "Import / Export", "Scripting", "Overlays & Widgets", "Servers", "Support", "Settings"],
                vm.Pages.Select(p => p.Title));
            foreach (var (theme, suffix) in new[] { (Cuebot.App.Shell.AppTheme.Dark, ""), (Cuebot.App.Shell.AppTheme.Light, "-light") })
            {
                vm.Settings.Theme = theme;
                foreach (var page in vm.Pages)
                {
                    vm.SelectedPage = page;
                    Save(window, $"page-{FileName(page.Title)}{suffix}");
                }
            }
        }
        finally
        {
            vm.Settings.Theme = Cuebot.App.Shell.AppTheme.Dark;
            app.RequestedThemeVariant = original;
        }
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dashboard_shows_tiles_activity_and_quick_actions()
    {
        var profile = _host.Get<Cuebot.Core.Actions.ProfileService>();
        profile.Profile.Actions.AddRange(
        [
            new Cuebot.Core.Actions.ActionDefinition { Name = "Shout out", SubActions = [new Cuebot.Core.Actions.SubActionDefinition { Type = "core.comment" }] },
            new Cuebot.Core.Actions.ActionDefinition { Name = "Scene: BRB", SubActions = [new Cuebot.Core.Actions.SubActionDefinition { Type = "core.comment" }] },
        ]);
        profile.NotifyChanged();
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var dashboard = vm.Dashboard;
        Assert.False(dashboard.HasEvents);
        Assert.False(dashboard.HasQuickActions);
        Assert.Equal($"0 of {dashboard.Connections.Count}", dashboard.ConnectedCount);
        Save(window, "dashboard-empty");

        _host.Get<Cuebot.Core.Events.EventBus>().Publish(new Cuebot.Core.Events.BotEvent("Twitch.ChatMessage",
            new Dictionary<string, object?> { ["user"] = "Ann", ["message"] = "hello there" }));
        var engine = _host.Get<Cuebot.Core.Actions.ActionEngine>();
        foreach (var name in new[] { "Shout out", "Scene: BRB", "Shout out" })
            Assert.True(await engine.RunAsync(profile.Snapshot.FindAction(name)!));
        _host.Get<Cuebot.Core.Logging.AppLog>().Warn("Test", "Something to look at");
        for (var i = 0; i < 100 && (dashboard.RunsToday < 3 || dashboard.EventsToday < 1 || dashboard.Warnings < 1); i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.Equal(1, dashboard.EventsToday);
        Assert.Equal(3, dashboard.RunsToday);
        Assert.Equal("No failures", dashboard.RunsDetail);
        Assert.True(dashboard.ProblemCount >= 1);
        // One row per action, most recent first.
        Assert.Equal(["Shout out", "Scene: BRB"], dashboard.QuickActions.Select(q => q.Name));
        dashboard.QuickActions[1].RunCommand.Execute(null);
        for (var i = 0; i < 100 && dashboard.RunsToday < 4; i++)
        {
            Pump();
            await Task.Delay(10);
        }
        Assert.Equal(["Scene: BRB", "Shout out"], dashboard.QuickActions.Select(q => q.Name));
        Save(window, "dashboard");
        window.Width = 960;
        window.Height = 600;
        Save(window, "dashboard-960");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Speech_sub_pages_sit_under_it_in_the_section_list()
    {
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var speech = vm.Pages.OfType<SpeechViewModel>().Single();
        Assert.True(speech.HasSubPages);
        Assert.False(vm.Dashboard.HasSubPages);
        vm.SelectedPage = speech;
        Pump();

        var subLists = window.GetVisualDescendants().OfType<ListBox>().Where(l => l.Name == "SubPages").ToList();
        var visible = Assert.Single(subLists, l => l.IsEffectivelyVisible);
        Assert.Equal(speech.SubPages, visible.ItemsSource);
        visible.SelectedIndex = 1;
        Pump();
        Assert.Equal(1, speech.SelectedTab);
        Assert.Equal("Engines", speech.SubPageTitle);
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs");
        Assert.Equal(1, tabs.SelectedIndex);
        Save(window, "speech-sub-pages");

        speech.SelectedTab = 0;
        Pump();
        Assert.Equal(0, visible.SelectedIndex);
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Stream_apps_and_apps_devices_show_their_tabs_as_sub_pages()
    {
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var apps = vm.Pages.OfType<Cuebot.App.ViewModels.StreamApps.StreamAppsPageViewModel>().Single();
        Assert.Equal("OBS & Streamlabs Desktop", apps.SubPages[0]);
        Assert.Equal("StreamFog", apps.SubPages[^1]);
        apps.SubPageIndex = apps.SubPages.Count - 1;
        Assert.Equal(6, apps.SelectedTab);             // the last tab, whatever optional apps this host shows
        Assert.Equal("StreamFog", apps.SubPageTitle);

        var devices = vm.Pages.OfType<Cuebot.App.ViewModels.Integrations.IntegrationsPageViewModel>().Single();
        Assert.Equal(["Apps & services", "Lights & cameras", "Wave Link", "GoXLR"], devices.SubPages);
        vm.SelectedPage = devices;
        devices.SubPageIndex = 2;
        Pump();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs");
        Assert.Equal(2, tabs.SelectedIndex);
        Assert.Equal("Wave Link", devices.SubPageTitle);
        Save(window, "apps-devices-wave-link");
        vm.SelectedPage = apps;
        Pump();
        Save(window, "stream-apps-streamfog");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Section_map_places_voice_control_stream_deck_input_and_settings_sub_pages()
    {
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        Assert.Equal(["Speech", "Voice Control"], vm.NavGroups.Single(g => g.Name == "Audio").Pages.Select(p => p.Title));
        Assert.Equal(["Decks", "Stream Deck", "Input"], vm.NavGroups.Single(g => g.Name == "Decks & Input").Pages.Select(p => p.Title));

        var input = vm.Pages.OfType<InputPageViewModel>().Single();
        Assert.Equal(["MIDI", "Game controllers"], input.SubPages);
        vm.SelectedPage = input;
        input.SubPageIndex = 1;
        Pump();
        Assert.Equal(1, window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs").SelectedIndex);
        Save(window, "input-game-controllers");

        Assert.Equal(["General", "Audio & text", "Backups", "Google Drive", "About"], vm.Settings.SubPages);
        vm.OpenAboutCommand.Execute(null);
        Pump();
        Assert.Same(vm.Settings, vm.SelectedPage);
        Assert.Equal("About", vm.Settings.SubPageTitle);
        Assert.Equal(4, vm.Settings.SubPageIndex);
        Save(window, "settings-about");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Narrow_number_boxes_leave_room_for_three_digits()
    {
        // Pages use NumericUpDowns 90-140 px wide; the arrow buttons must not eat the text area (found on Windows).
        var box = new NumericUpDown { Width = 110, Value = 100, FormatString = "0" };
        var window = new Window { Content = new StackPanel { Children = { box } }, Width = 400, Height = 200 };
        window.Show();
        Pump();
        var text = box.GetVisualDescendants().OfType<TextBox>().Single();
        var digits = new Avalonia.Media.FormattedText("1000", System.Globalization.CultureInfo.InvariantCulture,
            Avalonia.Media.FlowDirection.LeftToRight, new Avalonia.Media.Typeface(text.FontFamily), text.FontSize, null);
        Assert.True(text.Bounds.Width - text.Padding.Left - text.Padding.Right >= digits.Width,
            $"text area {text.Bounds.Width} px (padding {text.Padding}) is narrower than four digits ({digits.Width:0} px)");
        window.Close();
    }

    [Fact]
    public void The_app_offers_chat_moderation_for_twitch_youtube_and_kick()
    {
        var moderation = _host.Get<Cuebot.Core.Chat.Moderation.ChatModerationService>();
        foreach (var platform in new[] { "Twitch", "YouTube", "Kick" })
        {
            Assert.NotNull(moderation.ModeratorFor(platform));
            // Nothing is connected in the test host, so nothing is offered yet.
            Assert.Equal(Cuebot.Core.Chat.Moderation.ChatModerationActions.None, moderation.AvailableFor(platform));
        }
    }

    /// <summary>"Users &amp; Groups" → "users-groups".</summary>
    internal static string FileName(string title)
    {
        var name = string.Concat(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (name.Contains("--")) name = name.Replace("--", "-");
        return name.Trim('-');
    }

    [AvaloniaFact]
    public void Actions_page_list_and_graph_views_render_sample_flow()
    {
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var actions = vm.Pages.OfType<ActionsViewModel>().Single();
        vm.SelectedPage = actions;

        var cheer = actions.Actions.Single(a => a.Name == "Cheer reaction");
        actions.SelectedAction = cheer;
        cheer.Selected = cheer.Steps.Items[0];
        Save(window, "actions-list");

        actions.IsGraphView = true;
        Save(window, "actions-graph");
        vm.Settings.Theme = Cuebot.App.Shell.AppTheme.Light;
        Save(window, "actions-graph-light");
        vm.Settings.Theme = Cuebot.App.Shell.AppTheme.Dark;

        // A code sub-action shows the syntax-highlighting editor in the properties panel.
        var python = actions.SelectedAction!.Registry.GetSubAction("code.python")!;
        cheer.AddStep(python, new StepInsertTarget(cheer.Steps, cheer.Steps.Items.Count));
        actions.IsGraphView = false;
        Save(window, "actions-code");
        // Python is not installed in the test data folder: the editor offers to install it.
        var code = cheer.Selected!.Parameters.OfType<Cuebot.App.ViewModels.Actions.CodeParameterViewModel>().Single();
        Assert.True(code.HasNotice);
        Assert.Equal("Install Python", code.NoticeButtonText);
        Assert.True(code.HasNoticeButton);

        actions.AddStepCommand.Execute(null);
        Save(window, "actions-picker");
        Assert.True(actions.Picker.IsOpen);
        actions.Picker.Search = "obs scene";
        Assert.Contains(actions.Picker.Options, o => o.Name.Contains("scene", StringComparison.OrdinalIgnoreCase));
        actions.Picker.CancelCommand.Execute(null);

        Assert.Empty(TestApp.BindingErrors.Errors);
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
