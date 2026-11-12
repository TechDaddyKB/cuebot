using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuebot.App;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Profiles;
using Cuebot.Core.Storage;

namespace Cuebot.UiTests;

/// <summary>Settings &gt; Profiles, the top bar's profile chip and menu, and profiles in the jump box.</summary>
public sealed class ProfilesPageTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-profiles-ui-").FullName;
    private readonly ProfileCatalog _catalog;
    private readonly AppHost _host;
    private readonly DetachedShellHost _shell;

    public ProfilesPageTests()
    {
        _catalog = ProfileCatalog.Open(_dataDir);
        var paths = new AppPaths(_catalog.ActiveFolder, _dataDir);
        _host = AppHost.Create(paths);
        _shell = new DetachedShellHost(_host.Settings, _catalog);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SecretStore.ResetCache(_dataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void Making_renaming_switching_and_deleting_profiles()
    {
        var vm = new MainWindowViewModel(_host, _shell);
        var profiles = vm.Settings.Profiles;
        Assert.Equal("Main", vm.ProfileName);
        Assert.Equal("Cuebot", vm.WindowTitle);
        Assert.Equal("Main", profiles.OpenProfileName);

        File.WriteAllText(Path.Combine(_catalog.ActiveFolder, "actions.json"), "{}");   // not left to the app's own save timing
        File.WriteAllText(Path.Combine(_catalog.ActiveFolder, "commands.json"), "{}");
        profiles.NewName = "Night Owl";
        profiles.NewSource = profiles.Sources.Single(s => s.Id == "main");
        Assert.True(profiles.IsCopying);
        profiles.Parts.Single(p => p.Part == ProfileParts.Commands).IsChecked = false;
        profiles.CreateCommand.Execute(null);

        Assert.Contains("Night Owl", profiles.Status);
        Assert.Equal(["Main", "Night Owl"], profiles.Rows.Select(r => r.Name));
        Assert.Equal("Cuebot – Main", vm.WindowTitle);
        Assert.Contains(MainWindowViewModel.JumpProfilePrefix + "Night Owl", vm.JumpTargets);
        Assert.DoesNotContain(MainWindowViewModel.JumpProfilePrefix + "Main", vm.JumpTargets);
        Assert.Equal(["Main (open)", "Night Owl", "Manage profiles…"], vm.ProfileMenu.Select(m => m.Name));
        Assert.True(File.Exists(Path.Combine(_catalog.FolderOf("night-owl"), "actions.json")));   // copied
        Assert.False(File.Exists(Path.Combine(_catalog.FolderOf("night-owl"), "commands.json")));  // left out

        profiles.Selected = profiles.Rows.Single(r => r.Id == "night-owl");
        profiles.EditName = "Owl";
        profiles.SaveChangesCommand.Execute(null);
        Assert.Equal("Owl", _catalog.Find("night-owl")!.Name);

        Assert.True(vm.JumpTo("owl"));   // "Profile: Owl" is the only entry with "owl" in it
        Assert.Equal("night-owl", _shell.SwitchRequested);

        profiles.Selected = profiles.Rows.Single(r => r.Id == "night-owl");
        profiles.DeleteCommand.Execute(null);   // arms
        Assert.NotNull(_catalog.Find("night-owl"));
        profiles.DeleteCommand.Execute(null);
        Assert.Null(_catalog.Find("night-owl"));

        profiles.Selected = profiles.Rows.Single(r => r.Id == "main");
        Assert.False(profiles.CanSwitch);
        Assert.False(profiles.CanDelete);
    }

    [AvaloniaFact]
    public void The_profile_chip_shows_the_open_profile_and_manage_opens_the_profiles_tab()
    {
        var vm = new MainWindowViewModel(_host, _shell);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var chip = window.FindControl<Button>("ProfileChip")!;
        Assert.True(chip.IsVisible);
        vm.ProfileMenu.Last().Command.Execute(null);
        Assert.Same(vm.Settings, vm.SelectedPage);
        Assert.Equal("Profiles", vm.Settings.SubPageTitle);
        Dispatcher.UIThread.RunJobs();
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Without_profiles_the_chip_is_hidden()
    {
        var vm = new MainWindowViewModel(_host);
        Assert.False(vm.HasProfiles);
        Assert.False(vm.Settings.Profiles.IsAvailable);
        Assert.Equal("Cuebot", vm.WindowTitle);
    }
}
