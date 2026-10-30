using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;

namespace Cuebot.App.ViewModels;

/// <summary>
/// The bottom bar of the window: connections in use, how many actions and triggers exist, uptime, warnings and errors
/// logged since start, and the clock. Refreshed once a second on the UI thread.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly ProfileService _profile;
    private readonly AppLog _log;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly DispatcherTimer _timer;
    private int _warnings;
    private int _errors;

    public StatusBarViewModel(AppHost host, DashboardViewModel dashboard)
    {
        _dashboard = dashboard;
        _profile = host.Get<ProfileService>();
        _log = host.Get<AppLog>();
        foreach (var entry in _log.Recent()) Count(entry);
        _log.EntryAdded += OnEntry;
        _profile.Changed += OnProfileChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        Refresh();
    }

    [ObservableProperty] private string _connections = "";
    [ObservableProperty] private bool _allConnected;
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _problems = "No problems";
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private bool _hasErrors;

    /// <summary>Clicking the warnings opens Support (Report a problem shows the log).</summary>
    public event Action? ProblemsRequested;

    [RelayCommand]
    private void OpenProblems() => ProblemsRequested?.Invoke();

    private void OnEntry(LogEntry entry) => Dispatcher.UIThread.Post(() => { Count(entry); RefreshProblems(); });

    private void OnProfileChanged() => Dispatcher.UIThread.Post(RefreshCounts);

    private void Count(LogEntry entry)
    {
        if (entry.Level == LogLevel.Warning) _warnings++;
        else if (entry.Level == LogLevel.Error) _errors++;
    }

    internal void Refresh()
    {
        var inUse = _dashboard.SidebarConnections;
        var connected = inUse.Count(c => c.IsConnected);
        Connections = inUse.Count == 0 ? "Nothing connected" : $"{connected} of {inUse.Count} connected";
        AllConnected = inUse.Count > 0 && connected == inUse.Count;
        var up = DateTimeOffset.Now - _started;
        Uptime = $"Up {(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}";
        Clock = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        RefreshCounts();
        RefreshProblems();
    }

    private void RefreshCounts()
    {
        var actions = _profile.Snapshot.Actions;
        Counts = $"Actions {actions.Count} · Triggers {actions.Sum(a => a.Triggers.Count)}";
    }

    private void RefreshProblems()
    {
        HasWarnings = _warnings > 0;
        HasErrors = _errors > 0;
        Problems = (_warnings, _errors) switch
        {
            (0, 0) => "No problems",
            (_, 0) => Plural(_warnings, "warning"),
            (0, _) => Plural(_errors, "error"),
            _ => $"{Plural(_errors, "error")} · {Plural(_warnings, "warning")}",
        };
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    public void Dispose()
    {
        _timer.Stop();
        _log.EntryAdded -= OnEntry;
        _profile.Changed -= OnProfileChanged;
    }
}
