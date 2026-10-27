using Cuebot.Core.Logging;

namespace Cuebot.Core.Events;

/// <summary>Something that happened on a platform (a chat message, a follow, an OBS scene change...).</summary>
public sealed class BotEvent
{
    public BotEvent(string type, IReadOnlyDictionary<string, object?>? args = null)
    {
        Type = type;
        Args = args is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Namespaced event type, e.g. "twitch.chatMessage" or "obs.sceneChanged".</summary>
    public string Type { get; }

    public IReadOnlyDictionary<string, object?> Args { get; }

    public DateTimeOffset Time { get; } = DateTimeOffset.Now;
}

/// <summary>In-process publish/subscribe hub. Handlers run synchronously and must not block.</summary>
public sealed class EventBus(AppLog log)
{
    private readonly object _gate = new();
    private Action<BotEvent>[] _handlers = [];

    public IDisposable Subscribe(Action<BotEvent> handler)
    {
        lock (_gate) _handlers = [.. _handlers, handler];
        return new Subscription(() =>
        {
            lock (_gate) _handlers = _handlers.Where(h => h != handler).ToArray();
        });
    }

    public void Publish(BotEvent e)
    {
        foreach (var handler in _handlers)
        {
            try { handler(e); }
            catch (Exception ex) { log.Error("Events", $"Handler failed for {e.Type}", ex); }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
