using Cuebot.Core.Logging;

namespace Cuebot.Core.Users;

/// <summary>One viewer as a store knows them.</summary>
public sealed record ViewerRef(string Platform, string UserId, string UserName)
{
    public string Key => UserRecord.KeyOf(Platform, UserId, UserName);
}

/// <summary>A store that keeps data about viewers and can forget some of them.</summary>
public interface IViewerDataStore
{
    /// <summary>Shown in results, e.g. "user variables".</summary>
    string Name { get; }

    /// <summary>Removes (or anonymizes) every viewer the predicate matches; returns how many viewers were affected.</summary>
    int Forget(Func<ViewerRef, bool> match);
}

/// <summary>What <see cref="ViewerData"/> removed, per store.</summary>
public sealed record ForgetResult(IReadOnlyDictionary<string, int> PerStore)
{
    public int Total => PerStore.Values.Sum();
}

/// <summary>
/// Forgets viewers across every store that keeps data about them (users, variables, groups, command counters,
/// quotes, voice settings, ...), on request (a viewer asks to be deleted) and for YouTube on a schedule: YouTube's
/// developer policies allow keeping API data only while it is refreshed, so YouTube viewers not seen in chat for
/// <see cref="YouTubeRetentionDays"/> days are forgotten. Logs and backups are not touched.
/// </summary>
public sealed class ViewerData(IEnumerable<IViewerDataStore> stores, UserRegistry users, AppLog log, TimeProvider? time = null) : IDisposable
{
    public const int YouTubeRetentionDays = 30;
    public const string YouTube = "youtube";
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(6);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ITimer? _timer;

    /// <summary>Starts the YouTube retention check: shortly after start, then every few hours.</summary>
    public void Start() =>
        _timer ??= _time.CreateTimer(_ => RunRetention(), null, TimeSpan.FromMinutes(1), RetentionInterval);

    /// <summary>Forgets one viewer everywhere. Matches by user id when known, otherwise by user name.</summary>
    public ForgetResult Forget(string platform, string userId, string userName)
    {
        var result = ForgetWhere(v => Same(v, platform, userId, userName));
        log.Info("Users", $"Forgot a {platform} viewer ({result.Total} record(s) removed)");
        return result;
    }

    public ForgetResult ForgetWhere(Func<ViewerRef, bool> match)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            try
            {
                var n = store.Forget(match);
                if (n > 0) counts[store.Name] = n;
            }
            catch (Exception ex)
            {
                log.Error("Users", $"Could not forget viewers in {store.Name}", ex);
            }
        }
        return new ForgetResult(counts);
    }

    /// <summary>Forgets YouTube viewers not seen in chat within <see cref="YouTubeRetentionDays"/> days.</summary>
    public ForgetResult ApplyYouTubeRetention(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(YouTubeRetentionDays);
        var active = users.All()
            .Where(u => IsYouTube(u.Platform) && u.LastSeen >= cutoff)
            .Select(u => u.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = ForgetWhere(v => IsYouTube(v.Platform) && !active.Contains(v.Key));
        if (result.Total > 0)
            log.Info("Users", $"Forgot YouTube viewers not seen for {YouTubeRetentionDays} days ({result.Total} record(s))");
        return result;
    }

    private void RunRetention()
    {
        try { ApplyYouTubeRetention(_time.GetUtcNow()); }
        catch (Exception ex) { log.Error("Users", "YouTube data retention failed", ex); }
    }

    private static bool IsYouTube(string platform) => string.Equals(platform, YouTube, StringComparison.OrdinalIgnoreCase);

    public static bool Same(ViewerRef v, string platform, string userId, string userName) =>
        string.Equals(v.Platform, platform, StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(v.UserId)
            ? string.Equals(v.UserId, userId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(v.UserName, userName, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _timer?.Dispose();
}
