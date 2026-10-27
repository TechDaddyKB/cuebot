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
