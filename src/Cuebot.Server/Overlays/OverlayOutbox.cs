using Cuebot.Core.Overlays;

namespace Cuebot.Server.Overlays;

/// <summary>
/// Messages waiting for one overlay page. Past <see cref="SoftLimit"/> the oldest droppable message (chat, session
/// updates) makes room; alerts and commands are kept until <see cref="HardLimit"/>, after which the oldest of anything
/// goes, so a page that stopped reading cannot grow memory forever.
/// </summary>
internal sealed class OverlayOutbox : IDisposable
{
    public const int SoftLimit = 500;
    public const int HardLimit = 5000;

    private readonly LinkedList<OverlayMessage> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);

    public int Count
    {
        get
        {
            lock (_queue) return _queue.Count;
        }
    }

    public void Enqueue(OverlayMessage message)
    {
        lock (_queue)
        {
            _queue.AddLast(message);
            if (_queue.Count > SoftLimit)
            {
                var node = _queue.First;
                while (node is not null && !node.Value.Droppable) node = node.Next;
                if (node is not null) _queue.Remove(node);
                else if (_queue.Count > HardLimit) _queue.RemoveFirst();
            }
        }
        try { _signal.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Waits until something is queued, then returns everything queued so far, oldest first.</summary>
    public async Task<IReadOnlyList<OverlayMessage>> TakeAllAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);
        lock (_queue)
        {
            var items = _queue.ToArray();
            _queue.Clear();
            return items;
        }
    }

    public void Dispose() => _signal.Dispose();
}
