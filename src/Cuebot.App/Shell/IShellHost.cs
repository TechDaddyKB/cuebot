using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;

namespace Cuebot.App.Shell;

/// <summary>What the Settings page needs from the running shell (window, tray, process).</summary>
public interface IShellHost
{
    /// <summary>A tray host is present, so hiding to the tray will not lose the window.</summary>
    bool IsTrayAvailable { get; }

    event Action? TrayAvailabilityChanged;

    /// <summary>Re-reads tray/close/theme settings after they changed.</summary>
    void ApplySettings();

    void OpenFolder(string path);

    /// <summary>Shuts down cleanly and starts again with the same arguments.</summary>
    void Restart();

    bool CanRestart { get; }
}

public static class Theme
{
    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    /// <summary>Switches the whole app at runtime; theme-aware brushes (DynamicResource) update in place.</summary>
    public static void Apply(Application? app, AppTheme theme)
    {
        if (app is not null) app.RequestedThemeVariant = ToVariant(theme);
    }
}

/// <summary>Opens a folder in the platform file manager.</summary>
public static class FolderLauncher
{
    public static void Open(string path)
    {
        Directory.CreateDirectory(path);
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe") { ArgumentList = { path } }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { path } }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { path } };   // also goes through the portal in Flatpak
        info.UseShellExecute = false;
        try { Process.Start(info)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>Used where there is no real shell (tests, designer): applies the theme, cannot restart.</summary>
public sealed class DetachedShellHost(AppSettingsStore settings) : IShellHost
{
    public bool IsTrayAvailable => false;

    public event Action? TrayAvailabilityChanged { add { } remove { } }

    public void ApplySettings() => Theme.Apply(Application.Current, settings.Settings.General.Theme);

    public void OpenFolder(string path) { }

    public void Restart() { }

    public bool CanRestart => false;
}
