using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Events;

namespace Cuebot.Twitch.EventSub;

/// <summary>Remembers the ids of chat messages Cuebot sent, so their echo can be flagged <c>isInternal</c>. Thread-safe.</summary>
internal sealed class SentMessageTracker(int capacity = 200)
{
    private readonly HashSet<string> _set = [];
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public void Add(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        lock (_gate)
        {
            if (!_set.Add(messageId)) return;
            _order.Enqueue(messageId);
            if (_order.Count > capacity) _set.Remove(_order.Dequeue());
        }
    }

    public bool Contains(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return false;
        lock (_gate) return _set.Contains(messageId);
    }
}

/// <summary>
/// Turns EventSub notifications into the events Cuebot publishes: the mapped Twitch event plus what needs memory
/// across events (untimeouts, chat mode changes, hype train level ups, pyramids, sub and reward counters,
/// bot/internal flags) and the
/// platform-neutral moderation events ("chat.messageDeleted", "chat.userBanned", "chat.userTimedOut").
/// </summary>
internal sealed class TwitchEventRouter(
    TimeProvider time,
    Func<string> broadcasterId,
    Func<string> botUserId,
    SentMessageTracker sent,
    Func<int>? pyramidMinWidth = null,
    TwitchCounters? counters = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _timeouts = new(StringComparer.Ordinal);
    private readonly PyramidDetector _pyramids = new();
    private TwitchChatSettings? _chatSettings;
    private int _hypeTrainLevel;

    /// <summary>Sets the chat modes the next "chat settings updated" notification is compared with.</summary>
    public void SeedChatSettings(TwitchChatSettings settings)
    {
        lock (_gate) _chatSettings = settings;
    }

    public IReadOnlyList<BotEvent> Route(string subscriptionType, JsonElement payload, bool botSession = false)
    {
        var primary = EventSubMapper.Map(subscriptionType, payload, broadcasterId());
        if (primary is null) return [];
        return Route(primary, botSession);
    }

    internal IReadOnlyList<BotEvent> Route(BotEvent primary, bool botSession = false)
    {
        var events = new List<BotEvent>();
        switch (primary.Type)
        {
            case TwitchEventTypes.Whisper when botSession:
                events.Add(Retype(primary, TwitchEventTypes.BotWhisper, a => a["isBotWhisper"] = true));
                break;
            case TwitchEventTypes.ChatMessage:
                var message = Retype(primary, primary.Type, a =>
                {
                    var bot = botUserId();
                    a["isBot"] = bot.Length > 0 && Str(primary, "userId") == bot;
                    a["isBotAccount"] = a["isBot"];
                    a["isInternal"] = sent.Contains(Str(primary, "messageId"));
                });
                events.Add(message);
                if (message.Args.GetValueOrDefault("isSharedChat") is not true) events.AddRange(Pyramids(message));
                break;
            case TwitchEventTypes.UserTimedOut:
                events.Add(primary);
                lock (_gate)
                    _timeouts[Str(primary, "userId")] = time.GetUtcNow().AddSeconds(Int(primary, "duration"));
                events.Add(Neutral(NeutralChatEvents.UserTimedOut, primary, includeDuration: true));
                break;
            case TwitchEventTypes.UserBanned:
                events.Add(primary);
                lock (_gate) _timeouts.Remove(Str(primary, "userId"));
                events.Add(Neutral(NeutralChatEvents.UserBanned, primary, includeDuration: true));
                break;
            case TwitchEventTypes.UserUnbanned:
                bool wasTimedOut;
                lock (_gate)
                {
                    wasTimedOut = _timeouts.Remove(Str(primary, "userId"), out var endsAt) && endsAt > time.GetUtcNow();
                    if (_timeouts.Count > 1000) Prune();
                }
                events.Add(wasTimedOut ? Retype(primary, TwitchEventTypes.UserUntimedOut) : primary);
                break;
            case TwitchEventTypes.ChatMessageDeleted:
                events.Add(primary);
                events.Add(Neutral(NeutralChatEvents.MessageDeleted, primary, includeDuration: false));
                break;
            case TwitchEventTypes.ChatSettingsUpdated:
                events.Add(primary);
                var after = EventSubMapper.ChatSettingsOf(primary.Args);
                TwitchChatSettings? before;
                lock (_gate)
                {
                    before = _chatSettings;
                    _chatSettings = after;
                }
                events.AddRange(ChatModeChanges(before, after).Select(type => Retype(primary, type)));
                break;
            case TwitchEventTypes.HypeTrainStart:
                lock (_gate) _hypeTrainLevel = Int(primary, "level");
                events.Add(primary);
                break;
            case TwitchEventTypes.HypeTrainUpdate:
                events.Add(primary);
                var level = Int(primary, "level");
                bool levelUp;
                lock (_gate)
                {
                    levelUp = _hypeTrainLevel > 0 && level > _hypeTrainLevel;
                    _hypeTrainLevel = Math.Max(_hypeTrainLevel, level);
                }
                if (levelUp) events.Add(Retype(primary, TwitchEventTypes.HypeTrainLevelUp));
                break;
            case TwitchEventTypes.HypeTrainEnd:
                lock (_gate) _hypeTrainLevel = 0;
                events.Add(primary);
                break;
            case TwitchEventTypes.RewardRedemption when counters is not null:
                var (counter, userCounter) = counters.CountRedemption(Str(primary, "rewardId"), Str(primary, "userId"));
                events.Add(Retype(primary, primary.Type, a =>
                {
                    a["counter"] = counter;
                    a["userCounter"] = userCounter;
                }));
                break;
            case TwitchEventTypes.Sub or TwitchEventTypes.Resub when counters is not null:
                events.AddRange(CountSub(primary, counters));
                break;
            case TwitchEventTypes.StreamOnline:
                counters?.ResetStreamCounters();
                events.Add(primary);
                break;
            default:
                events.Add(primary);
                break;
        }
        return events;
    }

    /// <summary>The mode on/off/changed events between two chat settings states (none without a previous state).</summary>
    internal static IEnumerable<string> ChatModeChanges(TwitchChatSettings? before, TwitchChatSettings after)
    {
        if (before is null) yield break;
        if (before.EmoteMode != after.EmoteMode) yield return after.EmoteMode ? TwitchEventTypes.EmoteModeOn : TwitchEventTypes.EmoteModeOff;
        if (before.FollowerMode != after.FollowerMode)
            yield return after.FollowerMode ? TwitchEventTypes.FollowerModeOn : TwitchEventTypes.FollowerModeOff;
        else if (after.FollowerMode && before.FollowerModeDuration != after.FollowerModeDuration)
            yield return TwitchEventTypes.FollowerModeChanged;
        if (before.SlowMode != after.SlowMode) yield return after.SlowMode ? TwitchEventTypes.SlowModeOn : TwitchEventTypes.SlowModeOff;
        else if (after.SlowMode && before.SlowModeWaitTime != after.SlowModeWaitTime) yield return TwitchEventTypes.SlowModeChanged;
        if (before.SubscriberMode != after.SubscriberMode)
            yield return after.SubscriberMode ? TwitchEventTypes.SubscriberModeOn : TwitchEventTypes.SubscriberModeOff;
        if (before.UniqueChatMode != after.UniqueChatMode)
            yield return after.UniqueChatMode ? TwitchEventTypes.UniqueModeOn : TwitchEventTypes.UniqueModeOff;
    }

    /// <summary>A sub or resub: remembered as the latest subscriber, counted, plus the rollover event when due.</summary>
    private IEnumerable<BotEvent> CountSub(BotEvent sub, TwitchCounters counters)
    {
        var isResub = sub.Type == TwitchEventTypes.Resub;
        var isGift = sub.Args.GetValueOrDefault("isGift") is true;
        counters.SetLatestSubscriber(Str(sub, "userId"), Str(sub, "userName"), Str(sub, "user"), Str(sub, "tier"), isGift, time.GetUtcNow());
        var (state, rollover) = counters.CountSub(isResub, isGift);
        yield return state is null ? sub : Retype(sub, sub.Type, a => a["subCounter"] = state.Count);
        if (rollover is not null) yield return rollover;
    }

    private IEnumerable<BotEvent> Pyramids(BotEvent message)
    {
        PyramidResult? result;
        lock (_gate)
            result = _pyramids.Add(Str(message, "userId"), Str(message, "user"), Str(message, "message"), Math.Max(2, pyramidMinWidth?.Invoke() ?? 3));
        if (result is null) yield break;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = result.User,
            ["userId"] = result.UserId,
            ["emote"] = result.Token,
            ["width"] = result.Width,
        };
        if (result.Broken)
        {
            args["brokenBy"] = Str(message, "user");
            args["brokenByUserId"] = Str(message, "userId");
        }
        yield return new BotEvent(result.Broken ? TwitchEventTypes.PyramidBroken : TwitchEventTypes.Pyramid, args);
    }

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var key in _timeouts.Where(t => t.Value <= now).Select(t => t.Key).ToArray()) _timeouts.Remove(key);
    }

    private static BotEvent Neutral(string type, BotEvent source, bool includeDuration)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform"] = TwitchChatAdapter.PlatformName,
            ["userId"] = Str(source, "userId"),
            ["user"] = Str(source, "user"),
            ["userName"] = Str(source, "userName"),
        };
        if (includeDuration)
        {
            args["duration"] = Int(source, "duration");
            args["reason"] = Str(source, "reason");
        }
        else
        {
            args["messageId"] = Str(source, "messageId");
        }
        return new BotEvent(type, args);
    }

    private static BotEvent Retype(BotEvent source, string type, Action<Dictionary<string, object?>>? change = null)
    {
        var args = new Dictionary<string, object?>(source.Args, StringComparer.OrdinalIgnoreCase);
        change?.Invoke(args);
        return new BotEvent(type, args);
    }

    private static string Str(BotEvent e, string key) =>
        e.Args.GetValueOrDefault(key) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    private static int Int(BotEvent e, string key) =>
        int.TryParse(Str(e, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

/// <summary>A completed or broken chat pyramid.</summary>
internal sealed record PyramidResult(string UserId, string User, string Token, int Width, bool Broken);

/// <summary>
/// Detects one-person chat pyramids ("Kappa", "Kappa Kappa", "Kappa Kappa Kappa", "Kappa Kappa", "Kappa"). Not
/// thread-safe; the router serializes calls.
/// </summary>
internal sealed class PyramidDetector
{
    private string _userId = "";
    private string _user = "";
    private string _token = "";
    private int _width;
    private int _peak;
    private bool _descending;

    /// <summary>Feeds one chat message; returns a result when a pyramid of at least <paramref name="minWidth"/> completes or breaks.</summary>
    public PyramidResult? Add(string userId, string user, string message, int minWidth)
    {
        var words = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var token = words.Length > 0 && words.All(w => w == words[0]) ? words[0] : null;
        var count = token is null ? 0 : words.Length;

        if (_width > 0 && userId == _userId && token == _token)
        {
            if (!_descending && count == _width + 1)
            {
                _width = count;
                _peak = count;
                return null;
            }
            if (count == _width - 1 && _width >= 2)
            {
                _descending = true;
                _width = count;
                if (count != 1) return null;
                var done = _peak >= minWidth ? new PyramidResult(_userId, _user, _token, _peak, false) : null;
                Reset();
                return done;
            }
        }

        PyramidResult? broken = _width > 0 && Math.Max(_peak, _width) >= Math.Max(2, minWidth - 1)
            ? new PyramidResult(_userId, _user, _token, _peak, true)
            : null;
        Reset();
        if (count == 1)
        {
            _userId = userId;
            _user = user;
            _token = token!;
            _width = 1;
            _peak = 1;
        }
        return broken;
    }

    private void Reset()
    {
        _userId = _user = _token = "";
        _width = _peak = 0;
        _descending = false;
    }
}
