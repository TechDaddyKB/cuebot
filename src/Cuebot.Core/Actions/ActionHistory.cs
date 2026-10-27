namespace Cuebot.Core.Actions;

public enum ActionRunState
{
    /// <summary>Waiting in its queue (paused, blocked, or behind another run of the same action).</summary>
    Pending,
    Running,
    Completed,

    /// <summary>Cancelled while pending (queue cleared, run cancelled) or while running (shutdown, cancel).</summary>
    Cancelled,
    Failed,
}

/// <summary>One action run as seen by the Pending Actions and Action History views. Immutable: each state change
/// produces a new entry with the same <see cref="RunId"/>.</summary>
public sealed record ActionRunEntry
{
    public required Guid RunId { get; init; }
    public required Guid ActionId { get; init; }
    public required string ActionName { get; init; }

    /// <summary>Queue the run waited in ("Default" for actions without a queue).</summary>
    public required string Queue { get; init; }

    /// <summary>What started it: trigger name, "Manual", "Requeue", "Test: ..." etc.</summary>
    public required string Source { get; init; }

    public ActionRunState State { get; init; } = ActionRunState.Pending;
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Execution time (from start to completion, excluding time spent pending).</summary>
    public TimeSpan? Duration { get; init; }

    public string? Error { get; init; }

    /// <summary>Nesting depth: 0 for runs started by triggers/UI/requeue, &gt;0 for Run action / script calls.</summary>
    public int Depth { get; init; }

    /// <summary>Copy of the arguments when the run was queued (what Requeue replays).</summary>
    public IReadOnlyDictionary<string, object?> ArgsWhenQueued { get; init; } = new Dictionary<string, object?>();

    /// <summary>Copy of the arguments after the run finished (null until then).</summary>
    public IReadOnlyDictionary<string, object?>? ArgsAfterRun { get; init; }

    public bool IsActive => State is ActionRunState.Pending or ActionRunState.Running;
}

public enum ActionHistoryChange { Added, Updated, Removed, Cleared }

/// <summary>
/// Bounded in-memory list of recent and pending runs (newest first). Thread-safe; <see cref="Changed"/> is raised on
/// the thread that changed the run, so UIs must marshal.
/// </summary>
public sealed class ActionHistory
{
    public const int DefaultCapacity = 500;
    private readonly object _gate = new();
    private readonly LinkedList<ActionRunEntry> _entries = new();
    private readonly Dictionary<Guid, LinkedListNode<ActionRunEntry>> _byRun = [];

    public ActionHistory(int capacity = DefaultCapacity) => Capacity = Math.Max(1, capacity);

    /// <summary>Maximum number of finished runs kept (pending/running ones are never evicted).</summary>
    public int Capacity { get; }

    /// <summary>Raised after an entry is added/updated/removed (entry is null for Cleared).</summary>
    public event Action<ActionHistoryChange, ActionRunEntry?>? Changed;

    /// <summary>All entries, newest first.</summary>
    public IReadOnlyList<ActionRunEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>Runs waiting in a queue, oldest first.</summary>
    public IReadOnlyList<ActionRunEntry> Pending
    {
        get { lock (_gate) return _entries.Where(e => e.State == ActionRunState.Pending).Reverse().ToArray(); }
    }

    public ActionRunEntry? Find(Guid runId)
    {
        lock (_gate) return _byRun.TryGetValue(runId, out var node) ? node.Value : null;
    }

    /// <summary>Removes finished entries (pending and running ones stay).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            for (var node = _entries.First; node is not null;)
            {
                var next = node.Next;
                if (!node.Value.IsActive)
                {
                    _byRun.Remove(node.Value.RunId);
                    _entries.Remove(node);
                }
                node = next;
            }
        }
        Changed?.Invoke(ActionHistoryChange.Cleared, null);
    }

    internal void Add(ActionRunEntry entry)
    {
        List<ActionRunEntry> evicted;
        lock (_gate)
        {
            _byRun[entry.RunId] = _entries.AddFirst(entry);
            evicted = TrimLocked();
        }
        Changed?.Invoke(ActionHistoryChange.Added, entry);
        foreach (var e in evicted) Changed?.Invoke(ActionHistoryChange.Removed, e);
    }

    internal void Update(Guid runId, Func<ActionRunEntry, ActionRunEntry> update)
    {
        ActionRunEntry updated;
        List<ActionRunEntry> evicted;
        lock (_gate)
        {
            if (!_byRun.TryGetValue(runId, out var node)) return;
            node.Value = updated = update(node.Value);
            evicted = updated.IsActive ? [] : TrimLocked();
        }
        Changed?.Invoke(ActionHistoryChange.Updated, updated);
        foreach (var e in evicted) Changed?.Invoke(ActionHistoryChange.Removed, e);
    }

    /// <summary>Drops the oldest finished entries beyond <see cref="Capacity"/>.</summary>
    private List<ActionRunEntry> TrimLocked()
    {
        var evicted = new List<ActionRunEntry>();
        var finished = _entries.Count(e => !e.IsActive);
        for (var node = _entries.Last; node is not null && finished > Capacity;)
        {
            var prev = node.Previous;
            if (!node.Value.IsActive)
            {
                evicted.Add(node.Value);
                _byRun.Remove(node.Value.RunId);
                _entries.Remove(node);
                finished--;
            }
            node = prev;
        }
        return evicted;
    }
}
