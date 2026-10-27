using System.Text.Json.Serialization;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Variables;

/// <summary>One per-user variable as shown in the viewer.</summary>
public sealed record UserVariableInfo(
    string Platform, string UserId, string UserName, string Name, object? Value, bool Persisted, DateTimeOffset LastWrite)
{
    public string Text => VariableResolver.Format(Value);
    public string TypeName => VariableValues.TypeName(Value);
}

public sealed record UserVariableChange(
    string Platform, string UserId, string UserName, string Name, VariableChangeKind Kind, object? OldValue, object? NewValue, bool Persisted);

/// <summary>On-disk shape of user-variables.json.</summary>
public sealed class UserVariablesDocument
{
    public int Version { get; set; } = 1;
    public List<UserVariablesRecord> Users { get; set; } = [];
}

public sealed class UserVariablesRecord
{
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";

    /// <summary>Name → value; values keep their JSON type (string, number, boolean).</summary>
    public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset LastWrite { get; set; }
}

/// <summary>
/// Variables stored per viewer and platform (Streamer.bot's "user globals"). A user is identified by platform
/// (case-insensitive, stored lower-case, e.g. "twitch") and user id (exact); the latest user name is kept for
/// display and lookups. Persisted values go to user-variables.json; temporary ones last until restart or
/// <see cref="ClearTemporary"/>. Saves are debounced and done off the calling thread.
/// </summary>
public sealed class UserVariables : IDisposable
{
    public const string FileName = "user-variables.json";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Dictionary<string, UserEntry> _users = new(StringComparer.Ordinal);
    private readonly JsonFile<UserVariablesDocument>? _file;
    private readonly Timer? _saveTimer;
    private readonly TimeProvider _clock;
    private bool _dirty;

    public UserVariables(AppPaths? paths, AppLog log, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (paths is null) return;
        _file = new JsonFile<UserVariablesDocument>(paths.File(FileName), log);
        _saveTimer = new Timer(_ => Save());
        foreach (var record in _file.Value.Users)
        {
            if (string.IsNullOrWhiteSpace(record.Platform) || string.IsNullOrEmpty(record.UserId)) continue;
            var user = GetOrAddUser(record.Platform, record.UserId, record.UserName);
            foreach (var (name, value) in record.Variables)
            {
                user.Vars[name] = new Entry(name, VariableValues.Normalize(value), true, record.LastWrite);
            }
        }
    }

    public event Action<UserVariableChange>? Changed;

    public object? Get(string platform, string userId, string name)
    {
        TryGet(platform, userId, name, out var value);
        return value;
    }

    public string? GetText(string platform, string userId, string name) =>
        TryGet(platform, userId, name, out var value) ? VariableResolver.Format(value) : null;

    public bool TryGet(string platform, string userId, string name, out object? value)
    {
        lock (_gate)
        {
            if (_users.TryGetValue(Key(platform, userId), out var user) && user.Vars.TryGetValue(name, out var e))
            {
                value = e.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>All variables of one user (name → value).</summary>
    public IReadOnlyDictionary<string, object?> GetAll(string platform, string userId)
    {
        lock (_gate)
        {
            return _users.TryGetValue(Key(platform, userId), out var user)
                ? user.Vars.Values.ToDictionary(e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Stores a value; <paramref name="userName"/> (when not empty) updates the remembered name.</summary>
    public void Set(string platform, string userId, string? userName, string name, object? value, bool persist)
    {
        Validate(platform, userId, name);
        value = VariableValues.Normalize(value);
        UserVariableChange change;
        lock (_gate) change = Store(GetOrAddUser(platform, userId, userName), name, value, persist);
        Raise(change);
    }

    /// <summary>Atomically adds <paramref name="amount"/> (negative to decrement); missing starts at 0. Returns the new value.</summary>
    /// <exception cref="InvalidOperationException">The current value is not a number.</exception>
    public object Increment(string platform, string userId, string? userName, string name, decimal amount, bool persist)
    {
        Validate(platform, userId, name);
        UserVariableChange change;
        object result;
        lock (_gate)
        {
            var user = GetOrAddUser(platform, userId, userName);
            var current = user.Vars.TryGetValue(name, out var e) ? e.Value : null;
            try
            {
                result = VariableValues.Add(current, amount);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"User variable '{name}' of {user.UserName}: {ex.Message}", ex);
            }
            change = Store(user, name, result, persist);
        }
        Raise(change);
        return result;
    }

    public bool Delete(string platform, string userId, string name)
    {
        UserVariableChange? change = null;
        lock (_gate)
        {
            if (_users.TryGetValue(Key(platform, userId), out var user) && user.Vars.Remove(name, out var old))
            {
                change = Deleted(user, old);
                if (old.Persisted) MarkDirty();
                RemoveIfEmpty(user);
            }
        }
        if (change is null) return false;
        Raise(change);
        return true;
    }

    /// <summary>Deletes a variable from every user (optionally of one platform). Returns how many were removed.</summary>
    public int DeleteForAllUsers(string name, string? platform = null) =>
        RemoveWhere(platform, null, e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deletes all variables of one user.</summary>
    public int DeleteUser(string platform, string userId) => RemoveWhere(platform, userId, _ => true);

    /// <summary>Deletes every temporary variable (optionally of one platform).</summary>
    public int ClearTemporary(string? platform = null) => RemoveWhere(platform, null, e => !e.Persisted);

    /// <summary>Deletes everything (optionally of one platform).</summary>
    public int Clear(string? platform = null) => RemoveWhere(platform, null, _ => true);

    /// <summary>Finds a known user's id by (case-insensitive) user name on a platform.</summary>
    public string? FindUserId(string platform, string userName)
    {
        var p = NormalizePlatform(platform);
        lock (_gate)
        {
            foreach (var user in _users.Values)
            {
                if (user.Platform == p && string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase)) return user.UserId;
            }
        }
        return null;
    }

    /// <summary>Platforms that currently have user variables, sorted.</summary>
    public IReadOnlyList<string> Platforms()
    {
        lock (_gate) return _users.Values.Select(u => u.Platform).Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Rows for the viewer, filtered by platform and/or user, sorted by platform, user name, variable.</summary>
    public IReadOnlyList<UserVariableInfo> Snapshot(string? platform = null, string? userId = null, bool? persisted = null)
    {
        var p = platform is null ? null : NormalizePlatform(platform);
        lock (_gate)
        {
            return _users.Values
                .Where(u => (p is null || u.Platform == p) && (userId is null || u.UserId == userId))
                .SelectMany(u => u.Vars.Values
                    .Where(e => persisted is null || e.Persisted == persisted)
                    .Select(e => new UserVariableInfo(u.Platform, u.UserId, u.UserName, e.Name, e.Value, e.Persisted, e.LastWrite)))
                .OrderBy(i => i.Platform, StringComparer.Ordinal)
                .ThenBy(i => i.UserName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Writes pending changes to disk now.</summary>
    public void Flush() => Save();

    public static string NormalizePlatform(string platform) => platform.Trim().ToLowerInvariant();

    /// <summary>
    /// The platform an action run belongs to: the "platform" argument, else the prefix of the event type
    /// ("twitch.follow" → "twitch"). Null when neither is present.
    /// </summary>
    public static string? PlatformFromArgs(IReadOnlyDictionary<string, object?> args)
    {
        if (args.TryGetValue("platform", out var p) && p is string s && s.Trim().Length > 0) return NormalizePlatform(s);
        if (args.TryGetValue("eventType", out var t) && t is string type)
        {
            var dot = type.IndexOf('.');
            if (dot > 0) return NormalizePlatform(type[..dot]);
        }
        return null;
    }

    private static void Validate(string platform, string userId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }

    private static string Key(string platform, string userId) => NormalizePlatform(platform) + "\u001f" + userId;

    private UserEntry GetOrAddUser(string platform, string userId, string? userName)
    {
        var key = Key(platform, userId);
        if (!_users.TryGetValue(key, out var user))
        {
            user = new UserEntry(NormalizePlatform(platform), userId) { UserName = string.IsNullOrWhiteSpace(userName) ? userId : userName };
            _users[key] = user;
        }
        else if (!string.IsNullOrWhiteSpace(userName) && user.UserName != userName)
        {
            user.UserName = userName;
            if (user.Vars.Values.Any(e => e.Persisted)) MarkDirty();
        }
        return user;
    }

    private UserVariableChange Store(UserEntry user, string name, object? value, bool persist)
    {
        var existed = user.Vars.TryGetValue(name, out var old);
        var key = old?.Name ?? name;
        user.Vars[key] = new Entry(key, value, persist, _clock.GetUtcNow());
        if (persist || old?.Persisted == true) MarkDirty();
        return new UserVariableChange(user.Platform, user.UserId, user.UserName, key,
            existed ? VariableChangeKind.Updated : VariableChangeKind.Created, old?.Value, value, persist);
    }

    private int RemoveWhere(string? platform, string? userId, Func<Entry, bool> predicate)
    {
        var p = platform is null ? null : NormalizePlatform(platform);
        var changes = new List<UserVariableChange>();
        lock (_gate)
        {
            foreach (var user in _users.Values.ToArray())
            {
                if ((p is not null && user.Platform != p) || (userId is not null && user.UserId != userId)) continue;
                foreach (var entry in user.Vars.Values.Where(predicate).ToArray())
                {
                    user.Vars.Remove(entry.Name);
                    changes.Add(Deleted(user, entry));
                    if (entry.Persisted) MarkDirty();
                }
                RemoveIfEmpty(user);
            }
        }
        foreach (var change in changes) Raise(change);
        return changes.Count;
    }

    private static UserVariableChange Deleted(UserEntry user, Entry old) =>
        new(user.Platform, user.UserId, user.UserName, old.Name, VariableChangeKind.Deleted, old.Value, null, old.Persisted);

    private void RemoveIfEmpty(UserEntry user)
    {
        if (user.Vars.Count == 0) _users.Remove(Key(user.Platform, user.UserId));
    }

    private void MarkDirty()
    {
        if (_file is null) return;
        _dirty = true;
        _saveTimer?.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void Save()
    {
        if (_file is null) return;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            _file.Value.Version = 1;
            _file.Value.Users = _users.Values
                .Select(u => new UserVariablesRecord
                {
                    Platform = u.Platform,
                    UserId = u.UserId,
                    UserName = u.UserName,
                    Variables = u.Vars.Values.Where(e => e.Persisted)
                        .ToDictionary(e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase),
                    LastWrite = u.Vars.Values.Where(e => e.Persisted).Select(e => e.LastWrite).DefaultIfEmpty().Max(),
                })
                .Where(r => r.Variables.Count > 0)
                .OrderBy(r => r.Platform, StringComparer.Ordinal)
                .ThenBy(r => r.UserId, StringComparer.Ordinal)
                .ToList();
            _file.SaveNow();
        }
    }

    private void Raise(UserVariableChange change) => Changed?.Invoke(change);

    public void Dispose()
    {
        _saveTimer?.Dispose();
        Save();
        _file?.Dispose();
    }

    private sealed class UserEntry(string platform, string userId)
    {
        public string Platform { get; } = platform;
        public string UserId { get; } = userId;
        public string UserName { get; set; } = userId;
        public Dictionary<string, Entry> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Entry(string Name, object? Value, bool Persisted, DateTimeOffset LastWrite);
}
