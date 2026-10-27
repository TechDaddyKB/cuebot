namespace Cuebot.Core.Commands;

public enum CooldownKind { None, Global, User }

/// <summary>Result of <see cref="CooldownTracker.TryStart"/>: allowed, or which cooldown is running and for how long.</summary>
public readonly record struct CooldownResult(bool Allowed, CooldownKind Kind, TimeSpan Remaining)
{
    public static CooldownResult Ok => new(true, CooldownKind.None, TimeSpan.Zero);
}

/// <summary>
/// Global and per-user cooldowns keyed by any string (a command id, a trigger id...). Thread-safe; uses the
/// injected <see cref="TimeProvider"/> so tests control time.
/// </summary>
public sealed class CooldownTracker(TimeProvider? time = null)
{
    private const int PruneThreshold = 1000;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<(string Key, string User), DateTimeOffset> _readyAt = [];
    private readonly object _gate = new();

    /// <summary>
    /// When neither cooldown is running, starts both and returns <see cref="CooldownResult.Ok"/>; otherwise reports
    /// the running cooldown with the longest remaining time (global wins a tie).
    /// </summary>
    public CooldownResult TryStart(string key, string userId, TimeSpan globalCooldown, TimeSpan userCooldown)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var global = Remaining((key, ""), now);
            var user = Remaining((key, "user:" + userId), now);
            if (global > TimeSpan.Zero || user > TimeSpan.Zero)
            {
                return global >= user
                    ? new CooldownResult(false, CooldownKind.Global, global)
                    : new CooldownResult(false, CooldownKind.User, user);
            }

            if (globalCooldown > TimeSpan.Zero) _readyAt[(key, "")] = now + globalCooldown;
            if (userCooldown > TimeSpan.Zero) _readyAt[(key, "user:" + userId)] = now + userCooldown;
            if (_readyAt.Count > PruneThreshold) Prune(now);
            return CooldownResult.Ok;
        }
    }

    /// <summary>Remaining global cooldown for <paramref name="key"/>, or zero.</summary>
    public TimeSpan GlobalRemaining(string key)
    {
        lock (_gate) return Remaining((key, ""), _time.GetUtcNow());
    }

    /// <summary>Remaining cooldown of one user for <paramref name="key"/>, or zero.</summary>
    public TimeSpan UserRemaining(string key, string userId)
    {
        lock (_gate) return Remaining((key, "user:" + userId), _time.GetUtcNow());
    }

    /// <summary>Clears every cooldown of <paramref name="key"/> (global and all users).</summary>
    public void Reset(string key)
    {
        lock (_gate)
        {
            foreach (var k in _readyAt.Keys.Where(k => k.Key == key).ToArray()) _readyAt.Remove(k);
        }
    }

    /// <summary>Drops expired entries.</summary>
    public void Prune()
    {
        lock (_gate) Prune(_time.GetUtcNow());
    }

    private TimeSpan Remaining((string, string) key, DateTimeOffset now) =>
        _readyAt.TryGetValue(key, out var readyAt) && now < readyAt ? readyAt - now : TimeSpan.Zero;

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _readyAt.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _readyAt.Remove(key);
    }
}
