using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Chat;

/// <summary>
/// Combined chat and activity feed. Turns every platform's chat and activity events into
/// <see cref="ChatMessage"/>s and <see cref="FeedItem"/>s, keeps recent history, re-publishes them as
/// platform-neutral events ("chat.message", "chat.activity") for any-platform triggers, and sends chat to
/// one platform or all of them.
/// </summary>
public sealed class ChatHub : IDisposable
{
    public const string MessageEvent = "chat.message";
    public const string ActivityEvent = "chat.activity";
    public const string AllPlatforms = "All";
    private const int HistorySize = 500;

    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IReadOnlyList<IChatEventAdapter> _adapters;
    private readonly IReadOnlyList<IChatSender> _senders;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Queue<ChatMessage> _messages = new();
    private readonly Queue<FeedItem> _feed = new();

    public ChatHub(EventBus bus, AppLog log, IEnumerable<IChatEventAdapter> adapters, IEnumerable<IChatSender> senders)
    {
        _bus = bus;
        _log = log;
        _adapters = adapters.ToArray();
        _senders = senders.ToArray();
        _subscription = bus.Subscribe(OnEvent);
    }

    public event Action<ChatMessage>? MessageReceived;
    public event Action<FeedItem>? FeedItemAdded;

    /// <summary>Platforms that have a chat adapter, e.g. "Twitch", "Rumble".</summary>
    public IReadOnlyList<string> Platforms => _adapters.Select(a => a.Platform).Distinct().ToArray();

    public IReadOnlyList<string> SendablePlatforms => _senders.Where(s => s.CanSend).Select(s => s.Platform).ToArray();

    public IReadOnlyList<ChatMessage> RecentMessages()
    {
        lock (_gate) return _messages.ToArray();
    }

    public IReadOnlyList<FeedItem> RecentFeed()
    {
        lock (_gate) return _feed.ToArray();
    }

    /// <summary>
    /// Sends to one platform, or to every connected one with <see cref="AllPlatforms"/>. Returns the platforms
    /// that accepted the message; throws when nothing could send, with each platform's reason.
    /// </summary>
    public async Task<IReadOnlyList<string>> SendAsync(string platform, string text, CancellationToken ct = default)
    {
        var targets = string.Equals(platform, AllPlatforms, StringComparison.OrdinalIgnoreCase)
            ? _senders.Where(s => s.CanSend).ToArray()
            : _senders.Where(s => string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException(platform == AllPlatforms ? "No chat platform is connected" : $"{platform} cannot send chat messages");

        var sent = new List<string>();
        var errors = new List<string>();
        foreach (var sender in targets)
        {
            try
            {
                await sender.SendAsync(text, ct).ConfigureAwait(false);
                sent.Add(sender.Platform);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{sender.Platform}: {ex.Message}");
                _log.Warn("Chat", $"Could not send to {sender.Platform}: {ex.Message}");
            }
        }
        if (sent.Count == 0) throw new InvalidOperationException(string.Join("; ", errors));
        return sent;
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type is MessageEvent or ActivityEvent) return;
        foreach (var adapter in _adapters)
        {
            if (adapter.ToChatMessage(e) is { } message) Add(message);
            if (adapter.ToFeedItem(e) is { } item) Add(item);
        }
    }

    private void Add(ChatMessage message)
    {
        lock (_gate) Push(_messages, message);
        MessageReceived?.Invoke(message);
        _bus.Publish(new BotEvent(MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = message.Platform,
            ["user"] = message.User,
            ["userId"] = message.UserId,
            ["message"] = message.Text,
            ["rawInput"] = message.Text,
            ["messageId"] = message.Id,
            ["isBroadcaster"] = message.Roles.HasFlag(ChatRoles.Broadcaster),
            ["isModerator"] = message.Roles.HasFlag(ChatRoles.Moderator),
            ["isVip"] = message.Roles.HasFlag(ChatRoles.Vip),
            ["isSubscriber"] = message.Roles.HasFlag(ChatRoles.Subscriber),
            ["amount"] = message.Amount ?? "",
        }));
    }

    private void Add(FeedItem item)
    {
        lock (_gate) Push(_feed, item);
        FeedItemAdded?.Invoke(item);
        _bus.Publish(new BotEvent(ActivityEvent, new Dictionary<string, object?>
        {
            ["platform"] = item.Platform,
            ["kind"] = item.Kind.ToString(),
            ["user"] = item.User,
            ["title"] = item.Title,
            ["detail"] = item.Detail ?? "",
            ["amount"] = item.Amount ?? "",
        }));
    }

    private static void Push<T>(Queue<T> queue, T value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistorySize) queue.Dequeue();
    }

    public void Dispose() => _subscription.Dispose();
}
