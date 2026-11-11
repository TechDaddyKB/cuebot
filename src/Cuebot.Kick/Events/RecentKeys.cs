namespace Cuebot.Kick.Events;

/// <summary>Keys seen within a time window, for de-duplication. Thread-safe and bounded.</summary>
public sealed class RecentKeys(TimeSpan window, int capacity = 5000)
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset At)> _order = new();
    private readonly object _gate = new();

    /// <summary>Returns false when <paramref name="key"/> was already added within the window.</summary>
    public bool TryAdd(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            if (_seen.TryGetValue(key, out var at) && now - at < window) return false;
            _seen[key] = now;
            _order.Enqueue((key, now));
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_order.Count > 0 && (now - _order.Peek().At >= window || _order.Count > capacity))
        {
            var (key, at) = _order.Dequeue();
            if (_seen.TryGetValue(key, out var current) && current == at) _seen.Remove(key);
        }
    }
}

/// <summary>
/// Pairs copies of one occurrence that arrive from different sources (the websocket and the webhook relay) while
/// letting repeats from the same source through: two identical KICKs gifts from one viewer are two gifts, but the
/// webhook copy of a websocket gift is the same gift. Each copy cancels at most one unmatched copy from another source.
/// </summary>
public sealed class CrossSourceKeys(TimeSpan window, int capacity = 5000)
{
    private readonly Dictionary<string, List<(string Source, DateTimeOffset At)>> _pending = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset At)> _order = new();
    private readonly object _gate = new();

    /// <summary>Returns false when <paramref name="key"/> is another source's copy of an occurrence seen within the window.</summary>
    public bool TryAdd(string key, string source, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            if (_pending.TryGetValue(key, out var copies))
            {
                var match = copies.FindIndex(c => !string.Equals(c.Source, source, StringComparison.Ordinal) && now - c.At < window);
                if (match >= 0)
                {
                    copies.RemoveAt(match);
                    return false;
                }
            }
            else
            {
                _pending[key] = copies = [];
            }
            copies.Add((source, now));
            _order.Enqueue((key, now));
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_order.Count > 0 && (now - _order.Peek().At >= window || _order.Count > capacity))
        {
            var (key, at) = _order.Dequeue();
            if (!_pending.TryGetValue(key, out var copies)) continue;
            copies.RemoveAll(c => c.At <= at);
            if (copies.Count == 0) _pending.Remove(key);
        }
    }
}
