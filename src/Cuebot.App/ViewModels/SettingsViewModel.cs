using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;

namespace Cuebot.App.ViewModels;

public sealed class BackupRowViewModel(BackupInfo info)
{
    public BackupInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Created => Info.Created.ToString("yyyy-MM-dd HH:mm:ss");
    public string Reason => Info.Reason.Length == 0 ? "backup" : Info.Reason.Replace('-', ' ');
    public string Size => Info.Size switch
    {
        < 1024 => $"{Info.Size} B",
        < 1024 * 1024 => $"{Info.Size / 1024.0:0.#} KB",
        _ => $"{Info.Size / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>Settings page: General (tray, close, start, theme), Backups and About, bound to app.json.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppHost _host;
    private readonly AppSettingsStore _store;
    private readonly BackupService _backups;
    private readonly IShellHost _shell;
    private readonly AppLog _log;
    private bool _loading;

    public SettingsViewModel(AppHost host, IShellHost shell) : base("Settings")
    {
        _host = host;
        _store = host.Settings;
        _backups = host.Backups;
        _shell = shell;
        _log = host.Get<AppLog>();
        DataFolder = host.Get<AppPaths>().Root;

        _loading = true;
        var s = _store.Settings;
        _showTrayIcon = s.General.ShowTrayIcon;
        _closeToTray = s.General.CloseToTray;
        _startMinimized = s.General.StartMinimized;
        _theme = s.General.Theme;
        _backupOnStart = s.Backups.OnStart;
        _backupBeforeImport = s.Backups.BeforeImport;
        _backupKeep = s.Backups.Keep;
        _loading = false;

        _backups.Changed += () => OnUi(RefreshBackups);
        shell.TrayAvailabilityChanged += () => OnUi(() => OnPropertyChanged(nameof(TrayStatus)));
        RefreshBackups();
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    [ObservableProperty] private bool _showTrayIcon;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private bool _backupOnStart;
    [ObservableProperty] private bool _backupBeforeImport;
    [ObservableProperty] private decimal? _backupKeep;

    public string TrayStatus => _shell.IsTrayAvailable
        ? "A system tray is available."
        : "No system tray was found (some Wayland desktops have none): the window minimizes instead of hiding, so it is never lost.";

    partial void OnShowTrayIconChanged(bool value) => Update(s => s.General.ShowTrayIcon = value);
    partial void OnCloseToTrayChanged(bool value) => Update(s => s.General.CloseToTray = value);
    partial void OnStartMinimizedChanged(bool value) => Update(s => s.General.StartMinimized = value);
    partial void OnThemeChanged(AppTheme value) => Update(s => s.General.Theme = value);
    partial void OnBackupOnStartChanged(bool value) => Update(s => s.Backups.OnStart = value);
    partial void OnBackupBeforeImportChanged(bool value) => Update(s => s.Backups.BeforeImport = value);

    partial void OnBackupKeepChanged(decimal? value)
    {
        Update(s => s.Backups.Keep = (int)Math.Clamp(value ?? BackupSettings.DefaultKeep, 1, 1000));
        if (!_loading) Task.Run(_backups.Prune).ContinueWith(_ => OnUi(RefreshBackups), TaskScheduler.Default);
    }

    private void Update(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_store.Settings);
        try
        {
            _store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Settings", "Could not save app.json", ex);
        }
        _shell.ApplySettings();
    }

    // ---- Backups ----

    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedBackupName))]
    private BackupRowViewModel? _selectedBackup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand))]
    private bool _isBusy;

    public string SelectedBackupName => SelectedBackup?.Name ?? "";

    [ObservableProperty] private string _backupStatus = "";
    [ObservableProperty] private string? _pendingRestore;
    [ObservableProperty] private bool _isConfirmingRestore;

    public string BackupFolder => _backups.Folder;

    public bool CanRestart => _shell.CanRestart;

    public string BackupsSummary => Backups.Count == 0 ? "No backups yet." : $"{Backups.Count} backup(s), newest first.";

    private void RefreshBackups()
    {
        var selected = SelectedBackup?.Info.Path;
        Backups.Clear();
        foreach (var b in _backups.List()) Backups.Add(new BackupRowViewModel(b));
        SelectedBackup = Backups.FirstOrDefault(b => b.Info.Path == selected);
        var pending = _backups.PendingRestore;
        PendingRestore = pending is null ? null : $"{Path.GetFileName(pending)} will be restored the next time Cuebot starts.";
        OnPropertyChanged(nameof(BackupsSummary));
    }

    private bool CanBackUp() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private async Task BackUpNow()
    {
        IsBusy = true;
        BackupStatus = "Backing up...";
        try
        {
            var info = await Task.Run(() =>
            {
                _host.Get<ProfileService>().Flush();
                return _backups.Create("manual");
            });
            BackupStatus = $"Saved {info.Name}.";
            _log.Info("Backups", $"Backup created: {info.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = $"Backup failed: {ex.Message}";
            _log.Error("Backups", "Backup failed", ex);
        }
        finally
        {
            IsBusy = false;
            RefreshBackups();
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(_backups.Folder);
        _shell.OpenFolder(_backups.Folder);
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(DataFolder);

    private bool CanRestore() => SelectedBackup is not null;

    /// <summary>Asks for confirmation; nothing changes until the user confirms.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore() => IsConfirmingRestore = true;

    [RelayCommand]
    private void CancelRestore() => IsConfirmingRestore = false;

    /// <summary>Schedules the restore (a safety backup of the current data is made before it is applied) and restarts.</summary>
    [RelayCommand]
    private void ConfirmRestore(string? restartNow)
    {
        IsConfirmingRestore = false;
        if (SelectedBackup is not { } backup) return;
        try
        {
            _backups.ScheduleRestore(backup.Info.Path);
            _log.Info("Backups", $"Restore of {backup.Name} scheduled; the current data is backed up first");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = $"Cannot restore: {ex.Message}";
            return;
        }
        RefreshBackups();
        if (string.Equals(restartNow, "true", StringComparison.OrdinalIgnoreCase) && _shell.CanRestart) _shell.Restart();
        else BackupStatus = "Restart Cuebot to finish the restore.";
    }

    [RelayCommand]
    private void CancelPendingRestore()
    {
        _backups.CancelRestore();
        BackupStatus = "Restore cancelled.";
    }

    // ---- About ----

    public string Version => BackupService.Version;
    public string DataFolder { get; }
    public string DisplayBackend => Program.BackendName;
    public string Runtime => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})";
    public string OsDescription => RuntimeInformation.OSDescription;
}
