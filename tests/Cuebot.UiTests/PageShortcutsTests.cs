using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.ViewModels.Extras;
using Cuebot.App.ViewModels.Twitch;
using Cuebot.App.ViewModels.VoiceControl;
using Cuebot.App.Views;
using Cuebot.App.Views.Extras;
using Cuebot.App.Views.Twitch;
using Cuebot.App.Views.VoiceControl;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Quotes;

namespace Cuebot.UiTests;

/// <summary>Streamer.bot's per-view keyboard shortcuts on Cuebot's list pages (docs: 2.guide/05.advanced/01.shortcuts.md).</summary>
public sealed class PageShortcutsTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-pagekeys-").FullName;
    private readonly AppHost _host;

    public PageShortcutsTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 1360, Height = 860 };
        window.Show();
        Pump();
        return window;
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Pump();
    }

    private static void FocusItem(Window window, object item)
    {
        var control = window.GetVisualDescendants().OfType<Control>().First(c => c.DataContext == item && c.Focusable && c.IsEffectivelyVisible);
        control.Focus();
        Pump();
    }

    [AvaloniaFact]
    public void Every_page_shortcut_is_listed_in_settings()
    {
        Assert.Equal(AppShortcuts.All, new SettingsView().FindControl<ItemsControl>("ShortcutList")!.ItemsSource);
        foreach (var where in (string[])["Commands", "Timers", "MIDI", "Voice Control", "Quotes", "Twitch > Rewards"])
            Assert.Contains(AppShortcuts.Pages, s => s.Where == where);
        Assert.Contains(AppShortcuts.Actions, s => s.Gesture == "Ctrl+Home / Ctrl+End");
        Assert.Contains(AppShortcuts.Actions, s => s.Gesture == "Ctrl+R");
    }

    [AvaloniaFact]
    public async Task Commands_view_deletes_toggles_renames_groups_and_collapses()
    {
        var store = _host.Get<CommandStore>();
        store.Save(new CommandDefinition { Name = "Hello", Group = "Fun", Commands = ["!hello"] });
        store.Save(new CommandDefinition { Name = "Bye", Group = "Fun", Commands = ["!bye"] });
        store.Save(new CommandDefinition { Name = "Lurk", Commands = ["!lurk"] });
        var vm = new CommandsViewModel(store, _host.Get<Cuebot.Core.Users.GroupStore>());
        var view = new CommandsView { DataContext = vm };
        UserPromptRequest? asked = null;
        view.AskGroupName = request =>
        {
            asked = request;
            return Task.FromResult<string?>("Games");
        };
        var window = Show(view);

        var hello = vm.Groups.SelectMany(g => g.Commands).Single(c => c.Name == "Hello");
        vm.SelectCommand.Execute(hello);
        Pump();
        FocusItem(window, hello);

        Press(window, Key.O, RawInputModifiers.Control);
        Assert.False(store.All().Single(c => c.Name == "Hello").Enabled);
        Press(window, Key.O, RawInputModifiers.Control);
        Assert.True(store.All().Single(c => c.Name == "Hello").Enabled);

        Press(window, Key.R, RawInputModifiers.Control);
        await Task.Yield();
        Pump();
        Assert.Equal("Fun", asked?.OriginalText);
        Assert.All(store.All().Where(c => c.Name is "Hello" or "Bye"), c => Assert.Equal("Games", c.Group));

        Press(window, Key.OemMinus, RawInputModifiers.Control);
        Assert.All(vm.Groups, g => Assert.False(g.IsExpanded));
        Press(window, Key.Multiply, RawInputModifiers.Control);
        Assert.All(vm.Groups, g => Assert.True(g.IsExpanded));

        hello = vm.Groups.SelectMany(g => g.Commands).Single(c => c.Name == "Hello");
        vm.SelectCommand.Execute(hello);
        Pump();
        FocusItem(window, hello);
        Press(window, Key.Delete);
        Assert.DoesNotContain(store.All(), c => c.Name == "Hello");

        // Typing in the search box keeps its own keys.
        var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Search commands");
        search.Focus();
        vm.SelectCommand.Execute(vm.Groups.SelectMany(g => g.Commands).First());
        Press(window, Key.Delete);
        Assert.Equal(2, store.All().Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Timers_view_toggles_and_removes_the_focused_timer()
    {
        var vm = new TimersViewModel(_host.Get<ProfileService>());
        vm.AddTimerCommand.Execute(null);
        var window = Show(new TimersView { DataContext = vm });
        var timer = Assert.Single(vm.Timers);
        var enabled = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.DataContext == timer && Equals(c.Content, "Enabled"));
        enabled.Focus();
        Pump();

        var before = timer.Enabled;
        Press(window, Key.O, RawInputModifiers.Control);
        Assert.NotEqual(before, timer.Enabled);
        Press(window, Key.Delete);
        Assert.Empty(vm.Timers);
        window.Close();
    }

    [AvaloniaFact]
    public void Quotes_view_deletes_the_selected_quote()
    {
        var store = _host.Get<QuoteStore>();
        store.Add("first quote");
        store.Add("second quote");
        var vm = new QuotesViewModel(store);
        var window = Show(new QuotesView { DataContext = vm });
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "QuoteList");
        list.ContainerFromIndex(0)!.Focus();
        Pump();

        Press(window, Key.Delete);

        Assert.Single(vm.Quotes);
        Assert.Single(store.All());
        window.Close();
    }

    [AvaloniaFact]
    public void Voice_control_view_moves_and_deletes_the_selected_command()
    {
        var voice = _host.Get<Cuebot.VoiceControl.VoiceControlService>();
        voice.AddCommand("One", "one");
        voice.AddCommand("Two", "two");
        voice.AddCommand("Three", "three");
        var vm = new VoiceControlViewModel(voice);
        var window = Show(new VoiceControlView { DataContext = vm });
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "VoiceCommands");
        vm.Selected = vm.Commands[0];
        list.ContainerFromIndex(0)!.Focus();
        Pump();

        Press(window, Key.End, RawInputModifiers.Control);
        Assert.Equal(["Two", "Three", "One"], vm.Commands.Select(c => c.Name));
        Press(window, Key.Up, RawInputModifiers.Control);
        Assert.Equal(["Two", "One", "Three"], vm.Commands.Select(c => c.Name));
        Press(window, Key.Home, RawInputModifiers.Control);
        Assert.Equal(["One", "Two", "Three"], vm.Commands.Select(c => c.Name));
        Press(window, Key.Delete);
        Assert.Equal(["Two", "Three"], vm.Commands.Select(c => c.Name));
        window.Close();
    }

    [AvaloniaFact]
    public void Twitch_rewards_tab_adds_duplicates_toggles_and_deletes()
    {
        var vm = new TwitchRewardsViewModel(_host.Get<Cuebot.Twitch.TwitchService>());
        var window = Show(new TwitchRewardsView { DataContext = vm });
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "+ Reward")).Focus();
        Pump();

        Press(window, Key.A, RawInputModifiers.Control);
        var reward = Assert.Single(vm.Rewards);
        Assert.Same(reward, vm.Selected);
        reward.Title = "Hydrate";
        Press(window, Key.D, RawInputModifiers.Control);
        Assert.Equal(["Hydrate", "Hydrate (copy)"], vm.Rewards.Select(r => r.Title));
        Assert.True(vm.Selected!.IsNew);

        Press(window, Key.O, RawInputModifiers.Control);
        Assert.False(vm.Selected!.IsEnabled);   // a new reward is saved to Twitch with Save
        Press(window, Key.Delete);
        Assert.Equal(["Hydrate"], vm.Rewards.Select(r => r.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void Actions_page_moves_a_sub_action_to_the_ends_and_renames_a_group()
    {
        var profile = _host.Get<ProfileService>();
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "Edges",
            SubActions =
            [
                new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "1" } },
                new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "2" } },
                new SubActionDefinition { Type = "core.group", Parameters = { ["name"] = "Mine" } },
            ],
        });
        profile.NotifyChanged();
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        var actions = vm.Pages.OfType<ActionsViewModel>().Single();
        vm.SelectedPage = actions;
        var item = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Edges");
        Pump();
        var ids = item.Steps.Items.Select(s => s.Definition.Id).ToArray();

        item.Selected = item.Steps.Items[0];
        window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == item.Steps.Items[0]).Focus();
        Pump();
        Press(window, Key.End, RawInputModifiers.Control);
        item = actions.SelectedAction!;
        Assert.Equal([ids[1], ids[2], ids[0]], item.Steps.Items.Select(s => s.Definition.Id));
        item.Selected = item.Steps.Items[2];
        window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == item.Steps.Items[2]).Focus();
        Pump();
        Press(window, Key.Home, RawInputModifiers.Control);
        item = actions.SelectedAction!;
        Assert.Equal(ids, item.Steps.Items.Select(s => s.Definition.Id));

        var group = item.Steps.Items.Single(s => s.IsGroup);
        item.Selected = group;
        window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == group).Focus();
        Pump();
        Press(window, Key.R, RawInputModifiers.Control);
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Control;
        Assert.IsType<TextParameterViewModel>(focused?.DataContext);
        Assert.Equal("Mine", ((TextParameterViewModel)focused!.DataContext!).Value);
        window.Close();
    }
}
