namespace Cuebot.Twitch.EventSub;

/// <summary>Remembers the most recent message ids so a redelivered notification is handled once.</summary>
internal sealed class MessageDeduplicator(int capacity = 1000)
{
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>Returns false when <paramref name="messageId"/> was already seen.</summary>
    public bool TryAdd(string messageId)
    {
        lock (_gate)
        {
            if (!_seen.Add(messageId)) return false;
            _order.Enqueue(messageId);
            if (_order.Count > capacity) _seen.Remove(_order.Dequeue());
            return true;
        }
    }
}
