using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.Diagnostics;
using Cuebot.App.Updates;
using Cuebot.App.ViewModels.Support;
using Cuebot.App.Views.Support;
using Cuebot.Core;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Support;

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }
    public bool IsAvailable => true;
    public Task SetTextAsync(string text, CancellationToken ct = default) { Text = text; return Task.CompletedTask; }
    public Task<string?> GetTextAsync(CancellationToken ct = default) => Task.FromResult(Text);
}

internal sealed class FakeProcesses : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Runs { get; } = [];
    public int AuthExitCode { get; set; }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        Runs.Add((fileName, arguments));
        return Task.FromResult(arguments[0] == "auth"
            ? new ProcessResult(AuthExitCode, "", "")
            : new ProcessResult(0, "Creating issue in TechDaddyKB/cuebot\n\nhttps://github.com/TechDaddyKB/cuebot/issues/42\n", ""));
    }
}

/// <summary>
/// The Support page end to end with fakes for browser, clipboard, gh and GitHub's API: crash prompt, prefilled
/// report, preview editing, link vs clipboard fallback, gh confirmation, update check; renders in both themes
/// without binding errors (screenshots tests/Cuebot.UiTests/screenshots/support-*.png).
/// </summary>
public sealed class SupportPageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-support-").FullName;
    private readonly AppPaths _paths;
    private readonly List<string> _opened = [];
    private readonly List<string> _folders = [];
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeProcesses _processes = new();
    private readonly FakeGitHub _github = new();

    public SupportPageTests() => _paths = new AppPaths(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private SupportServices Services(bool ghInstalled = true, bool checkOnStart = false) => new()
    {
        Paths = _paths,
        Connections = () => [new ConnectionInfo("Twitch", "Connected", "Connected as techdaddykb"), new ConnectionInfo("OBS", "Disconnected", "")],
        Clipboard = _clipboard,
        OpenUrl = _opened.Add,
        OpenFolder = _folders.Add,
        HttpHandler = _github,
        Gh = new GhCli(_processes, () => ghInstalled ? "/usr/bin/gh" : null),
        Clock = () => new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
        Environment = () => ReportBuilderTests.Env,
        Packaging = new PackagingEnvironment(false, true, n => n == "APPIMAGE" ? "/home/u/Cuebot.AppImage" : null, _ => false, "/tmp/.mount/usr/lib/cuebot", Architecture.X64),
        CurrentVersion = SemVersion.Parse("0.1.0"),
        CheckUpdatesOnStart = checkOnStart,
        HomeFolder = _dir,
    };

    private void SeedData()
    {
        File.WriteAllText(_paths.File("twitch.json"), SecretScrubberTests.TwitchJson);
        File.WriteAllText(_paths.File("app.json"), """{ "general": { "showTrayIcon": true } }""");
        File.WriteAllLines(Path.Combine(_paths.Logs, "cuebot-20261027.log"),
            ["12:00:00 [Info] Cuebot: Starting", "12:00:01 [Info] Twitch: Connected as techdaddykb", "12:00:02 [Error] Twitch: refresh_token=g5xk2m9q7w1r4t8y0u3i6o2p5a7s9d rejected"]);
    }

    private static Window Show(Control content, double height = 1100)
    {
        var window = new Window
        {
            Content = content,
            Width = 1300,
            Height = height,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    private static Exception Boom()
    {
        try { throw new InvalidOperationException("Overlay renderer died"); }
        catch (Exception ex) { return ex; }
    }

    [AvaloniaFact]
    public async Task Crash_prompt_prefills_the_report_and_sends_only_on_click_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            SeedData();
            CrashCapture.WriteCrash(_paths, Boom(), "UI thread", true, [new ConnectionInfo("Twitch", "Connected", "")],
                new DateTimeOffset(2026, 10, 27, 11, 59, 0, TimeSpan.Zero), "Wayland");

            var vm = new SupportViewModel(Services());
            await Task.Yield();
            UiPump.Pump();
            vm.SelectedTab = SupportViewModel.UpdatesTab;

            // The banner the main window shows.
            var banner = Show(new CrashBannerView { DataContext = vm.CrashBanner }, 90);
            Assert.True(vm.CrashBanner.IsVisible);
            Assert.Equal(CrashBannerViewModel.Prompt, ((ICrashPrompt)vm.CrashBanner).Message);
            Assert.Equal("InvalidOperationException: Overlay renderer died", vm.CrashBanner.Detail);
            UiPump.Save(banner, "support-crash-banner");
            var reportRequested = false;
            vm.CrashBanner.ReportRequested += () => reportRequested = true;
            vm.CrashBanner.ReportCommand.Execute(null);
            Assert.True(reportRequested);
            Assert.False(vm.CrashBanner.IsVisible);
            Assert.Equal(SupportViewModel.ReportTab, vm.SelectedTab);
            Assert.Null(new CrashStore(_paths).LatestUnseen());   // offered once
            banner.Close();

            var report = vm.Report;
            Assert.Equal("Crash: InvalidOperationException: Overlay renderer died", report.Title);
            Assert.True(report.IncludeCrash);
            Assert.Contains("System.InvalidOperationException: Overlay renderer died", report.Preview);
            Assert.DoesNotContain("g5xk2m9q7w1r4t8y0u3i6o2p5a7s9d", report.Preview);
            Assert.DoesNotContain("techdaddykb", report.Preview);
            Assert.Empty(_opened);   // nothing sent yet

            report.Summary = "The overlay page crashed the app.";
            Assert.Contains("The overlay page crashed the app.", report.Preview);
            report.IncludeAccountNames = true;
            Assert.Contains("techdaddykb", report.Preview);
            report.IncludeAccountNames = false;

            var window = Show(new SupportView { DataContext = vm });
            Assert.True(report.GhAvailable);
            UiPump.Save(window, "support-report-dark");
            app.RequestedThemeVariant = ThemeVariant.Light;
            UiPump.Save(window, "support-report-light");
            app.RequestedThemeVariant = ThemeVariant.Dark;

            // Editing the preview sticks until "Rebuild preview".
            report.Preview += "\nEdited by hand";
            Assert.True(report.PreviewEdited);
            report.Steps = "1. Open overlay";
            Assert.EndsWith("Edited by hand", report.Preview);

            // Default path: browser link (the report is long because of the crash file, so the clipboard carries it).
            await report.OpenIssue();
            var url = Assert.Single(_opened);
            Assert.StartsWith("https://github.com/TechDaddyKB/cuebot/issues/new?title=", url);
            Assert.True(url.Length <= IssueLink.MaxUrlLength);
            var reportFile = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "reports"), "report-*.md"));
            Assert.Equal(report.Preview, File.ReadAllText(reportFile));
            if (url.Contains(Uri.EscapeDataString(ReportBuilder.PasteMarker), StringComparison.Ordinal))
            {
                Assert.Equal(report.Preview, _clipboard.Text);
                Assert.Contains("copied to the clipboard", report.Status);
            }

            // gh: the command is shown first, and runs only on the explicit click.
            report.PrepareGhCommand.Execute(null);
            Assert.True(report.IsGhConfirming);
            Assert.StartsWith("gh issue create --repo TechDaddyKB/cuebot --title \"Crash: InvalidOperationException: Overlay renderer died\" --body-file ", report.GhCommand);
            Assert.DoesNotContain(_processes.Runs, r => r.Args[0] == "issue");
            UiPump.Save(window, "support-report-gh");
            await report.RunGh();
            var run = Assert.Single(_processes.Runs, r => r.Args[0] == "issue");
            Assert.Equal("/usr/bin/gh", run.File);
            Assert.Equal("TechDaddyKB/cuebot", run.Args[3]);
            Assert.Equal("Issue created: https://github.com/TechDaddyKB/cuebot/issues/42", report.Status);
            Assert.False(report.IsGhConfirming);

            Assert.Empty(TestApp.BindingErrors.Errors);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public async Task Short_report_opens_the_full_link_and_title_is_required()
    {
        SeedData();
        var vm = new SupportViewModel(Services(ghInstalled: false));
        await Task.Yield();
        var report = vm.Report;
        Assert.False(report.GhAvailable);
        Assert.False(report.HasCrash);
        report.IncludeLog = false;
        report.IncludeAppSettings = false;

        await report.OpenIssue();
        Assert.Empty(_opened);
        Assert.Equal("Give the report a short title first.", report.Error);

        report.Title = "Chat freezes";
        report.Summary = "Chat stops after a raid";
        await report.OpenIssue();
        var url = Assert.Single(_opened);
        Assert.DoesNotContain(Uri.EscapeDataString(ReportBuilder.PasteMarker), url);
        Assert.Contains(Uri.EscapeDataString("Chat stops after a raid"), url);
        Assert.Null(_clipboard.Text);

        await report.CopyReportCommand.ExecuteAsync(null);
        Assert.Equal(report.Preview, _clipboard.Text);
        report.OpenReportsFolderCommand.Execute(null);
        Assert.Equal(Path.Combine(_dir, "reports"), Assert.Single(_folders));
    }

    [AvaloniaFact]
    public async Task Updates_tab_checks_shows_the_asset_and_remembers_settings()
    {
        TestApp.BindingErrors.Clear();
        _github.Respond = _ => FakeGitHub.Json(FakeGitHub.Release("v0.2.0", body: "New overlay editor"));
        var vm = new SupportViewModel(Services());
        var updates = vm.Updates;
        Assert.Equal("v0.1.0", updates.CurrentVersion);
        Assert.Equal("AppImage", updates.Package);
        Assert.Equal("Not checked yet", updates.LastChecked);
        var found = new List<ReleaseInfo>();
        updates.UpdateFound += found.Add;

        await updates.CheckNow();
        Assert.True(updates.IsUpdateAvailable);
        Assert.Equal("Update available: v0.2.0 — Cuebot v0.2.0", updates.Headline);
        Assert.Equal("New overlay editor", updates.ReleaseNotes);
        Assert.True(updates.HasDownload);
        Assert.Equal("Download Cuebot-x86_64.AppImage", updates.DownloadLabel);
        Assert.Single(found);
        updates.DownloadCommand.Execute(null);
        updates.OpenReleasePageCommand.Execute(null);
        Assert.Equal(["https://example.invalid/v0.2.0/Cuebot-x86_64.AppImage", "https://github.com/TechDaddyKB/cuebot/releases/tag/v0.2.0"], _opened);

        vm.SelectedTab = SupportViewModel.UpdatesTab;
        var window = Show(new SupportView { DataContext = vm }, 900);
        UiPump.Save(window, "support-updates");
        Assert.Empty(TestApp.BindingErrors.Errors);
        window.Close();

        updates.Channel = UpdateChannel.Prerelease;
        updates.CheckOnStart = false;
        var stored = new UpdateSettingsStore(_paths).Settings;
        Assert.Equal(UpdateChannel.Prerelease, stored.Channel);
        Assert.False(stored.CheckOnStart);
        Assert.NotNull(stored.LastChecked);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Start_check_is_silent_on_errors_and_runs_at_most_daily()
    {
        _github.Respond = _ => throw new HttpRequestException("offline");
        var first = new SupportViewModel(Services(checkOnStart: true));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        UiPump.Pump();
        Assert.Single(_github.Requests);
        Assert.Equal("", first.Updates.Status);   // silent
        Assert.Null(new UpdateSettingsStore(_paths).Settings.LastChecked);
        first.Dispose();

        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var second = new SupportViewModel(Services(checkOnStart: true));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        UiPump.Pump();
        Assert.Equal(2, _github.Requests.Count);
        Assert.Equal("No public releases yet.", second.Updates.Status);
        second.Dispose();

        var third = new SupportViewModel(Services(checkOnStart: true));   // checked less than 24 h ago
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, _github.Requests.Count);
        third.Dispose();
    }
}
