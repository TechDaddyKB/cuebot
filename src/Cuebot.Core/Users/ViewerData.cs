using System.Text.Json.Nodes;
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
/// <see cref="YouTubeRetentionDays"/> days are forgotten. Daily logs and backups are not touched. Stores whose data spans
/// linked accounts (<see cref="IPersonDataStore"/>: identity links, notes and the mod log) make "forget this viewer"
/// reach every account confirmed or verified as the same person, and can export what they hold first.
/// </summary>
public sealed class ViewerData(IEnumerable<IViewerDataStore> stores, UserRegistry users, AppLog log, TimeProvider? time = null,
    IEnumerable<IPersonDataStore>? persons = null) : IDisposable
{
    private readonly IReadOnlyList<IPersonDataStore> _persons = persons?.ToArray() ?? [];

    public const int YouTubeRetentionDays = 30;
    public const string YouTube = "youtube";
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(6);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ITimer? _timer;

    /// <summary>Starts the YouTube retention check: shortly after start, then every few hours.</summary>
    public void Start() =>
        _timer ??= _time.CreateTimer(_ => RunRetention(), null, TimeSpan.FromMinutes(1), RetentionInterval);

    /// <summary>
    /// The viewer's accounts: this one, and those confirmed or verified as the same person by an identity link (suggested
    /// links are not followed, so a wrong guess never erases someone else).
    /// </summary>
    public IReadOnlyList<ViewerRef> AccountsOf(string platform, string userId, string userName)
    {
        var accounts = new List<ViewerRef> { new(platform, userId, userName) };
        foreach (var store in _persons)
        {
            try
            {
                foreach (var a in store.LinkedAccounts(platform, userId, userName))
                    if (!accounts.Any(x => Same(x, a.Platform, a.UserId, a.UserName))) accounts.Add(a);
            }
            catch (Exception ex)
            {
                log.Error("Users", $"Could not find linked accounts in {store.Name}", ex);
            }
        }
        return accounts;
    }

    /// <summary>
    /// Forgets one viewer everywhere, on every linked account (<see cref="AccountsOf"/>). Matches by user id when known,
    /// otherwise by user name. The mod log keeps active bans (the row and the platform user id), so they still work.
    /// </summary>
    public ForgetResult Forget(string platform, string userId, string userName)
    {
        var accounts = AccountsOf(platform, userId, userName);
        var result = ForgetWhere(v => accounts.Any(a => Same(v, a.Platform, a.UserId, a.UserName)));
        var counts = new Dictionary<string, int>(result.PerStore, StringComparer.Ordinal);
        foreach (var store in _persons)
        {
            try
            {
                var n = store.Erase(accounts);
                if (n > 0) counts[store.Name] = counts.GetValueOrDefault(store.Name) + n;
            }
            catch (Exception ex)
            {
                log.Error("Users", $"Could not forget viewers in {store.Name}", ex);
            }
        }
        result = new ForgetResult(counts);
        log.Info("Users", $"Forgot a {platform} viewer on {accounts.Count} account(s) ({result.Total} record(s) removed)");
        return result;
    }

    /// <summary>
    /// What Cuebot holds about a viewer, for an access request (section 9.2 of the mod chat plan): their accounts, what
    /// the viewer list knows, and each person store's data (links, notes, mod log rows). Other stores (variables, quotes,
    /// voice settings) are listed by name only.
    /// </summary>
    public JsonObject Export(string platform, string userId, string userName)
    {
        var accounts = AccountsOf(platform, userId, userName);
        var viewers = new JsonArray();
        foreach (var a in accounts)
            if ((a.UserId.Length > 0 ? users.Get(a.Platform, a.UserId) : users.FindByName(a.Platform, a.UserName)) is { } u)
                viewers.Add(new JsonObject
                {
                    ["platform"] = u.Platform, ["userId"] = u.UserId, ["userName"] = u.UserName, ["displayName"] = u.DisplayName,
                    ["firstSeen"] = u.FirstSeen.ToString("O"), ["lastSeen"] = u.LastSeen.ToString("O"), ["messages"] = u.MessageCount,
                });
        var result = new JsonObject
        {
            ["exportedAt"] = (_time.GetUtcNow()).ToString("O"),
            ["accounts"] = new JsonArray(accounts.Select(a => (JsonNode)new JsonObject { ["platform"] = a.Platform, ["userId"] = a.UserId, ["name"] = a.UserName }).ToArray()),
            ["viewers"] = viewers,
            ["otherStores"] = new JsonArray(stores.Select(s => (JsonNode)JsonValue.Create(s.Name)).ToArray()),
        };
        foreach (var store in _persons)
        {
            try { result[store.Name] = store.Export(accounts); }
            catch (Exception ex) { log.Error("Users", $"Could not export from {store.Name}", ex); }
        }
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
