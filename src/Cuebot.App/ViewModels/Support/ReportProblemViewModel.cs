using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Diagnostics;

namespace Cuebot.App.ViewModels.Support;

/// <summary>
/// "Report a problem": the user's description, which auto-collected sections to include, an editable preview of
/// exactly what will be sent, and the ways to send it. Nothing leaves the machine until the user clicks a button.
/// </summary>
public sealed partial class ReportProblemViewModel : ObservableObject
{
    private readonly SupportServices _services;
    private readonly CrashStore _crashes;
    private CollectedInfo _info = new();
    private string? _crashFile;
    private bool _settingPreview;
    private string? _gh;
    private string? _ghBodyFile;
    private readonly ReportSettingsStore _reportSettings;
    private readonly ReportSender _sender;

    public ReportProblemViewModel(SupportServices services)
    {
        _services = services;
        _crashes = new CrashStore(services.Paths);
        _reportSettings = new ReportSettingsStore(services.Paths);
        _sender = new ReportSender(services.HttpHandler, services.ReportEndpoint);
        _sendCrashesAutomatically = _reportSettings.Settings.SendCrashesAutomatically;
        ReportsFolder = Path.Combine(services.Paths.Root, "reports");
        _crashFile = _crashes.Latest();
        _includeCrash = false;   // an older crash is offered, not attached; the crash prompt attaches a new one
        Refresh();
    }

    public string ReportsFolder { get; }

    // ---- The user's text ----

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _steps = "";
    [ObservableProperty] private string _expected = "";
    [ObservableProperty] private string _actual = "";

    // ---- Sections ----

    [ObservableProperty] private bool _includeConnections = true;
    [ObservableProperty] private bool _includeAppSettings = true;
    [ObservableProperty] private bool _includeModuleSettings;
    [ObservableProperty] private bool _includeLog = true;
    [ObservableProperty] private bool _includeCrash;
    [ObservableProperty] private bool _includeAccountNames;

    public bool HasCrash => _info.Crash is not null;

    public string CrashLabel => _info.Crash is { } c
        ? $"Include the crash from {c.Time.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)} ({SecretScrubber.ScrubText(c.Summary)})"
        : "";

    // ---- Preview and status ----

    /// <summary>The issue body exactly as it will be sent; editable.</summary>
    [ObservableProperty] private string _preview = "";

    /// <summary>The user changed the preview by hand; option changes no longer rebuild it.</summary>
    [ObservableProperty] private bool _previewEdited;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";

    /// <summary>Set when the last action saved a report file (offers Open folder).</summary>
    [ObservableProperty] private string _savedPath = "";

    // ---- Send (the report intake) ----

    /// <summary>The issue the last Send filed (or added to).</summary>
    [ObservableProperty] private string _issueUrl = "";

    /// <summary>Send crash reports on the next start without asking (saved in reports.json; off by default).</summary>
    [ObservableProperty] private bool _sendCrashesAutomatically;

    partial void OnSendCrashesAutomaticallyChanged(bool value)
    {
        _reportSettings.Settings.SendCrashesAutomatically = value;
        _reportSettings.Save();
    }

    public bool HasIssue => IssueUrl.Length > 0;

    partial void OnIssueUrlChanged(string value) => OnPropertyChanged(nameof(HasIssue));

    // ---- gh ----

    [ObservableProperty] private bool _ghAvailable;
    [ObservableProperty] private bool _isGhConfirming;
    [ObservableProperty] private string _ghCommand = "";
    [ObservableProperty] private bool _isBusy;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Title) or nameof(Summary) or nameof(Steps) or nameof(Expected) or nameof(Actual)
                or nameof(IncludeConnections) or nameof(IncludeAppSettings) or nameof(IncludeModuleSettings)
                or nameof(IncludeLog) or nameof(IncludeCrash) or nameof(IncludeAccountNames):
                if (!PreviewEdited) RebuildPreview();
                break;
            case nameof(Preview):
                if (!_settingPreview) PreviewEdited = true;
                IsGhConfirming = false;   // the prepared body file no longer matches
                break;
        }
    }

    public ReportInput Input => new() { Title = Title, Summary = Summary, Steps = Steps, Expected = Expected, Actual = Actual };

    public ReportOptions Options => new()
    {
        IncludeConnections = IncludeConnections,
        IncludeAppSettings = IncludeAppSettings,
        IncludeModuleSettings = IncludeModuleSettings,
        IncludeLog = IncludeLog,
        IncludeCrash = IncludeCrash,
        IncludeAccountNames = IncludeAccountNames,
        HomeFolder = _services.HomeFolder,
    };

    /// <summary>Re-reads logs, settings and connection states.</summary>
    [RelayCommand]
    public void Refresh()
    {
        _info = InfoGatherer.Gather(_services.Paths, _services.Environment(), _services.Connections(), _crashFile);
        OnPropertyChanged(nameof(HasCrash));
        OnPropertyChanged(nameof(CrashLabel));
        if (!PreviewEdited) RebuildPreview();
    }

    /// <summary>Attaches a crash file (from the crash prompt) and prefills the title.</summary>
    public void AttachCrash(string path)
    {
        _crashFile = path;
        PreviewEdited = false;
        Refresh();
        if (_info.Crash is not null)
        {
            IncludeCrash = true;
            if (string.IsNullOrWhiteSpace(Title)) Title = ReportBuilder.DefaultTitle(_info);
            if (string.IsNullOrWhiteSpace(Actual)) Actual = "Cuebot closed unexpectedly.";
        }
    }

    /// <summary>Discards edits to the preview and builds it again from the form.</summary>
    [RelayCommand]
    public void RebuildPreview()
    {
        _settingPreview = true;
        try { Preview = ReportBuilder.Build(Input, _info, Options); }
        finally { _settingPreview = false; }
        PreviewEdited = false;
    }

    private bool CheckTitle()
    {
        Error = "";
        if (!string.IsNullOrWhiteSpace(Title)) return true;
        Error = "Give the report a short title first.";
        return false;
    }

    /// <summary>Saves the preview as <c>reports/report-*.md</c>.</summary>
    [RelayCommand]
    private void SaveReport() => SaveReportFile();

    public string? SaveReportFile()
    {
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            var path = Path.Combine(ReportsFolder, $"report-{_services.Clock().LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md");
            File.WriteAllText(path, Preview);
            SavedPath = path;
            Status = $"Saved to {path}";
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Could not save the report: {ex.Message}";
            return null;
        }
    }

    [RelayCommand]
    private void OpenReportsFolder() => _services.OpenFolder(ReportsFolder);

    [RelayCommand]
    private async Task CopyReport()
    {
        if (_services.Clipboard is not { IsAvailable: true } clipboard)
        {
            Error = "The clipboard is not available; use Save report instead.";
            return;
        }
        await clipboard.SetTextAsync(Preview);
        Status = "The report was copied to the clipboard.";
    }

    /// <summary>
    /// Sends the report as shown in the preview to Cuebot's report intake, which files it as a GitHub issue (no GitHub
    /// account needed). A report with a crash attached goes as a crash, so repeats of the same crash join one issue.
    /// </summary>
    [RelayCommand]
    public async Task Send()
    {
        if (!CheckTitle()) return;
        if (Title.Trim().Length < 3)
        {
            Error = "Make the title a few words long.";
            return;
        }
        IsBusy = true;
        Status = "Sending…";
        try
        {
            var crash = IncludeCrash ? _info.Crash : null;
            var environment = _info.Environment;
            var result = await _sender.SendAsync(crash is null ? "problem" : "crash", Title, Preview, VersionOf(environment), OsOf(environment),
                _reportSettings.Settings.InstallId, crash is null ? null : CrashSignature.Of(crash.Exception));
            if (!result.Sent)
            {
                Status = "";
                Error = result.Error;
                return;
            }
            IssueUrl = result.Url;
            Status = result.Duplicate
                ? $"Thanks! This crash was already reported, so your report was added to issue #{result.Number}."
                : $"Thanks! Your report is issue #{result.Number}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Attaches the crash and sends it with the default form (the crash prompt's one-click Send).</summary>
    public async Task<bool> SendCrashAsync(string crashFile)
    {
        AttachCrash(crashFile);
        await Send();
        return HasIssue;
    }

    [RelayCommand]
    private void OpenSentIssue()
    {
        if (HasIssue) _services.OpenUrl(IssueUrl);
    }

    private static string VersionOf(EnvironmentSnapshot environment)
    {
        var version = (environment.AppVersion.Length > 0 ? environment.AppVersion : "0.0.0").Split('+')[0].Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9A-Za-z.+-]{1,40}$") ? version : "0.0.0";
    }

    private static string OsOf(EnvironmentSnapshot environment) =>
        ReportBuilder.Truncate((environment.Distro.Length > 0 ? environment.Distro : environment.Os).Trim(), 100);

    /// <summary>
    /// Opens GitHub's new-issue page prefilled. When the report is too long for a link, the link carries a short
    /// body and the full report goes to the clipboard (and a file) for pasting.
    /// </summary>
    [RelayCommand]
    public async Task OpenIssue()
    {
        if (!CheckTitle()) return;
        var path = SaveReportFile();
        var link = IssueLink.Create(Title, Preview, ReportBuilder.BuildShort(Input, _info, Options));
        if (link.BodyTruncated)
        {
            var copied = false;
            if (_services.Clipboard is { IsAvailable: true } clipboard)
            {
                try
                {
                    await clipboard.SetTextAsync(Preview);
                    copied = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            }
            Status = copied
                ? "The report is too long for a link, so the full report was copied to the clipboard. In the browser, paste it where the issue says so."
                  + (path is null ? "" : $" It was also saved to {path}.")
                : $"The report is too long for a link. Open {path ?? "the saved report"} and paste its text into the issue.";
        }
        else
        {
            Status = "Opened GitHub in your browser. Check the text and click \"Submit new issue\" there.";
        }
        _services.OpenUrl(link.Url);
    }

    // ---- gh ----

    /// <summary>Looks for an installed, signed-in GitHub CLI (runs <c>gh auth status</c> only).</summary>
    public async Task DetectGhAsync()
    {
        _gh = await _services.Gh.FindReadyAsync();
        GhAvailable = _gh is not null;
    }

    /// <summary>Saves the body file and shows the exact command; nothing runs yet.</summary>
    [RelayCommand]
    public void PrepareGh()
    {
        if (!CheckTitle() || _gh is null) return;
        if (SaveReportFile() is not { } path) return;
        _ghBodyFile = path;
        GhCommand = GhCli.DisplayCommand(Title.Trim(), path);
        IsGhConfirming = true;
    }

    [RelayCommand]
    private void CancelGh() => IsGhConfirming = false;

    /// <summary>Runs the command shown by <see cref="PrepareGh"/> (only from the explicit "Run" click).</summary>
    [RelayCommand]
    public async Task RunGh()
    {
        if (!IsGhConfirming || _gh is null || _ghBodyFile is null) return;
        IsBusy = true;
        Error = "";
        try
        {
            var result = await _services.Gh.CreateIssueAsync(_gh, Title.Trim(), _ghBodyFile);
            if (result.ExitCode == 0)
            {
                var url = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
                Status = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? $"Issue created: {url}" : "Issue created.";
            }
            else
            {
                Error = $"gh failed: {(result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim())}";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Error = $"gh could not be run: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsGhConfirming = false;
        }
    }
}
