namespace Cuebot.Rumble;

/// <summary>A set that remembers only its most recent <c>capacity</c> entries. Thread-safe.</summary>
internal sealed class RecentSet<T>(int capacity) where T : notnull
{
    private readonly HashSet<T> _items = [];
    private readonly Queue<T> _order = new();
    private readonly object _gate = new();

    /// <summary>Adds the item; returns false when it was already present.</summary>
    public bool Add(T item)
    {
        lock (_gate)
        {
            if (!_items.Add(item)) return false;
            _order.Enqueue(item);
            while (_order.Count > capacity) _items.Remove(_order.Dequeue());
            return true;
        }
    }
}
