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
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;

namespace Cuebot.UiTests;

/// <summary>App keyboard shortcuts: window KeyBindings, the Settings list, and the Actions page's own keys.</summary>
public sealed class ShortcutsTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-keys-").FullName;
    private readonly AppHost _host;

    public ShortcutsTests() => _host = AppHost.Create(new AppPaths(_dataDir));

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

    [AvaloniaFact]
    public void Window_shortcuts_are_key_bindings_and_listed_in_settings()
    {
        var (window, vm) = Open();
        foreach (var shortcut in AppShortcuts.Window)
        {
            var binding = Assert.Single(window.KeyBindings, k => k.Gesture == KeyGesture.Parse(shortcut.Gesture));
            Assert.Same(vm.ShortcutCommands[shortcut.Id], binding.Command);
        }
        Assert.Equal(AppShortcuts.All, new SettingsView().FindControl<ItemsControl>("ShortcutList")!.ItemsSource);

        // Real key presses through the window.
        window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.C, null);
        Pump();
        Assert.Equal("Chat", vm.SelectedPage.Title);
        window.KeyPress(Key.V, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.V, null);
        Pump();
        Assert.Equal("Variables", vm.SelectedPage.Title);
        window.KeyPress(Key.I, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.I, null);
        Pump();
        Assert.Equal("Import / Export", vm.SelectedPage.Title);
        vm.SelectedPage = vm.Dashboard;
        window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.E, null);
        Pump();
        Assert.Equal("Import / Export", vm.SelectedPage.Title);
        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Pump();
        Assert.Equal("Support", vm.SelectedPage.Title);

        // Ctrl+S writes the profile now.
        var profile = _host.Get<ProfileService>();
        profile.Profile.Actions.Add(new ActionDefinition { Name = "Saved by the shortcut" });
        profile.NotifyChanged();
        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        Pump();
        Assert.StartsWith("Saved", vm.SaveStatus);
        Assert.Contains("Saved by the shortcut", File.ReadAllText(Path.Combine(_dataDir, "actions.json")));

        // While typing, Ctrl+Alt shortcuts stay out of the way (AltGr is Ctrl+Alt on many layouts).
        vm.IsTypingText = () => true;
        Assert.False(vm.ShortcutCommands["chat"].CanExecute(null));
        Assert.False(vm.ShortcutCommands["logs"].CanExecute(null));
        Assert.True(vm.ShortcutCommands["save"].CanExecute(null));
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_a_text_box_keeps_altgr_characters()
    {
        var (window, vm) = Open();
        vm.SelectedPage = vm.Pages.Single(p => p.Title == "Actions");
        Pump();
        var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.IsEnabled);
        box.Focus();
        Pump();
        window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.E, "€");
        Pump();
        Assert.Equal("Actions", vm.SelectedPage.Title);   // Ctrl+Alt+E did not open Import / Export
        window.Close();
    }

    [AvaloniaFact]
    public void Actions_page_keys_toggle_move_delete_and_test()
    {
        var profile = _host.Get<ProfileService>();
        var action = new ActionDefinition
        {
            Name = "Keys",
            Triggers = [new TriggerDefinition { Type = "core.appStarted" }],
            SubActions =
            [
                new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "1" } },
                new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "2" } },
            ],
        };
        profile.Profile.Actions.Add(action);
        profile.NotifyChanged();
        var (window, vm) = Open();
        var actions = vm.Pages.OfType<ActionsViewModel>().Single();
        vm.SelectedPage = actions;
        var item = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Keys");
        Pump();

        // F5 opens the test-trigger overlay for the selected action (only on the Actions page).
        Assert.True(vm.ShortcutCommands["testTrigger"].CanExecute(null));
        window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        Assert.True(actions.Test.IsOpen);
        actions.Test.CancelCommand.Execute(null);
        vm.SelectedPage = vm.Dashboard;
        Assert.False(vm.ShortcutCommands["testTrigger"].CanExecute(null));
        vm.SelectedPage = actions;
        Pump();

        // Editor: Ctrl+Down moves the selected sub-action, Ctrl+O turns it off.
        var rowButton = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == item.Steps.Items[0]);
        rowButton.Focus();
        item.Selected = item.Steps.Items[0];
        var first = item.Steps.Items[0].Definition.Id;
        window.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
        Pump();
        item = actions.SelectedAction!;
        Assert.Equal(first, item.Steps.Items[1].Definition.Id);
        window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == item.Steps.Items[1]).Focus();
        item.Selected = item.Steps.Items[1];
        window.KeyPress(Key.O, RawInputModifiers.Control, PhysicalKey.O, "o");
        Pump();
        Assert.False(actions.SelectedAction!.Steps.Items[1].Enabled);

        // Action list: Ctrl+O turns the action off; Delete asks first, a second press deletes.
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ActionList");
        list.ContainerFromItem(actions.SelectedAction!)!.Focus();
        window.KeyPress(Key.O, RawInputModifiers.Control, PhysicalKey.O, "o");
        Pump();
        Assert.False(actions.SelectedAction!.Enabled);
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Pump();
        Assert.Contains("again", actions.EditStatus);
        Assert.Contains(actions.Actions, a => a.Name == "Keys");
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Pump();
        Assert.DoesNotContain(actions.Actions, a => a.Name == "Keys");
        Assert.Null(profile.Snapshot.FindAction("Keys"));
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
