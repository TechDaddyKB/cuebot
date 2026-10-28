namespace Cuebot.Kick;

/// <summary>A reward's group and redemption counters (reset when the stream goes online unless persisted).</summary>
public sealed class KickRewardState
{
    /// <summary>Reward group name for the group sub-actions; empty means "not configured".</summary>
    public string Group { get; set; } = "";

    public bool PersistCounter { get; set; }
    public bool PersistUserCounter { get; set; }
    public int Counter { get; set; }
    public Dictionary<string, int> UserCounters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Local Kick reward state with no Kick API behind it (Streamer.bot's reward counters and reward groups): per-reward
/// redemption counters, per-user counters and group names. Stored in kick.json; thread-safe.
/// </summary>
public sealed class KickRewardCounters
{
    private readonly Func<KickSettings> _settings;
    private readonly Action _save;
    private readonly object _gate = new();

    public KickRewardCounters(Func<KickSettings> settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    /// <summary>Counts a redemption; returns the reward's count and the user's count including this one.</summary>
    internal (int Counter, int UserCounter) CountRedemption(string rewardId, string userId)
    {
        if (rewardId.Length == 0) return (0, 0);
        lock (_gate)
        {
            var state = State(rewardId);
            state.Counter++;
            var user = 0;
            if (userId.Length > 0 && userId != "0") state.UserCounters[userId] = user = state.UserCounters.GetValueOrDefault(userId) + 1;
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

    private void Change(string rewardId, Action<KickRewardState> change)
    {
        if (string.IsNullOrWhiteSpace(rewardId)) throw new ArgumentException("No reward given.");
        lock (_gate)
        {
            change(State(rewardId));
            _save();
        }
    }

    private KickRewardState State(string rewardId)
    {
        var rewards = _settings().Rewards;
        if (!rewards.TryGetValue(rewardId, out var state)) rewards[rewardId] = state = new KickRewardState();
        return state;
    }
}
