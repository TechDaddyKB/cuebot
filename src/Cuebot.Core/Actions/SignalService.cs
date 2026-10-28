namespace Cuebot.Core.Actions;

/// <summary>
/// Named in-process signals ("Send local signal" / "Wait for local signal"). A wait only sees signals sent after it
/// started, unless the sender queued the signal: a queued signal nobody was waiting for is kept and handed to the next
/// wait for that name (oldest first). One send releases every current waiter of that name. Names are case-insensitive.
/// </summary>
public sealed class SignalService
{
    /// <summary>At most this many queued signals are kept per name; older ones are dropped.</summary>
    public const int MaxQueuedPerName = 100;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<TaskCompletionSource<IReadOnlyDictionary<string, object?>>>> _waiters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<IReadOnlyDictionary<string, object?>>> _queued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a signal is sent (name, number of waiters released).</summary>
    public event Action<string, int>? Sent;

    /// <summary>Releases everyone waiting for <paramref name="name"/>. Returns how many waiters were released.</summary>
    /// <param name="queue">When nobody is waiting, keep the signal for the next wait instead of dropping it.</param>
    public int Send(string name, IReadOnlyDictionary<string, object?>? payload = null, bool queue = false)
    {
        List<TaskCompletionSource<IReadOnlyDictionary<string, object?>>>? waiters;
        var data = payload ?? new Dictionary<string, object?>();
        lock (_gate)
        {
            _waiters.Remove(name, out waiters);
            if (queue && (waiters is null || waiters.Count == 0))
            {
                if (!_queued.TryGetValue(name, out var pending)) _queued[name] = pending = new();
                pending.Enqueue(data);
                while (pending.Count > MaxQueuedPerName) pending.Dequeue();
            }
        }
        var released = 0;
        foreach (var w in waiters ?? [])
        {
            if (w.TrySetResult(data)) released++;
        }
        Sent?.Invoke(name, released);
        return released;
    }

    /// <summary>Number of runs currently waiting for <paramref name="name"/>.</summary>
    public int WaitingCount(string name)
    {
        lock (_gate) return _waiters.TryGetValue(name, out var list) ? list.Count : 0;
    }

    /// <summary>Number of queued signals of <paramref name="name"/> that no wait has picked up yet.</summary>
    public int QueuedCount(string name)
    {
        lock (_gate) return _queued.TryGetValue(name, out var q) ? q.Count : 0;
    }

    /// <summary>
    /// Waits for the next <see cref="Send"/> of <paramref name="name"/> (or takes the oldest queued one). Returns the
    /// payload, or null on timeout. A null or infinite <paramref name="timeout"/> waits until cancelled.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>?> WaitAsync(string name, TimeSpan? timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_queued.TryGetValue(name, out var pending) && pending.Count > 0)
            {
                var data = pending.Dequeue();
                if (pending.Count == 0) _queued.Remove(name);
                return data;
            }
            if (!_waiters.TryGetValue(name, out var list)) _waiters[name] = list = [];
            list.Add(tcs);
        }
        try
        {
            var wait = timeout is { } t && t != Timeout.InfiniteTimeSpan
                ? tcs.Task.WaitAsync(t < TimeSpan.Zero ? TimeSpan.Zero : t, ct)
                : tcs.Task.WaitAsync(ct);
            return await wait.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (_waiters.TryGetValue(name, out var list))
                {
                    list.Remove(tcs);
                    if (list.Count == 0) _waiters.Remove(name);
                }
            }
        }
    }
}
