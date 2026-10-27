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

    public ConnectionStatusViewModel(IConnectionStatus status)
    {
        _status = status;
        status.StatusChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
    }

    public string Name => _status.Name;
    public string State => _status.State.ToString();
    public string Message => _status.StatusMessage;
    public bool IsConnected => _status.State == ConnectionState.Connected;
    public bool IsError => _status.State == ConnectionState.Error;
}

/// <summary>Connection overview, recent action runs and the live log.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private const int MaxRows = 500;

    public DashboardViewModel(AppLog log, ActionEngine engine, IEnumerable<IConnectionStatus> connections) : base("Dashboard")
    {
        Connections = connections.Select(c => new ConnectionStatusViewModel(c)).ToArray();
        foreach (var entry in log.Recent()) Log.Add(entry);
        log.EntryAdded += entry => OnUi(() => Append(Log, entry));
        engine.RunCompleted += run => OnUi(() => Append(Runs, run, atTop: true));
    }

    public IReadOnlyList<ConnectionStatusViewModel> Connections { get; }
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
