namespace Cuebot.Rumble.Triggers;

/// <summary>Global and per-user cooldowns for command triggers, keyed by trigger id. Thread-safe.</summary>
internal sealed class CooldownTracker(TimeProvider time)
{
    private const int PruneThreshold = 1000;
    private readonly Dictionary<(Guid Trigger, string User), DateTimeOffset> _readyAt = [];
    private readonly object _gate = new();

    /// <summary>
    /// Returns false while either cooldown is running; otherwise starts both cooldowns and returns true.
    /// </summary>
    public bool TryStart(Guid triggerId, string userId, TimeSpan globalCooldown, TimeSpan userCooldown)
    {
        var globalKey = (triggerId, "");
        var userKey = (triggerId, "user:" + userId);
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (IsCoolingDown(globalKey, now) || IsCoolingDown(userKey, now)) return false;

            if (globalCooldown > TimeSpan.Zero) _readyAt[globalKey] = now + globalCooldown;
            if (userCooldown > TimeSpan.Zero) _readyAt[userKey] = now + userCooldown;
            if (_readyAt.Count > PruneThreshold) Prune(now);
            return true;
        }
    }

    private bool IsCoolingDown((Guid, string) key, DateTimeOffset now) =>
        _readyAt.TryGetValue(key, out var readyAt) && now < readyAt;

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _readyAt.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _readyAt.Remove(key);
    }
}
