using System.Collections.Concurrent;
using Cuebot.Core.Events;
using Cuebot.Kick.Api;
using Cuebot.Kick.Compat;

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
    private readonly object _memoryGate = new();
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _sentOrder = new();
    private bool? _isLive;
    private (string Title, string CategoryId, string CategoryName, string CategoryThumbnail)? _channel;
    private (string Title, string StartedAt)? _stream;

    /// <summary>The broadcaster account for Streamer.bot's broadcastUser* arguments (null until signed in).</summary>
    public Func<KickBroadcasterArgs?>? Broadcaster { get; set; }

    /// <summary>The current title and category, so the next channel update can report what changed.</summary>
    public void SeedChannel(KickChannel channel)
    {
        lock (_memoryGate)
            _channel = (channel.Title, channel.Category is { Id: > 0 } c ? KickArgs.Id(c.Id) : "", channel.Category?.Name ?? "",
                channel.Category?.Thumbnail ?? "");
    }

    /// <summary>Remembers a message Cuebot sent, so its echo in chat is flagged <c>isInternal</c>.</summary>
    public void MarkSent(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        lock (_memoryGate)
        {
            if (!_sent.Add(messageId)) return;
            _sentOrder.Enqueue(messageId);
            if (_sentOrder.Count > 200) _sent.Remove(_sentOrder.Dequeue());
        }
    }

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
        bus.Publish(new BotEvent(e.Type, WithStreamerBotNames(e)));
        PublishNeutral(e);
        Published?.Invoke(e);
    }

    /// <summary>
    /// The event's arguments plus Streamer.bot's names, including those that need memory across events: old title
    /// and category, the stream's category and start, whether a chat message was sent by Cuebot.
    /// </summary>
    private Dictionary<string, object?> WithStreamerBotNames(KickEvent e)
    {
        var a = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
        lock (_memoryGate)
        {
            switch (e.Type)
            {
                case KickEventTypes.ChatMessage:
                    a.TryAdd("isInternal", _sent.Contains(a.GetString("messageId")));
                    break;
                case KickEventTypes.ChannelUpdate:
                    var now = (a.GetString("title"), a.GetString("categoryId"), a.GetString("category"), a.GetString("categoryThumbnail"));
                    if (_channel is { } old)
                    {
                        a["statusUpdated"] = old.Title != now.Item1;
                        a["categoryUpdated"] = old.CategoryId != now.Item2;
                        a["oldStatus"] = old.Title;
                        a["oldCategoryId"] = old.CategoryId;
                        a["oldCategoryName"] = old.CategoryName;
                        a["oldCategoryThumbnail"] = old.CategoryThumbnail;
                        if (now.Item4.Length == 0 && !a.ContainsKey("categoryThumbnail") && old.CategoryId == now.Item2)
                            now.Item4 = old.CategoryThumbnail;
                    }
                    _channel = now;
                    a.TryAdd("categoryThumbnail", now.Item4);
                    break;
                case KickEventTypes.StreamOnline:
                    _stream = (a.GetString("title"), a.GetString("startedAt"));
                    if (_channel is { } live)
                    {
                        a.TryAdd("categoryName", live.CategoryName);
                        a.TryAdd("categoryId", live.CategoryId);
                        a.TryAdd("categoryThumbnail", live.CategoryThumbnail);
                    }
                    break;
                case KickEventTypes.StreamOffline:
                    if (_stream is { } ended)
                    {
                        a.TryAdd("title", ended.Title);
                        if (ended.StartedAt.Length > 0) a.TryAdd("startedAt", ended.StartedAt);
                    }
                    _stream = null;
                    break;
            }
        }
        StreamerBotVariables.Apply(e.Type, a, Broadcaster?.Invoke(), time.GetLocalNow());
        return a;
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
