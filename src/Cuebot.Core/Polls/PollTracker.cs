using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;

namespace Cuebot.Core.Polls;

/// <summary>
/// The latest poll and prediction from each source (Twitch, YouTube, Cuebot), for overlay widgets and the app. Platform
/// events come in through the <see cref="IPollAdapter"/>s, Cuebot's own through <see cref="Set"/>. Every change goes to
/// overlay pages as the <c>cuebot-poll</c> event (vote counts while open may be dropped for a slow page; the final
/// state never is), and <see cref="Snapshot"/> is sent to pages when they connect.
/// </summary>
public sealed class PollTracker : IDisposable
{
    /// <summary>The overlay listener widgets get poll and prediction states on.</summary>
    public const string Listener = "cuebot-poll";

    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Kind), PollState> _latest = [];
    private readonly OverlayFeed _feed;
    private readonly AppLog _log;
    private readonly IReadOnlyList<IPollAdapter> _adapters;
    private readonly IDisposable _subscription;

    public PollTracker(EventBus bus, OverlayFeed feed, IEnumerable<IPollAdapter> adapters, AppLog log)
    {
        _feed = feed;
        _log = log;
        _adapters = adapters.ToArray();
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>Raised (on the caller's thread) with each new state.</summary>
    public event Action<PollState>? Changed;

    /// <summary>The latest state per source and kind, newest first.</summary>
    public IReadOnlyList<PollState> Current
    {
        get
        {
            lock (_gate) return _latest.Values.ToArray();
        }
    }

    /// <summary>Every latest state as JSON (for an overlay page's hello).</summary>
    public JsonArray Snapshot() => new([.. Current.Select(p => (JsonNode)p.ToJson())]);

    /// <summary>Records a state and sends it to overlay pages.</summary>
    public void Set(PollState state)
    {
        lock (_gate) _latest[(state.Source, state.Kind)] = state;
        var message = new JsonObject { ["type"] = "event", ["listener"] = Listener, ["event"] = state.ToJson() }.ToJsonString();
        _feed.Publish(new OverlayMessage(message, Droppable: state.Status == PollState.Active));
        Changed?.Invoke(state);
    }

    private void OnEvent(BotEvent e)
    {
        foreach (var adapter in _adapters)
        {
            try
            {
                if (adapter.ToPoll(e) is { } state)
                {
                    Set(state);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Polls", $"Could not read {e.Type} as a poll", ex);
            }
        }
    }

    public void Dispose() => _subscription.Dispose();
}
