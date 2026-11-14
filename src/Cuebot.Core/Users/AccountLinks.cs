using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Variables;

namespace Cuebot.Core.Users;

/// <summary>One platform account in a link group.</summary>
public sealed class LinkedAccount
{
    /// <summary>Platform, lower case ("twitch").</summary>
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";

    public LinkedAccount() { }

    public LinkedAccount(string platform, string userId, string userName)
    {
        Platform = UserVariables.NormalizePlatform(platform);
        UserId = userId;
        UserName = userName.Trim().TrimStart('@');
    }

    internal string Key => Platform + ":" + UserId;
}

/// <summary>Accounts of one viewer on different platforms (at most one per platform).</summary>
public sealed class AccountLinkGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<LinkedAccount> Accounts { get; set; } = [];
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class AccountLinksData
{
    public List<AccountLinkGroup> Groups { get; set; } = [];

    /// <summary>Minutes a link request waits for the other account to confirm it.</summary>
    public int RequestMinutes { get; set; } = 10;
}

public enum LinkOutcome
{
    /// <summary>Waiting for the other account to confirm from its own platform.</summary>
    Pending,
    Linked,
    AlreadyLinked,
    SamePlatform,
    /// <summary>The asking account already has a different account on that platform.</summary>
    PlatformTaken,
    /// <summary>The two link groups both have an account on the same platform, so they cannot be joined.</summary>
    Conflict,
    UnknownPlatform,
}

public sealed record LinkResult(LinkOutcome Outcome, AccountLinkGroup? Group = null, LinkedAccount? Other = null);

/// <summary>
/// Links one viewer's accounts across platforms. A link needs both sides: the viewer asks from one platform
/// ("!linkkick bob" on Twitch) and confirms from the other ("!linktwitch alice" on Kick) within
/// <see cref="AccountLinksData.RequestMinutes"/>. Linked accounts share one points wallet. Saved in account-links.json.
/// Thread-safe.
/// </summary>
public sealed class AccountLinks
{
    /// <summary>Platforms that can be linked, lower case, with the words viewers may use for them.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlatformWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["twitch"] = "twitch", ["tw"] = "twitch",
        ["kick"] = "kick",
        ["youtube"] = "youtube", ["yt"] = "youtube",
        ["rumble"] = "rumble",
    };

    private readonly JsonFile<AccountLinksData>? _file;
    private readonly AccountLinksData _data;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<(LinkedAccount From, string Platform, string Name, DateTimeOffset Expires)> _pending = [];

    public AccountLinks(JsonFile<AccountLinksData>? file, AppLog log, TimeProvider? time = null)
    {
        _file = file;
        _data = file?.Value ?? new AccountLinksData();
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a link is made or removed.</summary>
    public event Action? Changed;

    /// <summary>"twitch" for "Twitch", "tw", "yt"...; null when not a linkable platform.</summary>
    public static string? PlatformOf(string? word) =>
        word is not null && PlatformWords.TryGetValue(word.Trim(), out var p) ? p : null;

    /// <summary>"Twitch", "YouTube"... for a lower case platform.</summary>
    public static string DisplayName(string platform) => UserVariables.NormalizePlatform(platform) switch
    {
        "twitch" => "Twitch",
        "kick" => "Kick",
        "youtube" => "YouTube",
        "rumble" => "Rumble",
        var other => other.Length > 0 ? char.ToUpperInvariant(other[0]) + other[1..] : other,
    };

    public int RequestMinutes
    {
        get { lock (_gate) return _data.RequestMinutes; }
        set { lock (_gate) { _data.RequestMinutes = Math.Clamp(value, 1, 1440); Save(); } }
    }

    public IReadOnlyList<AccountLinkGroup> Groups
    {
        get { lock (_gate) return _data.Groups.Select(Copy).ToArray(); }
    }

    /// <summary>The accounts sharing a wallet with this one (itself first). Just itself when it is not linked.</summary>
    public IReadOnlyList<LinkedAccount> LinkedTo(string platform, string userId)
    {
        var key = UserVariables.NormalizePlatform(platform) + ":" + userId;
        lock (_gate)
        {
            var group = Find(key);
            if (group is null) return [new LinkedAccount(platform, userId, "")];
            return group.Accounts.Where(a => a.Key == key).Concat(group.Accounts.Where(a => a.Key != key)).Select(Copy).ToArray();
        }
    }

    /// <summary>The group this account belongs to, or null.</summary>
    public AccountLinkGroup? GroupOf(string platform, string userId)
    {
        lock (_gate) return Find(UserVariables.NormalizePlatform(platform) + ":" + userId) is { } g ? Copy(g) : null;
    }

    public bool IsLinked(string platform, string userId) => GroupOf(platform, userId) is not null;

    /// <summary>
    /// A viewer asks to link their account on <paramref name="targetPlatform"/> named <paramref name="targetName"/>.
    /// Completes the link when that account already asked for this one; otherwise waits for it to confirm.
    /// </summary>
    public LinkResult Request(LinkedAccount from, string targetPlatform, string targetName, IEnumerable<string>? fromNames = null)
    {
        var platform = PlatformOf(targetPlatform) ?? UserVariables.NormalizePlatform(targetPlatform);
        if (!PlatformWords.Values.Contains(platform)) return new LinkResult(LinkOutcome.UnknownPlatform);
        from = new LinkedAccount(from.Platform, from.UserId, from.UserName);
        if (from.Platform == platform) return new LinkResult(LinkOutcome.SamePlatform);
        var name = Clean(targetName);
        var names = (fromNames ?? []).Append(from.UserName).Select(Clean).Where(n => n.Length > 0).ToHashSet();
        LinkResult result;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _pending.RemoveAll(p => p.Expires <= now);
            if (Find(from.Key) is { } mine && mine.Accounts.FirstOrDefault(a => a.Platform == platform) is { } existing)
                return Clean(existing.UserName) == name
                    ? new LinkResult(LinkOutcome.AlreadyLinked, Copy(mine), Copy(existing))
                    : new LinkResult(LinkOutcome.PlatformTaken, Copy(mine), Copy(existing));
            var match = _pending.FindIndex(p => p.Platform == from.Platform && names.Contains(p.Name)
                && p.From.Platform == platform && Clean(p.From.UserName) == name);
            if (match < 0)
            {
                _pending.RemoveAll(p => p.From.Key == from.Key && p.Platform == platform);
                _pending.Add((from, platform, name, now + TimeSpan.FromMinutes(_data.RequestMinutes)));
                return new LinkResult(LinkOutcome.Pending);
            }
            var other = _pending[match].From;
            result = Join(other, from);
            result = result with { Other = Copy(other) };
            if (result.Outcome == LinkOutcome.Linked)
                _pending.RemoveAll(p => p.From.Key == from.Key || p.From.Key == other.Key);
        }
        if (result.Outcome == LinkOutcome.Linked) Changed?.Invoke();
        return result;
    }

    /// <summary>Links two accounts directly (from the app, no confirmation).</summary>
    public LinkResult Link(LinkedAccount a, LinkedAccount b)
    {
        a = new LinkedAccount(a.Platform, a.UserId, a.UserName);
        b = new LinkedAccount(b.Platform, b.UserId, b.UserName);
        if (a.Platform == b.Platform) return new LinkResult(LinkOutcome.SamePlatform);
        LinkResult result;
        lock (_gate) result = Join(a, b);
        if (result.Outcome == LinkOutcome.Linked) Changed?.Invoke();
        return result;
    }

    /// <summary>Takes one account out of its group (the group goes when one account is left). False when not linked.</summary>
    public bool Unlink(string platform, string userId)
    {
        var key = UserVariables.NormalizePlatform(platform) + ":" + userId;
        lock (_gate)
        {
            if (Find(key) is not { } group) return false;
            group.Accounts.RemoveAll(a => a.Key == key);
            if (group.Accounts.Count < 2) _data.Groups.Remove(group);
            Save();
        }
        _log.Info("Users", $"Unlinked {DisplayName(platform)} account {userId}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Keeps a linked account's name current (names change; ids do not).</summary>
    public void Rename(string platform, string userId, string userName)
    {
        var key = UserVariables.NormalizePlatform(platform) + ":" + userId;
        userName = userName.Trim().TrimStart('@');
        if (userName.Length == 0) return;
        lock (_gate)
        {
            if (Find(key)?.Accounts.FirstOrDefault(a => a.Key == key) is not { } account || account.UserName == userName) return;
            account.UserName = userName;
            Save();
        }
    }

    /// <summary>Pending requests (for the app): who asked, for which platform and name, and until when.</summary>
    public IReadOnlyList<(LinkedAccount From, string Platform, string Name, DateTimeOffset Expires)> Pending
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                _pending.RemoveAll(p => p.Expires <= now);
                return _pending.ToArray();
            }
        }
    }

    // ---- under _gate ----

    private LinkResult Join(LinkedAccount a, LinkedAccount b)
    {
        var ga = Find(a.Key);
        var gb = Find(b.Key);
        if (ga is not null && ga == gb) return new LinkResult(LinkOutcome.AlreadyLinked, Copy(ga), Copy(b));
        var accounts = (ga?.Accounts ?? [a]).Concat(gb?.Accounts ?? [b]).ToList();
        if (accounts.GroupBy(x => x.Platform).Any(g => g.Count() > 1)) return new LinkResult(LinkOutcome.Conflict, null, Copy(b));
        var group = ga ?? gb ?? new AccountLinkGroup();
        group.Accounts = accounts;
        group.LinkedAt = _time.GetUtcNow();
        if (ga is not null && gb is not null) _data.Groups.Remove(gb);
        if (!_data.Groups.Contains(group)) _data.Groups.Add(group);
        Save();
        _log.Info("Users", $"Linked {string.Join(" + ", accounts.Select(x => $"{DisplayName(x.Platform)} {x.UserName}"))}");
        return new LinkResult(LinkOutcome.Linked, Copy(group), Copy(b));
    }

    private AccountLinkGroup? Find(string key) => _data.Groups.FirstOrDefault(g => g.Accounts.Any(a => a.Key == key));

    private void Save() => _file?.ScheduleSave();

    private static string Clean(string name) => name.Trim().TrimStart('@').ToLowerInvariant();

    private static LinkedAccount Copy(LinkedAccount a) => new() { Platform = a.Platform, UserId = a.UserId, UserName = a.UserName };

    private static AccountLinkGroup Copy(AccountLinkGroup g) =>
        new() { Id = g.Id, LinkedAt = g.LinkedAt, Accounts = g.Accounts.Select(Copy).ToList() };
}
