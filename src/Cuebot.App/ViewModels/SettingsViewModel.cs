using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.App.Shell.GoogleDrive;
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
    public string Size => FormatSize(Info.Size);

    public static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:0.#} KB",
        _ => $"{size / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>A backup in the Google Drive folder.</summary>
public sealed class RemoteBackupRowViewModel(RemoteBackup info)
{
    public RemoteBackup Info { get; } = info;
    public string Name => Info.Name;
    public string Created => Info.Created.ToString("yyyy-MM-dd HH:mm:ss");
    public string Reason => Info.Reason.Length == 0 ? "backup" : Info.Reason.Replace('-', ' ');
    public string Size => BackupRowViewModel.FormatSize(Info.Size);
}

/// <summary>Settings page: General (tray, close, start, theme), Backups and About, bound to app.json.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppHost _host;
    private readonly AppSettingsStore _store;
    private readonly BackupService _backups;
    private readonly GoogleDriveBackups _drive;
    private readonly IShellHost _shell;
    private readonly AppLog _log;
    private bool _loading;

    public SettingsViewModel(AppHost host, IShellHost shell) : base("Settings")
    {
        _host = host;
        _store = host.Settings;
        _backups = host.Backups;
        _drive = host.DriveBackups;
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
        _driveEnabled = s.Backups.GoogleDrive.Enabled;
        _driveKeep = s.Backups.GoogleDrive.Keep;
        _driveClientId = s.Backups.GoogleDrive.ClientId;
        _driveClientSecret = s.Backups.GoogleDrive.ClientSecret;
        _textCulture = CultureOptions.FirstOrDefault(c => string.Equals(c.Name, s.Text.Culture, StringComparison.OrdinalIgnoreCase))
                       ?? CultureOptions[0];
        _loading = false;

        _backups.Changed += () => OnUi(RefreshBackups);
        _drive.Changed += () => OnUi(RefreshDrive);
        shell.TrayAvailabilityChanged += () => OnUi(() => OnPropertyChanged(nameof(TrayStatus)));
        RefreshDrive();
        RefreshBackups();
        _ = RefreshAudio();
    }

    // ---- Audio (read-only for now) ----

    [ObservableProperty] private string _audioBackend = "Loading...";
    [ObservableProperty] private string _audioStatus = "";
    [ObservableProperty] private string _audioError = "";

    /// <summary>True once the backend and device lists have been read (or failed).</summary>
    [ObservableProperty] private bool _audioLoaded;

    public ObservableCollection<string> OutputDevices { get; } = [];
    public ObservableCollection<string> InputDevices { get; } = [];

    /// <summary>Opening the backend and listing devices can take a moment (native audio), so it runs off the UI thread.</summary>
    [RelayCommand]
    private async Task RefreshAudio()
    {
        AudioStatus = "Listing devices...";
        AudioError = "";
        try
        {
            var audio = _host.Get<Cuebot.Speech.AudioOutput>();
            var (backend, outputs, inputs) = await Task.Run(() => (audio.BackendName, audio.GetOutputDevices(), audio.GetInputDevices()));
            AudioBackend = backend;
            Fill(OutputDevices, outputs);
            Fill(InputDevices, inputs);
            AudioStatus = "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or IOException or ObjectDisposedException)
        {
            AudioBackend = "Unavailable";
            AudioStatus = "";
            AudioError = ex.Message;
        }
        AudioLoaded = true;

        static void Fill(ObservableCollection<string> target, IReadOnlyList<Cuebot.Speech.AudioDevice> devices)
        {
            target.Clear();
            foreach (var d in devices) target.Add(d.IsDefault ? $"{d.Name} (default)" : d.Name);
            if (target.Count == 0) target.Add("(no devices found)");
        }
    }

    // ---- Text ----

    /// <summary>"" (neutral, $) followed by every specific culture.</summary>
    public static IReadOnlyList<CultureOption> CultureOptions { get; } = BuildCultureOptions();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CulturePreview))]
    private CultureOption _textCulture;

    partial void OnTextCultureChanged(CultureOption value)
    {
        if (value is null) return;
        Update(s => s.Text.Culture = value.Name);
        if (!_loading) Shell.TextCulture.Apply(_host.Get<Cuebot.Core.Variables.TextEnvironment>(), value.Name);
    }

    /// <summary>What %amount:c2%, %n:N2% and %date% look like with the chosen culture.</summary>
    public string CulturePreview
    {
        get
        {
            var culture = Shell.TextCulture.Resolve(TextCulture?.Name);
            var now = DateTime.Now;
            return string.Create(culture, $"%amount:c2% → {1234.5m:C2}    %n:N2% → {1234.5m:N2}    %date% → {now.ToString(Cuebot.Core.Variables.BuiltInVariables.DateFormat, culture)}    %time% → {now.ToString(Cuebot.Core.Variables.BuiltInVariables.TimeFormat, culture)}");
        }
    }

    private static IReadOnlyList<CultureOption> BuildCultureOptions()
    {
        var list = new List<CultureOption> { new("", "Neutral (invariant, $ currency) — default") };
        list.AddRange(System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.SpecificCultures)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CultureOption(c.Name, $"{c.Name} — {c.EnglishName}")));
        return list;
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

    /// <summary>What the restore confirmation names: the local or the Google Drive backup.</summary>
    [ObservableProperty] private string _restoreTargetName = "";

    private bool _restoreFromDrive;

    /// <summary>Asks for confirmation; nothing changes until the user confirms.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        _restoreFromDrive = false;
        RestoreTargetName = SelectedBackupName;
        IsConfirmingRestore = true;
    }

    [RelayCommand]
    private void CancelRestore() => IsConfirmingRestore = false;

    /// <summary>
    /// Schedules the restore (a safety backup of the current data is made before it is applied) and restarts. A Drive
    /// backup is downloaded first.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRestore(string? restartNow)
    {
        IsConfirmingRestore = false;
        if (_restoreFromDrive)
        {
            if (SelectedRemoteBackup is not { } remote) return;
            IsDriveBusy = true;
            try
            {
                if (!await _drive.ScheduleRestoreAsync(remote.Info)) return;
            }
            finally
            {
                IsDriveBusy = false;
            }
        }
        else
        {
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

    // ---- Google Drive ----

    public ObservableCollection<RemoteBackupRowViewModel> RemoteBackups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DriveRestoreCommand))]
    private RemoteBackupRowViewModel? _selectedRemoteBackup;

    [ObservableProperty] private bool _driveEnabled;
    [ObservableProperty] private decimal? _driveKeep;
    [ObservableProperty] private string _driveClientId = "";
    [ObservableProperty] private string _driveClientSecret = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DriveRefreshCommand), nameof(DriveUploadCommand), nameof(DriveRestoreCommand), nameof(DriveSignInCommand))]
    private bool _isDriveBusy;

    [ObservableProperty] private bool _driveSignedIn;
    [ObservableProperty] private string _driveAccount = "";
    [ObservableProperty] private string _driveStatus = "";
    [ObservableProperty] private string _driveError = "";

    public string DriveSummary => !DriveSignedIn ? "Not signed in."
        : RemoteBackups.Count == 0 ? "No backups on Google Drive yet (or not listed yet)."
        : $"{RemoteBackups.Count} backup(s) in \"{GoogleDriveBackups.FolderName}\", newest first.";

    partial void OnDriveEnabledChanged(bool value) => Update(s => s.Backups.GoogleDrive.Enabled = value);

    partial void OnDriveKeepChanged(decimal? value) =>
        Update(s => s.Backups.GoogleDrive.Keep = (int)Math.Clamp(value ?? GoogleDriveBackupSettings.DefaultKeep, 1, 1000));

    partial void OnDriveClientIdChanged(string value)
    {
        Update(s => s.Backups.GoogleDrive.ClientId = value.Trim());
        DriveSignInCommand.NotifyCanExecuteChanged();
    }

    partial void OnDriveClientSecretChanged(string value) => Update(s => s.Backups.GoogleDrive.ClientSecret = value.Trim());

    private void RefreshDrive()
    {
        DriveSignedIn = _drive.IsSignedIn;
        DriveAccount = _drive.Settings.AccountEmail.Length > 0 ? $"Signed in as {_drive.Settings.AccountEmail}" : DriveSignedIn ? "Signed in" : "";
        DriveStatus = _drive.Status;
        DriveError = _drive.Error;
        var selected = SelectedRemoteBackup?.Info.Id;
        RemoteBackups.Clear();
        foreach (var b in _drive.Remote) RemoteBackups.Add(new RemoteBackupRowViewModel(b));
        SelectedRemoteBackup = RemoteBackups.FirstOrDefault(b => b.Info.Id == selected);
        OnPropertyChanged(nameof(DriveSummary));
        DriveSignInCommand.NotifyCanExecuteChanged();
        DriveSignOutCommand.NotifyCanExecuteChanged();
        DriveRefreshCommand.NotifyCanExecuteChanged();
        DriveUploadCommand.NotifyCanExecuteChanged();
    }

    private bool CanDriveSignIn() => !IsDriveBusy && DriveClientId.Trim().Length > 0;

    /// <summary>Opens Google's consent page in the browser and waits for the loopback redirect (cancellable).</summary>
    [RelayCommand(CanExecute = nameof(CanDriveSignIn), IncludeCancelCommand = true)]
    private async Task DriveSignIn(CancellationToken ct)
    {
        IsDriveBusy = true;
        try
        {
            await _drive.SignInAsync(ct);
            await _drive.RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or IOException or OperationCanceledException
                                       or System.Net.Sockets.SocketException)
        {
            // The service logged it and set its Error/Status.
        }
        finally
        {
            IsDriveBusy = false;
            RefreshDrive();
        }
    }

    private bool CanDriveSignOut() => DriveSignedIn;

    [RelayCommand(CanExecute = nameof(CanDriveSignOut))]
    private async Task DriveSignOut()
    {
        await _drive.SignOutAsync();
        RefreshDrive();
    }

    private bool CanUseDrive() => DriveSignedIn && !IsDriveBusy;

    [RelayCommand(CanExecute = nameof(CanUseDrive))]
    private async Task DriveRefresh()
    {
        IsDriveBusy = true;
        try { await _drive.RefreshAsync(); }
        finally { IsDriveBusy = false; RefreshDrive(); }
    }

    /// <summary>Uploads the selected local backup (or the newest one when none is selected).</summary>
    [RelayCommand(CanExecute = nameof(CanUseDrive))]
    private async Task DriveUpload()
    {
        var backup = SelectedBackup?.Info ?? _backups.List().FirstOrDefault();
        if (backup is null)
        {
            BackupStatus = "Back up first; there is nothing to upload yet.";
            return;
        }
        IsDriveBusy = true;
        try { await _drive.UploadAsync(backup); }
        finally { IsDriveBusy = false; RefreshDrive(); }
    }

    private bool CanDriveRestore() => SelectedRemoteBackup is not null && !IsDriveBusy;

    /// <summary>Asks for confirmation; the backup is downloaded only once the user confirms.</summary>
    [RelayCommand(CanExecute = nameof(CanDriveRestore))]
    private void DriveRestore()
    {
        if (SelectedRemoteBackup is not { } remote) return;
        _restoreFromDrive = true;
        RestoreTargetName = $"{remote.Name} (Google Drive)";
        IsConfirmingRestore = true;
    }

    // ---- About ----

    public string Version => BackupService.Version;
    public string DataFolder { get; }
    public string DisplayBackend => Program.BackendName;
    public string Runtime => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})";
    public string OsDescription => RuntimeInformation.OSDescription;
}

/// <summary>A text culture choice: its name ("" = default) and how it is shown.</summary>
public sealed record CultureOption(string Name, string Display);
