using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Diagnostics;
using Cuebot.App.Updates;

namespace Cuebot.App.ViewModels.Support;

/// <summary>"Updates": current version, channel, check on start, check now. Never downloads or installs anything.</summary>
public sealed partial class UpdatesViewModel : ObservableObject, IDisposable
{
    private readonly SupportServices _services;
    private readonly UpdateSettingsStore _store;
    private readonly HttpClient _http;
    private readonly UpdateChecker _checker;
    private readonly PackageKind _package;
    private bool _loading;

    public UpdatesViewModel(SupportServices services)
    {
        _services = services;
        _store = new UpdateSettingsStore(services.Paths);
        _http = UpdateChecker.CreateHttpClient(services.HttpHandler);
        _checker = new UpdateChecker(_http, services.CurrentVersion, services.Clock);
        _package = PackagingDetector.Detect(services.Packaging);
        _loading = true;
        _checkOnStart = _store.Settings.CheckOnStart;
        _channel = _store.Settings.Channel;
        _loading = false;
        _status = _store.Settings.LastResult;
    }

    public static IReadOnlyList<UpdateChannel> Channels { get; } = [UpdateChannel.Stable, UpdateChannel.Prerelease];

    public string CurrentVersion => $"v{_services.CurrentVersion}";

    public string Package => PackagingDetector.Describe(_package);

    public PackageKind PackageKind => _package;

    [ObservableProperty] private bool _checkOnStart;
    [ObservableProperty] private UpdateChannel _channel;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownload), nameof(DownloadLabel), nameof(Headline), nameof(ReleaseNotes), nameof(Instructions))]
    private ReleaseInfo? _available;

    public bool IsUpdateAvailable => Available is not null;

    public string Headline => Available is { } r ? $"Update available: v{r.Version}" + (r.Name != r.Tag && r.Name.Length > 0 ? $" — {r.Name}" : "") : "";

    public string ReleaseNotes => Available is { } r ? (r.Notes.Trim().Length > 0 ? r.Notes.Trim() : "(no release notes)") : "";

    public string Instructions => PackagingDetector.Instructions(_package);

    private ReleaseAsset? Asset => Available?.FindAsset(PackagingDetector.AssetName(_package, Available.Version.ToString(), _services.Packaging.Architecture));

    public bool HasDownload => Asset is not null;

    public string DownloadLabel => Asset is { } a ? $"Download {a.Name}" : "";

    public string LastChecked => _store.Settings.LastChecked is { } t
        ? $"Last checked {t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)}"
        : "Not checked yet";

    /// <summary>Raised (on the caller's thread) when a check finds a newer release.</summary>
    public event Action<ReleaseInfo>? UpdateFound;

    partial void OnAvailableChanged(ReleaseInfo? value) => OnPropertyChanged(nameof(IsUpdateAvailable));

    partial void OnCheckOnStartChanged(bool value) => Save(s => s.CheckOnStart = value);

    partial void OnChannelChanged(UpdateChannel value) => Save(s => s.Channel = value);

    private void Save(Action<UpdateSettings> change)
    {
        if (_loading) return;
        change(_store.Settings);
        _store.Save();
    }

    [RelayCommand]
    public Task CheckNow() => CheckAsync(silent: false);

    /// <summary>The start-up check: only when enabled and due (once per 24 h); network problems stay silent.</summary>
    public Task CheckOnStartAsync() =>
        _store.Settings.StartCheckDue(_services.Clock()) ? CheckAsync(silent: true) : Task.CompletedTask;

    private async Task CheckAsync(bool silent)
    {
        if (IsChecking) return;
        IsChecking = true;
        if (!silent) Status = "Checking...";
        try
        {
            var result = await _checker.CheckAsync(Channel);
            var failed = result.Status is UpdateCheckStatus.Failed or UpdateCheckStatus.RateLimited;
            if (failed && silent) return;
            Status = result.Message;
            if (!failed)
            {
                _store.Settings.LastChecked = result.CheckedAt;
                _store.Settings.LastResult = result.Message;
                _store.Save();
                OnPropertyChanged(nameof(LastChecked));
            }
            Available = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Release : null;
            if (Available is { } release) UpdateFound?.Invoke(release);
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => _services.OpenUrl(Available?.HtmlUrl ?? SupportLinks.ReleasesUrl);

    /// <summary>Opens the asset's download link in the browser (the browser downloads it; Cuebot does not).</summary>
    [RelayCommand]
    private void Download()
    {
        if (Asset is { } a) _services.OpenUrl(a.DownloadUrl);
    }

    public void Dispose() => _http.Dispose();
}
