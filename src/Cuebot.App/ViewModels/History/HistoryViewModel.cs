using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.App.ViewModels.History;

/// <summary>An argument in the run inspector; <see cref="Changed"/> marks values the run added or changed.</summary>
public sealed record RunArgRow(string Name, string Value, bool Changed);

/// <summary>
/// History page. Tab 1: Action History — recent runs (newest first) with search and state filter, an arguments
/// inspector (when queued / after the run, changes highlighted), "Run again with these args" (requeue), cancel for
/// active runs and "Open action". Pending work per queue is summarized with a link to the Queues page, which owns
/// pausing and clearing queues. Tab 2: <see cref="TriggerViewerViewModel"/>.
/// </summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    public static readonly IReadOnlyList<string> StateFilters = ["All runs", "Pending / running", "Completed", "Failed", "Cancelled"];

    private readonly ActionEngine _engine;
    private readonly Dictionary<Guid, RunRowViewModel> _rows = [];
    private bool _refreshPosted;

    public HistoryViewModel(ActionEngine engine, EventBus bus, Registry registry) : base("History")
    {
        _engine = engine;
        Triggers = new TriggerViewerViewModel(bus, registry);
        engine.History.Changed += (_, _) => OnUi(ScheduleRefresh);
        engine.Queues.Changed += () => OnUi(ScheduleRefresh);
        Refresh();
    }

    public TriggerViewerViewModel Triggers { get; }

    public IReadOnlyList<string> StateFilterOptions => StateFilters;

    public ObservableCollection<RunRowViewModel> Runs { get; } = [];
    public ObservableCollection<RunArgRow> ArgsWhenQueued { get; } = [];
    public ObservableCollection<RunArgRow> ArgsAfterRun { get; } = [];

    /// <summary>Raised to show the Queues page (pending work lives there).</summary>
    public event Action? OpenQueuesRequested;

    /// <summary>Raised to show an action on the Actions page.</summary>
    public event Action<Guid>? OpenActionRequested;

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _stateFilter = StateFilters[0];
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _pendingSummary = "";
    [ObservableProperty] private int _activeCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun), nameof(CanCancelSelected), nameof(SelectedTitle), nameof(SelectedDetails),
        nameof(HasArgsAfterRun), nameof(SelectedError), nameof(HasSelectedError))]
    private RunRowViewModel? _selectedRun;

    public bool HasSelectedRun => SelectedRun is not null;
    public bool CanCancelSelected => SelectedRun?.IsActive == true;
    public bool HasArgsAfterRun => SelectedRun?.Entry.ArgsAfterRun is not null;
    public string SelectedTitle => SelectedRun is { } r ? $"{r.ActionName} · {r.StateText}" : "";
    public string SelectedError => SelectedRun?.Entry.Error ?? "";
    public bool HasSelectedError => !string.IsNullOrEmpty(SelectedError);

    public string SelectedDetails => SelectedRun is not { } r
        ? ""
        : $"Trigger: {r.Source} · Queue: {r.Queue} · Queued {r.Queued} · Started {r.Started} · Duration {r.Duration}"
          + (r.Entry.Depth > 0 ? $" · nested (depth {r.Entry.Depth})" : "");

    public string Summary => Runs.Count == 0
        ? _engine.History.Entries.Count == 0
            ? "No runs yet. Actions marked 'Exclude from history' never appear here."
            : "No runs match the search."
        : $"{Runs.Count} run(s), newest first (the last {_engine.History.Capacity} finished runs are kept)";

    partial void OnSearchChanged(string value) => Refresh();
    partial void OnStateFilterChanged(string value) => Refresh();
    partial void OnSelectedRunChanged(RunRowViewModel? value) => ShowArgs();

    private void ScheduleRefresh()
    {
        if (_refreshPosted) return;
        _refreshPosted = true;
        // Bursts of run updates (a queue resuming) are applied in one pass.
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    internal void Refresh()
    {
        var entries = _engine.History.Entries;
        var seen = new HashSet<Guid>();
        var shown = new List<RunRowViewModel>();
        foreach (var entry in entries)
        {
            seen.Add(entry.RunId);
            if (_rows.TryGetValue(entry.RunId, out var row)) row.Entry = entry;
            else _rows[entry.RunId] = row = new RunRowViewModel(entry);
            if (Matches(entry)) shown.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(id => !seen.Contains(id)).ToArray()) _rows.Remove(gone);
        var selected = SelectedRun;
        Runs.SyncTo(shown);
        SelectedRun = selected is not null && shown.Contains(selected) ? selected : null;

        ActiveCount = entries.Count(e => e.IsActive);
        var busy = _engine.Queues.Queues.Where(q => q.Pending > 0 || q.Running > 0 || q.Paused).ToArray();
        PendingSummary = busy.Length == 0
            ? "No queue has pending or running actions."
            : string.Join(" · ", busy.Select(q => $"{q.Name}: {q.Running} running, {q.Pending} pending{(q.Paused ? " (paused)" : "")}"));

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanCancelSelected));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedDetails));
        OnPropertyChanged(nameof(SelectedError));
        OnPropertyChanged(nameof(HasSelectedError));
        OnPropertyChanged(nameof(HasArgsAfterRun));
        ShowArgs();
    }

    private bool Matches(ActionRunEntry e)
    {
        var state = StateFilter switch
        {
            "Pending / running" => e.IsActive,
            "Completed" => e.State == ActionRunState.Completed,
            "Failed" => e.State == ActionRunState.Failed,
            "Cancelled" => e.State == ActionRunState.Cancelled,
            _ => true,
        };
        if (!state) return false;
        var q = Search.Trim();
        if (q.Length == 0) return true;
        return Contains(e.ActionName) || Contains(e.Source) || Contains(e.Queue) || Contains(e.Error)
               || e.ArgsWhenQueued.Any(p => Contains(p.Key) || Contains(VariableResolver.Format(p.Value)));

        bool Contains(string? text) => text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void ShowArgs()
    {
        var before = SelectedRun?.Entry.ArgsWhenQueued;
        var after = SelectedRun?.Entry.ArgsAfterRun;
        Sync(ArgsWhenQueued, Rows(before, null));
        Sync(ArgsAfterRun, Rows(after, before));

        static IReadOnlyList<RunArgRow> Rows(IReadOnlyDictionary<string, object?>? args, IReadOnlyDictionary<string, object?>? baseline)
        {
            if (args is null) return [];
            return args.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var value = VariableResolver.Format(p.Value);
                    var changed = baseline is not null
                                  && (!TryGet(baseline, p.Key, out var old) || VariableResolver.Format(old) != value);
                    return new RunArgRow(p.Key, value, changed);
                })
                .ToArray();
        }

        static bool TryGet(IReadOnlyDictionary<string, object?> args, string key, out object? value)
        {
            if (args.TryGetValue(key, out value)) return true;
            foreach (var p in args)
            {
                if (!string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                value = p.Value;
                return true;
            }
            return false;
        }

        // Unchanged rows keep their instances (records compare by value), so the list does not flicker.
        static void Sync(ObservableCollection<RunArgRow> target, IReadOnlyList<RunArgRow> rows) =>
            target.SyncTo(rows.Select(r => target.FirstOrDefault(c => c == r) ?? r).ToArray());
    }

    /// <summary>Runs the selected run's action again with the arguments it had when it was queued.</summary>
    // Completes when the new run ends (it may wait in a paused queue), so allow running again meanwhile.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunAgain()
    {
        if (SelectedRun is not { } run) return;
        Status = $"Queued '{run.ActionName}' again with its original arguments.";
        try
        {
            var ok = await _engine.RequeueAsync(run.RunId);
            if (!ok) Status = $"Could not run '{run.ActionName}' again — see Dashboard log.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled from here or the Queues page; the history shows it.
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        if (SelectedRun is not { } run) return;
        Status = _engine.Cancel(run.RunId) ? $"Cancelled '{run.ActionName}'." : "That run already finished.";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _engine.History.Clear();
        Status = "History cleared (pending and running runs stay).";
    }

    [RelayCommand]
    private void OpenQueues() => OpenQueuesRequested?.Invoke();

    [RelayCommand]
    private void OpenAction()
    {
        if (SelectedRun is { } run) OpenActionRequested?.Invoke(run.Entry.ActionId);
    }
}
