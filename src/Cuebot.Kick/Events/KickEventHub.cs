using System.Collections.Concurrent;
using Cuebot.Core.Events;

namespace Cuebot.Kick.Events;

/// <summary>
/// Merges every <see cref="IKickEventSource"/> (plus API polling) into bus events: drops duplicates of the same
/// occurrence (same <see cref="KickEvent.DedupeKey"/> within a window, e.g. a chat message from both the websocket
/// and the webhook relay), turns stream online/offline into state transitions, publishes <c>kick.*</c> events and
/// the platform-neutral <c>chat.messageDeleted</c> / <c>chat.userBanned</c> / <c>chat.userTimedOut</c>, and
/// remembers user ids by name for moderation by user name.
/// </summary>
internal sealed class KickEventHub(EventBus bus, TimeProvider time, TimeSpan dedupeWindow)
{
    private readonly RecentKeys _recent = new(dedupeWindow);
    private readonly ConcurrentDictionary<string, string> _userIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _liveGate = new();
    private bool? _isLive;

    /// <summary>Raised after each published kick.* event.</summary>
    public event Action<KickEvent>? Published;

    /// <summary>Live state as last reported by any source; null until known.</summary>
    public bool? IsLive
    {
        get { lock (_liveGate) return _isLive; }
    }

    public void Attach(IKickEventSource source) => source.EventReceived += Handle;

    public void Detach(IKickEventSource source) => source.EventReceived -= Handle;

    /// <summary>Records the live state without publishing (first API poll after connecting).</summary>
    public void SetInitialLive(bool live)
    {
        lock (_liveGate) _isLive ??= live;
    }

    public void ResetLive()
    {
        lock (_liveGate) _isLive = null;
    }

    /// <summary>A user id seen for <paramref name="userName"/> in any event, or null.</summary>
    public string? FindUserId(string userName) =>
        _userIds.TryGetValue(userName.Trim().TrimStart('@'), out var id) ? id : null;

    public void Handle(KickEvent e)
    {
        if (!ShouldPublish(e)) return;
        Remember(e.Args);
        bus.Publish(new BotEvent(e.Type, e.Args));
        PublishNeutral(e);
        Published?.Invoke(e);
    }

    private bool ShouldPublish(KickEvent e)
    {
        switch (e.Type)
        {
            case KickEventTypes.StreamOnline or KickEventTypes.StreamOffline:
                var live = e.Type == KickEventTypes.StreamOnline;
                lock (_liveGate)
                {
                    if (_isLive == live) return false;
                    _isLive = live;
                    return true;
                }
            case KickEventTypes.ViewerCountUpdate:
                return true;
            default:
                return _recent.TryAdd(e.Type + "|" + e.DedupeKey, time.GetUtcNow());
        }
    }

    private void PublishNeutral(KickEvent e)
    {
        var type = e.Type switch
        {
            KickEventTypes.MessageDeleted => KickEventTypes.NeutralMessageDeleted,
            KickEventTypes.UserBanned => KickEventTypes.NeutralUserBanned,
            KickEventTypes.UserTimedOut => KickEventTypes.NeutralUserTimedOut,
            _ => null,
        };
        if (type is not null) bus.Publish(new BotEvent(type, e.Args));
    }

    private void Remember(IReadOnlyDictionary<string, object?> args)
    {
        Store(args.GetString("user"), args.GetString("userId"));
        Store(args.GetString("recipient"), args.GetString("recipientId"));
        Store(args.GetString("replyToUser"), args.GetString("replyToUserId"));
        Store(args.GetString("moderator"), args.GetString("moderatorId"));
    }

    private void Store(string name, string id)
    {
        if (name.Length == 0 || id.Length == 0 || id == "0") return;
        if (_userIds.Count > 20_000) _userIds.Clear();
        _userIds[name] = id;
    }
}
