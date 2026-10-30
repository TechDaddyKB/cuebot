using Cuebot.Core.Auth;
using Cuebot.Core.Logging;

namespace Cuebot.App.Shell.GoogleDrive;

/// <summary>
/// Copies backups to a "Cuebot Backups" folder in the user's Google Drive (replacing Streamer.bot's cloud backups,
/// which need a Streamer.bot account): signs in with the user's own "Desktop app" OAuth client (loopback + PKCE,
/// <c>drive.file</c> scope only), uploads every new backup when enabled, keeps the newest N there, lists them and
/// downloads one for the normal restore path (<see cref="BackupService.ScheduleRestore"/>).
/// </summary>
public sealed class GoogleDriveBackups : IDisposable
{
    public const string FolderName = "Cuebot Backups";
    private const string Source = "Backups";

    private readonly AppSettingsStore _store;
    private readonly BackupService _backups;
    private readonly GoogleDriveClient _client;
    private readonly AppLog _log;
    private readonly AppCredentials _builtInApp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlyList<RemoteBackup> _remote = [];

    public GoogleDriveBackups(AppSettingsStore store, BackupService backups, GoogleDriveClient client, AppLog log,
        AppCredentials? builtInApp = null)
    {
        _store = store;
        _builtInApp = builtInApp ?? BuiltInApps.Google;
        _backups = backups;
        _client = client;
        _log = log;
        _client.Credentials = () => (App.ClientId, App.ClientSecret, Settings.RefreshToken);
        _backups.Created += OnBackupCreated;
    }

    public GoogleDriveBackupSettings Settings => _store.Settings.Backups.GoogleDrive;

    /// <summary>The Google client signed in with: the user's own (Advanced) or the one built into Cuebot.</summary>
    public AppCredentials App => AppCredentials.Choose(Settings.ClientId, Settings.ClientSecret, _builtInApp);

    /// <summary>True when this build has a Google client of its own.</summary>
    public bool HasBuiltInApp => _builtInApp.IsSet;

    public bool IsConfigured => App.IsSet;

    public bool IsSignedIn => Settings.IsSignedIn;

    /// <summary>Opens Google's consent page; the app opens the system browser, tests answer the redirect themselves.</summary>
    public Action<Uri> OpenBrowser { get; set; } = uri => UrlLauncher.Open(uri.AbsoluteUri);

    /// <summary>What the last operation did, for the backups page.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The last failure, cleared by the next success.</summary>
    public string Error { get; private set; } = "";

    public DateTime? LastUpload { get; private set; }

    /// <summary>The Drive folder's backups as of the last list, upload or prune, newest first.</summary>
    public IReadOnlyList<RemoteBackup> Remote => _remote;

    /// <summary>Raised from any thread when status, sign-in or the remote list changed.</summary>
    public event Action? Changed;

    /// <summary>At start: when enabled and signed in, uploads the newest local backup unless Drive already has it.</summary>
    public async Task StartAsync()
    {
        if (!Settings.Enabled || !IsSignedIn || !IsConfigured) return;
        var newest = _backups.List().FirstOrDefault();
        await Run("Checking Google Drive...", async ct =>
        {
            var folder = await FolderAsync(ct).ConfigureAwait(false);
            var remote = await _client.ListAsync(folder, ct).ConfigureAwait(false);
            SetRemote(remote);
            if (newest is null || remote.Any(r => string.Equals(r.Name, newest.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Status = $"{remote.Count} backup(s) on Google Drive.";
                return;
            }
            await UploadCoreAsync(folder, newest, ct).ConfigureAwait(false);
        }, _shutdown.Token).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- sign-in

    /// <summary>Browser sign-in: listens on a loopback port, opens Google's consent page, exchanges the code (PKCE).</summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new GoogleDriveException("This build has no Google app of its own; enter the OAuth client ID of your Google Cloud \"Desktop app\" client under Advanced.");
        var app = App;
        using var receiver = new LoopbackReceiver("Google Drive");
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        SetStatus("Waiting for the Google sign-in in your browser...");
        OpenBrowser(_client.BuildAuthorizeUri(app.ClientId, receiver.RedirectUri, Pkce.Challenge(verifier), state));
        try
        {
            var result = await receiver.WaitForRedirectAsync(ct).ConfigureAwait(false);
            if (result.TryGetValue("error", out var error))
                throw new GoogleDriveException(error == "access_denied" ? "Sign-in was cancelled in the browser." : $"Google sign-in failed: {error}");
            if (result.GetValueOrDefault("state") != state)
                throw new GoogleDriveException("The sign-in response did not match this request. Try again.");
            var tokens = await _client.ExchangeCodeAsync(app.ClientId, app.ClientSecret,
                result.GetValueOrDefault("code") ?? "", verifier, receiver.RedirectUri, ct).ConfigureAwait(false);
            Settings.RefreshToken = tokens.RefreshToken;
            Settings.FolderId = "";
            Save();
            Settings.AccountEmail = await _client.GetAccountEmailAsync(ct).ConfigureAwait(false);
            Save();
            _log.Info(Source, $"Signed in to Google Drive{(Settings.AccountEmail.Length > 0 ? " as " + Settings.AccountEmail : "")}");
            Error = "";
            SetStatus("Signed in. New backups are copied to Google Drive.");
        }
        catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            Fail("Google Drive sign-in failed", ex);
            throw;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Sign-in cancelled.");
            throw;
        }
    }

    /// <summary>Forgets the sign-in and revokes it at Google (best effort). Backups already on Drive stay there.</summary>
    public async Task SignOutAsync()
    {
        var token = Settings.RefreshToken;
        Settings.SignOut();
        Save();
        SetRemote([]);
        Error = "";
        SetStatus("Signed out of Google Drive.");
        _log.Info(Source, "Signed out of Google Drive");
        if (token.Length > 0) await _client.RevokeAsync(token, CancellationToken.None).ConfigureAwait(false);
        else _client.Forget();
    }

    // ---------------------------------------------------------------- backups

    /// <summary>Re-reads the Drive folder.</summary>
    public Task<bool> RefreshAsync(CancellationToken ct = default) =>
        Run("Listing Google Drive backups...", async token =>
        {
            var remote = await _client.ListAsync(await FolderAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            SetRemote(remote);
            Status = $"{remote.Count} backup(s) on Google Drive.";
        }, ct);

    /// <summary>Uploads one local backup, then deletes the oldest ones on Drive beyond the configured count.</summary>
    public Task<bool> UploadAsync(BackupInfo backup, CancellationToken ct = default) =>
        Run($"Uploading {backup.Name}...", async token =>
            await UploadCoreAsync(await FolderAsync(token).ConfigureAwait(false), backup, token).ConfigureAwait(false), ct);

    /// <summary>
    /// Downloads <paramref name="remote"/> into <see cref="BackupService.DownloadsFolder"/> and schedules it like a
    /// local restore (applied at the next start, after a safety backup). Returns false when it failed (see <see cref="Error"/>).
    /// </summary>
    public Task<bool> ScheduleRestoreAsync(RemoteBackup remote, CancellationToken ct = default) =>
        Run($"Downloading {remote.Name}...", async token =>
        {
            var name = Path.GetFileName(remote.Name);
            if (name.Length == 0 || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = $"drive-{remote.Id}.zip";
            var target = Path.Combine(_backups.DownloadsFolder, name);
            await _client.DownloadAsync(remote.Id, target, token).ConfigureAwait(false);
            try
            {
                _backups.ScheduleRestore(target);
            }
            catch (InvalidDataException)
            {
                File.Delete(target);
                throw new GoogleDriveException($"{remote.Name} is not a valid backup zip.");
            }
            _log.Info(Source, $"Restore of {remote.Name} from Google Drive scheduled; the current data is backed up first");
            Status = $"{remote.Name} was downloaded and will be restored the next time Cuebot starts.";
        }, ct);

    private void OnBackupCreated(BackupInfo backup)
    {
        if (!Settings.Enabled || !IsSignedIn || !IsConfigured) return;
        _ = Task.Run(() => UploadAsync(backup, _shutdown.Token));
    }

    private async Task UploadCoreAsync(string folder, BackupInfo backup, CancellationToken ct)
    {
        Status = $"Uploading {backup.Name}...";
        Changed?.Invoke();
        await _client.UploadAsync(folder, backup.Path, ct).ConfigureAwait(false);
        LastUpload = DateTime.Now;
        _log.Info(Source, $"Uploaded {backup.Name} to Google Drive");

        var remote = await _client.ListAsync(folder, ct).ConfigureAwait(false);
        var keep = Math.Max(1, Settings.Keep);
        var removed = 0;
        foreach (var old in remote.Where(r => BackupService.TryParseName(r.Name, out _, out _)).Skip(keep))
        {
            await _client.DeleteAsync(old.Id, ct).ConfigureAwait(false);
            removed++;
        }
        if (removed > 0)
        {
            _log.Info(Source, $"Deleted {removed} old backup(s) from Google Drive (keeping {keep})");
            remote = await _client.ListAsync(folder, ct).ConfigureAwait(false);
        }
        SetRemote(remote);
        Status = $"Uploaded {backup.Name}. {remote.Count} backup(s) on Google Drive.";
    }

    private async Task<string> FolderAsync(CancellationToken ct)
    {
        var id = await _client.EnsureFolderAsync(FolderName, Settings.FolderId, ct).ConfigureAwait(false);
        if (id != Settings.FolderId)
        {
            Settings.FolderId = id;
            Save();
        }
        return id;
    }

    /// <summary>One Drive operation at a time; failures become <see cref="Error"/> (and sign the user out when Google says so).</summary>
    private async Task<bool> Run(string busy, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsSignedIn) throw new GoogleDriveException("Sign in to Google Drive first.", 401, "signInRequired", signInRequired: true);
            SetStatus(busy);
            await work(ct).ConfigureAwait(false);
            Error = "";
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or IOException or UnauthorizedAccessException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            if (ex is GoogleDriveException { SignInRequired: true } && IsSignedIn)
            {
                Settings.SignOut();
                Save();
                _client.Forget();
                SetRemote([]);
            }
            Fail("Google Drive backup failed", ex);
            return false;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Fail(string what, Exception ex)
    {
        var message = ex is OperationCanceledException ? "Google Drive did not answer in time." : ex.Message;
        Error = ex is GoogleDriveException { SignInRequired: true } && !IsSignedIn
            ? $"{message} Sign in to Google Drive again."
            : message;
        Status = "";
        _log.Warn(Source, $"{what}: {message}");
        Changed?.Invoke();
    }

    private void SetRemote(IReadOnlyList<RemoteBackup> remote) => _remote = remote;

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            _store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Source, "Could not save app.json", ex);
        }
    }

    public void Dispose()
    {
        _backups.Created -= OnBackupCreated;
        _shutdown.Cancel();   // not disposed: an upload still running may read the token or release the gate
    }
}
