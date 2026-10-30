using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Commands;

/// <summary>
/// Owns commands.json and command-counters.json. Thread-safe: callers get copies and change commands through the
/// methods here, which persist and raise <see cref="Changed"/> (raised on the calling thread; the UI marshals).
/// </summary>
public sealed class CommandStore : Users.IViewerDataStore, IDisposable
{
    private readonly JsonFile<CommandsFile>? _file;
    private readonly JsonFile<CommandCountersFile>? _countersFile;
    private readonly object _gate = new();
    private readonly List<CommandDefinition> _commands;
    private readonly Dictionary<Guid, long> _counters = [];
    private readonly Dictionary<Guid, Dictionary<string, long>> _userCounters = [];
    private IReadOnlyList<CommandDefinition> _snapshot = [];
    private bool _disposed;

    public CommandStore(AppPaths? paths, AppLog log)
    {
        if (paths is null)
        {
            _commands = [];
        }
        else
        {
            _file = new JsonFile<CommandsFile>(paths.File("commands.json"), log);
            _commands = _file.Value.Commands;
            _countersFile = new JsonFile<CommandCountersFile>(paths.File("command-counters.json"), log);
            foreach (var (id, count) in _countersFile.Value.Counters) _counters[id] = count;
            foreach (var (id, users) in _countersFile.Value.UserCounters)
                _userCounters[id] = new Dictionary<string, long>(users, StringComparer.OrdinalIgnoreCase);
        }
        _snapshot = Json.Clone(_commands);
    }

    /// <summary>Raised after commands are added, edited, removed or enabled/disabled.</summary>
    public event Action? Changed;

    /// <summary>Raised after a counter changes, with the command id.</summary>
    public event Action<Guid>? CountersChanged;

    /// <summary>Immutable-by-convention copy for matching; do not edit (use <see cref="Get"/> for an editable copy).</summary>
    public IReadOnlyList<CommandDefinition> Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>Editable copies of every command, in stored order.</summary>
    public IReadOnlyList<CommandDefinition> All() => Json.Clone(Snapshot.ToList());

    /// <summary>Distinct non-empty group names, sorted.</summary>
    public IReadOnlyList<string> Groups =>
        Snapshot.Select(c => c.Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>An editable copy of the command with this id.</summary>
    public CommandDefinition? Get(Guid id) => Snapshot.FirstOrDefault(c => c.Id == id) is { } c ? Json.Clone(c) : null;

    /// <summary>Find by id (as text) or name, case-insensitively. Returns an editable copy.</summary>
    public CommandDefinition? Find(string idOrName)
    {
        idOrName = idOrName.Trim();
        var snapshot = Snapshot;
        var found = Guid.TryParse(idOrName, out var id) ? snapshot.FirstOrDefault(c => c.Id == id) : null;
        found ??= snapshot.FirstOrDefault(c => string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        return found is null ? null : Json.Clone(found);
    }

    /// <summary>Adds the command, or replaces the stored one with the same id. The store keeps a copy.</summary>
    public void Save(CommandDefinition command)
    {
        var copy = Json.Clone(command);
        Mutate(list =>
        {
            var index = list.FindIndex(c => c.Id == copy.Id);
            if (index >= 0) list[index] = copy;
            else list.Add(copy);
            return true;
        });
    }

    public bool Remove(Guid id)
    {
        var removed = Mutate(list => list.RemoveAll(c => c.Id == id) > 0);
        if (removed)
        {
            lock (_gate)
            {
                _counters.Remove(id);
                _userCounters.Remove(id);
                SaveCounters();
            }
        }
        return removed;
    }

    public bool SetEnabled(Guid id, bool enabled) =>
        Mutate(list =>
        {
            var command = list.FirstOrDefault(c => c.Id == id);
            if (command is null || command.Enabled == enabled) return false;
            command.Enabled = enabled;
            return true;
        });

    /// <summary>Enables or disables every command in a group. Returns how many commands the group has.</summary>
    public int SetGroupEnabled(string group, bool enabled)
    {
        var count = 0;
        Mutate(list =>
        {
            var changed = false;
            foreach (var command in list.Where(c => string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase)))
            {
                count++;
                changed |= command.Enabled != enabled;
                command.Enabled = enabled;
            }
            return changed;
        });
        return count;
    }

    /// <summary>Moves every command of <paramref name="oldName"/> into <paramref name="newName"/>.</summary>
    public int RenameGroup(string oldName, string newName)
    {
        var count = 0;
        Mutate(list =>
        {
            foreach (var command in list.Where(c => string.Equals(c.Group, oldName, StringComparison.OrdinalIgnoreCase)))
            {
                command.Group = newName;
                count++;
            }
            return count > 0;
        });
        return count;
    }

    /// <summary>Renames a user group (groups.json) in every command's allowed and denied group lists.</summary>
    public int RenameUserGroup(string oldName, string newName)
    {
        var count = 0;
        Mutate(list =>
        {
            foreach (var command in list)
            {
                var changed = false;
                foreach (var groups in new[] { command.AllowedGroups, command.DeniedGroups })
                    for (var i = 0; i < groups.Count; i++)
                        if (string.Equals(groups[i].Trim(), oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            groups[i] = newName;
                            changed = true;
                        }
                if (changed) count++;
            }
            return count > 0;
        });
        return count;
    }

    // ---- counters ----

    public long GetCounter(Guid id)
    {
        lock (_gate) return _counters.GetValueOrDefault(id);
    }

    public long GetUserCounter(Guid id, string userKey)
    {
        lock (_gate) return _userCounters.TryGetValue(id, out var users) ? users.GetValueOrDefault(userKey) : 0;
    }

    /// <summary>Counts one use. Returns the new command and user counters.</summary>
    public (long Counter, long UserCounter) Increment(Guid id, string userKey)
    {
        (long, long) result;
        lock (_gate)
        {
            var counter = _counters[id] = _counters.GetValueOrDefault(id) + 1;
            if (!_userCounters.TryGetValue(id, out var users))
                _userCounters[id] = users = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var userCounter = users[userKey] = users.GetValueOrDefault(userKey) + 1;
            result = (counter, userCounter);
            SaveCounters();
        }
        CountersChanged?.Invoke(id);
        return result;
    }

    public void ResetCounter(Guid id)
    {
        lock (_gate)
        {
            _counters.Remove(id);
            SaveCounters();
        }
        CountersChanged?.Invoke(id);
    }

    /// <summary>Resets one user's counter, or every user's when <paramref name="userKey"/> is null.</summary>
    public void ResetUserCounters(Guid id, string? userKey = null)
    {
        lock (_gate)
        {
            if (userKey is null) _userCounters.Remove(id);
            else if (_userCounters.TryGetValue(id, out var users)) users.Remove(userKey);
            SaveCounters();
        }
        CountersChanged?.Invoke(id);
    }

    /// <summary>Resets the user counters whose key matches (e.g. every "twitch:" user). Returns how many were reset.</summary>
    public int ResetUserCountersWhere(Guid id, Func<string, bool> userKeyMatches)
    {
        int removed;
        lock (_gate)
        {
            if (!_userCounters.TryGetValue(id, out var users)) return 0;
            var keys = users.Keys.Where(userKeyMatches).ToArray();
            foreach (var key in keys) users.Remove(key);
            removed = keys.Length;
            if (removed > 0) SaveCounters();
        }
        if (removed > 0) CountersChanged?.Invoke(id);
        return removed;
    }

    /// <summary>Key used for per-user counters and cooldowns: "platform:userId" (user name when there is no id).</summary>
    public static string UserKey(string platform, string userId, string userName) =>
        $"{platform}:{(userId.Length > 0 ? userId : userName)}".ToLowerInvariant();

    public void Flush()
    {
        _file?.Flush();
        _countersFile?.Flush();
    }

    private bool Mutate(Func<List<CommandDefinition>, bool> change)
    {
        lock (_gate)
        {
            if (!change(_commands)) return false;
            _snapshot = Json.Clone(_commands);
            _file?.ScheduleSave();
            SaveCounters(); // persistence flags may have changed
        }
        Changed?.Invoke();
        return true;
    }

    string Users.IViewerDataStore.Name => "command counters";

    int Users.IViewerDataStore.Forget(Func<Users.ViewerRef, bool> match)
    {
        lock (_gate)
        {
            var viewers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var users in _userCounters.Values)
            {
                foreach (var key in users.Keys.Where(k => match(FromKey(k))).ToList())
                {
                    users.Remove(key);
                    viewers.Add(key);
                }
            }
            if (viewers.Count > 0) SaveCounters();
            return viewers.Count;
        }

        // Counter keys are "platform:userId" (user name when there is no id).
        static Users.ViewerRef FromKey(string key)
        {
            var colon = key.IndexOf(':');
            var platform = colon < 0 ? "" : key[..colon];
            var who = colon < 0 ? key : key[(colon + 1)..];
            return new Users.ViewerRef(platform, who, who);
        }
    }

    /// <summary>Writes only counters of commands that ask for persistence. Caller holds the lock.</summary>
    private void SaveCounters()
    {
        if (_countersFile is null) return;
        var value = _countersFile.Value;
        value.Counters.Clear();
        value.UserCounters.Clear();
        foreach (var command in _commands)
        {
            if (command.PersistCounter && _counters.TryGetValue(command.Id, out var count)) value.Counters[command.Id] = count;
            if (command.PersistUserCounter && _userCounters.TryGetValue(command.Id, out var users) && users.Count > 0)
                value.UserCounters[command.Id] = new Dictionary<string, long>(users);
        }
        _countersFile.ScheduleSave();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _file?.Dispose();
        _countersFile?.Dispose();
    }
}
