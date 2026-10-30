using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.App.ViewModels.History;

namespace Cuebot.App.ViewModels;

public sealed partial class ConnectionStatusViewModel : ObservableObject
{
    private readonly IConnectionStatus _status;

    private bool _wasActive;

    public ConnectionStatusViewModel(IConnectionStatus status)
    {
        _status = status;
        _wasActive = IsActive;
        status.StatusChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _wasActive |= IsActive;
            OnPropertyChanged(string.Empty);
            Changed?.Invoke();
        });
    }

    /// <summary>Raised on the UI thread after the status changed.</summary>
    public event Action? Changed;

    public string Name => _status.Name;
    public string State => _status.State.ToString();
    public string Message => _status.StatusMessage;
    public bool IsConnected => _status.State == ConnectionState.Connected;
    public bool IsError => _status.State == ConnectionState.Error;

    /// <summary>Connected, connecting or failing: something the user set up.</summary>
    public bool IsActive => _status.State != ConnectionState.Disconnected;

    /// <summary>Shown in the sidebar: active now, or active at some point this session (so a drop stays visible).</summary>
    public bool ShowInSidebar => IsActive || _wasActive;
}

/// <summary>Connection overview, recent action runs and the live log.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private const int MaxRows = 500;

    public DashboardViewModel(AppLog log, ActionEngine engine, IEnumerable<IConnectionStatus> connections) : base("Dashboard")
    {
        _engine = engine;
        Connections = connections.Select(c => new ConnectionStatusViewModel(c)).ToArray();
        foreach (var c in Connections) c.Changed += RefreshSidebar;
        RefreshSidebar();
        foreach (var entry in log.Recent()) AddLog(entry);
        log.EntryAdded += entry => OnUi(() => AddLog(entry));
        engine.RunCompleted += run => OnUi(() => AddRun(run));
    }

    private readonly ActionEngine _engine;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private const int MaxEvents = 200;
    private const int MaxQuickActions = 6;

    public IReadOnlyList<ConnectionStatusViewModel> Connections { get; }

    /// <summary>
    /// The sidebar's short list: only connections in use, so two dozen services that were never set up don't crowd the
    /// navigation. The Dashboard shows all of them.
    /// </summary>
    public ObservableCollection<ConnectionStatusViewModel> SidebarConnections { get; } = [];

    public bool HasSidebarConnections => SidebarConnections.Count > 0;

    private void RefreshSidebar()
    {
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(ConnectionsDetail));
        OnPropertyChanged(nameof(ConnectionsBad));
        var wanted = Connections.Where(c => c.ShowInSidebar).ToList();
        if (wanted.SequenceEqual(SidebarConnections)) return;
        SidebarConnections.Clear();
        foreach (var c in wanted) SidebarConnections.Add(c);
        OnPropertyChanged(nameof(HasSidebarConnections));
    }

    // ===== Status tiles =====

    public string ConnectedCount => $"{Connections.Count(c => c.IsConnected)} of {Connections.Count}";

    public string ConnectionsDetail => Connections.Count(c => c.IsError) switch
    {
        0 when Connections.Any(c => c.IsConnected) => "Connected services are fine",
        0 => "Nothing connected yet",
        1 => "1 connection has an error",
        var n => $"{n} connections have errors",
    };

    public bool ConnectionsBad => Connections.Any(c => c.IsError);

    [ObservableProperty] private int _runsToday;
    [ObservableProperty] private int _failedToday;
    public string RunsDetail => FailedToday == 0 ? "No failures" : FailedToday == 1 ? "1 failed" : $"{FailedToday} failed";
    public bool HasFailures => FailedToday > 0;
    partial void OnFailedTodayChanged(int value)
    {
        OnPropertyChanged(nameof(RunsDetail));
        OnPropertyChanged(nameof(HasFailures));
    }

    [ObservableProperty] private int _eventsToday;
    [ObservableProperty] private string _eventsDetail = "None yet";

    [ObservableProperty] private int _warnings;
    [ObservableProperty] private int _errors;
    public int ProblemCount => Warnings + Errors;
    public bool HasErrors => Errors > 0;
    public string ProblemsDetail => (Warnings, Errors) switch
    {
        (0, 0) => "Nothing logged",
        (_, 0) => Warnings == 1 ? "1 warning in the log" : $"{Warnings} warnings in the log",
        (0, _) => Errors == 1 ? "1 error in the log" : $"{Errors} errors in the log",
        _ => $"{Errors} errors, {Warnings} warnings",
    };
    partial void OnWarningsChanged(int value) => RefreshProblemTile();
    partial void OnErrorsChanged(int value) => RefreshProblemTile();
    private void RefreshProblemTile()
    {
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(ProblemsDetail));
        OnPropertyChanged(nameof(HasErrors));
    }

    /// <summary>The "today" counters start again after midnight.</summary>
    private void RollDay()
    {
        var now = DateOnly.FromDateTime(DateTime.Now);
        if (now == _today) return;
        _today = now;
        RunsToday = FailedToday = EventsToday = 0;
        EventsDetail = "None yet";
    }

    // ===== Activity =====

    /// <summary>Events from the event bus, newest first (fed by the History page's trigger viewer).</summary>
    public ObservableCollection<TriggerEventRow> Events { get; } = [];

    public bool HasEvents => Events.Count > 0;

    public void AddEvent(TriggerEventRow row)
    {
        RollDay();
        EventsToday++;
        EventsDetail = "Last at " + row.Event.Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Events.Insert(0, row);
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        OnPropertyChanged(nameof(HasEvents));
    }

    private void AddLog(LogEntry entry)
    {
        if (entry.Level == LogLevel.Warning) Warnings++;
        else if (entry.Level >= LogLevel.Error) Errors++;
        Append(Log, entry);
    }

    private void AddRun(ActionRunRecord run)
    {
        RollDay();
        RunsToday++;
        if (!run.Success) FailedToday++;
        Append(Runs, run, atTop: true);
        OnPropertyChanged(nameof(HasRuns));
        if (run.ExcludedFromHistory) return;
        var existing = QuickActions.FirstOrDefault(q => q.Name == run.ActionName);
        if (existing is not null) QuickActions.Remove(existing);
        QuickActions.Insert(0, new QuickActionViewModel(run, RunAgain));
        while (QuickActions.Count > MaxQuickActions) QuickActions.RemoveAt(QuickActions.Count - 1);
        OnPropertyChanged(nameof(HasQuickActions));
    }

    // ===== Quick actions =====

    /// <summary>The actions run most recently (one row each), to run again with one click.</summary>
    public ObservableCollection<QuickActionViewModel> QuickActions { get; } = [];

    public bool HasQuickActions => QuickActions.Count > 0;

    private void RunAgain(string name) => _ = _engine.RunByNameAsync(name);
    public ObservableCollection<LogEntry> Log { get; } = [];
    public ObservableCollection<ActionRunRecord> Runs { get; } = [];
    public bool HasRuns => Runs.Count > 0;

    [ObservableProperty] private bool _showDebug;

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        Warnings = Errors = 0;
    }

    private static void Append<T>(ObservableCollection<T> rows, T row, bool atTop = false)
    {
        if (atTop) rows.Insert(0, row);
        else rows.Add(row);
        while (rows.Count > MaxRows) rows.RemoveAt(atTop ? rows.Count - 1 : 0);
    }
}

/// <summary>One quick-action row on the Dashboard.</summary>
public sealed partial class QuickActionViewModel(ActionRunRecord run, Action<string> runAgain)
{
    public string Name => run.ActionName;
    public bool Succeeded => run.Success;
    public string LastRun => "Last run " + run.CompletedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                             + (run.Success ? "" : " (failed)");

    [RelayCommand]
    private void Run() => runAgain(Name);
}
