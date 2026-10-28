using Cuebot.Core.Events;

namespace Cuebot.Twitch;

/// <summary>Sub counter values after a change.</summary>
public sealed record SubCounterState(int Count, int Rollover, int RolloverCount);

/// <summary>
/// Local Twitch state with no Twitch API behind it: the sub counter (with rollover), per-reward redemption
/// counters, reward groups and the latest subscriber. Stored in twitch.json; thread-safe.
/// </summary>
public sealed class TwitchCounters
{
    private readonly Func<TwitchSettings> _settings;
    private readonly Action _save;
    private readonly object _gate = new();

    public TwitchCounters(Func<TwitchSettings> settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    // ---------------------------------------------------------------- sub counter

    public SubCounterState SubCounter
    {
        get
        {
            lock (_gate) return Snapshot(_settings().SubCounter);
        }
    }

    /// <summary>Sets the count (and optionally the rollover count). Does not roll over.</summary>
    public SubCounterState SetSubCount(int count, int? rolloverCount = null)
    {
        lock (_gate)
        {
            var counter = _settings().SubCounter;
            counter.Count = Math.Max(0, count);
            if (rolloverCount is { } rollovers) counter.RolloverCount = Math.Max(0, rollovers);
            _save();
            return Snapshot(counter);
        }
    }

    /// <summary>
    /// Counts a subscription event (resubs and gifted subs only when the counter is set to). Returns the counter and
    /// the "twitch.subCounterRollover" event when this sub completed a round.
    /// </summary>
    internal (SubCounterState? State, BotEvent? Rollover) CountSub(bool isResub, bool isGift)
    {
        lock (_gate)
        {
            var counter = _settings().SubCounter;
            if ((isResub && !counter.CountResubs) || (isGift && !counter.CountGiftedSubs)) return (null, null);
            counter.Count++;
            BotEvent? rollover = null;
            if (counter.Rollover > 0 && counter.Count >= counter.Rollover)
            {
                counter.Count -= counter.Rollover;
                counter.RolloverCount++;
                rollover = new BotEvent(TwitchEventTypes.SubCounterRollover, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rollover"] = counter.Rollover,
                    ["rolloverCount"] = counter.RolloverCount,
                    ["subCounter"] = counter.Count,
                });
            }
            _save();
            return (Snapshot(counter), rollover);
        }
    }

    private static SubCounterState Snapshot(TwitchSubCounter c) => new(c.Count, c.Rollover, c.RolloverCount);

    // ---------------------------------------------------------------- latest subscriber

    public TwitchLatestSubscriber LatestSubscriber
    {
        get
        {
            lock (_gate)
            {
                var s = _settings().LatestSubscriber;
                return new TwitchLatestSubscriber
                {
                    UserId = s.UserId, UserLogin = s.UserLogin, UserName = s.UserName, Tier = s.Tier, IsGift = s.IsGift, At = s.At,
                };
            }
        }
    }

    internal void SetLatestSubscriber(string userId, string login, string name, string tier, bool isGift, DateTimeOffset at)
    {
        if (userId.Length == 0) return;
        lock (_gate)
        {
            _settings().LatestSubscriber = new TwitchLatestSubscriber
            {
                UserId = userId, UserLogin = login, UserName = name, Tier = tier, IsGift = isGift, At = at,
            };
            _save();
        }
    }

    // ---------------------------------------------------------------- reward counters and groups

    /// <summary>Counts a redemption; returns the reward's count and the user's count including this one.</summary>
    internal (int Counter, int UserCounter) CountRedemption(string rewardId, string userId)
    {
        if (rewardId.Length == 0) return (0, 0);
        lock (_gate)
        {
            var state = State(rewardId);
            state.Counter++;
            var user = 0;
            if (userId.Length > 0) state.UserCounters[userId] = user = state.UserCounters.GetValueOrDefault(userId) + 1;
            _save();
            return (state.Counter, user);
        }
    }

    public (int Counter, int UserCounter) GetRewardCounter(string rewardId, string userId = "")
    {
        lock (_gate)
        {
            if (!_settings().Rewards.TryGetValue(rewardId, out var state)) return (0, 0);
            return (state.Counter, userId.Length > 0 ? state.UserCounters.GetValueOrDefault(userId) : 0);
        }
    }

    public void ResetRewardCounter(string rewardId) => Change(rewardId, s => s.Counter = 0);

    public void ResetRewardUserCounters(string rewardId) => Change(rewardId, s => s.UserCounters.Clear());

    /// <summary>Every user's redemption count of a reward (user id → count).</summary>
    public IReadOnlyDictionary<string, int> GetRewardUserCounters(string rewardId)
    {
        lock (_gate)
            return _settings().Rewards.TryGetValue(rewardId, out var state)
                ? new Dictionary<string, int>(state.UserCounters, StringComparer.Ordinal)
                : new Dictionary<string, int>();
    }

    /// <summary>Resets one user's counter of one reward.</summary>
    public void ResetUserRewardCounter(string rewardId, string userId) => Change(rewardId, s => s.UserCounters.Remove(userId));

    /// <summary>Resets one user's counters of every reward. Returns how many rewards had a count for them.</summary>
    public int ResetUserRewardCounters(string userId)
    {
        lock (_gate)
        {
            var changed = _settings().Rewards.Values.Count(s => s.UserCounters.Remove(userId));
            if (changed > 0) _save();
            return changed;
        }
    }

    /// <summary>Stream online: counters of rewards that do not persist them start over.</summary>
    internal void ResetStreamCounters()
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var state in _settings().Rewards.Values)
            {
                if (!state.PersistCounter && state.Counter != 0)
                {
                    state.Counter = 0;
                    changed = true;
                }
                if (!state.PersistUserCounter && state.UserCounters.Count > 0)
                {
                    state.UserCounters.Clear();
                    changed = true;
                }
            }
            if (changed) _save();
        }
    }

    /// <summary>The reward's group ("" when it has none).</summary>
    public string GetGroup(string rewardId)
    {
        lock (_gate) return _settings().Rewards.TryGetValue(rewardId, out var state) ? state.Group : "";
    }

    public void SetGroup(string rewardId, string group) => Change(rewardId, s => s.Group = group.Trim());

    /// <summary>Whether the reward's counter and user counters survive going live.</summary>
    public (bool Counter, bool UserCounter) GetPersistence(string rewardId)
    {
        lock (_gate)
            return _settings().Rewards.TryGetValue(rewardId, out var state) ? (state.PersistCounter, state.PersistUserCounter) : (false, false);
    }

    public void SetPersistence(string rewardId, bool counter, bool userCounter) => Change(rewardId, s =>
    {
        s.PersistCounter = counter;
        s.PersistUserCounter = userCounter;
    });

    /// <summary>Every group name in use, sorted.</summary>
    public IReadOnlyList<string> Groups
    {
        get
        {
            lock (_gate)
                return _settings().Rewards.Values.Select(s => s.Group).Where(g => g.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Ids of the rewards in <paramref name="group"/> (case-insensitive).</summary>
    public IReadOnlyList<string> RewardsInGroup(string group)
    {
        lock (_gate)
            return _settings().Rewards.Where(r => r.Value.Group.Equals(group.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Key).ToArray();
    }

    private void Change(string rewardId, Action<TwitchRewardState> change)
    {
        if (string.IsNullOrWhiteSpace(rewardId)) throw new ArgumentException("No reward given.");
        lock (_gate)
        {
            change(State(rewardId));
            _save();
        }
    }

    private TwitchRewardState State(string rewardId)
    {
        var rewards = _settings().Rewards;
        if (!rewards.TryGetValue(rewardId, out var state)) rewards[rewardId] = state = new TwitchRewardState();
        return state;
    }
}
