using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Users;

/// <summary>A chatter Cuebot has seen on one platform.</summary>
public sealed class UserRecord
{
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long MessageCount { get; set; }
    public ChatRoles Roles { get; set; }

    /// <summary>Identity within the registry: "platform:userId" (user name when there is no id), lower case.</summary>
    public string Key => KeyOf(Platform, UserId, UserName);

    public static string KeyOf(string platform, string userId, string userName) =>
        CommandStore.UserKey(platform, userId, userName);
}

public sealed class UserSettings
{
    /// <summary>A viewer is present when they chatted within this many minutes.</summary>
    public int PresentMinutes { get; set; } = 10;

    /// <summary>How often the "Present viewers" trigger fires, in minutes (0 turns it off).</summary>
    public int PresentIntervalMinutes { get; set; } = 5;

    /// <summary>Forget who has spoken when a platform reports the stream went live (otherwise only at app start).</summary>
    public bool ResetFirstWordsOnStreamOnline { get; set; } = true;
}

/// <summary>users.json.</summary>
public sealed class UsersFile
{
    public int Version { get; set; } = 1;
    public UserSettings Settings { get; set; } = new();
    public List<UserRecord> Users { get; set; } = [];
}

public static class UserEvents
{
    /// <summary>A user's first chat message since the app started, the stream went live, or first words were reset.</summary>
    public const string FirstWords = "users.firstWords";

    /// <summary>Periodic list of present (recently active) viewers, one event per platform.</summary>
    public const string PresentViewers = "users.presentViewers";
}

/// <summary>
/// Tracks every chatter from the combined chat (users.json): names, first/last seen, message count, roles. Also
/// keeps the first-words cache (in memory) and publishes "users.firstWords" and periodic "users.presentViewers".
/// Subscribes to the bus when constructed; <see cref="IPeriodicService.Tick"/> (driven by the timer service)
/// handles present viewers and batched saving. Thread-safe.
/// </summary>
public sealed class UserRegistry : IPeriodicService, IDisposable
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

    private readonly JsonFile<UsersFile>? _file;
    private readonly UsersFile _data;
    private readonly Dictionary<string, UserRecord> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _spoken = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventBus _bus;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private DateTimeOffset? _nextPresent;
    private DateTimeOffset _lastSave;
    private bool _dirty;
    private bool _disposed;

    public UserRegistry(AppPaths? paths, EventBus bus, AppLog log, TimeProvider? time = null)
    {
        _bus = bus;
        _time = time ?? TimeProvider.System;
        if (paths is not null)
        {
            _file = new JsonFile<UsersFile>(paths.File("users.json"), log);
            _data = _file.Value;
        }
        else
        {
            _data = new UsersFile();
        }
        foreach (var user in _data.Users) _users[user.Key] = user;
        _lastSave = _time.GetUtcNow();
        _subscription = bus.Subscribe(Handle);
    }

    /// <summary>Raised after a chat message updates (or creates) a user, with a copy of the record.</summary>
    public event Action<UserRecord>? UserSeen;

    public UserSettings Settings
    {
        get { lock (_gate) return Json.Clone(_data.Settings); }
    }

    public void UpdateSettings(Action<UserSettings> change)
    {
        lock (_gate)
        {
            change(_data.Settings);
            _nextPresent = null;
            MarkDirty();
        }
    }

    public int Count
    {
        get { lock (_gate) return _users.Count; }
    }

    public IReadOnlyList<UserRecord> All()
    {
        lock (_gate) return Json.Clone(_users.Values.ToList());
    }

    public UserRecord? Get(string platform, string userId)
    {
        lock (_gate) return _users.TryGetValue(UserRecord.KeyOf(platform, userId, ""), out var u) ? Json.Clone(u) : null;
    }

    /// <summary>Finds by user name or display name on a platform (case-insensitive, leading '@' ignored).</summary>
    public UserRecord? FindByName(string platform, string name)
    {
        name = name.Trim().TrimStart('@');
        lock (_gate)
        {
            var found = _users.Values.FirstOrDefault(u =>
                string.Equals(u.Platform, platform, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase)));
            return found is null ? null : Json.Clone(found);
        }
    }

    /// <summary>True when the user has chatted since the first-words cache was last cleared.</summary>
    public bool HasSpoken(string platform, string userId, string userName = "")
    {
        lock (_gate) return _spoken.Contains(UserRecord.KeyOf(platform, userId, userName));
    }

    /// <summary>Clears the first-words cache for one platform, or all with null / "All". Returns users forgotten.</summary>
    public int ResetFirstWords(string? platform = null)
    {
        lock (_gate)
        {
            if (platform is null || platform.Length == 0 || string.Equals(platform, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase))
            {
                var count = _spoken.Count;
                _spoken.Clear();
                return count;
            }
            var prefix = platform.ToLowerInvariant() + ":";
            return _spoken.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Users who chatted within <paramref name="window"/> (default: the PresentMinutes setting), newest first.</summary>
    public IReadOnlyList<UserRecord> PresentUsers(string? platform = null, TimeSpan? window = null)
    {
        lock (_gate)
        {
            var since = _time.GetUtcNow() - (window ?? TimeSpan.FromMinutes(Math.Max(0, _data.Settings.PresentMinutes)));
            return Json.Clone(_users.Values
                .Where(u => u.LastSeen >= since && PlatformMatches(u.Platform, platform))
                .OrderByDescending(u => u.LastSeen)
                .ToList());
        }
    }

    /// <summary>A random present user, optionally excluding one key (e.g. the user who triggered the action).</summary>
    public UserRecord? RandomPresentUser(string? platform = null, string? excludeKey = null, Random? random = null)
    {
        var candidates = PresentUsers(platform).Where(u => !string.Equals(u.Key, excludeKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 0 ? null : candidates[(random ?? Random.Shared).Next(candidates.Length)];
    }

    /// <summary>Processes bus events: chat messages update users; stream-online activity may reset first words.</summary>
    public void Handle(BotEvent e)
    {
        if (e.Type == ChatHub.MessageEvent) OnMessage(e);
        else if (e.Type == ChatHub.ActivityEvent
                 && string.Equals(e.Args.GetString("kind"), nameof(FeedKind.StreamOnline), StringComparison.OrdinalIgnoreCase))
        {
            bool reset;
            lock (_gate) reset = _data.Settings.ResetFirstWordsOnStreamOnline;
            if (reset) ResetFirstWords(e.Args.GetString("platform"));
        }
    }

    private void OnMessage(BotEvent e)
    {
        var args = e.Args;
        var platform = args.GetString("platform");
        var userId = args.GetString("userId");
        var display = args.GetString("user");
        var userName = args.GetString("userName");
        if (userName.Length == 0) userName = display;
        if (platform.Length == 0 || (userId.Length == 0 && userName.Length == 0)) return;
        if (args.GetBool("isInternal")) return;

        var key = UserRecord.KeyOf(platform, userId, userName);
        var now = _time.GetUtcNow();
        bool first;
        UserRecord copy;
        lock (_gate)
        {
            if (!_users.TryGetValue(key, out var user))
            {
                user = new UserRecord { Platform = platform, UserId = userId, FirstSeen = now };
                _users[key] = user;
                _data.Users.Add(user);
            }
            user.UserName = userName;
            user.DisplayName = args.GetString("displayName") is { Length: > 0 } dn ? dn : display.Length > 0 ? display : userName;
            user.LastSeen = now;
            user.MessageCount++;
            user.Roles = ChatArgs.RolesOf(args);
            first = _spoken.Add(key);
            MarkDirty();
            copy = Json.Clone(user);
        }

        UserSeen?.Invoke(copy);
        if (first)
        {
            var firstArgs = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)
            {
                ["firstSeen"] = copy.FirstSeen.ToString("o"),
                ["messageCount"] = copy.MessageCount,
                ["isNewUser"] = copy.MessageCount == 1,
            };
            _bus.Publish(new BotEvent(UserEvents.FirstWords, firstArgs));
        }
    }

    void IPeriodicService.Tick(DateTimeOffset now) => Tick(now);

    /// <summary>Publishes present viewers when due and saves batched changes.</summary>
    public void Tick(DateTimeOffset now)
    {
        List<BotEvent> events = [];
        lock (_gate)
        {
            var interval = _data.Settings.PresentIntervalMinutes;
            if (interval <= 0)
            {
                _nextPresent = null;
            }
            else if (_nextPresent is null)
            {
                _nextPresent = now + TimeSpan.FromMinutes(interval);
            }
            else if (now >= _nextPresent)
            {
                _nextPresent = now + TimeSpan.FromMinutes(interval);
                var since = now - TimeSpan.FromMinutes(Math.Max(0, _data.Settings.PresentMinutes));
                foreach (var platform in _users.Values.Where(u => u.LastSeen >= since).GroupBy(u => u.Platform, StringComparer.OrdinalIgnoreCase))
                {
                    var users = platform.OrderByDescending(u => u.LastSeen).ToArray();
                    events.Add(new BotEvent(UserEvents.PresentViewers, new Dictionary<string, object?>
                    {
                        ["platform"] = platform.Key,
                        ["users"] = string.Join(", ", users.Select(u => u.DisplayName)),
                        ["userIds"] = string.Join(", ", users.Select(u => u.UserId)),
                        ["count"] = users.Length,
                    }));
                }
            }

            if (_dirty && now - _lastSave >= SaveInterval) SaveLocked(now);
        }
        foreach (var e in events) _bus.Publish(e);
    }

    /// <summary>Writes users.json now if anything changed.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_dirty) SaveLocked(_time.GetUtcNow());
        }
        _file?.Flush();
    }

    private void MarkDirty() => _dirty = true;

    private void SaveLocked(DateTimeOffset now)
    {
        _dirty = false;
        _lastSave = now;
        _file?.ScheduleSave();
    }

    private static bool PlatformMatches(string platform, string? filter) =>
        filter is null || filter.Length == 0 || string.Equals(filter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, filter, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _subscription.Dispose();
        Flush();
        _file?.Dispose();
    }
}
