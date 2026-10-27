using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Storage;

namespace Cuebot.App.Shell;

public enum AppTheme { Dark, Light, System }

/// <summary>App shell settings, stored as <c>app.json</c> in the data folder.</summary>
public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public BackupSettings Backups { get; set; } = new();
    public TextSettings Text { get; set; } = new();
    public WindowPlacement? Window { get; set; }
}

public sealed class GeneralSettings
{
    /// <summary>Show an icon in the system tray (StatusNotifierItem on Linux).</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>The close button hides the window to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Start hidden in the tray (same as <c>--minimized</c>).</summary>
    public bool StartMinimized { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.Dark;
}

/// <summary>How text templates format values (applied to the shared <c>TextEnvironment</c>).</summary>
public sealed class TextSettings
{
    /// <summary>
    /// Culture name for format specifiers and %date%/%time% (e.g. "de-DE"). Empty: culture-neutral with "$" as the
    /// currency symbol.
    /// </summary>
    public string Culture { get; set; } = "";
}

public sealed class BackupSettings
{
    public const int DefaultKeep = 10;

    /// <summary>Zip the data folder every time Cuebot starts.</summary>
    public bool OnStart { get; set; } = true;

    /// <summary>Zip the data folder before an import replaces data.</summary>
    public bool BeforeImport { get; set; } = true;

    /// <summary>How many backups to keep; older ones are deleted.</summary>
    public int Keep { get; set; } = DefaultKeep;
}

/// <summary>Saved main window bounds, in device-independent pixels (position in screen pixels).</summary>
public sealed record WindowPlacement
{
    public int? X { get; init; }
    public int? Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool Maximized { get; init; }
}

/// <summary>Loads and saves <c>app.json</c>. Saves are immediate and atomic; the file is small.</summary>
public sealed class AppSettingsStore
{
    public const string FileName = "app.json";
    private readonly object _gate = new();

    public AppSettingsStore(AppPaths paths)
    {
        Path = paths.File(FileName);
        Settings = Load(Path, out var error);
        LoadError = error;
    }

    public string Path { get; }

    public AppSettings Settings { get; }

    /// <summary>Set when app.json existed but could not be read (it is kept as app.json.corrupt-*).</summary>
    public string? LoadError { get; }

    public event Action? Saved;

    public void Save()
    {
        lock (_gate)
        {
            Settings.Backups.Keep = Math.Clamp(Settings.Backups.Keep, 1, 1000);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Json.Options));
            File.Move(temp, Path, overwrite: true);
        }
        Saved?.Invoke();
    }

    /// <summary>Saves, swallowing IO errors (returns false), for callers on shutdown paths.</summary>
    public bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AppSettings Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options) ?? new AppSettings();
            settings.General ??= new GeneralSettings();
            settings.Backups ??= new BackupSettings();
            settings.Text ??= new TextSettings();
            if (settings.Backups.Keep < 1) settings.Backups.Keep = BackupSettings.DefaultKeep;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            error = $"app.json was unreadable ({ex.Message}); using defaults";
            try { File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
    }
}
