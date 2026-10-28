using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;

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
        Connections = connections.Select(c => new ConnectionStatusViewModel(c)).ToArray();
        foreach (var c in Connections) c.Changed += RefreshSidebar;
        RefreshSidebar();
        foreach (var entry in log.Recent()) Log.Add(entry);
        log.EntryAdded += entry => OnUi(() => Append(Log, entry));
        engine.RunCompleted += run => OnUi(() => Append(Runs, run, atTop: true));
    }

    public IReadOnlyList<ConnectionStatusViewModel> Connections { get; }

    /// <summary>
    /// The sidebar's short list: only connections in use, so two dozen services that were never set up don't crowd the
    /// navigation. The Dashboard shows all of them.
    /// </summary>
    public ObservableCollection<ConnectionStatusViewModel> SidebarConnections { get; } = [];

    public bool HasSidebarConnections => SidebarConnections.Count > 0;

    private void RefreshSidebar()
    {
        var wanted = Connections.Where(c => c.ShowInSidebar).ToList();
        if (wanted.SequenceEqual(SidebarConnections)) return;
        SidebarConnections.Clear();
        foreach (var c in wanted) SidebarConnections.Add(c);
        OnPropertyChanged(nameof(HasSidebarConnections));
    }
    public ObservableCollection<LogEntry> Log { get; } = [];
    public ObservableCollection<ActionRunRecord> Runs { get; } = [];

    [ObservableProperty] private bool _showDebug;

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private static void Append<T>(ObservableCollection<T> rows, T row, bool atTop = false)
    {
        if (atTop) rows.Insert(0, row);
        else rows.Add(row);
        while (rows.Count > MaxRows) rows.RemoveAt(atTop ? rows.Count - 1 : 0);
    }
}
