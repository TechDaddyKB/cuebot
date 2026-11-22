using System.Collections.Concurrent;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Core.Commands;

/// <summary>A platform that can say whether a viewer follows the channel and at which sub tier.</summary>
public interface IViewerStatusSource
{
    string Platform { get; }

    /// <summary>True or false; throws when the platform cannot answer (no sign-in or scope).</summary>
    Task<bool> IsFollowerAsync(string userId, CancellationToken ct);

    /// <summary>The sub tier (1, 2 or 3), or 0 when not subscribed; throws when the platform cannot answer.</summary>
    Task<int> SubTierAsync(string userId, CancellationToken ct);
}

/// <summary>What is known about one viewer fact.</summary>
public enum ViewerFact
{
    No,
    Yes,

    /// <summary>Not looked up yet: a lookup can answer it.</summary>
    Unknown,

    /// <summary>The last lookup failed; it is not retried for a minute.</summary>
    Failed,
}

/// <summary>
/// Follower and sub tier answers with a time limit (followers 30 minutes, subs 10), so a command with such a rule costs
/// at most one API call per viewer per period. A platform without a source answers No. Thread-safe.
/// </summary>
public sealed class ViewerStatusCache : IDisposable
{
    private static readonly TimeSpan FollowerTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TierTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(1);
    private const int MaxEntries = 10_000;

    private readonly record struct Entry(int? Value, DateTimeOffset Expires);

    private readonly Dictionary<string, IViewerStatusSource> _sources;
    private readonly TimeProvider _time;
    private readonly IDisposable? _subscription;
    private readonly ConcurrentDictionary<string, Entry> _followers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _tiers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);

    public ViewerStatusCache(IEnumerable<IViewerStatusSource>? sources = null, EventBus? bus = null, TimeProvider? time = null)
    {
        _sources = (sources ?? []).ToDictionary(s => s.Platform, StringComparer.OrdinalIgnoreCase);
        _time = time ?? TimeProvider.System;
        _subscription = bus?.Subscribe(OnEvent);
    }

    public ViewerFact Follower(string platform, string userId)
    {
        if (!_sources.ContainsKey(platform) || userId.Length == 0) return ViewerFact.No;
        return Read(_followers, Key(platform, userId), out var value) switch
        {
            ViewerFact.Yes => value > 0 ? ViewerFact.Yes : ViewerFact.No,
            var other => other,
        };
    }

    /// <summary>Whether a subscriber is at <paramref name="minimum"/> or above.</summary>
    public ViewerFact TierAtLeast(string platform, string userId, int minimum)
    {
        if (!_sources.ContainsKey(platform) || userId.Length == 0) return ViewerFact.No;
        return Read(_tiers, Key(platform, userId), out var value) switch
        {
            ViewerFact.Yes => value >= minimum ? ViewerFact.Yes : ViewerFact.No,
            var other => other,
        };
    }

    /// <summary>Looks up what is still unknown. One lookup per viewer and fact runs at a time; never throws.</summary>
    public async Task RefreshAsync(string platform, string userId, bool follower, bool tier, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(platform, out var source) || userId.Length == 0) return;
        var key = Key(platform, userId);
        var work = new List<Task>(2);
        if (follower && Read(_followers, key, out _) == ViewerFact.Unknown)
            work.Add(Once("f|" + key, () => Fetch(_followers, key, FollowerTtl, async () => await source.IsFollowerAsync(userId, ct).ConfigureAwait(false) ? 1 : 0)));
        if (tier && Read(_tiers, key, out _) == ViewerFact.Unknown)
            work.Add(Once("t|" + key, () => Fetch(_tiers, key, TierTtl, () => source.SubTierAsync(userId, ct))));
        await Task.WhenAll(work).ConfigureAwait(false);
    }

    private Task Once(string key, Func<Task> start)
    {
        var created = new Lazy<Task>(() => RunAndForget(key, start));
        return _running.GetOrAdd(key, _ => created.Value);
    }

    private async Task RunAndForget(string key, Func<Task> start)
    {
        await Task.Yield();
        try
        {
            await start().ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private async Task Fetch(ConcurrentDictionary<string, Entry> store, string key, TimeSpan ttl, Func<Task<int>> lookup)
    {
        Entry entry;
        try
        {
            entry = new Entry(await lookup().ConfigureAwait(false), _time.GetUtcNow() + ttl);
        }
        catch (Exception)
        {
            entry = new Entry(null, _time.GetUtcNow() + FailedTtl);
        }
        Put(store, key, entry);
    }

    private ViewerFact Read(ConcurrentDictionary<string, Entry> store, string key, out int value)
    {
        value = 0;
        if (!store.TryGetValue(key, out var entry) || entry.Expires <= _time.GetUtcNow()) return ViewerFact.Unknown;
        if (entry.Value is not { } known) return ViewerFact.Failed;
        value = known;
        return ViewerFact.Yes;
    }

    private void Put(ConcurrentDictionary<string, Entry> store, string key, Entry entry)
    {
        if (store.Count >= MaxEntries)
        {
            var now = _time.GetUtcNow();
            foreach (var (k, e) in store)
                if (e.Expires <= now) store.TryRemove(k, out _);
            if (store.Count >= MaxEntries) store.Clear();
        }
        store[key] = entry;
    }

    // A follow event answers the question without a call.
    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.ActivityEvent || e.Args.GetString("kind") != nameof(FeedKind.Follow) || e.Args.GetBool("isTest")) return;
        var platform = e.Args.GetString("platform");
        var userId = e.Args.GetString("userId");
        if (userId.Length > 0 && _sources.ContainsKey(platform))
            Put(_followers, Key(platform, userId), new Entry(1, _time.GetUtcNow() + FollowerTtl));
    }

    private static string Key(string platform, string userId) => platform.ToLowerInvariant() + "|" + userId;

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>
/// Built-in groups, computed from the chatter instead of stored, usable in a command's allowed and denied groups. A
/// stored group with the same name wins, so a streamer's existing group keeps its meaning.
/// </summary>
public static class BuiltInGroups
{
    public const string Moderators = "@Moderators";
    public const string Vips = "@VIPs";
    public const string Subscribers = "@Subscribers";
    public const string SubscribersT2 = "@Subscribers T2+";
    public const string SubscribersT3 = "@Subscribers T3";
    public const string Followers = "@Followers";
    public const string Banned = "@Banned";
    public const string KnownBots = "@KnownBots";

    public static IReadOnlyList<string> All { get; } =
        [Moderators, Vips, Subscribers, SubscribersT2, SubscribersT3, Followers, Banned, KnownBots];

    public static bool IsBuiltIn(string name) => All.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool NeedsFollower(string name) => string.Equals(name.Trim(), Followers, StringComparison.OrdinalIgnoreCase);

    public static bool NeedsTier(string name) =>
        string.Equals(name.Trim(), SubscribersT2, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name.Trim(), SubscribersT3, StringComparison.OrdinalIgnoreCase);
}
