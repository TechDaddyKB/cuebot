using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Shell.GoogleDrive;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests;

/// <summary>The Settings page's Google Drive tab against the fake Drive: sign in, upload, list, restore; both themes.</summary>
public sealed class GoogleDrivePageTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-drive-").FullName;
    private readonly ManualTime _time = new();
    private readonly FakeGoogleDrive _drive;
    private readonly AppHost _host;

    public GoogleDrivePageTests()
    {
        _drive = new FakeGoogleDrive(_time);
        _host = AppHost.Create(new AppPaths(_dataDir), overrides: services =>
            services.AddSingleton(_ => new GoogleDriveClient(new HttpClient(_drive), FakeGoogleDrive.Endpoints, _time)));
        Directory.CreateDirectory(ScreenshotDir);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
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
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, name + ".png"));
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Pump();
        }
    }

    [AvaloniaFact]
    public async Task Sign_in_upload_list_and_restore_from_the_google_drive_tab()
    {
        TestApp.BindingErrors.Clear();
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        _host.Backups.Create("startup");
        _host.DriveBackups.OpenBrowser = _drive.ApproveConsent;
        var vm = new MainWindowViewModel(_host);
        vm.SelectedPage = vm.Settings;
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Pump();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Sections");
            tabs.SelectedIndex = 3;
            Pump();
            var settings = vm.Settings;
            Assert.False(settings.DriveSignedIn);
            // Without a client ID, sign-in needs the build's own Google app.
            Assert.Equal(_host.DriveBackups.HasBuiltInApp, settings.DriveSignInCommand.CanExecute(null));
            Save(window, "settings-google-drive-signed-out");

            settings.DriveClientId = FakeGoogleDrive.ClientId;
            settings.DriveClientSecret = FakeGoogleDrive.ClientSecret;
            Assert.Equal(FakeGoogleDrive.ClientId, _host.Settings.Settings.Backups.GoogleDrive.ClientId);
            Assert.True(settings.DriveSignInCommand.CanExecute(null));

            await settings.DriveSignInCommand.ExecuteAsync(null);
            Pump();
            Assert.True(settings.DriveSignedIn, settings.DriveError);
            Assert.Equal("Signed in as tester@example.com", settings.DriveAccount);
            Assert.Empty(settings.RemoteBackups);

            await settings.DriveUploadCommand.ExecuteAsync(null);
            Pump();
            Assert.Single(settings.RemoteBackups);
            Assert.Equal("", settings.DriveError);

            // A new backup is uploaded automatically (on a background task).
            await settings.BackUpNowCommand.ExecuteAsync(null);
            // The list can refresh a moment before the status line does.
            await Until(() => settings.RemoteBackups.Count == 2 && settings.DriveStatus.Contains("Uploaded"),
                "the new backup reached Google Drive");
            Save(window, "settings-google-drive-dark");
            app.RequestedThemeVariant = ThemeVariant.Light;
            Save(window, "settings-google-drive-light");
            app.RequestedThemeVariant = ThemeVariant.Dark;

            // Restore asks first, then downloads and schedules the normal restore.
            settings.SelectedRemoteBackup = settings.RemoteBackups[^1];
            settings.DriveRestoreCommand.Execute(null);
            Assert.True(settings.IsConfirmingRestore);
            Assert.EndsWith("(Google Drive)", settings.RestoreTargetName);
            Save(window, "settings-google-drive-restore-confirm");
            await settings.ConfirmRestoreCommand.ExecuteAsync("false");
            Pump();
            Assert.NotNull(_host.Backups.PendingRestore);
            Assert.StartsWith(_host.Backups.DownloadsFolder, _host.Backups.PendingRestore);
            Assert.NotNull(settings.PendingRestore);
            settings.CancelPendingRestoreCommand.Execute(null);

            // A Drive error is shown on the page.
            _drive.QuotaFull = true;
            await settings.DriveUploadCommand.ExecuteAsync(null);
            Pump();
            Assert.Contains("Google Drive is full", settings.DriveError);
            Save(window, "settings-google-drive-error");

            await settings.DriveSignOutCommand.ExecuteAsync(null);
            Pump();
            Assert.False(settings.DriveSignedIn);
            Assert.Empty(settings.RemoteBackups);
            Assert.True(_drive.Revoked);

            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        }
        finally
        {
            app.RequestedThemeVariant = original;
            window.Close();
        }
    }
}
