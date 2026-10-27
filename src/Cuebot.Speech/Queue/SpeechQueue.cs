namespace Cuebot.Speech;

/// <summary>Thread-safe priority FIFO with a length limit and removal of arbitrary items.</summary>
internal sealed class SpeechQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<SpeechRequest> _items = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;

    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    public IReadOnlyList<SpeechRequest> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    /// <summary>
    /// Adds <paramref name="request"/> after every item of equal or higher priority. When the queue holds
    /// <paramref name="maxLength"/> items (0 = unlimited), DropNewest refuses the new item and DropOldest evicts the
    /// oldest item of the lowest priority; the refused or evicted request is returned in <paramref name="dropped"/>.
    /// </summary>
    /// <returns>False when the request was refused (queue full or completed).</returns>
    public bool TryEnqueue(SpeechRequest request, int maxLength, QueueDropPolicy policy, out SpeechRequest? dropped)
    {
        dropped = null;
        lock (_gate)
        {
            if (_completed) return false;
            if (maxLength > 0 && _items.Count >= maxLength)
            {
                if (policy == QueueDropPolicy.DropNewest)
                {
                    dropped = request;
                    return false;
                }
                var lowest = _items.Min(i => i.Options.Priority);
                if (request.Options.Priority < lowest)
                {
                    dropped = request;
                    return false;
                }
                var victim = _items.First(i => i.Options.Priority == lowest);
                _items.Remove(victim);
                dropped = victim;
            }
            var before = _items.First;
            while (before is not null && before.Value.Options.Priority >= request.Options.Priority) before = before.Next;
            if (before is null) _items.AddLast(request);
            else _items.AddBefore(before, request);
        }
        if (dropped is null) _signal.Release();
        return true;
    }

    /// <summary>Waits until an item may be available (it may since have been removed).</summary>
    public Task WaitAsync(CancellationToken ct) => _signal.WaitAsync(ct);

    public SpeechRequest? TryTake()
    {
        lock (_gate)
        {
            var first = _items.First;
            if (first is null) return null;
            _items.RemoveFirst();
            return first.Value;
        }
    }

    public List<SpeechRequest> RemoveAll(Func<SpeechRequest, bool> match)
    {
        var removed = new List<SpeechRequest>();
        lock (_gate)
        {
            for (var node = _items.First; node is not null;)
            {
                var next = node.Next;
                if (match(node.Value))
                {
                    removed.Add(node.Value);
                    _items.Remove(node);
                }
                node = next;
            }
        }
        return removed;
    }

    /// <summary>Refuses further items.</summary>
    public void Complete()
    {
        lock (_gate) _completed = true;
        _signal.Release();
    }
}
