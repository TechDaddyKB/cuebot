using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;

namespace Cuebot.Core.Points;

/// <summary>points.json: the loyalty currency's name and earning rules.</summary>
public sealed class PointsSettings
{
    /// <summary>Earning on/off (balances and sub-actions keep working when off).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Currency name (plural), e.g. "points", "cookies".</summary>
    public string Name { get; set; } = "points";

    /// <summary>Singular name, used for an amount of exactly 1.</summary>
    public string SingularName { get; set; } = "point";

    /// <summary>Optional symbol written before amounts instead of the name (e.g. "🍪"); empty by default.</summary>
    public string Symbol { get; set; } = "";

    /// <summary>User variable holding each viewer's balance; other variable sub-actions can read and change it.</summary>
    public string VariableName { get; set; } = "points";

    /// <summary>Only pay for presence and chat while a platform reports the stream as live.</summary>
    public bool OnlyWhileLive { get; set; } = true;

    /// <summary>Paid to every present viewer each <see cref="PresentIntervalMinutes"/> (0 = off).</summary>
    public long PresentAmount { get; set; } = 1;

    public int PresentIntervalMinutes { get; set; } = 1;

    /// <summary>Paid for a chat message, at most once per <see cref="MessageCooldownSeconds"/> per viewer (0 = off).</summary>
    public long MessageAmount { get; set; } = 1;

    public int MessageCooldownSeconds { get; set; } = 60;

    public long FollowBonus { get; set; } = 10;
    public long SubscriptionBonus { get; set; } = 100;

    /// <summary>Paid to the gifter per gifted subscription.</summary>
    public long GiftBonusPerSub { get; set; } = 50;

    /// <summary>Paid to the raiding broadcaster.</summary>
    public long RaidBonus { get; set; }

    /// <summary>Highest balance anyone can earn to (0 = no cap). Set/Add sub-actions are capped too.</summary>
    public long MaxBalance { get; set; }

    /// <summary>Balances that fire the "Points threshold reached" trigger when crossed upwards.</summary>
    public List<long> Thresholds { get; set; } = [];

    /// <summary>User names that never earn automatically (bots).</summary>
    public List<string> IgnoredUsers { get; set; } = [];
}

public sealed record PointsEntry(int Rank, string Platform, string UserId, string UserName, long Points);

public sealed record PointsChange(string Platform, string UserId, string UserName, long OldPoints, long NewPoints, string Reason);

public static class PointsEvents
{
    public const string ThresholdCrossed = "points.thresholdCrossed";
}

/// <summary>
/// First-class loyalty currency on top of <see cref="UserVariables"/>: each balance is the persisted user variable
/// named <see cref="PointsSettings.VariableName"/> (default "points"), so the generic variable sub-actions see the same
/// numbers. Earns from chat (with cooldown), presence while live (ticked by the timer service, using the
/// <see cref="UserRegistry"/>'s present viewers) and follow/sub/gift/raid bonuses (recognized through the platform
/// modules' <see cref="IChatEventAdapter"/>s, which carry the user id). Live state comes from chat.activity
/// StreamOnline/StreamOffline. Thread-safe: every balance change runs under one lock.
/// </summary>
public sealed class PointsService : IPeriodicService, IDisposable
{
    private readonly UserVariables _vars;
    private readonly JsonFile<PointsSettings>? _file;
    private readonly PointsSettings _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly UserRegistry? _users;
    private readonly IReadOnlyList<IChatEventAdapter> _adapters;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastMessagePay = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _nextPresentPay;

    public PointsService(UserVariables vars, JsonFile<PointsSettings>? settings, EventBus bus, AppLog log,
        UserRegistry? users = null, IEnumerable<IChatEventAdapter>? adapters = null, TimeProvider? time = null)
    {
        _vars = vars;
        _file = settings;
        _settings = settings?.Value ?? new PointsSettings();
        _bus = bus;
        _log = log;
        _users = users;
        _adapters = adapters?.ToArray() ?? [];
        _time = time ?? TimeProvider.System;
        _subscription = bus.Subscribe(Handle);
    }

    /// <summary>Raised after any balance change (on the thread that made it).</summary>
    public event Action<PointsChange>? Changed;

    public PointsSettings Settings
    {
        get { lock (_gate) return Json.Clone(_settings); }
    }

    public void UpdateSettings(Action<PointsSettings> change)
    {
        lock (_gate)
        {
            change(_settings);
            if (string.IsNullOrWhiteSpace(_settings.VariableName)) _settings.VariableName = "points";
            _settings.VariableName = _settings.VariableName.Trim();
            if (string.IsNullOrWhiteSpace(_settings.Name)) _settings.Name = "points";
            _nextPresentPay = null;
            _file?.ScheduleSave();
        }
    }

    public string VariableName
    {
        get { lock (_gate) return _settings.VariableName; }
    }

    public string CurrencyName
    {
        get { lock (_gate) return _settings.Name; }
    }

    /// <summary>"1,250 points", "1 point", or "🍪1,250" when a symbol is set.</summary>
    public string Format(long amount)
    {
        lock (_gate)
        {
            var number = amount.ToString("#,0", CultureInfo.InvariantCulture);
            if (_settings.Symbol.Length > 0) return _settings.Symbol + number;
            var name = Math.Abs(amount) == 1 && _settings.SingularName.Trim().Length > 0 ? _settings.SingularName : _settings.Name;
            return $"{number} {name}";
        }
    }

    // ---- live state ----

    public bool IsLive(string platform)
    {
        lock (_gate) return _live.Contains(platform);
    }

    public IReadOnlyList<string> LivePlatforms
    {
        get { lock (_gate) return _live.Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    /// <summary>Marks a platform live or offline (normally from stream online/offline events).</summary>
    public void SetLive(string platform, bool live)
    {
        lock (_gate)
        {
            if (live) _live.Add(platform);
            else _live.Remove(platform);
        }
    }

    // ---- balances ----

    public long Get(string platform, string userId)
    {
        lock (_gate) return Read(platform, userId);
    }

    /// <summary>Adds (or with a negative amount removes) points; the result stays within 0..MaxBalance. Returns the balance.</summary>
    public long Add(string platform, string userId, string? userName, long amount, string reason = "add")
    {
        PointsChange change;
        lock (_gate) change = Write(platform, userId, userName, Read(platform, userId) + amount, reason);
        Publish(change);
        return change.NewPoints;
    }

    /// <summary>Removes points down to at least zero. Returns the balance.</summary>
    public long Remove(string platform, string userId, string? userName, long amount) =>
        Add(platform, userId, userName, -Math.Abs(amount), "remove");

    public long Set(string platform, string userId, string? userName, long amount)
    {
        PointsChange change;
        lock (_gate) change = Write(platform, userId, userName, amount, "set");
        Publish(change);
        return change.NewPoints;
    }

    /// <summary>Spends points only when the user has enough. Returns false (and changes nothing) otherwise.</summary>
    public bool TrySpend(string platform, string userId, string? userName, long amount, out long balance)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Cannot spend a negative amount");
        PointsChange change;
        lock (_gate)
        {
            balance = Read(platform, userId);
            if (balance < amount) return false;
            change = Write(platform, userId, userName, balance - amount, "spend");
            balance = change.NewPoints;
        }
        Publish(change);
        return true;
    }

    /// <summary>Moves points between two users atomically.</summary>
    /// <exception cref="InvalidOperationException">Not enough points, a non-positive amount, or the same user.</exception>
    public (long From, long To) Transfer(string platform, string fromId, string? fromName, string toPlatform, string toId,
        string? toName, long amount)
    {
        if (amount <= 0) throw new InvalidOperationException("The amount to give must be above zero");
        if (string.Equals(platform, toPlatform, StringComparison.OrdinalIgnoreCase) && fromId == toId)
            throw new InvalidOperationException("Cannot give points to yourself");
        PointsChange from, to;
        lock (_gate)
        {
            var balance = Read(platform, fromId);
            if (balance < amount) throw new InvalidOperationException($"Not enough {_settings.Name}: has {balance}, needs {amount}");
            var target = Read(toPlatform, toId);
            var max = _settings.MaxBalance;
            if (max > 0 && target + amount > max) throw new InvalidOperationException($"{toName ?? toId} would go above the maximum of {max}");
            from = Write(platform, fromId, fromName, balance - amount, "transfer");
            to = Write(toPlatform, toId, toName, target + amount, "transfer");
        }
        Publish(from);
        Publish(to);
        return (from.NewPoints, to.NewPoints);
    }

    /// <summary>Top balances (highest first; ties by name), optionally of one platform. Count 0 = everyone.</summary>
    public IReadOnlyList<PointsEntry> Leaderboard(string? platform = null, int count = 10)
    {
        var name = VariableName;
        var rows = _vars.Snapshot(string.IsNullOrWhiteSpace(platform) ? null : platform)
            .Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(v => (v.Platform, v.UserId, v.UserName, Points: ToLong(v.Value)))
            .OrderByDescending(v => v.Points)
            .ThenBy(v => v.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<PointsEntry>();
        for (var i = 0; i < rows.Count && (count <= 0 || i < count); i++)
        {
            // Equal balances share a rank (1, 2, 2, 4).
            var rank = i > 0 && rows[i].Points == rows[i - 1].Points ? result[i - 1].Rank : i + 1;
            result.Add(new PointsEntry(rank, rows[i].Platform, rows[i].UserId, rows[i].UserName, rows[i].Points));
        }
        return result;
    }

    /// <summary>1-based rank of a user among everyone (0 when they have no balance).</summary>
    public int Rank(string platform, string userId)
    {
        var board = Leaderboard(null, 0);
        var key = UserVariables.NormalizePlatform(platform);
        return board.FirstOrDefault(e => e.Platform == key && e.UserId == userId)?.Rank ?? 0;
    }

    /// <summary>Deletes every balance (optionally of one platform). Returns how many were removed.</summary>
    public int ResetAll(string? platform = null)
    {
        int removed;
        lock (_gate) removed = _vars.DeleteForAllUsers(_settings.VariableName, string.IsNullOrWhiteSpace(platform) ? null : platform);
        _log.Info("Points", $"Reset {removed} balance(s)");
        Changed?.Invoke(new PointsChange(platform ?? "", "", "", 0, 0, "reset"));
        return removed;
    }

    /// <summary>Finds a user id by name: chat users first, then users that have variables. Null when unknown.</summary>
    public string? FindUserId(string platform, string userName)
    {
        userName = userName.Trim().TrimStart('@');
        if (userName.Length == 0) return null;
        return _users?.FindByName(platform, userName)?.UserId is { Length: > 0 } id ? id : _vars.FindUserId(platform, userName);
    }

    // ---- earning ----

    /// <summary>Processes one bus event (subscribed automatically; public for tests).</summary>
    public void Handle(BotEvent e)
    {
        try
        {
            if (e.Type == ChatHub.MessageEvent) OnMessage(e.Args);
            else if (e.Type == ChatHub.ActivityEvent) OnActivity(e.Args);
            else if (!e.Type.StartsWith("points.", StringComparison.Ordinal) && !e.Type.StartsWith("variables.", StringComparison.Ordinal))
                OnPlatformEvent(e);
        }
        catch (Exception ex)
        {
            _log.Error("Points", $"Could not award points for {e.Type}", ex);
        }
    }

    void IPeriodicService.Tick(DateTimeOffset now) => Tick(now);

    /// <summary>Pays present viewers when the interval is due.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_users is null) return;
        long amount;
        bool onlyLive;
        string[] live;
        lock (_gate)
        {
            var interval = _settings.PresentIntervalMinutes;
            if (!_settings.Enabled || _settings.PresentAmount <= 0 || interval <= 0)
            {
                _nextPresentPay = null;
                return;
            }
            if (_nextPresentPay is null)
            {
                _nextPresentPay = now + TimeSpan.FromMinutes(interval);
                return;
            }
            if (now < _nextPresentPay) return;
            _nextPresentPay = now + TimeSpan.FromMinutes(interval);
            amount = _settings.PresentAmount;
            onlyLive = _settings.OnlyWhileLive;
            live = _live.ToArray();
        }
        if (onlyLive && live.Length == 0) return;
        var present = onlyLive ? live.SelectMany(p => _users.PresentUsers(p)) : _users.PresentUsers();
        foreach (var user in present)
        {
            if (user.UserId.Length == 0 || IsIgnored(user.UserName) || IsIgnored(user.DisplayName)) continue;
            Add(user.Platform, user.UserId, user.DisplayName.Length > 0 ? user.DisplayName : user.UserName, amount, "present");
        }
    }

    private void OnMessage(IReadOnlyDictionary<string, object?> args)
    {
        if (args.GetBool("isInternal")) return;
        var platform = args.GetString("platform");
        var userId = args.GetString("userId");
        var user = args.GetString("user");
        if (platform.Length == 0 || userId.Length == 0 || IsIgnored(user)) return;
        var now = _time.GetUtcNow();
        long amount;
        lock (_gate)
        {
            if (!_settings.Enabled || _settings.MessageAmount <= 0) return;
            if (_settings.OnlyWhileLive && !_live.Contains(platform)) return;
            var key = UserVariables.NormalizePlatform(platform) + ":" + userId;
            if (_lastMessagePay.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(Math.Max(0, _settings.MessageCooldownSeconds))) return;
            _lastMessagePay[key] = now;
            amount = _settings.MessageAmount;
        }
        Add(platform, userId, user, amount, "chat");
    }

    private void OnActivity(IReadOnlyDictionary<string, object?> args)
    {
        var kind = args.GetString("kind");
        var platform = args.GetString("platform");
        if (platform.Length == 0) return;
        if (string.Equals(kind, nameof(FeedKind.StreamOnline), StringComparison.OrdinalIgnoreCase))
        {
            SetLive(platform, true);
            lock (_gate) _nextPresentPay = null;
        }
        else if (string.Equals(kind, nameof(FeedKind.StreamOffline), StringComparison.OrdinalIgnoreCase))
        {
            SetLive(platform, false);
        }
    }

    /// <summary>Follow / subscription / gift / raid bonuses from the modules' own events (they carry the user id).</summary>
    private void OnPlatformEvent(BotEvent e)
    {
        if (_adapters.Count == 0) return;
        foreach (var adapter in _adapters)
        {
            if (adapter.ToFeedItem(e) is not { } item) continue;
            long bonus;
            lock (_gate)
            {
                if (!_settings.Enabled) return;
                bonus = item.Kind switch
                {
                    FeedKind.Follow => _settings.FollowBonus,
                    FeedKind.Subscription => _settings.SubscriptionBonus,
                    FeedKind.GiftSubscription => _settings.GiftBonusPerSub * GiftCount(e.Args, item.Title),
                    FeedKind.Raid => _settings.RaidBonus,
                    _ => 0,
                };
            }
            if (bonus <= 0) return;
            if (e.Args.GetBool("isAnonymous")) return;
            var userId = e.Args.GetString("userId");
            var name = item.User.Length > 0 ? item.User : e.Args.GetString("user");
            if (userId.Length == 0) userId = FindUserId(item.Platform, name) ?? "";
            if (userId.Length == 0 || IsIgnored(name))
            {
                if (userId.Length == 0) _log.Debug("Points", $"No user id for {name} ({item.Kind}); bonus skipped");
                return;
            }
            Add(item.Platform, userId, name, bonus, item.Kind.ToString());
            return;
        }
    }

    private static long GiftCount(IReadOnlyDictionary<string, object?> args, string title)
    {
        foreach (var key in new[] { "total", "count", "gifts" })
        {
            if (VariableValues.TryGetNumber(args.GetValueOrDefault(key), out var n) && n > 0) return (long)n;
        }
        return (long)(ExtrasText.FirstNumber(title) ?? 1);
    }

    private bool IsIgnored(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate) return _settings.IgnoredUsers.Contains(name.Trim().TrimStart('@'), StringComparer.OrdinalIgnoreCase);
    }

    // ---- storage (call under _gate) ----

    private long Read(string platform, string userId) => ToLong(_vars.Get(platform, userId, _settings.VariableName));

    private PointsChange Write(string platform, string userId, string? userName, long value, string reason)
    {
        if (string.IsNullOrWhiteSpace(platform)) throw new InvalidOperationException("No platform for the points user");
        if (string.IsNullOrEmpty(userId)) throw new InvalidOperationException("No user id for the points user");
        var old = Read(platform, userId);
        if (value < 0) value = 0;
        if (_settings.MaxBalance > 0 && value > _settings.MaxBalance) value = _settings.MaxBalance;
        _vars.Set(platform, userId, userName, _settings.VariableName, value, persist: true);
        var name = string.IsNullOrWhiteSpace(userName) ? userId : userName;
        return new PointsChange(UserVariables.NormalizePlatform(platform), userId, name, old, value, reason);
    }

    private void Publish(PointsChange change)
    {
        Changed?.Invoke(change);
        if (change.NewPoints <= change.OldPoints) return;
        long[] crossed;
        string currency;
        lock (_gate)
        {
            crossed = _settings.Thresholds.Where(t => change.OldPoints < t && change.NewPoints >= t).Distinct().Order().ToArray();
            currency = _settings.Name;
        }
        foreach (var threshold in crossed)
        {
            _bus.Publish(new BotEvent(PointsEvents.ThresholdCrossed, new Dictionary<string, object?>
            {
                ["platform"] = change.Platform,
                ["userId"] = change.UserId,
                ["user"] = change.UserName,
                ["userName"] = change.UserName,
                ["threshold"] = threshold,
                ["points"] = change.NewPoints,
                ["previousPoints"] = change.OldPoints,
                ["pointsName"] = currency,
                ["pointsFormatted"] = Format(change.NewPoints),
            }));
        }
    }

    internal static long ToLong(object? value) =>
        VariableValues.TryGetNumber(value, out var n) ? (long)decimal.Floor(Math.Clamp(n, long.MinValue, long.MaxValue)) : 0;

    public void Dispose() => _subscription.Dispose();
}
