using Cuebot.App.Shell;
using Cuebot.Core;

namespace Cuebot.UiTests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-settings-").FullName;

    [Fact]
    public void Defaults_when_app_json_is_missing()
    {
        var s = new AppSettingsStore(new AppPaths(_root)).Settings;
        Assert.True(s.General.ShowTrayIcon);
        Assert.False(s.General.CloseToTray);
        Assert.False(s.General.StartMinimized);
        Assert.Equal(AppTheme.Dark, s.General.Theme);
        Assert.True(s.Backups.OnStart);
        Assert.Equal(10, s.Backups.Keep);
        Assert.Null(s.Window);
    }

    [Fact]
    public void Saves_and_reloads_with_readable_enum_names()
    {
        var store = new AppSettingsStore(new AppPaths(_root));
        store.Settings.General.Theme = AppTheme.Light;
        store.Settings.General.CloseToTray = true;
        store.Settings.Backups.Keep = 4;
        store.Save();

        Assert.Contains("\"Light\"", File.ReadAllText(Path.Combine(_root, AppSettingsStore.FileName)));
        var reloaded = new AppSettingsStore(new AppPaths(_root)).Settings;
        Assert.Equal(AppTheme.Light, reloaded.General.Theme);
        Assert.True(reloaded.General.CloseToTray);
        Assert.Equal(4, reloaded.Backups.Keep);
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_defaults_are_used()
    {
        File.WriteAllText(Path.Combine(_root, AppSettingsStore.FileName), "{ not json");
        var store = new AppSettingsStore(new AppPaths(_root));
        Assert.NotNull(store.LoadError);
        Assert.Equal(AppTheme.Dark, store.Settings.General.Theme);
        Assert.Single(Directory.GetFiles(_root, "app.json.corrupt-*"));
    }

    [Fact]
    public void Partial_file_fills_in_missing_sections()
    {
        File.WriteAllText(Path.Combine(_root, AppSettingsStore.FileName), """{ "general": { "theme": "System" } }""");
        var s = new AppSettingsStore(new AppPaths(_root)).Settings;
        Assert.Equal(AppTheme.System, s.General.Theme);
        Assert.True(s.General.ShowTrayIcon);
        Assert.Equal(10, s.Backups.Keep);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
