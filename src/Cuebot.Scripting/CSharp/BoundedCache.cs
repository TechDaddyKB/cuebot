namespace Cuebot.Scripting;

/// <summary>A small thread-safe least-recently-used cache.</summary>
internal sealed class BoundedCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _index = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_index.Remove(key, out var existing)) _order.Remove(existing);
            _index[key] = _order.AddFirst((key, value));
            while (_index.Count > capacity)
            {
                _index.Remove(_order.Last!.Value.Key);
                _order.RemoveLast();
            }
        }
    }
}
