namespace Cuebot.Rumble.Chat;

/// <summary>
/// Keeps a message that reached Cuebot through one source (the live chat stream or the Live Stream API) from being
/// published again when the other source reports it too, e.g. right after the chat stream reconnects. Each
/// recorded message cancels at most one echo, and only within a few minutes, so a chatter repeating themselves is
/// still heard. The API has no message ids, so messages are matched by author, text and time. Thread-safe.
/// </summary>
internal sealed class ChatEchoFilter
{
    private const int Capacity = 500;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    private readonly LinkedList<Entry> _entries = new();
    private readonly object _gate = new();

    public void Record(string user, string text, DateTimeOffset time)
    {
        lock (_gate)
        {
            _entries.AddLast(new Entry(user, text, time));
            if (_entries.Count > Capacity) _entries.RemoveFirst();
        }
    }

    /// <summary>Returns true (and forgets the record) when a matching message was recorded.</summary>
    public bool TryConsume(string user, string text, DateTimeOffset time)
    {
        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                var e = node.Value;
                if ((e.Time - time).Duration() <= Window && e.Text == text && string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase))
                {
                    _entries.Remove(node);
                    return true;
                }
            }
            return false;
        }
    }

    private sealed record Entry(string User, string Text, DateTimeOffset Time);
}
