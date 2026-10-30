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

    /// <summary>The section list next to the icon rail is closed (rail only).</summary>
    public bool CollapseSectionList { get; set; }
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

    /// <summary>The optional Google Drive copy of every backup.</summary>
    public GoogleDriveBackupSettings GoogleDrive { get; set; } = new();
}

/// <summary>
/// Google Drive backups. The OAuth client is Cuebot's built-in one, or the user's own ("Desktop app" client in Google
/// Cloud) when entered under Advanced; Google treats a desktop client's secret as non-confidential. The refresh token is stored here like other modules store theirs.
/// </summary>
public sealed class GoogleDriveBackupSettings
{
    public const int DefaultKeep = 10;

    /// <summary>Upload every new backup (and, at start, the newest one if Drive does not have it yet).</summary>
    public bool Enabled { get; set; } = true;

    public string ClientId { get; set; } = "";
    [Secret]
    public string ClientSecret { get; set; } = "";
    [Secret]
    public string RefreshToken { get; set; } = "";

    /// <summary>The signed-in Google account, shown in the UI.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>The "Cuebot Backups" folder's id, found or created on the first upload.</summary>
    public string FolderId { get; set; } = "";

    /// <summary>How many backups to keep on Drive; older ones are deleted there.</summary>
    public int Keep { get; set; } = DefaultKeep;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSignedIn => RefreshToken.Length > 0;

    public void SignOut()
    {
        RefreshToken = "";
        AccountEmail = "";
        FolderId = "";
    }
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
        Settings = Load(Path, out var error, out var needsSave);
        LoadError = error;
        if (needsSave) TrySave();   // move secrets still stored in app.json into the secret store
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
            Settings.Backups.GoogleDrive.Keep = Math.Clamp(Settings.Backups.GoogleDrive.Keep, 1, 1000);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, SecretJson.Serialize(Settings, Path));
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

    private static AppSettings Load(string path, out string? error, out bool needsSave)
    {
        error = null;
        needsSave = false;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = SecretJson.Deserialize<AppSettings>(File.ReadAllText(path), path, out needsSave) ?? new AppSettings();
            settings.General ??= new GeneralSettings();
            settings.Backups ??= new BackupSettings();
            settings.Text ??= new TextSettings();
            if (settings.Backups.Keep < 1) settings.Backups.Keep = BackupSettings.DefaultKeep;
            settings.Backups.GoogleDrive ??= new GoogleDriveBackupSettings();
            if (settings.Backups.GoogleDrive.Keep < 1) settings.Backups.GoogleDrive.Keep = GoogleDriveBackupSettings.DefaultKeep;
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
