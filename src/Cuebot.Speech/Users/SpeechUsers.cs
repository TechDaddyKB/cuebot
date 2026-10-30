using Cuebot.Core.Storage;

namespace Cuebot.Speech;

/// <summary>Per-user speech settings (speech-users.json).</summary>
public sealed class SpeechUserStore
{
    public List<SpeechUser> Users { get; set; } = [];
}

/// <summary>What Cuebot knows and remembers about one chatter.</summary>
public sealed class SpeechUser
{
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";

    /// <summary>Display name as last seen.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Assigned alias; empty uses the source's alias.</summary>
    public string Alias { get; set; } = "";

    /// <summary>Spoken instead of the user name (pronunciation fix); empty for none.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>Rate override -10..10; null keeps the alias rate.</summary>
    public int? Rate { get; set; }

    /// <summary>Pitch override -10..10; null keeps the alias pitch.</summary>
    public int? Pitch { get; set; }

    /// <summary>Never read aloud.</summary>
    public bool Ignored { get; set; }

    /// <summary>Opted out themselves with "!tts me off".</summary>
    public bool OptedOut { get; set; }

    /// <summary>Always read aloud, bypassing permissions.</summary>
    public bool Forced { get; set; }

    /// <summary>Counts as a regular for permissions.</summary>
    public bool Regular { get; set; }

    /// <summary>Sticky random voice ("engineId|voiceId"); empty when none was picked yet.</summary>
    public string RandomVoice { get; set; } = "";

    /// <summary>Alias and voice used for the user's last spoken message ("alias|engineId|voiceId").</summary>
    public string LastVoice { get; set; } = "";

    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Thread-safe access to <see cref="SpeechUserStore"/>; users are matched by platform and id, else by name.</summary>
public sealed class SpeechUserDirectory : Cuebot.Core.Users.IViewerDataStore
{
    private readonly object _gate = new();
    private readonly SpeechUserStore _store;
    private readonly Action _save;

    /// <param name="save">Schedules persisting <paramref name="store"/>.</param>
    public SpeechUserDirectory(SpeechUserStore store, Action save)
    {
        _store = store;
        _save = save;
    }

    public SpeechUserDirectory(JsonFile<SpeechUserStore> file) : this(file.Value, file.ScheduleSave)
    {
    }

    /// <summary>A directory kept in memory only.</summary>
    public static SpeechUserDirectory InMemory() => new(new SpeechUserStore(), () => { });

    public IReadOnlyList<SpeechUser> All
    {
        get
        {
            lock (_gate) return _store.Users.ToArray();
        }
    }

    /// <summary>Finds a user by id (when given) or name, without creating one.</summary>
    public SpeechUser? Find(string? platform, string? userId, string? userName)
    {
        lock (_gate) return FindLocked(platform, userId, userName);
    }

    /// <summary>Finds or creates the user, refreshing the remembered name.</summary>
    public SpeechUser GetOrAdd(string platform, string userId, string userName)
    {
        lock (_gate)
        {
            var user = FindLocked(platform, userId, userName);
            if (user is null)
            {
                user = new SpeechUser { Platform = platform, UserId = userId, UserName = userName };
                _store.Users.Add(user);
            }
            else
            {
                if (userName.Length > 0) user.UserName = userName;
                if (user.UserId.Length == 0 && userId.Length > 0) user.UserId = userId;
                if (user.Platform.Length == 0) user.Platform = platform;
            }
            return user;
        }
    }

    /// <summary>Changes a user under the store lock and schedules a save.</summary>
    public SpeechUser Update(string platform, string userId, string userName, Action<SpeechUser> change)
    {
        SpeechUser user;
        lock (_gate)
        {
            user = GetOrAdd(platform, userId, userName);
            change(user);
        }
        Save();
        return user;
    }

    public bool Remove(SpeechUser user)
    {
        bool removed;
        lock (_gate) removed = _store.Users.Remove(user);
        if (removed) Save();
        return removed;
    }

    string Cuebot.Core.Users.IViewerDataStore.Name => "voice settings";

    int Cuebot.Core.Users.IViewerDataStore.Forget(Func<Cuebot.Core.Users.ViewerRef, bool> match)
    {
        int removed;
        lock (_gate) removed = _store.Users.RemoveAll(u => match(new Cuebot.Core.Users.ViewerRef(u.Platform, u.UserId, u.UserName)));
        if (removed > 0) Save();
        return removed;
    }

    public void Save()
    {
        lock (_gate) _save();
    }

    private SpeechUser? FindLocked(string? platform, string? userId, string? userName)
    {
        var users = _store.Users;
        static bool Same(string a, string? b) => string.Equals(a, b?.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);
        bool PlatformMatches(SpeechUser u) =>
            string.IsNullOrEmpty(platform) || u.Platform.Length == 0 || Same(u.Platform, platform);

        if (!string.IsNullOrEmpty(userId))
        {
            var byId = users.FirstOrDefault(u => u.UserId.Length > 0 && Same(u.UserId, userId) && PlatformMatches(u));
            if (byId is not null) return byId;
        }
        return string.IsNullOrWhiteSpace(userName)
            ? null
            : users.FirstOrDefault(u => Same(u.UserName, userName) && PlatformMatches(u));
    }
}
