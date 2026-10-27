namespace Cuebot.Rumble.LiveApi;

/// <summary>
/// Picks the new entries out of a "recent items" window that the API returns again on every poll.
/// Items are identified by a key; identical items (same key) are told apart by how often the key occurs, so
/// two equal gifts in one window count as two. Remembering the highest count ever seen per key (instead of only
/// the previous window) means an entry that briefly drops out of a response is not reported again.
/// Not thread-safe; bounded to <see cref="Capacity"/> keys.
/// </summary>
internal sealed class NewItemFilter<TKey> where TKey : notnull
{
    public const int Capacity = 5000;

    private readonly Dictionary<TKey, int> _seenCounts = [];
    private readonly Queue<TKey> _order = new();

    /// <summary>Records <paramref name="items"/> and returns those not seen before, in their original order.</summary>
    public IReadOnlyList<T> TakeNew<T>(IEnumerable<T> items, Func<T, TKey> key)
    {
        var occurrences = new Dictionary<TKey, int>();
        var fresh = new List<T>();
        foreach (var item in items)
        {
            var k = key(item);
            var n = occurrences[k] = occurrences.GetValueOrDefault(k) + 1;
            if (n > _seenCounts.GetValueOrDefault(k)) fresh.Add(item);
        }

        foreach (var (k, count) in occurrences)
        {
            if (!_seenCounts.TryGetValue(k, out var seen)) _order.Enqueue(k);
            if (count > seen) _seenCounts[k] = count;
        }
        while (_order.Count > Capacity) _seenCounts.Remove(_order.Dequeue());
        return fresh;
    }
}
