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

/// <summary>One loyalty currency: its name, the user variable holding balances, earning rules and multipliers.</summary>
public class PointsCurrency
{
    /// <summary>Earning on/off (balances and sub-actions keep working when off).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Currency name (plural), e.g. "points", "cookies". Unique; sub-actions pick a currency by this name.</summary>
    public string Name { get; set; } = "points";

    /// <summary>Singular name, used for an amount of exactly 1.</summary>
    public string SingularName { get; set; } = "point";

    /// <summary>Optional symbol written before amounts instead of the name (e.g. "🍪"); empty by default.</summary>
    public string Symbol { get; set; } = "";

    /// <summary>User variable holding each viewer's balance (unique per currency); variable sub-actions can read and change it.</summary>
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

    // ---- multipliers (automatic earning only: presence, chat and bonuses) ----

    /// <summary>Subscribers (tier 1, or a subscriber whose tier is not known).</summary>
    public decimal SubscriberMultiplier { get; set; } = 1;

    public decimal Tier2Multiplier { get; set; } = 1;
    public decimal Tier3Multiplier { get; set; } = 1;
    public decimal VipMultiplier { get; set; } = 1;

    /// <summary>Moderators and the broadcaster.</summary>
    public decimal ModeratorMultiplier { get; set; } = 1;

    /// <summary>User group name → multiplier (groups from the Users &amp; Groups page).</summary>
    public Dictionary<string, decimal> GroupMultipliers { get; set; } = [];

    /// <summary>"platform:userId" → a per-user multiplier, applied on top of the role/group one.</summary>
    public Dictionary<string, decimal> UserMultipliers { get; set; } = [];
}

/// <summary>
/// points.json. The root object is the default currency, so version-1 files (one currency, no Version field) load
/// as the default currency unchanged, with every balance (the same user variable) kept; <see cref="Currencies"/>
/// holds the additional currencies.
/// </summary>
public sealed class PointsSettings : PointsCurrency
{
    /// <summary>2 = multiple currencies and multipliers. Version 1 files have the same root shape and need no conversion.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Additional currencies (besides the default one at the root).</summary>
    public List<PointsCurrency> Currencies { get; set; } = [];
}

public sealed record PointsEntry(int Rank, string Platform, string UserId, string UserName, long Points);

public sealed record PointsChange(string Platform, string UserId, string UserName, long OldPoints, long NewPoints, string Reason,
    string Currency = "");

public static class PointsEvents
{
    public const string ThresholdCrossed = "points.thresholdCrossed";
}

/// <summary>
/// First-class loyalty currencies on top of <see cref="UserVariables"/>: each balance is the persisted user variable
/// named by the currency's <see cref="PointsCurrency.VariableName"/> (default currency: "points"), so the generic
/// variable sub-actions see the same numbers. Earns from chat (with cooldown), presence while live (ticked by the timer
/// service, using the <see cref="UserRegistry"/>'s present viewers) and follow/sub/gift/raid bonuses (recognized through
/// the platform modules' <see cref="IChatEventAdapter"/>s, which carry the user id); automatic earnings are multiplied
/// by <see cref="Multiplier"/>. Live state comes from chat.activity StreamOnline/StreamOffline.
/// <para>
/// Every method taking a <c>currency</c> accepts a currency's name, variable name or singular name; null or empty is
/// the default currency, so callers that predate multiple currencies keep working unchanged.
/// Thread-safe: every balance change runs under one lock.
/// </para>
/// </summary>
public sealed class PointsService : IPeriodicService, IDisposable
{
    private readonly UserVariables _vars;
    private readonly JsonFile<PointsSettings>? _file;
    private readonly PointsSettings _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly UserRegistry? _users;
    private readonly GroupStore? _groups;
    private readonly IReadOnlyList<IChatEventAdapter> _adapters;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastMessagePay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextPresentPay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tiers = new(StringComparer.OrdinalIgnoreCase);

    public PointsService(UserVariables vars, JsonFile<PointsSettings>? settings, EventBus bus, AppLog log,
        UserRegistry? users = null, IEnumerable<IChatEventAdapter>? adapters = null, TimeProvider? time = null, GroupStore? groups = null)
    {
        _vars = vars;
        _file = settings;
        _settings = settings?.Value ?? new PointsSettings();
        _bus = bus;
        _log = log;
        _users = users;
        _groups = groups;
        _adapters = adapters?.ToArray() ?? [];
        _time = time ?? TimeProvider.System;
        lock (_gate) Normalize(_settings);
        _subscription = bus.Subscribe(Handle);
    }

    /// <summary>Raised after any balance change (on the thread that made it).</summary>
    public event Action<PointsChange>? Changed;

    /// <summary>Raised after currencies are added, removed or renamed.</summary>
    public event Action? CurrenciesChanged;

    /// <summary>A copy of everything (the default currency at the root, the others in Currencies).</summary>
    public PointsSettings Settings
    {
        get { lock (_gate) return Json.Clone(_settings); }
    }

    /// <summary>
    /// Changes the settings (the root is the default currency). A change leaving two currencies with the same name or
    /// variable is rejected and nothing changes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Duplicate currency name or variable.</exception>
    public void UpdateSettings(Action<PointsSettings> change)
    {
        bool renamed;
        lock (_gate)
        {
            var before = Identity(_settings);
            var copy = Json.Clone(_settings);
            change(copy);
            Normalize(copy);
            Validate(copy);
            foreach (var p in typeof(PointsSettings).GetProperties())
            {
                if (p.CanWrite) p.SetValue(_settings, p.GetValue(copy));
            }
            _nextPresentPay.Clear();
            _file?.ScheduleSave();
            renamed = before != Identity(_settings);
        }
        if (renamed) CurrenciesChanged?.Invoke();
    }

    /// <summary>Changes one currency (null = the default).</summary>
    /// <exception cref="InvalidOperationException">Unknown currency, or a duplicate name or variable.</exception>
    public void UpdateCurrency(string? currency, Action<PointsCurrency> change)
    {
        string key;
        lock (_gate) key = Resolve(currency).VariableName;
        UpdateSettings(s => change(All(s).First(c => string.Equals(c.VariableName, key, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Adds a currency (earning rules start at the defaults). Returns a copy.</summary>
    /// <exception cref="InvalidOperationException">No name, or the name or variable is taken.</exception>
    public PointsCurrency AddCurrency(string name, string? singularName = null, string? variableName = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("A currency needs a name");
        var currency = new PointsCurrency
        {
            Name = name,
            SingularName = string.IsNullOrWhiteSpace(singularName)
                ? name.Length > 1 && name.EndsWith('s') ? name[..^1] : name
                : singularName.Trim(),
            VariableName = string.IsNullOrWhiteSpace(variableName) ? VariableNameFor(name) : variableName.Trim(),
        };
        UpdateSettings(s => s.Currencies.Add(currency));
        return Json.Clone(currency);
    }

    /// <summary>Removes an additional currency (never the default). Balances stay as user variables unless <paramref name="deleteBalances"/>.</summary>
    public bool RemoveCurrency(string currency, bool deleteBalances = false)
    {
        string variable;
        lock (_gate)
        {
            var c = Find(currency);
            if (c is null || ReferenceEquals(c, _settings)) return false;
            variable = c.VariableName;
        }
        UpdateSettings(s => s.Currencies.RemoveAll(c => string.Equals(c.VariableName, variable, StringComparison.OrdinalIgnoreCase)));
        if (deleteBalances) _vars.DeleteForAllUsers(variable, null);
        return true;
    }

    /// <summary>Copies of every currency, the default first.</summary>
    public IReadOnlyList<PointsCurrency> Currencies
    {
        get { lock (_gate) return All(_settings).Select(c => Json.Clone(c)).ToArray(); }
    }

    /// <summary>Every currency name, the default first.</summary>
    public IReadOnlyList<string> CurrencyNames
    {
        get { lock (_gate) return All(_settings).Select(c => c.Name).ToArray(); }
    }

    /// <summary>A copy of one currency; null when unknown (null/empty = the default).</summary>
    public PointsCurrency? GetCurrency(string? currency)
    {
        lock (_gate) return Find(currency) is { } c ? Json.Clone(c) : null;
    }

    public bool CurrencyExists(string? currency)
    {
        lock (_gate) return Find(currency) is not null;
    }

    /// <summary>The default currency's balance variable.</summary>
    public string VariableName
    {
        get { lock (_gate) return _settings.VariableName; }
    }

    /// <summary>The default currency's name.</summary>
    public string CurrencyName
    {
        get { lock (_gate) return _settings.Name; }
    }

    /// <summary>A currency's canonical name (null/empty = the default).</summary>
    /// <exception cref="InvalidOperationException">Unknown currency.</exception>
    public string NameOf(string? currency)
    {
        lock (_gate) return Resolve(currency).Name;
    }

    /// <summary>"1,250 points", "1 point", or "🍪1,250" when a symbol is set.</summary>
    public string Format(long amount, string? currency = null)
    {
        lock (_gate) return FormatWith(Resolve(currency), amount);
    }

    private static string FormatWith(PointsCurrency c, long amount)
    {
        var number = amount.ToString("#,0", CultureInfo.InvariantCulture);
        if (c.Symbol.Length > 0) return c.Symbol + number;
        var name = Math.Abs(amount) == 1 && c.SingularName.Trim().Length > 0 ? c.SingularName : c.Name;
        return $"{number} {name}";
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

    public long Get(string platform, string userId, string? currency = null)
    {
        lock (_gate) return Read(Resolve(currency), platform, userId);
    }

    /// <summary>Adds (or with a negative amount removes) points; the result stays within 0..MaxBalance. Returns the balance.</summary>
    public long Add(string platform, string userId, string? userName, long amount, string reason = "add", string? currency = null)
    {
        PointsChange change;
        PointsCurrency c;
        lock (_gate)
        {
            c = Resolve(currency);
            change = Write(c, platform, userId, userName, Read(c, platform, userId) + amount, reason);
        }
        Publish(change, c);
        return change.NewPoints;
    }

    /// <summary>Removes points down to at least zero. Returns the balance.</summary>
    public long Remove(string platform, string userId, string? userName, long amount, string? currency = null) =>
        Add(platform, userId, userName, -Math.Abs(amount), "remove", currency);

    public long Set(string platform, string userId, string? userName, long amount, string? currency = null)
    {
        PointsChange change;
        PointsCurrency c;
        lock (_gate)
        {
            c = Resolve(currency);
            change = Write(c, platform, userId, userName, amount, "set");
        }
        Publish(change, c);
        return change.NewPoints;
    }

    /// <summary>Spends points only when the user has enough. Returns false (and changes nothing) otherwise.</summary>
    public bool TrySpend(string platform, string userId, string? userName, long amount, out long balance, string? currency = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Cannot spend a negative amount");
        PointsChange change;
        PointsCurrency c;
        lock (_gate)
        {
            c = Resolve(currency);
            balance = Read(c, platform, userId);
            if (balance < amount) return false;
            change = Write(c, platform, userId, userName, balance - amount, "spend");
            balance = change.NewPoints;
        }
        Publish(change, c);
        return true;
    }

    /// <summary>Moves points between two users atomically.</summary>
    /// <exception cref="InvalidOperationException">Not enough points, a non-positive amount, or the same user.</exception>
    public (long From, long To) Transfer(string platform, string fromId, string? fromName, string toPlatform, string toId,
        string? toName, long amount, string? currency = null)
    {
        if (amount <= 0) throw new InvalidOperationException("The amount to give must be above zero");
        if (string.Equals(platform, toPlatform, StringComparison.OrdinalIgnoreCase) && fromId == toId)
            throw new InvalidOperationException("Cannot give points to yourself");
        PointsChange from, to;
        PointsCurrency c;
        lock (_gate)
        {
            c = Resolve(currency);
            var balance = Read(c, platform, fromId);
            if (balance < amount) throw new InvalidOperationException($"Not enough {c.Name}: has {balance}, needs {amount}");
            var target = Read(c, toPlatform, toId);
            var max = c.MaxBalance;
            if (max > 0 && target + amount > max) throw new InvalidOperationException($"{toName ?? toId} would go above the maximum of {max}");
            from = Write(c, platform, fromId, fromName, balance - amount, "transfer");
            to = Write(c, toPlatform, toId, toName, target + amount, "transfer");
        }
        Publish(from, c);
        Publish(to, c);
        return (from.NewPoints, to.NewPoints);
    }

    /// <summary>Top balances (highest first; ties by name), optionally of one platform. Count 0 = everyone.</summary>
    public IReadOnlyList<PointsEntry> Leaderboard(string? platform = null, int count = 10, string? currency = null)
    {
        string name;
        lock (_gate) name = Resolve(currency).VariableName;
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
    public int Rank(string platform, string userId, string? currency = null)
    {
        var board = Leaderboard(null, 0, currency);
        var key = UserVariables.NormalizePlatform(platform);
        return board.FirstOrDefault(e => e.Platform == key && e.UserId == userId)?.Rank ?? 0;
    }

    /// <summary>Deletes every balance (optionally of one platform). Returns how many were removed.</summary>
    public int ResetAll(string? platform = null, string? currency = null)
    {
        int removed;
        string name;
        lock (_gate)
        {
            var c = Resolve(currency);
            name = c.Name;
            removed = _vars.DeleteForAllUsers(c.VariableName, string.IsNullOrWhiteSpace(platform) ? null : platform);
        }
        _log.Info("Points", $"Reset {removed} {name} balance(s)");
        Changed?.Invoke(new PointsChange(platform ?? "", "", "", 0, 0, "reset", name));
        return removed;
    }

    /// <summary>Finds a user id by name: chat users first, then users that have variables. Null when unknown.</summary>
    public string? FindUserId(string platform, string userName)
    {
        userName = userName.Trim().TrimStart('@');
        if (userName.Length == 0) return null;
        return _users?.FindByName(platform, userName)?.UserId is { Length: > 0 } id ? id : _vars.FindUserId(platform, userName);
    }

    // ---- multipliers ----

    /// <summary>
    /// The multiplier for a user's automatic earnings: the highest of the role (moderator/broadcaster, VIP, subscriber
    /// tier) and user-group multipliers that apply and are not 1 (1 when none do), times the user's own multiplier.
    /// Roles come from <paramref name="roles"/> when given, else from the user's last chat message; the subscriber tier
    /// from the last subscription event seen this session (a subscriber of unknown tier counts as tier 1).
    /// </summary>
    public decimal Multiplier(string platform, string userId, string? userName = null, string? currency = null, ChatRoles? roles = null)
    {
        PointsCurrency c;
        int tier;
        lock (_gate)
        {
            c = Json.Clone(Resolve(currency));
            tier = _tiers.GetValueOrDefault(UserKey(platform, userId));
        }
        var r = roles ?? _users?.Get(platform, userId)?.Roles ?? ChatRoles.None;
        var candidates = new List<decimal>();
        if (r.HasFlag(ChatRoles.Moderator) || r.HasFlag(ChatRoles.Broadcaster)) candidates.Add(c.ModeratorMultiplier);
        if (r.HasFlag(ChatRoles.Vip)) candidates.Add(c.VipMultiplier);
        if (r.HasFlag(ChatRoles.Subscriber) || tier > 0)
            candidates.Add(tier switch { 3 => c.Tier3Multiplier, 2 => c.Tier2Multiplier, _ => c.SubscriberMultiplier });
        if (_groups is not null && c.GroupMultipliers.Count > 0)
        {
            foreach (var group in _groups.GroupsOf(UserVariables.NormalizePlatform(platform), userId, userName ?? ""))
            {
                if (Lookup(c.GroupMultipliers, group) is { } m) candidates.Add(m);
            }
        }
        var baseMultiplier = candidates.Where(m => m != 1).DefaultIfEmpty(1).Max();
        var own = Lookup(c.UserMultipliers, UserKey(platform, userId)) ?? 1;
        return Math.Max(0, baseMultiplier * own);
    }

    /// <summary>Applies the user's multiplier to an automatic amount (rounded half away from zero).</summary>
    public long Multiplied(long amount, string platform, string userId, string? userName = null, string? currency = null, ChatRoles? roles = null)
    {
        var m = Multiplier(platform, userId, userName, currency, roles);
        return m == 1 ? amount : (long)Math.Round(amount * m, MidpointRounding.AwayFromZero);
    }

    /// <summary>A group's multiplier (null when none is set).</summary>
    public decimal? GetGroupMultiplier(string group, string? currency = null)
    {
        lock (_gate) return Lookup(Resolve(currency).GroupMultipliers, group.Trim());
    }

    /// <summary>Sets a group's multiplier; null removes it.</summary>
    public void SetGroupMultiplier(string group, decimal? multiplier, string? currency = null)
    {
        group = group.Trim();
        if (group.Length == 0) throw new InvalidOperationException("No group given");
        if (multiplier < 0) throw new InvalidOperationException("A multiplier cannot be negative");
        UpdateCurrency(currency, c =>
        {
            foreach (var key in c.GroupMultipliers.Keys.Where(k => string.Equals(k, group, StringComparison.OrdinalIgnoreCase)).ToArray())
                c.GroupMultipliers.Remove(key);
            if (multiplier is { } m) c.GroupMultipliers[group] = m;
        });
    }

    /// <summary>A user's own multiplier (null when none is set).</summary>
    public decimal? GetUserMultiplier(string platform, string userId, string? currency = null)
    {
        lock (_gate) return Lookup(Resolve(currency).UserMultipliers, UserKey(platform, userId));
    }

    /// <summary>Sets a user's own multiplier; null removes it.</summary>
    public void SetUserMultiplier(string platform, string userId, decimal? multiplier, string? currency = null)
    {
        if (string.IsNullOrEmpty(userId)) throw new InvalidOperationException("No user id given");
        if (multiplier < 0) throw new InvalidOperationException("A multiplier cannot be negative");
        var key = UserKey(platform, userId);
        UpdateCurrency(currency, c =>
        {
            foreach (var k in c.UserMultipliers.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
                c.UserMultipliers.Remove(k);
            if (multiplier is { } m) c.UserMultipliers[key] = m;
        });
    }

    /// <summary>The subscriber tier (1-3) last seen for a user in a subscription event, 0 when unknown.</summary>
    public int SubscriberTier(string platform, string userId)
    {
        lock (_gate) return _tiers.GetValueOrDefault(UserKey(platform, userId));
    }

    /// <summary>Remembers a user's subscriber tier for this session (normally learned from subscription events).</summary>
    public void SetSubscriberTier(string platform, string userId, int tier)
    {
        lock (_gate)
        {
            if (tier is >= 1 and <= 3) _tiers[UserKey(platform, userId)] = tier;
            else _tiers.Remove(UserKey(platform, userId));
        }
    }

    /// <summary>Pays every member of a user group. Returns how many were paid.</summary>
    /// <exception cref="InvalidOperationException">Unknown group or currency.</exception>
    public int AwardGroup(string group, long amount, bool applyMultipliers = false, string? currency = null)
    {
        if (_groups is null) throw new InvalidOperationException("User groups are not available");
        if (!_groups.Exists(group)) throw new InvalidOperationException($"Unknown group '{group.Trim()}'");
        lock (_gate) Resolve(currency);
        var paid = 0;
        foreach (var member in _groups.UsersIn(group))
        {
            var id = member.UserId.Length > 0 ? member.UserId : FindUserId(member.Platform, member.UserName) ?? "";
            if (id.Length == 0 || member.Platform.Length == 0) continue;
            var value = applyMultipliers ? Multiplied(amount, member.Platform, id, member.UserName, currency) : amount;
            Add(member.Platform, id, member.UserName.Length > 0 ? member.UserName : null, value, "group", currency);
            paid++;
        }
        return paid;
    }

    /// <summary>Pays every present viewer (optionally of one platform). Returns how many were paid.</summary>
    public int AwardPresent(string? platform, long amount, bool applyMultipliers = false, string? currency = null)
    {
        lock (_gate) Resolve(currency);
        if (_users is null) return 0;
        var paid = 0;
        foreach (var user in _users.PresentUsers(string.IsNullOrWhiteSpace(platform) ? null : platform))
        {
            if (user.UserId.Length == 0) continue;
            var name = user.DisplayName.Length > 0 ? user.DisplayName : user.UserName;
            var value = applyMultipliers ? Multiplied(amount, user.Platform, user.UserId, name, currency) : amount;
            Add(user.Platform, user.UserId, name, value, "award", currency);
            paid++;
        }
        return paid;
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

    /// <summary>Pays present viewers for each currency whose interval is due.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_users is null) return;
        var due = new List<(string Currency, long Amount, bool OnlyLive, string[] Ignored)>();
        string[] live;
        lock (_gate)
        {
            foreach (var c in All(_settings))
            {
                var interval = c.PresentIntervalMinutes;
                if (!c.Enabled || c.PresentAmount <= 0 || interval <= 0)
                {
                    _nextPresentPay.Remove(c.VariableName);
                    continue;
                }
                if (!_nextPresentPay.TryGetValue(c.VariableName, out var next))
                {
                    _nextPresentPay[c.VariableName] = now + TimeSpan.FromMinutes(interval);
                    continue;
                }
                if (now < next) continue;
                _nextPresentPay[c.VariableName] = now + TimeSpan.FromMinutes(interval);
                due.Add((c.VariableName, c.PresentAmount, c.OnlyWhileLive, c.IgnoredUsers.ToArray()));
            }
            live = _live.ToArray();
        }
        foreach (var (currency, amount, onlyLive, ignored) in due)
        {
            if (onlyLive && live.Length == 0) continue;
            var present = onlyLive ? live.SelectMany(p => _users.PresentUsers(p)) : _users.PresentUsers();
            foreach (var user in present)
            {
                if (user.UserId.Length == 0 || IsIgnored(ignored, user.UserName) || IsIgnored(ignored, user.DisplayName)) continue;
                var name = user.DisplayName.Length > 0 ? user.DisplayName : user.UserName;
                Add(user.Platform, user.UserId, name, Multiplied(amount, user.Platform, user.UserId, name, currency), "present", currency);
            }
        }
    }

    private void OnMessage(IReadOnlyDictionary<string, object?> args)
    {
        if (args.GetBool("isInternal")) return;
        var platform = args.GetString("platform");
        var userId = args.GetString("userId");
        var user = args.GetString("user");
        if (platform.Length == 0 || userId.Length == 0) return;
        var now = _time.GetUtcNow();
        var pay = new List<(string Currency, long Amount)>();
        lock (_gate)
        {
            foreach (var c in All(_settings))
            {
                if (!c.Enabled || c.MessageAmount <= 0 || IsIgnored(c.IgnoredUsers, user)) continue;
                if (c.OnlyWhileLive && !_live.Contains(platform)) continue;
                var key = c.VariableName + ":" + UserKey(platform, userId);
                if (_lastMessagePay.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(Math.Max(0, c.MessageCooldownSeconds))) continue;
                _lastMessagePay[key] = now;
                pay.Add((c.VariableName, c.MessageAmount));
            }
        }
        if (pay.Count == 0) return;
        var roles = ChatArgs.RolesOf(args);
        foreach (var (currency, amount) in pay)
            Add(platform, userId, user, Multiplied(amount, platform, userId, user, currency, roles), "chat", currency);
    }

    private void OnActivity(IReadOnlyDictionary<string, object?> args)
    {
        var kind = args.GetString("kind");
        var platform = args.GetString("platform");
        if (platform.Length == 0) return;
        if (string.Equals(kind, nameof(FeedKind.StreamOnline), StringComparison.OrdinalIgnoreCase))
        {
            SetLive(platform, true);
            lock (_gate) _nextPresentPay.Clear();
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
            if (e.Args.GetBool("isAnonymous")) return;
            var userId = e.Args.GetString("userId");
            var name = item.User.Length > 0 ? item.User : e.Args.GetString("user");
            if (item.Kind == FeedKind.Subscription && userId.Length > 0
                && ParseTier(e.Args.GetValueOrDefault("tier") ?? e.Args.GetValueOrDefault("subTier")) is var tier and > 0)
                SetSubscriberTier(item.Platform, userId, tier);
            var bonuses = new List<(string Currency, long Bonus, string[] Ignored)>();
            lock (_gate)
            {
                foreach (var c in All(_settings))
                {
                    if (!c.Enabled) continue;
                    var bonus = item.Kind switch
                    {
                        FeedKind.Follow => c.FollowBonus,
                        FeedKind.Subscription => c.SubscriptionBonus,
                        FeedKind.GiftSubscription => c.GiftBonusPerSub * GiftCount(e.Args, item.Title),
                        FeedKind.Raid => c.RaidBonus,
                        _ => 0,
                    };
                    if (bonus > 0) bonuses.Add((c.VariableName, bonus, c.IgnoredUsers.ToArray()));
                }
            }
            if (bonuses.Count == 0) return;
            if (userId.Length == 0) userId = FindUserId(item.Platform, name) ?? "";
            if (userId.Length == 0)
            {
                _log.Debug("Points", $"No user id for {name} ({item.Kind}); bonus skipped");
                return;
            }
            foreach (var (currency, bonus, ignored) in bonuses)
            {
                if (IsIgnored(ignored, name)) continue;
                Add(item.Platform, userId, name, Multiplied(bonus, item.Platform, userId, name, currency), item.Kind.ToString(), currency);
            }
            return;
        }
    }

    /// <summary>Subscriber tier from an event argument: 1-3, Twitch's 1000/2000/3000, "Tier 2", "Prime" (1). 0 when unknown.</summary>
    public static int ParseTier(object? value)
    {
        var text = value?.ToString()?.Trim() ?? "";
        if (text.Length == 0) return 0;
        if (text.Contains("prime", StringComparison.OrdinalIgnoreCase)) return 1;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return 0;
        if (n >= 1000) n /= 1000;
        return n is >= 1 and <= 3 ? n : 0;
    }

    private static long GiftCount(IReadOnlyDictionary<string, object?> args, string title)
    {
        foreach (var key in new[] { "total", "count", "gifts" })
        {
            if (VariableValues.TryGetNumber(args.GetValueOrDefault(key), out var n) && n > 0) return (long)n;
        }
        return (long)(ExtrasText.FirstNumber(title) ?? 1);
    }

    private static bool IsIgnored(IEnumerable<string> ignored, string? name) =>
        !string.IsNullOrWhiteSpace(name) && ignored.Contains(name.Trim().TrimStart('@'), StringComparer.OrdinalIgnoreCase);

    // ---- currencies (call under _gate) ----

    private static IEnumerable<PointsCurrency> All(PointsSettings s) => s.Currencies.Prepend(s);

    private static string Identity(PointsSettings s) => string.Join("|", All(s).Select(c => c.Name + "/" + c.VariableName));

    /// <summary>By name, then variable name, then singular name (names and variables never collide, see <see cref="Validate"/>).</summary>
    private PointsCurrency? Find(string? currency)
    {
        var wanted = currency?.Trim() ?? "";
        if (wanted.Length == 0) return _settings;
        var all = All(_settings).ToArray();
        return all.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(c => string.Equals(c.VariableName, wanted, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(c => string.Equals(c.SingularName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private PointsCurrency Resolve(string? currency) =>
        Find(currency) ?? throw new InvalidOperationException(
            $"Unknown currency '{currency?.Trim()}' (known: {string.Join(", ", All(_settings).Select(c => c.Name))})");

    private static void Normalize(PointsSettings s)
    {
        s.Version = 2;
        s.Currencies ??= [];
        s.Currencies.RemoveAll(c => c is null);
        var all = All(s).ToArray();
        for (var i = 0; i < all.Length; i++)
        {
            var c = all[i];
            c.Name = string.IsNullOrWhiteSpace(c.Name) ? (i == 0 ? "points" : "currency" + i) : c.Name.Trim();
            c.SingularName = c.SingularName?.Trim() ?? "";
            c.Symbol = c.Symbol?.Trim() ?? "";
            c.VariableName = string.IsNullOrWhiteSpace(c.VariableName) ? (i == 0 ? "points" : VariableNameFor(c.Name)) : c.VariableName.Trim();
            c.Thresholds ??= [];
            c.IgnoredUsers ??= [];
            c.GroupMultipliers ??= [];
            c.UserMultipliers ??= [];
        }
    }

    /// <summary>Names and variable names must identify one currency each (a name may equal its own variable).</summary>
    private static void Validate(PointsSettings s)
    {
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var all = All(s).ToArray();
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].VariableName.Any(char.IsWhiteSpace)) throw new InvalidOperationException("A currency variable name cannot contain spaces");
            foreach (var key in new[] { all[i].Name, all[i].VariableName })
            {
                if (owner.TryGetValue(key, out var other) && other != i)
                    throw new InvalidOperationException($"'{key}' is already used by the currency '{all[other].Name}'");
                owner[key] = i;
            }
        }
    }

    /// <summary>"Gold coins" → "goldCoins".</summary>
    internal static string VariableNameFor(string name)
    {
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray())).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return "currency";
        return string.Concat(words.Select((w, i) => (i == 0 ? char.ToLowerInvariant(w[0]) : char.ToUpperInvariant(w[0])) + w[1..]));
    }

    private static decimal? Lookup(Dictionary<string, decimal> map, string key)
    {
        foreach (var (k, v) in map)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    private static string UserKey(string platform, string userId) => UserVariables.NormalizePlatform(platform) + ":" + userId;

    // ---- storage (call under _gate) ----

    private long Read(PointsCurrency c, string platform, string userId) => ToLong(_vars.Get(platform, userId, c.VariableName));

    private PointsChange Write(PointsCurrency c, string platform, string userId, string? userName, long value, string reason)
    {
        if (string.IsNullOrWhiteSpace(platform)) throw new InvalidOperationException("No platform for the points user");
        if (string.IsNullOrEmpty(userId)) throw new InvalidOperationException("No user id for the points user");
        var old = Read(c, platform, userId);
        if (value < 0) value = 0;
        if (c.MaxBalance > 0 && value > c.MaxBalance) value = c.MaxBalance;
        _vars.Set(platform, userId, userName, c.VariableName, value, persist: true);
        var name = string.IsNullOrWhiteSpace(userName) ? userId : userName;
        return new PointsChange(UserVariables.NormalizePlatform(platform), userId, name, old, value, reason, c.Name);
    }

    private void Publish(PointsChange change, PointsCurrency c)
    {
        Changed?.Invoke(change);
        if (change.NewPoints <= change.OldPoints) return;
        long[] crossed;
        string currency, formatted;
        lock (_gate)
        {
            crossed = c.Thresholds.Where(t => change.OldPoints < t && change.NewPoints >= t).Distinct().Order().ToArray();
            currency = c.Name;
            formatted = FormatWith(c, change.NewPoints);
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
                ["currency"] = currency,
                ["pointsFormatted"] = formatted,
            }));
        }
    }

    internal static long ToLong(object? value) =>
        VariableValues.TryGetNumber(value, out var n) ? (long)decimal.Floor(Math.Clamp(n, long.MinValue, long.MaxValue)) : 0;

    public void Dispose() => _subscription.Dispose();
}
