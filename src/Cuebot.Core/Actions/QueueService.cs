namespace Cuebot.Core.Actions;

/// <summary>Live state of one action queue, for the Queues view.</summary>
public sealed record QueueState(string Name, bool Blocking, bool Paused, int Pending, int Running, int Completed)
{
    public bool IsDefault => string.Equals(Name, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Action queues. Every top-level run waits here until it may start:
/// <list type="bullet">
/// <item>a paused queue holds its runs (except actions marked Always Run);</item>
/// <item>a blocking queue runs one action at a time, in arrival order;</item>
/// <item>the built-in "Default" queue never blocks; actions without a queue land there, and unless marked
/// Concurrent, runs of the same such action still wait for each other (the pre-queue behaviour).</item>
/// </list>
/// Queue definitions (name, blocking) live in the profile; paused state and counters are runtime-only.
/// </summary>
public sealed class QueueService : IDisposable
{
    public const string DefaultQueue = "Default";

    private readonly ProfileService _profile;
    private readonly object _gate = new();
    private readonly Dictionary<string, Runtime> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _busyKeys = [];

    public QueueService(ProfileService profile)
    {
        _profile = profile;
        _profile.Changed += OnProfileChanged;
    }

    /// <summary>Raised (on any thread) when queues are paused/resumed/cleared or pending/running counts change.</summary>
    public event Action? Changed;

    /// <summary>"" or null means the Default queue.</summary>
    public static string Normalize(string? name) => string.IsNullOrWhiteSpace(name) ? DefaultQueue : name.Trim();

    /// <summary>Default first, then the profile's queues, then any unknown queue names that runs referenced.</summary>
    public IReadOnlyList<QueueState> Queues
    {
        get
        {
            var names = new List<string> { DefaultQueue };
            foreach (var q in _profile.Snapshot.Queues)
            {
                if (!names.Contains(q.Name, StringComparer.OrdinalIgnoreCase)) names.Add(q.Name);
            }
            lock (_gate)
            {
                foreach (var name in _queues.Keys)
                {
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
                return names.Select(StateLocked).ToArray();
            }
        }
    }

    public QueueState GetState(string? name)
    {
        lock (_gate) return StateLocked(Normalize(name));
    }

    public bool IsBlocking(string? name)
    {
        var normalized = Normalize(name);
        return !string.Equals(normalized, DefaultQueue, StringComparison.OrdinalIgnoreCase) &&
               _profile.Snapshot.FindQueue(normalized)?.Blocking == true;
    }

    public bool IsPaused(string? name)
    {
        lock (_gate) return _queues.TryGetValue(Normalize(name), out var q) && q.Paused;
    }

    public int GetPendingCount(string? name)
    {
        lock (_gate) return _queues.TryGetValue(Normalize(name), out var q) ? q.Waiting.Count : 0;
    }

    public void Pause(string? name) => SetPaused([Normalize(name)], true);

    public void Resume(string? name) => SetPaused([Normalize(name)], false);

    public void PauseAll() => SetPaused(Queues.Select(q => q.Name), true);

    public void ResumeAll() => SetPaused(Queues.Select(q => q.Name), false);

    /// <summary>Cancels every pending run in the queue (running ones continue). Returns how many were cancelled.</summary>
    public int Clear(string? name)
    {
        List<Request> cancelled;
        lock (_gate)
        {
            if (!_queues.TryGetValue(Normalize(name), out var q)) return 0;
            cancelled = [.. q.Waiting];
            q.Waiting.Clear();
        }
        foreach (var r in cancelled)
        {
            r.Registration.Unregister();
            r.Completion.TrySetCanceled();
        }
        if (cancelled.Count > 0) Changed?.Invoke();
        return cancelled.Count;
    }

    /// <summary>
    /// Waits until a run may start in <paramref name="queue"/>. Dispose the returned lease when the run ends.
    /// Throws <see cref="OperationCanceledException"/> when cancelled or when the queue is cleared.
    /// </summary>
    /// <param name="exclusiveKey">Runs sharing a key never overlap (used for non-concurrent actions without a queue).</param>
    internal Task<IDisposable> EnterAsync(string? queue, bool alwaysRun, Guid? exclusiveKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new Request(Normalize(queue), alwaysRun, exclusiveKey);
        lock (_gate)
        {
            var q = GetOrAddLocked(request.Queue);
            request.Node = q.Waiting.AddLast(request);
            PumpLocked(q);
            if (!request.Completion.Task.IsCompleted && ct.CanBeCanceled)
            {
                // Registered under the lock so PumpLocked/Clear always see (and dispose) it.
                request.Registration = ct.UnsafeRegister(_ => CancelWaiting(request, ct), null);
            }
        }
        Changed?.Invoke();
        return request.Completion.Task;
    }

    private void CancelWaiting(Request request, CancellationToken ct)
    {
        lock (_gate)
        {
            // Not in a list any more means it already started (or the queue was cleared).
            if (request.Node is not { List: { } list } node) return;
            list.Remove(node);
        }
        if (request.Completion.TrySetCanceled(ct)) Changed?.Invoke();
    }

    private void Release(Request request)
    {
        lock (_gate)
        {
            var q = GetOrAddLocked(request.Queue);
            q.Running--;
            q.Completed++;
            if (request.ExclusiveKey is { } key) _busyKeys.Remove(key);
            PumpAllLocked();
        }
        Changed?.Invoke();
    }

    private void SetPaused(IEnumerable<string> names, bool paused)
    {
        lock (_gate)
        {
            foreach (var name in names) GetOrAddLocked(name).Paused = paused;
            PumpAllLocked();
        }
        Changed?.Invoke();
    }

    private void OnProfileChanged()
    {
        // A queue may have stopped blocking.
        lock (_gate) PumpAllLocked();
        Changed?.Invoke();
    }

    private void PumpAllLocked()
    {
        foreach (var q in _queues.Values) PumpLocked(q);
    }

    private void PumpLocked(Runtime q)
    {
        var blocking = IsBlocking(q.Name);
        for (var node = q.Waiting.First; node is not null;)
        {
            var next = node.Next;
            var r = node.Value;
            if (blocking && q.Running > 0) break;
            var held = (q.Paused && !r.AlwaysRun) || (r.ExclusiveKey is { } k && _busyKeys.Contains(k));
            if (!held)
            {
                q.Waiting.Remove(node);
                q.Running++;
                if (r.ExclusiveKey is { } key) _busyKeys.Add(key);
                r.Registration.Unregister();
                r.Completion.TrySetResult(new Lease(this, r));
            }
            node = next;
        }
    }

    private Runtime GetOrAddLocked(string name)
    {
        if (!_queues.TryGetValue(name, out var q)) _queues[name] = q = new Runtime(DisplayName(name));
        return q;
    }

    private string DisplayName(string name) =>
        string.Equals(name, DefaultQueue, StringComparison.OrdinalIgnoreCase)
            ? DefaultQueue
            : _profile.Snapshot.FindQueue(name)?.Name ?? name;

    private QueueState StateLocked(string name)
    {
        var blocking = IsBlocking(name);
        return _queues.TryGetValue(name, out var q)
            ? new QueueState(DisplayName(name), blocking, q.Paused, q.Waiting.Count, q.Running, q.Completed)
            : new QueueState(DisplayName(name), blocking, false, 0, 0, 0);
    }

    public void Dispose() => _profile.Changed -= OnProfileChanged;

    private sealed class Runtime(string name)
    {
        public string Name { get; } = name;
        public bool Paused { get; set; }
        public int Running { get; set; }
        public int Completed { get; set; }
        public LinkedList<Request> Waiting { get; } = new();
    }

    private sealed class Request(string queue, bool alwaysRun, Guid? exclusiveKey)
    {
        public string Queue { get; } = queue;
        public bool AlwaysRun { get; } = alwaysRun;
        public Guid? ExclusiveKey { get; } = exclusiveKey;
        public LinkedListNode<Request>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(QueueService owner, Request request) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(request);
        }
    }
}
