using System.IO.Compression;
using System.Net;
using Cuebot.App.Shell;
using Cuebot.App.Shell.GoogleDrive;
using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.UiTests;

/// <summary>Google Drive backups against <see cref="FakeGoogleDrive"/>: sign-in, folder, upload, retention, list, restore, refresh, errors.</summary>
public sealed class GoogleDriveBackupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-drive-").FullName;
    private readonly AppPaths _paths;
    private readonly ManualTime _time = new();
    private readonly FakeGoogleDrive _drive;
    private readonly AppSettingsStore _store;
    private readonly BackupService _backups;
    private readonly GoogleDriveBackups _service;
    private DateTime _now = new(2026, 10, 1, 12, 0, 0);

    public GoogleDriveBackupTests()
    {
        _paths = new AppPaths(_root);
        File.WriteAllText(Path.Combine(_root, "actions.json"), "{\"v\":1}");
        _drive = new FakeGoogleDrive(_time);
        _store = new AppSettingsStore(_paths);
        _store.Settings.Backups.GoogleDrive.ClientId = FakeGoogleDrive.ClientId;
        _store.Settings.Backups.GoogleDrive.ClientSecret = FakeGoogleDrive.ClientSecret;
        _backups = new BackupService(_paths, () => 50, () => _now);
        _service = new GoogleDriveBackups(_store, _backups,
            new GoogleDriveClient(new HttpClient(_drive), FakeGoogleDrive.Endpoints, _time), new AppLog());
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GoogleDriveBackupSettings S => _store.Settings.Backups.GoogleDrive;

    /// <summary>Starts signed in, the way a later run of Cuebot finds app.json.</summary>
    private void SignedIn()
    {
        S.RefreshToken = _drive.RefreshToken;
        S.Enabled = false;   // tests upload explicitly unless they test the automatic upload
    }

    private BackupInfo Backup(string reason = "manual")
    {
        var info = _backups.Create(reason);
        _now = _now.AddMinutes(1);
        return info;
    }

    /// <summary>Plays the browser: checks the consent URL, then follows Google's redirect to the loopback receiver.</summary>
    private void AnswerConsent(Uri consent, Func<string, string>? redirectQuery = null)
    {
        var q = Query(consent);
        Assert.Equal("accounts.test", consent.Host);
        Assert.Equal(FakeGoogleDrive.ClientId, q["client_id"]);
        Assert.Equal(GoogleDriveClient.Scope, q["scope"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.StartsWith("http://127.0.0.1:", q["redirect_uri"]);
        _drive.IssuedChallenge = q["code_challenge"];
        var query = redirectQuery?.Invoke(q["state"])
                    ?? $"state={Uri.EscapeDataString(q["state"])}&code={Uri.EscapeDataString(_drive.AuthorizationCode)}&scope={Uri.EscapeDataString(GoogleDriveClient.Scope)}";
        _ = Task.Run(async () =>
        {
            using var browser = new HttpClient();
            await browser.GetStringAsync(q["redirect_uri"] + "?" + query, Ct);
        });
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Sign_in_exchanges_the_code_with_pkce_and_stores_the_refresh_token()
    {
        _service.OpenBrowser = uri => AnswerConsent(uri);

        await _service.SignInAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.True(_service.IsSignedIn);
        Assert.Equal(_drive.RefreshToken, S.RefreshToken);
        Assert.Equal("tester@example.com", S.AccountEmail);
        Assert.Contains("POST /token", _drive.Calls);
        var reloaded = new AppSettingsStore(_paths).Settings.Backups.GoogleDrive;
        Assert.Equal(_drive.RefreshToken, reloaded.RefreshToken);
        Assert.Equal("", _service.Error);
    }

    [Fact]
    public async Task Sign_in_reports_a_cancelled_consent_and_a_state_mismatch()
    {
        _service.OpenBrowser = uri => AnswerConsent(uri, state => $"error=access_denied&state={Uri.EscapeDataString(state)}");
        var denied = await Assert.ThrowsAsync<GoogleDriveException>(() => _service.SignInAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct));
        Assert.Contains("cancelled", denied.Message);
        Assert.False(_service.IsSignedIn);
        Assert.Contains("cancelled", _service.Error);

        _service.OpenBrowser = uri => AnswerConsent(uri, _ => "state=forged&code=4/0Xyz");
        var forged = await Assert.ThrowsAsync<GoogleDriveException>(() => _service.SignInAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct));
        Assert.Contains("did not match", forged.Message);
        Assert.DoesNotContain("POST /token", _drive.Calls);
    }

    [Fact]
    public async Task Sign_in_needs_a_client_id()
    {
        S.ClientId = " ";
        var error = await Assert.ThrowsAsync<GoogleDriveException>(() => _service.SignInAsync(Ct));
        Assert.Contains("client ID", error.Message);
    }

    [Fact]
    public async Task Upload_creates_the_folder_once_and_puts_the_zip_in_it()
    {
        SignedIn();
        var first = Backup();
        Assert.True(await _service.UploadAsync(first, Ct), _service.Error);
        var second = Backup();
        Assert.True(await _service.UploadAsync(second, Ct), _service.Error);

        var folder = Assert.Single(_drive.Files, f => f.MimeType == GoogleDriveClient.FolderMimeType);
        Assert.Equal(GoogleDriveBackups.FolderName, folder.Name);
        Assert.Equal(folder.Id, S.FolderId);
        Assert.Equal([first.Name, second.Name], _drive.ZipsInFolder.Select(f => f.Name));
        Assert.Equal(File.ReadAllBytes(second.Path), _drive.ZipsInFolder[1].Content);
        Assert.Single(_drive.Calls, c => c == "POST /drive/v3/files");   // the folder was created once
        Assert.Equal(1, _drive.TokenRefreshes);   // one access token for both uploads
        Assert.Equal([second.Name, first.Name], _service.Remote.Select(r => r.Name));
        Assert.Equal(new FileInfo(second.Path).Length, _service.Remote[0].Size);
        Assert.Equal("manual", _service.Remote[0].Reason);
    }

    [Fact]
    public async Task An_existing_folder_is_found_and_a_deleted_one_is_recreated()
    {
        SignedIn();
        var existing = _drive.Seed("cuebot-20260901-100000-startup-v0.1.0.zip", [1, 2, 3]);
        Assert.True(await _service.RefreshAsync(Ct), _service.Error);
        Assert.Equal(existing.Parents[0], S.FolderId);
        Assert.Equal("cuebot-20260901-100000-startup-v0.1.0.zip", Assert.Single(_service.Remote).Name);

        _drive.Folder!.Trashed = true;
        Assert.True(await _service.UploadAsync(Backup(), Ct), _service.Error);
        Assert.NotEqual(existing.Parents[0], S.FolderId);
        Assert.Single(_service.Remote);
    }

    [Fact]
    public async Task Keeps_only_the_newest_n_backups_on_drive_and_leaves_other_files_alone()
    {
        SignedIn();
        S.Keep = 2;
        var folder = _drive.Seed("cuebot-20260101-000000-startup-v0.1.0.zip", [1]).Parents[0];
        _drive.Seed("my notes.zip", [9], folder);
        var backups = Enumerable.Range(0, 3).Select(_ => Backup()).ToArray();
        foreach (var b in backups) Assert.True(await _service.UploadAsync(b, Ct), _service.Error);

        Assert.Equal([backups[1].Name, backups[2].Name, "my notes.zip"], _drive.ZipsInFolder.Select(f => f.Name));
        Assert.Equal(3, _service.Remote.Count);
    }

    [Fact]
    public async Task Listing_follows_next_page_tokens()
    {
        SignedIn();
        var folder = _drive.Seed("cuebot-20260101-000000-a-v0.1.0.zip", [1]).Parents[0];
        for (var i = 1; i <= 4; i++) _drive.Seed($"cuebot-2026010{i}-000000-b-v0.1.0.zip", [1], folder);

        Assert.True(await _service.RefreshAsync(Ct), _service.Error);

        Assert.Equal(5, _service.Remote.Count);
        Assert.Equal("cuebot-20260104-000000-b-v0.1.0.zip", _service.Remote[0].Name);   // newest by name first
        Assert.Equal(new DateTime(2026, 1, 4), _service.Remote[0].Created);
    }

    [Fact]
    public async Task Restore_downloads_into_the_downloads_folder_and_schedules_the_normal_restore()
    {
        SignedIn();
        var original = Backup();
        Assert.True(await _service.UploadAsync(original, Ct), _service.Error);
        var remote = _service.Remote[0];
        File.Delete(original.Path);
        File.WriteAllText(Path.Combine(_root, "actions.json"), "{\"v\":2}");

        Assert.True(await _service.ScheduleRestoreAsync(remote, Ct), _service.Error);

        var downloaded = Path.Combine(_backups.DownloadsFolder, remote.Name);
        Assert.Equal(Path.GetFullPath(downloaded), _backups.PendingRestore);
        Assert.Empty(_backups.List());   // downloads are not listed (so neither pruned nor re-uploaded)
        _backups.ApplyPendingRestore();
        Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(_root, "actions.json")));
        Assert.False(File.Exists(downloaded));   // the downloaded copy is removed once restored
        Assert.Contains(_backups.List(), b => b.Reason == "pre-restore");
    }

    [Fact]
    public async Task Restore_rejects_a_file_that_is_not_a_zip()
    {
        SignedIn();
        var bogus = _drive.Seed("cuebot-20260101-000000-manual-v0.1.0.zip", "not a zip"u8.ToArray());
        Assert.True(await _service.RefreshAsync(Ct));

        Assert.False(await _service.ScheduleRestoreAsync(_service.Remote[0], Ct));

        Assert.Contains("not a valid backup", _service.Error);
        Assert.Null(_backups.PendingRestore);
        Assert.False(File.Exists(Path.Combine(_backups.DownloadsFolder, bogus.Name)));
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_and_a_rejected_one_retried_once()
    {
        SignedIn();
        Assert.True(await _service.RefreshAsync(Ct));
        Assert.Equal(1, _drive.TokenRefreshes);

        _time.Now = _time.Now.AddHours(2);   // expired: refreshed before the call
        Assert.True(await _service.RefreshAsync(Ct), _service.Error);
        Assert.Equal(2, _drive.TokenRefreshes);

        _drive.InvalidateAccessToken();   // rejected with 401: refreshed and retried
        Assert.True(await _service.RefreshAsync(Ct), _service.Error);
        Assert.Equal(3, _drive.TokenRefreshes);
        Assert.Contains("GET /drive/v3/files", _drive.Calls);
    }

    [Fact]
    public async Task A_revoked_refresh_token_signs_out_with_a_clear_error()
    {
        SignedIn();
        _drive.RefreshRevoked = true;

        Assert.False(await _service.UploadAsync(Backup(), Ct));

        Assert.False(_service.IsSignedIn);
        Assert.Equal("", new AppSettingsStore(_paths).Settings.Backups.GoogleDrive.RefreshToken);
        Assert.Contains("invalid_grant", _service.Error);
        Assert.Contains("Sign in to Google Drive again", _service.Error);
        Assert.DoesNotContain(_drive.RefreshToken, _service.Error);
    }

    [Fact]
    public async Task Drive_errors_are_reported_without_signing_out()
    {
        SignedIn();
        _drive.QuotaFull = true;

        Assert.False(await _service.UploadAsync(Backup(), Ct));

        Assert.True(_service.IsSignedIn);
        Assert.Contains("Google Drive is full", _service.Error);
        Assert.Contains("quota", _service.Error);
        Assert.Empty(_drive.Uploads);

        _drive.QuotaFull = false;
        Assert.True(await _service.UploadAsync(Backup(), Ct));
        Assert.Equal("", _service.Error);
    }

    [Fact]
    public async Task Network_failures_become_an_error_message()
    {
        var offline = new GoogleDriveBackups(_store, _backups,
            new GoogleDriveClient(new HttpClient(new Offline()), FakeGoogleDrive.Endpoints, _time), new AppLog());
        SignedIn();

        Assert.False(await offline.RefreshAsync(Ct));

        Assert.Contains("No such host", offline.Error);
        offline.Dispose();
    }

    [Fact]
    public async Task New_backups_upload_automatically_when_enabled()
    {
        SignedIn();
        S.Enabled = true;
        var changed = new SemaphoreSlim(0);
        _service.Changed += () => changed.Release();

        var backup = Backup("startup");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!_drive.Uploads.Contains(backup.Name) || _service.Remote.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the backup was not uploaded");
            await changed.WaitAsync(TimeSpan.FromSeconds(1), Ct);
        }
        Assert.Equal(backup.Name, _service.Remote[0].Name);
    }

    [Fact]
    public async Task Start_uploads_the_newest_backup_only_when_drive_lacks_it()
    {
        SignedIn();
        var older = Backup("startup");
        var newest = Backup("startup");
        S.Enabled = true;

        await _service.StartAsync();
        Assert.Equal([newest.Name], _drive.Uploads);

        await _service.StartAsync();
        Assert.Equal([newest.Name], _drive.Uploads);   // already there
        Assert.DoesNotContain(older.Name, _drive.Uploads);
    }

    [Fact]
    public async Task Sign_out_revokes_and_forgets_the_account_but_keeps_drive_files()
    {
        SignedIn();
        Assert.True(await _service.UploadAsync(Backup(), Ct));

        await _service.SignOutAsync();

        Assert.True(_drive.Revoked);
        Assert.False(_service.IsSignedIn);
        Assert.Equal("", S.FolderId);
        Assert.Empty(_service.Remote);
        Assert.Single(_drive.ZipsInFolder);
        Assert.False(await _service.RefreshAsync(Ct));
        Assert.Contains("Sign in", _service.Error);
    }

    [Fact]
    public void Restoring_the_oldest_backup_at_the_keep_limit_does_not_prune_it_first()
    {
        var backups = new BackupService(_paths, () => 2, () => _now);
        var oldest = backups.Create("one");
        _now = _now.AddMinutes(1);
        backups.Create("two");
        _now = _now.AddMinutes(1);
        backups.ScheduleRestore(oldest.Path);

        var safety = backups.ApplyPendingRestore();

        Assert.NotNull(safety);
        Assert.Equal(2, backups.List().Count);
        using var zip = ZipFile.OpenRead(safety!.Path);
        Assert.Contains(zip.Entries, e => e.FullName == "actions.json");
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known. (oauth.test:443)", null, HttpStatusCode.ServiceUnavailable);
    }
}
