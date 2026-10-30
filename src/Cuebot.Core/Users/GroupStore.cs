using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Users;

/// <summary>One member of a group: a user on a platform. Matched by user id when both sides have one, else by name.</summary>
public sealed class GroupMember
{
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";

    public bool Is(string platform, string userId, string userName)
    {
        if (!string.Equals(Platform, platform, StringComparison.OrdinalIgnoreCase)) return false;
        if (UserId.Length > 0 && userId.Length > 0) return string.Equals(UserId, userId, StringComparison.OrdinalIgnoreCase);
        return userName.Length > 0 && string.Equals(UserName, userName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GroupDefinition
{
    public string Name { get; set; } = "";
    public List<GroupMember> Users { get; set; } = [];
}

/// <summary>groups.json.</summary>
public sealed class GroupsFile
{
    public int Version { get; set; } = 1;
    public List<GroupDefinition> Groups { get; set; } = [];
}

/// <summary>Bus events published by <see cref="GroupStore"/>.</summary>
public static class GroupEvents
{
    public const string Added = "groups.added";
    public const string Removed = "groups.removed";
    public const string Cleared = "groups.cleared";
    public const string UsersAdded = "groups.usersAdded";
    public const string UsersRemoved = "groups.usersRemoved";
}

/// <summary>
/// Named groups of per-platform users (groups.json), used by command permissions and sub-actions. Thread-safe;
/// every change persists, raises <see cref="Changed"/> and publishes a <see cref="GroupEvents"/> bus event.
/// Group names are case-insensitive.
/// </summary>
public sealed class GroupStore : IViewerDataStore, IDisposable
{
    private readonly JsonFile<GroupsFile>? _file;
    private readonly List<GroupDefinition> _groups;
    private readonly EventBus? _bus;
    private readonly object _gate = new();

    public GroupStore(AppPaths? paths, AppLog log, EventBus? bus = null)
    {
        _bus = bus;
        if (paths is null)
        {
            _groups = [];
            return;
        }
        _file = new JsonFile<GroupsFile>(paths.File("groups.json"), log);
        _groups = _file.Value.Groups;
    }

    public event Action? Changed;

    /// <summary>Raised after a rename with (old name, new name), so stores that reference groups by name can follow.</summary>
    public event Action<string, string>? Renamed;

    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return _groups.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    /// <summary>Copies of all groups with their members.</summary>
    public IReadOnlyList<GroupDefinition> All()
    {
        lock (_gate) return Json.Clone(_groups);
    }

    public bool Exists(string group)
    {
        lock (_gate) return Find(group) is not null;
    }

    /// <summary>Creates an empty group. False when the name is blank or already used.</summary>
    public bool Add(string group)
    {
        group = group.Trim();
        if (group.Length == 0) return false;
        lock (_gate)
        {
            if (Find(group) is not null) return false;
            _groups.Add(new GroupDefinition { Name = group });
            Save();
        }
        Notify(GroupEvents.Added, group);
        return true;
    }

    public bool Remove(string group)
    {
        string name;
        lock (_gate)
        {
            var found = Find(group);
            if (found is null) return false;
            name = found.Name;
            _groups.Remove(found);
            Save();
        }
        Notify(GroupEvents.Removed, name);
        return true;
    }

    public bool Rename(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return false;
        lock (_gate)
        {
            var found = Find(oldName);
            if (found is null) return false;
            var clash = Find(newName);
            if (clash is not null && clash != found) return false;
            oldName = found.Name;
            found.Name = newName;
            Save();
        }
        Renamed?.Invoke(oldName, newName);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every member. Returns how many were removed, or -1 when the group does not exist.</summary>
    public int Clear(string group)
    {
        int count;
        string name;
        lock (_gate)
        {
            var found = Find(group);
            if (found is null) return -1;
            name = found.Name;
            count = found.Users.Count;
            found.Users.Clear();
            Save();
        }
        Notify(GroupEvents.Cleared, name, extra: new() { ["count"] = count });
        return count;
    }

    /// <summary>
    /// Adds a user. False when they are already a member, or the group does not exist and
    /// <paramref name="createGroup"/> is false.
    /// </summary>
    public bool AddUser(string group, string platform, string userId, string userName, bool createGroup = false)
    {
        var created = false;
        string name;
        lock (_gate)
        {
            var found = Find(group);
            if (found is null)
            {
                if (!createGroup || group.Trim().Length == 0) return false;
                found = new GroupDefinition { Name = group.Trim() };
                _groups.Add(found);
                created = true;
            }
            name = found.Name;
            var existing = found.Users.FirstOrDefault(u => u.Is(platform, userId, userName));
            if (existing is not null)
            {
                // Fill in an id or a new name learnt later.
                var refreshed = (existing.UserId.Length == 0 && userId.Length > 0)
                                || (userName.Length > 0 && existing.UserName != userName);
                if (existing.UserId.Length == 0) existing.UserId = userId;
                if (userName.Length > 0) existing.UserName = userName;
                if (refreshed) Save();
                return false;
            }
            found.Users.Add(new GroupMember { Platform = platform, UserId = userId, UserName = userName });
            Save();
        }
        if (created) Notify(GroupEvents.Added, name);
        Notify(GroupEvents.UsersAdded, name, platform, userId, userName);
        return true;
    }

    /// <summary>Removes a user matched by id or name. False when they were not a member.</summary>
    public bool RemoveUser(string group, string platform, string userId, string userName)
    {
        string name;
        lock (_gate)
        {
            var found = Find(group);
            if (found is null) return false;
            name = found.Name;
            if (found.Users.RemoveAll(u => u.Is(platform, userId, userName)) == 0) return false;
            Save();
        }
        Notify(GroupEvents.UsersRemoved, name, platform, userId, userName);
        return true;
    }

    public bool Contains(string group, string platform, string userId, string userName)
    {
        lock (_gate) return Find(group)?.Users.Any(u => u.Is(platform, userId, userName)) ?? false;
    }

    /// <summary>Copies of the members of a group (empty when it does not exist).</summary>
    public IReadOnlyList<GroupMember> UsersIn(string group)
    {
        lock (_gate) return Find(group) is { } g ? Json.Clone(g.Users) : [];
    }

    /// <summary>Names of the groups a user belongs to.</summary>
    public IReadOnlyList<string> GroupsOf(string platform, string userId, string userName)
    {
        lock (_gate)
        {
            return _groups.Where(g => g.Users.Any(u => u.Is(platform, userId, userName))).Select(g => g.Name).ToArray();
        }
    }

    public void Flush() => _file?.Flush();

    private GroupDefinition? Find(string group) =>
        _groups.FirstOrDefault(g => string.Equals(g.Name, group.Trim(), StringComparison.OrdinalIgnoreCase));

    string IViewerDataStore.Name => "groups";

    int IViewerDataStore.Forget(Func<ViewerRef, bool> match)
    {
        lock (_gate)
        {
            var viewers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _groups)
            {
                foreach (var member in group.Users.Where(m => match(new ViewerRef(m.Platform, m.UserId, m.UserName))).ToList())
                {
                    group.Users.Remove(member);
                    viewers.Add(UserRecord.KeyOf(member.Platform, member.UserId, member.UserName));
                }
            }
            if (viewers.Count > 0) Save();
            return viewers.Count;
        }
    }

    private void Save() => _file?.ScheduleSave();

    private void Notify(string eventType, string group, string platform = "", string userId = "", string userName = "",
        Dictionary<string, object?>? extra = null)
    {
        Changed?.Invoke();
        if (_bus is null) return;
        var args = extra ?? new Dictionary<string, object?>();
        args["groupName"] = group;
        // Streamer.bot names. Cuebot groups are identified by name and have no bot flag.
        args["group.id"] = group;
        args["group.name"] = group;
        args["group.bots"] = false;
        if (platform.Length > 0 || userId.Length > 0 || userName.Length > 0)
        {
            args["platform"] = platform;
            args["user"] = userName;
            args["userName"] = userName;
            args["userId"] = userId;
            args["group.user.count"] = 1;
            args["group.user0.userId"] = userId;
            args["group.user0.user"] = userName;
            args["group.user0.userName"] = userName.ToLowerInvariant();
            args["group.user0.userType"] = platform.ToLowerInvariant();
        }
        _bus.Publish(new BotEvent(eventType, args));
    }

    public void Dispose() => _file?.Dispose();
}
