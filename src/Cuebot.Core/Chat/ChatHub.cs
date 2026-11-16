using Cuebot.Core.Commands;
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

    /// <summary>Platform-neutral moderation events the platform modules publish (also read by speech and overlays).</summary>
    public const string MessageDeletedEvent = "chat.messageDeleted";
    public const string UserBannedEvent = "chat.userBanned";
    public const string UserTimedOutEvent = "chat.userTimedOut";

    /// <summary>
    /// A moderation action a platform reports in full, whoever made it (Twitch <c>channel.moderate</c>). Args: platform,
    /// action (the mod log's name: ban, timeout, unban, delete, mode, term-add...), moderator, moderatorUserId, userId,
    /// user, userName, messageId, message, duration (seconds), reason, channelId (another channel of a shared chat).
    /// </summary>
    public const string ModerationActionEvent = "chat.moderationAction";

    private const int HistorySize = 500;

    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IReadOnlyList<IChatEventAdapter> _adapters;
    private readonly IReadOnlyList<IChatSender> _senders;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Queue<ChatMessage> _messages = new();
    private readonly Queue<FeedItem> _feed = new();
    private readonly Queue<ChatModerationNotice> _moderation = new();
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Raised when a platform reports a deleted message, a ban or a timeout, whoever made it (Cuebot, a mod on the
    /// website, another tool), so every client can mark the lines and the mod log can record outside actions.
    /// </summary>
    public event Action<ChatModerationNotice>? ModerationSeen;

    /// <summary>Raised for every <see cref="ModerationActionEvent"/>: the mod log records these actions made outside Cuebot.</summary>
    public event Action<Moderation.ExternalModerationNotice>? ModerationActionSeen;

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

    /// <summary>Recent deletions, bans and timeouts, oldest first.</summary>
    public IReadOnlyList<ChatModerationNotice> RecentModeration()
    {
        lock (_gate) return _moderation.ToArray();
    }

    /// <summary>True when the platform reported this message deleted.</summary>
    public bool IsDeleted(ChatMessage message)
    {
        lock (_gate) return _deleted.Contains(Key(message.Platform, message.Id));
    }

    /// <summary>True when some connected platform can send this as the bot account (<see cref="AllPlatforms"/>: any of them).</summary>
    public bool CanSendAsBot(string platform) =>
        _senders.Any(s => s.CanSend && s.CanSendAsBot
                          && (platform == AllPlatforms || string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Sends to one platform, or to every connected one with <see cref="AllPlatforms"/>. Returns the platforms
    /// that accepted the message; throws when nothing could send, with each platform's reason.
    /// </summary>
    public Task<IReadOnlyList<string>> SendAsync(string platform, string text, CancellationToken ct = default) =>
        SendAsync(platform, text, ChatSendOptions.Default, ct);

    /// <summary>As <see cref="SendAsync(string, string, CancellationToken)"/>, with the account and reply options.</summary>
    public async Task<IReadOnlyList<string>> SendAsync(string platform, string text, ChatSendOptions options, CancellationToken ct = default)
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
                if (options == ChatSendOptions.Default) await sender.SendAsync(text, ct).ConfigureAwait(false);
                else await sender.SendAsync(text, options, ct).ConfigureAwait(false);
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
        if (e.Type == ModerationActionEvent)
        {
            if (Moderation.ExternalModerationNotice.From(e) is { } action) ModerationActionSeen?.Invoke(action);
            return;
        }
        if (e.Type is MessageDeletedEvent or UserBannedEvent or UserTimedOutEvent)
        {
            if (ChatModerationNotice.From(e) is { } notice) Add(notice);
            return;
        }
        foreach (var adapter in _adapters)
        {
            if (adapter.ToChatMessage(e) is { } message) Add(message, e);
            if (adapter.ToFeedItem(e) is { } item)
                Add(e.Args.GetBool("isTest") ? item with { IsTest = true, IsEmulated = e.Args.GetBool("isEmulated") } : item);
        }
    }

    /// <summary>
    /// Platform event args that "chat.message" carries over when the platform sets them, so commands, speech and
    /// emotes can tell whispers, bot and Cuebot's own messages, first-time chatters and emote positions apart.
    /// </summary>
    internal static readonly string[] ForwardedArgs =
    [
        "messageKind", "isBot", "isBotAccount", "isInternal", "userName",
        "emotes", "emoteNames", "emoteList",
        "isFirstMessage", "firstMessage", "isFirstTime", "firstTime",
        "isSharedChat", "sourceBroadcasterId", "broadcasterId", "isHighlighted",
    ];

    /// <summary>
    /// The platform event's full arguments, under one key of "chat.message" (a read-only dictionary), for consumers
    /// that pass the platform's variables on: First Words runs get them as Streamer.bot's do (badges, broadcast.*...).
    /// </summary>
    public const string PlatformArgsKey = "platformArgs";

    private void Add(ChatMessage message, BotEvent source)
    {
        lock (_gate) Push(_messages, message);
        MessageReceived?.Invoke(message);
        var args = MessageArgs(message);
        foreach (var name in ForwardedArgs)
            if (source.Args.TryGetValue(name, out var value) && !args.ContainsKey(name)) args[name] = value;
        args[PlatformArgsKey] = source.Args;
        _bus.Publish(new BotEvent(MessageEvent, args));
    }

    private static Dictionary<string, object?> MessageArgs(ChatMessage message) => new(StringComparer.OrdinalIgnoreCase)
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
        };

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

    private void Add(ChatModerationNotice notice)
    {
        lock (_gate)
        {
            Push(_moderation, notice);
            if (notice.Kind == ChatModerationKind.MessageDeleted && notice.MessageId.Length > 0)
            {
                _deleted.Add(Key(notice.Platform, notice.MessageId));
                if (_deleted.Count > HistorySize * 4) _deleted.Clear();   // the history holds 500 lines; old keys are useless
            }
        }
        ModerationSeen?.Invoke(notice);
    }

    private static string Key(string platform, string id) => platform.ToLowerInvariant() + "|" + id;

    private static void Push<T>(Queue<T> queue, T value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistorySize) queue.Dequeue();
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>What a platform reported: a deleted message, a ban or a timeout.</summary>
public enum ChatModerationKind { MessageDeleted, UserBanned, UserTimedOut }

/// <summary>
/// A deletion, ban or timeout a platform reported ("chat.messageDeleted", "chat.userBanned", "chat.userTimedOut"), with
/// the moderator where the platform names one.
/// </summary>
public sealed record ChatModerationNotice(string Platform, ChatModerationKind Kind, DateTimeOffset Time)
{
    public string MessageId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string User { get; init; } = "";

    /// <summary>Timeouts: the length in seconds.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>The moderator's name, when the platform reports it.</summary>
    public string Moderator { get; init; } = "";

    public string Reason { get; init; } = "";

    /// <summary>
    /// The platform also reports this action in full (<see cref="ChatHub.ModerationActionEvent"/>), so the mod log takes
    /// it from there instead (arg <c>modLogged</c>).
    /// </summary>
    public bool LoggedElsewhere { get; init; }

    internal static ChatModerationNotice? From(BotEvent e)
    {
        var a = e.Args;
        var platform = a.GetString("platform");
        if (platform.Length == 0) return null;
        var kind = e.Type switch
        {
            ChatHub.MessageDeletedEvent => ChatModerationKind.MessageDeleted,
            ChatHub.UserBannedEvent => ChatModerationKind.UserBanned,
            _ => ChatModerationKind.UserTimedOut,
        };
        var duration = double.TryParse(a.GetString("duration"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? (int)seconds : 0;
        return new ChatModerationNotice(platform, kind, e.Time)
        {
            MessageId = kind == ChatModerationKind.MessageDeleted ? a.GetString("messageId") : "",
            UserId = a.GetString("userId"),
            User = a.GetString("user") is { Length: > 0 } user ? user : a.GetString("userName"),
            DurationSeconds = kind == ChatModerationKind.UserTimedOut && duration > 0 ? duration : null,
            Moderator = a.GetString("moderator") is { Length: > 0 } mod ? mod : a.GetString("moderatorName"),
            Reason = a.GetString("reason"),
            LoggedElsewhere = a.GetValueOrDefault("modLogged") is true,
        };
    }
}
