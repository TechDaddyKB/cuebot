using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Twitch.Compat;

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
    TwitchCounters? counters = null,
    Func<string>? broadcasterLogin = null)
{
    private const int RememberedMessages = 300;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _timeouts = new(StringComparer.Ordinal);
    private readonly PyramidDetector _pyramids = new();
    private readonly Dictionary<string, RewardSettings> _rewards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _messages = new(StringComparer.Ordinal);
    private readonly Queue<string> _messageOrder = new();
    private readonly Dictionary<string, int> _giftBombs = new(StringComparer.Ordinal);
    private TwitchChatSettings? _chatSettings;
    private TwitchBroadcasterArgs? _broadcaster;
    private (string Title, string GameId, string GameName)? _channel;
    private (string User, string UserName, string UserId, string Image)? _raidTarget;
    private bool? _sharedChatHost;
    private int _hypeTrainLevel;
    private int _pyramidCount;
    private int _pyramidBrokenCount;

    /// <summary>Sets the chat modes the next "chat settings updated" notification is compared with.</summary>
    public void SeedChatSettings(TwitchChatSettings settings)
    {
        lock (_gate) _chatSettings = settings;
    }

    /// <summary>The broadcaster's display name and type, for Streamer.bot's broadcastUser* arguments.</summary>
    public void SeedBroadcaster(TwitchUser user)
    {
        lock (_gate) _broadcaster = new TwitchBroadcasterArgs(user.Login, user.Id, user.DisplayName, user.BroadcasterType);
    }

    /// <summary>Reward settings that EventSub redemptions do not carry (skipsQueue, maxPerStream...).</summary>
    public void SeedRewards(IEnumerable<TwitchReward> rewards)
    {
        lock (_gate)
            foreach (var r in rewards)
                _rewards[r.Id] = new RewardSettings(r.SkipQueue, r.IsUserInputRequired, r.MaxPerStream, r.MaxPerUserPerStream,
                    r.GlobalCooldownSeconds, r.BackgroundColor);
    }

    /// <summary>The current title and category, so the next stream update can report what changed.</summary>
    public void SeedChannel(TwitchChannelInfo channel)
    {
        lock (_gate) _channel = (channel.Title, channel.GameId, channel.GameName);
    }

    public IReadOnlyList<BotEvent> Route(string subscriptionType, JsonElement payload, bool botSession = false)
    {
        var primary = EventSubMapper.Map(subscriptionType, payload, broadcasterId());
        if (primary is null) return [];
        return Route(primary, botSession);
    }

    /// <summary>
    /// The events to publish for <paramref name="primary"/>, each with Streamer.bot's argument names added
    /// (see <see cref="StreamerBotVariables"/>).
    /// </summary>
    internal IReadOnlyList<BotEvent> Route(BotEvent primary, bool botSession = false)
    {
        primary = Remember(primary);
        var broadcaster = Broadcaster();
        return RouteCore(primary, botSession)
            .Select(e => e.Type.StartsWith("twitch.", StringComparison.Ordinal) ? StreamerBotVariables.Apply(e, broadcaster) : e)
            .ToList();
    }

    private TwitchBroadcasterArgs Broadcaster()
    {
        lock (_gate)
            if (_broadcaster is { } seeded && seeded.UserId == broadcasterId()) return seeded;
        return new TwitchBroadcasterArgs(broadcasterLogin?.Invoke() ?? "", broadcasterId());
    }

    /// <summary>Values Streamer.bot reports that need memory across events: reward settings, old title, raid target...</summary>
    private BotEvent Remember(BotEvent e)
    {
        lock (_gate)
        {
            switch (e.Type)
            {
                case TwitchEventTypes.ChatMessage:
                    var id = Str(e, "messageId");
                    if (id.Length > 0 && !_messages.ContainsKey(id))
                    {
                        _messages[id] = Str(e, "message");
                        _messageOrder.Enqueue(id);
                        if (_messageOrder.Count > RememberedMessages) _messages.Remove(_messageOrder.Dequeue());
                    }
                    return e;
                case TwitchEventTypes.ChatMessageDeleted when !e.Args.ContainsKey("message"):
                    return _messages.TryGetValue(Str(e, "messageId"), out var deleted) ? Retype(e, e.Type, a => a["message"] = deleted) : e;
                case TwitchEventTypes.RewardCreated or TwitchEventTypes.RewardUpdated:
                    _rewards[Str(e, "rewardId")] = new RewardSettings(Bool(e, "skipsQueue"), Bool(e, "requiresUserInput"),
                        Int(e, "maxPerStream"), Int(e, "maxPerUserPerStream"), Int(e, "globalCooldown"), Str(e, "backgroundColor"));
                    return e;
                case TwitchEventTypes.RewardDeleted:
                    _rewards.Remove(Str(e, "rewardId"));
                    return e;
                case TwitchEventTypes.RewardRedemption or TwitchEventTypes.RewardRedemptionUpdated:
                    if (!_rewards.TryGetValue(Str(e, "rewardId"), out var reward)) return e;
                    return Retype(e, e.Type, a =>
                    {
                        a.TryAdd("skipsQueue", reward.SkipsQueue);
                        a.TryAdd("requiresUserInput", reward.RequiresUserInput);
                        a.TryAdd("maxPerStream", reward.MaxPerStream);
                        a.TryAdd("maxPerUserPerStream", reward.MaxPerUserPerStream);
                        a.TryAdd("globalCooldown", reward.GlobalCooldown);
                        a.TryAdd("backgroundColor", reward.BackgroundColor);
                    });
                case TwitchEventTypes.StreamUpdate:
                    var before = _channel;
                    _channel = (Str(e, "title"), Str(e, "gameId"), Str(e, "gameName"));
                    if (before is not { } old) return e;
                    return Retype(e, e.Type, a =>
                    {
                        a["statusUpdate"] = old.Title != Str(e, "title");
                        a["gameUpdate"] = old.GameId != Str(e, "gameId");
                        a["oldStatus"] = old.Title;
                        a["oldGameId"] = old.GameId;
                        a["oldGameName"] = old.GameName;
                    });
                case TwitchEventTypes.GiftBomb or TwitchEventTypes.SharedChatGiftBomb:
                    if (Str(e, "communityGiftId") is { Length: > 0 } bomb)
                    {
                        if (_giftBombs.Count > 50) _giftBombs.Clear();
                        _giftBombs[bomb] = Int(e, "total");
                    }
                    return e;
                case TwitchEventTypes.SubGift or TwitchEventTypes.SharedChatSubGift:
                    return _giftBombs.TryGetValue(Str(e, "communityGiftId"), out var size)
                        ? Retype(e, e.Type, a => a["subBombCount"] = size)
                        : Retype(e, e.Type, a => a["subBombCount"] = 0);
                case TwitchEventTypes.SharedChatBegin or TwitchEventTypes.SharedChatUpdate:
                    _sharedChatHost = Bool(e, "isHost");
                    return e;
                case TwitchEventTypes.SharedChatEnd:
                    _sharedChatHost = null;
                    return e;
                case TwitchEventTypes.HypeTrainStart or TwitchEventTypes.HypeTrainUpdate or TwitchEventTypes.HypeTrainEnd:
                    var host = _sharedChatHost;
                    return Retype(e, e.Type, a =>
                    {
                        a.TryAdd("isInSharedChat", host is not null);
                        a.TryAdd("isSharedChatHost", host is true);
                    });
                case TwitchEventTypes.RaidStart:
                    _raidTarget = (Str(e, "user"), Str(e, "userName"), Str(e, "userId"), Str(e, "profileImageUrl"));
                    return e;
                case TwitchEventTypes.RaidSend:
                    var sent = _raidTarget;
                    _raidTarget = null;
                    return sent is { } target && target.UserId == Str(e, "userId") && !e.Args.ContainsKey("profileImageUrl")
                        ? Retype(e, e.Type, a => a["profileImageUrl"] = target.Image)
                        : e;
                case TwitchEventTypes.RaidCancelled:
                    var cancelled = _raidTarget;
                    _raidTarget = null;
                    if (cancelled is not { } c) return e;
                    return Retype(e, e.Type, a =>
                    {
                        a["raidUser"] = c.User;
                        a["raidUserName"] = c.UserName;
                        a["raidUserId"] = c.UserId;
                        a["profileImageUrl"] = c.Image;
                    });
                default:
                    return e;
            }
        }
    }

    private List<BotEvent> RouteCore(BotEvent primary, bool botSession)
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
                int previous;
                lock (_gate)
                {
                    previous = _hypeTrainLevel;
                    levelUp = _hypeTrainLevel > 0 && level > _hypeTrainLevel;
                    _hypeTrainLevel = Math.Max(_hypeTrainLevel, level);
                }
                if (levelUp) events.Add(Retype(primary, TwitchEventTypes.HypeTrainLevelUp, a => a["prevLevel"] = previous));
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
        {
            result = _pyramids.Add(Str(message, "userId"), Str(message, "user"), Str(message, "message"), Math.Max(2, pyramidMinWidth?.Invoke() ?? 3),
                Str(message, "userName"));
            if (result is not null && result.Broken) _pyramidBrokenCount++;
            else if (result is not null) _pyramidCount++;
        }
        if (result is null) yield break;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = result.User,
            ["userName"] = result.UserName,
            ["userId"] = result.UserId,
            ["emote"] = result.Token,
            ["width"] = result.Width,
        };
        lock (_gate)
        {
            if (result.Broken) args["totalPyramidsBroken"] = _pyramidBrokenCount;
            else args["totalPyramids"] = _pyramidCount;
        }
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

    private static bool Bool(BotEvent e, string key) => e.Args.GetValueOrDefault(key) is true;

    private sealed record RewardSettings(bool SkipsQueue, bool RequiresUserInput, int MaxPerStream, int MaxPerUserPerStream,
        int GlobalCooldown, string BackgroundColor);
}

/// <summary>A completed or broken chat pyramid.</summary>
internal sealed record PyramidResult(string UserId, string User, string Token, int Width, bool Broken, string UserName = "");

/// <summary>
/// Detects one-person chat pyramids ("Kappa", "Kappa Kappa", "Kappa Kappa Kappa", "Kappa Kappa", "Kappa"). Not
/// thread-safe; the router serializes calls.
/// </summary>
internal sealed class PyramidDetector
{
    private string _userId = "";
    private string _user = "";
    private string _userName = "";
    private string _token = "";
    private int _width;
    private int _peak;
    private bool _descending;

    /// <summary>Feeds one chat message; returns a result when a pyramid of at least <paramref name="minWidth"/> completes or breaks.</summary>
    public PyramidResult? Add(string userId, string user, string message, int minWidth, string userName = "")
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
                var done = _peak >= minWidth ? new PyramidResult(_userId, _user, _token, _peak, false, _userName) : null;
                Reset();
                return done;
            }
        }

        PyramidResult? broken = _width > 0 && Math.Max(_peak, _width) >= Math.Max(2, minWidth - 1)
            ? new PyramidResult(_userId, _user, _token, _peak, true, _userName)
            : null;
        Reset();
        if (count == 1)
        {
            _userId = userId;
            _user = user;
            _userName = userName;
            _token = token!;
            _width = 1;
            _peak = 1;
        }
        return broken;
    }

    private void Reset()
    {
        _userId = _user = _userName = _token = "";
        _width = _peak = 0;
        _descending = false;
    }
}
