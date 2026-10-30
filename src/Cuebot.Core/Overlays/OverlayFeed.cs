using System.Text.Json.Nodes;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Overlays;

/// <summary>
/// A message for overlay pages, already serialized. <see cref="OverlayId"/> limits it to one overlay (null: every
/// overlay). <see cref="Droppable"/> messages (chat, session updates) may be discarded for a page that cannot keep up;
/// the rest (alerts, commands) never are.
/// </summary>
public sealed record OverlayMessage(string Json, Guid? OverlayId = null, bool Droppable = false)
{
    /// <summary>An event in StreamElements' shape: <c>{"type":"event","listener":...,"event":...}</c>. Chat is droppable.</summary>
    public static OverlayMessage Event(string listener, JsonNode? data, Guid? overlayId = null) =>
        new(new JsonObject { ["type"] = "event", ["listener"] = listener, ["event"] = data?.DeepClone() }.ToJsonString(),
            overlayId, Droppable: listener is "message");

    /// <summary>The session data (<c>onSessionUpdate</c>); only the newest matters, so it is droppable.</summary>
    public static OverlayMessage Session(JsonObject session) =>
        new(new JsonObject { ["type"] = "session", ["session"] = session.DeepClone() }.ToJsonString(), Droppable: true);

    /// <summary>A command for the page, e.g. "reload", "mute", "unmute", "skip", "pause", "resume".</summary>
    public static OverlayMessage Command(string command, Guid? overlayId = null) =>
        new(new JsonObject { ["type"] = "command", ["command"] = command }.ToJsonString(), overlayId);
}

/// <summary>
/// Where event converters and the session store send messages for overlay pages; the overlay server subscribes and
/// delivers them. Handlers run on the publisher's thread and must not block.
/// </summary>
public sealed class OverlayFeed(AppLog log)
{
    private readonly object _gate = new();
    private Action<OverlayMessage>[] _handlers = [];

    public IDisposable Subscribe(Action<OverlayMessage> handler)
    {
        lock (_gate) _handlers = [.. _handlers, handler];
        return new Subscription(() =>
        {
            lock (_gate) _handlers = _handlers.Where(h => h != handler).ToArray();
        });
    }

    public void Publish(OverlayMessage message)
    {
        foreach (var handler in Volatile.Read(ref _handlers))
        {
            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                log.Error("Overlays", "An overlay feed handler failed", ex);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
