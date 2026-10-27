using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;

namespace Cuebot.App.ViewModels;

/// <summary>One action queue: its definition (none for Default) and live state.</summary>
public sealed partial class QueueRowViewModel : ObservableObject
{
    private readonly QueuesViewModel _page;
    private string _nameText;

    public QueueRowViewModel(QueuesViewModel page, QueueDefinition? definition, QueueState state)
    {
        _page = page;
        Definition = definition;
        _state = state;
        _nameText = definition?.Name ?? state.Name;
    }

    /// <summary>Null for the built-in Default queue and for unknown queue names that runs referenced.</summary>
    public QueueDefinition? Definition { get; }

    public bool IsEditable => Definition is not null;
    public bool IsDefault => State.IsDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Paused), nameof(Counts))]
    private QueueState _state;

    [ObservableProperty] private string _nameError = "";

    public string Name => Definition?.Name ?? State.Name;
    public bool Paused => State.Paused;
    public string StatusText => State.Paused ? "Paused" : State.Running > 0 ? "Running" : "Idle";
    public string Counts => $"{State.Pending} pending · {State.Running} running · {State.Completed} completed";

    public string Kind => IsDefault
        ? "Built-in · never blocks; actions without a queue run here"
        : Definition is null ? "Not in the profile (referenced by a run)" : "";

    /// <summary>Renames the queue (and every action in it) when the new name is valid.</summary>
    public string NameText
    {
        get => _nameText;
        set
        {
            if (_nameText == value) return;
            _nameText = value;
            OnPropertyChanged();
            NameError = Definition is null ? "" : _page.Rename(this, value) ?? "";
        }
    }

    public bool Blocking
    {
        get => Definition?.Blocking ?? false;
        set
        {
            if (Definition is null || Definition.Blocking == value) return;
            Definition.Blocking = value;
            OnPropertyChanged();
            _page.ProfileEdited();
        }
    }

    [RelayCommand]
    private void Pause() => _page.PauseCommand.Execute(this);

    [RelayCommand]
    private void Resume() => _page.ResumeCommand.Execute(this);

    [RelayCommand]
    private void Clear() => _page.ClearQueueCommand.Execute(this);

    [RelayCommand]
    private void Delete() => _page.DeleteQueueCommand.Execute(this);
}

/// <summary>One run in the Pending or History list.</summary>
public sealed partial class RunRowViewModel(ActionRunEntry entry) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Started), nameof(Duration), nameof(IsActive), nameof(IsFailed), nameof(IsDone),
        nameof(HasError))]
    private ActionRunEntry _entry = entry;

    public Guid RunId => Entry.RunId;
    public string ActionName => Entry.ActionName;
    public string Queue => Entry.Queue;
    public string Source => Entry.Source;
    public string StateText => Entry.State.ToString();
    public bool IsActive => Entry.IsActive;
    public bool IsFailed => Entry.State is ActionRunState.Failed or ActionRunState.Cancelled;
    public bool IsDone => Entry.State == ActionRunState.Completed;
    public bool HasError => !string.IsNullOrEmpty(Entry.Error);
    public string Queued => Time(Entry.QueuedAt);
    public string Started => Entry.StartedAt is { } s ? Time(s) : "—";

    public string Duration => Entry.Duration switch
    {
        null => Entry.State == ActionRunState.Running ? "running" : "—",
        { TotalMilliseconds: < 1000 } d => $"{d.TotalMilliseconds:0} ms",
        { TotalSeconds: < 60 } d => string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:0.00} s"),
        { } d => d.ToString(@"m\:ss", CultureInfo.InvariantCulture),
    };

    private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// Queues page: queue definitions (profile) with live state, and the engine's pending runs and run history with
/// argument inspection, requeue and cancel.
/// </summary>
public sealed partial class QueuesViewModel : PageViewModel
{
    private readonly ProfileService _profile;
    private readonly ActionEngine _engine;
    private readonly Dictionary<string, QueueRowViewModel> _queueRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, RunRowViewModel> _runRows = [];
    private bool _runsRefreshPosted;

    public QueuesViewModel(ProfileService profile, ActionEngine engine) : base("Queues")
    {
        _profile = profile;
        _engine = engine;
        profile.Changed += () => OnUi(RefreshQueues);
        engine.Queues.Changed += () => OnUi(RefreshQueues);
        engine.History.Changed += (_, _) => OnUi(ScheduleRunsRefresh);
        RefreshQueues();
        RefreshRuns();
    }

    public ObservableCollection<QueueRowViewModel> Queues { get; } = [];
    public ObservableCollection<RunRowViewModel> Pending { get; } = [];
    public ObservableCollection<RunRowViewModel> History { get; } = [];
    public ObservableCollection<NameValueRow> ArgsWhenQueued { get; } = [];
    public ObservableCollection<NameValueRow> ArgsAfterRun { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun), nameof(CanCancelSelected), nameof(SelectedTitle), nameof(HasArgsAfterRun))]
    private RunRowViewModel? _selectedRun;

    [ObservableProperty] private string _status = "";

    public bool HasSelectedRun => SelectedRun is not null;
    public bool CanCancelSelected => SelectedRun?.IsActive == true;
    public bool HasArgsAfterRun => SelectedRun?.Entry.ArgsAfterRun is not null;

    public string SelectedTitle => SelectedRun is { } r
        ? $"{r.ActionName} · {r.StateText}{(r.Entry.Error is { Length: > 0 } e ? " · " + e : "")}"
        : "";

    public string PendingSummary => Pending.Count == 0 ? "Nothing is waiting or running." : $"{Pending.Count} pending or running";
    public string HistorySummary => History.Count == 0
        ? "No finished runs yet. Actions marked 'Exclude from history' never appear here."
        : $"{History.Count} finished run(s), newest first (the last {_engine.History.Capacity} are kept)";

    partial void OnSelectedRunChanged(RunRowViewModel? value) => ShowArgs();

    // Each list has its own selection (a list box clears a selected item it does not contain); the inspector shows
    // whichever was picked last.
    [ObservableProperty] private RunRowViewModel? _selectedPending;
    [ObservableProperty] private RunRowViewModel? _selectedHistory;

    partial void OnSelectedPendingChanged(RunRowViewModel? value)
    {
        if (value is null) return;
        SelectedHistory = null;
        SelectedRun = value;
    }

    partial void OnSelectedHistoryChanged(RunRowViewModel? value)
    {
        if (value is null) return;
        SelectedPending = null;
        SelectedRun = value;
    }

    // ---- queues ----

    private void RefreshQueues()
    {
        var states = _engine.Queues.Queues;
        var rows = new List<QueueRowViewModel>();
        foreach (var state in states)
        {
            var definition = _profile.Profile.Queues.FirstOrDefault(q => string.Equals(q.Name, state.Name, StringComparison.OrdinalIgnoreCase));
            var row = Row(state.Name, definition, state);
            rows.Add(row);
        }
        // Queues just added to the live profile may not be in the engine snapshot yet.
        foreach (var definition in _profile.Profile.Queues.Where(q => rows.All(r => r.Definition != q)))
            rows.Add(Row(definition.Name, definition, _engine.Queues.GetState(definition.Name)));
        foreach (var stale in _queueRows.Where(p => !rows.Contains(p.Value)).Select(p => p.Key).ToArray()) _queueRows.Remove(stale);
        Queues.SyncTo(rows);
    }

    private QueueRowViewModel Row(string name, QueueDefinition? definition, QueueState state)
    {
        var existing = _queueRows.Values.FirstOrDefault(r => definition is not null ? r.Definition == definition
            : r.Definition is null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new QueueRowViewModel(this, definition, state);
            _queueRows[definition?.Id.ToString() ?? name] = existing;
        }
        else
        {
            existing.State = state;
        }
        return existing;
    }

    internal void ProfileEdited() => _profile.NotifyChanged();

    /// <summary>Applies a rename; returns an error message when the name is not usable.</summary>
    internal string? Rename(QueueRowViewModel row, string newName)
    {
        if (row.Definition is not { } definition) return null;
        var name = newName.Trim();
        if (name.Length == 0) return "A queue needs a name.";
        if (string.Equals(name, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase)) return "'Default' is the built-in queue.";
        if (_profile.Profile.Queues.Any(q => q != definition && string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "Another queue has this name.";
        if (definition.Name == name) return null;
        var old = definition.Name;
        definition.Name = name;
        // Actions in the queue follow the rename.
        foreach (var action in _profile.Profile.Actions.Where(a => string.Equals(a.Queue, old, StringComparison.OrdinalIgnoreCase)))
            action.Queue = name;
        _profile.NotifyChanged();
        return null;
    }

    [RelayCommand]
    private void AddQueue()
    {
        var name = "New queue";
        for (var i = 2; _profile.Profile.Queues.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"New queue {i}";
        _profile.Profile.Queues.Add(new QueueDefinition { Name = name });
        _profile.NotifyChanged();
        RefreshQueues();
    }

    /// <summary>Deletes a queue; its actions move to the Default queue.</summary>
    [RelayCommand]
    private void DeleteQueue(QueueRowViewModel row)
    {
        if (row.Definition is not { } definition) return;
        _profile.Profile.Queues.Remove(definition);
        var moved = 0;
        foreach (var action in _profile.Profile.Actions.Where(a => string.Equals(a.Queue, definition.Name, StringComparison.OrdinalIgnoreCase)))
        {
            action.Queue = "";
            moved++;
        }
        _profile.NotifyChanged();
        Status = moved == 0 ? $"Deleted queue '{definition.Name}'." : $"Deleted queue '{definition.Name}'; {moved} action(s) moved to Default.";
        RefreshQueues();
    }

    [RelayCommand]
    private void Pause(QueueRowViewModel row) => _engine.Queues.Pause(row.Name);

    [RelayCommand]
    private void Resume(QueueRowViewModel row) => _engine.Queues.Resume(row.Name);

    [RelayCommand]
    private void ClearQueue(QueueRowViewModel row)
    {
        var count = _engine.Queues.Clear(row.Name);
        Status = $"Cancelled {count} pending run(s) in '{row.Name}'.";
    }

    [RelayCommand]
    private void PauseAll() => _engine.Queues.PauseAll();

    [RelayCommand]
    private void ResumeAll() => _engine.Queues.ResumeAll();

    // ---- runs ----

    private void ScheduleRunsRefresh()
    {
        if (_runsRefreshPosted) return;
        _runsRefreshPosted = true;
        // Bursts of history changes (a queue resuming, a long run list) are applied in one pass.
        Dispatcher.UIThread.Post(() =>
        {
            _runsRefreshPosted = false;
            RefreshRuns();
        }, DispatcherPriority.Background);
    }

    internal void RefreshRuns()
    {
        var entries = _engine.History.Entries;
        var seen = new HashSet<Guid>();
        var pending = new List<RunRowViewModel>();
        var history = new List<RunRowViewModel>();
        foreach (var entry in entries)
        {
            seen.Add(entry.RunId);
            if (_runRows.TryGetValue(entry.RunId, out var row)) row.Entry = entry;
            else _runRows[entry.RunId] = row = new RunRowViewModel(entry);
            (entry.IsActive ? pending : history).Add(row);
        }
        foreach (var gone in _runRows.Keys.Where(id => !seen.Contains(id)).ToArray()) _runRows.Remove(gone);
        pending.Reverse(); // oldest first, in the order they will start
        var selected = SelectedRun;
        Pending.SyncTo(pending);
        History.SyncTo(history);
        SelectedRun = selected is not null && _runRows.ContainsKey(selected.RunId) ? selected : null;
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(HistorySummary));
        OnPropertyChanged(nameof(CanCancelSelected));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(HasArgsAfterRun));
        ShowArgs();
    }

    private void ShowArgs()
    {
        Show(ArgsWhenQueued, SelectedRun?.Entry.ArgsWhenQueued);
        Show(ArgsAfterRun, SelectedRun?.Entry.ArgsAfterRun);

        static void Show(ObservableCollection<NameValueRow> target, IReadOnlyDictionary<string, object?>? args) =>
            target.SyncTo(Rows(target, args));

        static IReadOnlyList<NameValueRow> Rows(ObservableCollection<NameValueRow> current, IReadOnlyDictionary<string, object?>? args)
        {
            if (args is null) return [];
            var rows = args.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new NameValueRow(p.Key, VariableResolver.Format(p.Value)))
                .ToArray();
            // Unchanged rows keep their instances (records compare by value), so the list does not flicker.
            return rows.Select(r => current.FirstOrDefault(c => c == r) ?? r).ToArray();
        }
    }

    // The command completes when the requeued run ends (it may wait in a paused queue), so allow requeueing again meanwhile.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Requeue()
    {
        if (SelectedRun is not { } run) return;
        Status = $"Requeued '{run.ActionName}'.";
        try
        {
            var ok = await _engine.RequeueAsync(run.RunId);
            if (!ok) Status = $"Could not requeue '{run.ActionName}' — see Dashboard log.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled from this page or by clearing its queue; the history shows it.
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
}
