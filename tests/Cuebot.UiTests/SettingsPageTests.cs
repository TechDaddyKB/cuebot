using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;

namespace Cuebot.UiTests;

/// <summary>The Settings page renders and binds to app.json; the theme switches at runtime.</summary>
public sealed class SettingsPageTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-settings-").FullName;
    private readonly AppHost _host;

    public SettingsPageTests()
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        Directory.CreateDirectory(ScreenshotDir);
    }

    private (Window Window, MainWindowViewModel ViewModel) Open()
    {
        var vm = new MainWindowViewModel(_host);
        vm.SelectedPage = vm.Settings;
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
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public async Task Every_settings_section_renders_without_binding_errors()
    {
        TestApp.BindingErrors.Clear();
        _host.Backups.Create("startup");
        var (window, vm) = Open();
        Assert.Contains(vm.Pages, p => p is SettingsViewModel);
        Assert.Single(vm.Settings.Backups);

        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Sections");
        var names = new[] { "general", "backups", "about" };
        for (var i = 0; i < names.Length; i++)
        {
            tabs.SelectedIndex = i;
            Save(window, $"settings-{names[i]}");
        }

        // Back up now adds a row; Restore asks for confirmation first.
        tabs.SelectedIndex = 1;
        await vm.Settings.BackUpNowCommand.ExecuteAsync(null);
        Pump();
        Assert.Equal(2, vm.Settings.Backups.Count);
        vm.Settings.SelectedBackup = vm.Settings.Backups[^1];
        vm.Settings.RestoreCommand.Execute(null);
        Assert.True(vm.Settings.IsConfirmingRestore);
        Save(window, "settings-restore-confirm");
        vm.Settings.ConfirmRestoreCommand.Execute("false");
        Assert.False(vm.Settings.IsConfirmingRestore);
        Assert.Equal(vm.Settings.Backups[^1].Info.Path, _host.Backups.PendingRestore);
        Assert.NotNull(vm.Settings.PendingRestore);
        vm.Settings.CancelPendingRestoreCommand.Execute(null);
        Pump();
        Assert.Null(vm.Settings.PendingRestore);

        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Changing_a_setting_writes_app_json()
    {
        var (window, vm) = Open();
        vm.Settings.CloseToTray = true;
        vm.Settings.StartMinimized = true;
        vm.Settings.BackupKeep = 3;

        var reloaded = new AppSettingsStore(new AppPaths(_dataDir)).Settings;
        Assert.True(reloaded.General.CloseToTray);
        Assert.True(reloaded.General.StartMinimized);
        Assert.Equal(3, reloaded.Backups.Keep);
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_switches_at_runtime_and_updates_brushes_in_place()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            var (window, vm) = Open();
            var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
            var darkCard = ((ISolidColorBrush)card.Background!).Color;

            vm.Settings.Theme = AppTheme.Light;
            Pump();
            Assert.Equal(ThemeVariant.Light, app.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            var lightCard = ((ISolidColorBrush)card.Background!).Color;
            Assert.NotEqual(darkCard, lightCard);
            Assert.True(lightCard.R > 200, $"light card should be light, was {lightCard}");
            Save(window, "settings-light");

            vm.Settings.Theme = AppTheme.Dark;
            Pump();
            Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
            Assert.Equal(darkCard, ((ISolidColorBrush)card.Background!).Color);

            vm.Settings.Theme = AppTheme.System;
            Pump();
            Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
            Assert.Equal(AppTheme.System, new AppSettingsStore(new AppPaths(_dataDir)).Settings.General.Theme);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [Theory]
    [InlineData(AppTheme.Dark, "Dark")]
    [InlineData(AppTheme.Light, "Light")]
    [InlineData(AppTheme.System, "Default")]
    public void Theme_maps_to_fluent_variants(AppTheme theme, string variant) =>
        Assert.Equal(variant, Theme.ToVariant(theme).Key.ToString());

    [AvaloniaFact]
    public void Every_cuebot_brush_exists_in_both_themes()
    {
        var app = Application.Current!;
        foreach (var key in ThemeBrushes.Keys)
        {
            // One shared brush (so StaticResource users follow the theme too) and a colour per variant.
            Assert.True(app.TryGetResource(key, null, out var brush), key);
            Assert.IsType<SolidColorBrush>(brush);
            Assert.True(app.TryGetResource(key + "Color", ThemeVariant.Dark, out var dark), key);
            Assert.True(app.TryGetResource(key + "Color", ThemeVariant.Light, out var light), key);
            Assert.IsType<Color>(dark);
            Assert.NotEqual(dark, light);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
