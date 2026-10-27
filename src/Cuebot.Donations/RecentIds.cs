namespace Cuebot.Donations;

/// <summary>Remembers the last few hundred event ids so a re-sent event is published once.</summary>
internal sealed class RecentIds(int capacity = 500)
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>True the first time an id is seen. Empty ids are never deduplicated.</summary>
    public bool Add(string id) => AddAll([id]);

    /// <summary>
    /// True when none of the (non-empty) keys was seen before; then all of them are remembered. An event without
    /// keys is always new.
    /// </summary>
    public bool AddAll(IReadOnlyList<string> keys)
    {
        var real = keys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (real.Length == 0) return true;
        lock (_gate)
        {
            if (real.Any(_set.Contains)) return false;
            foreach (var key in real)
            {
                _set.Add(key);
                _order.Enqueue(key);
            }
            while (_order.Count > capacity) _set.Remove(_order.Dequeue());
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _set.Clear();
            _order.Clear();
        }
    }
}
