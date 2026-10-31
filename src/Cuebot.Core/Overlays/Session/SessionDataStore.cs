using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Overlays.Session;

/// <summary>A count and a summed amount (money, bits, Kicks...; for other types the amount equals the count).</summary>
public sealed class SessionTally
{
    public long Count { get; set; }
    public double Amount { get; set; }

    public void Add(long count, double amount)
    {
        Count += count;
        Amount += amount;
    }

    public void Clear() => (Count, Amount) = (0, 0);

    public JsonObject ToJson() => new() { ["count"] = Count, ["amount"] = OverlayAlerts.ToNode(Math.Round(Amount, 2)) };
}

/// <summary>Everything kept for one alert type ("follower", "subscriber", "tip", ...).</summary>
public sealed class SessionTypeData
{
    public JsonObject? Latest { get; set; }
    public SessionTally Session { get; set; } = new();
    public SessionTally Week { get; set; } = new();
    public SessionTally Month { get; set; } = new();
    public SessionTally Total { get; set; } = new();
    public double Goal { get; set; }
    public List<JsonObject> Recent { get; set; } = [];

    /// <summary>Largest single amount per period ("session", "weekly", "monthly", "alltime").</summary>
    public Dictionary<string, JsonObject> TopDonation { get; set; } = [];

    /// <summary>Summed amount per user name, per period.</summary>
    public Dictionary<string, Dictionary<string, double>> Donators { get; set; } = [];

    /// <summary>Subscriber kinds this session ("new", "resub", "gifted").</summary>
    public Dictionary<string, long> SubscriberKinds { get; set; } = [];

    /// <summary>Everyone this session, in the order they first appeared, with their summed amount (for credit rolls).</summary>
    public Dictionary<string, double> SessionUsers { get; set; } = [];
}

/// <summary>session-data.json.</summary>
public sealed class SessionDataDocument
{
    public Dictionary<string, SessionTypeData> Types { get; set; } = [];

    /// <summary>The most recent alerts of every type, newest first (StreamElements' <c>recents</c>).</summary>
    public List<JsonObject> Recents { get; set; } = [];

    public string WeekKey { get; set; } = "";
    public string MonthKey { get; set; } = "";

    /// <summary>When the last live platform went offline; the session resets <see cref="SessionDataStore.ResetDelay"/> later.</summary>
    public DateTimeOffset? OfflineSince { get; set; }

    public DateTimeOffset? SessionStarted { get; set; }
}

/// <summary>
/// Stream totals across every platform, in StreamElements' session data shape, for goal bars, labels and event lists:
/// <c>{type}-latest</c>, <c>-session</c>, <c>-week</c>, <c>-month</c>, <c>-total</c> (<c>{count, amount}</c>),
/// <c>-goal</c>, <c>-recent</c>, <c>-session-users</c> (everyone this session, <c>[{name, amount}]</c> in the order they
/// first appeared, at most <see cref="SessionUsersSize"/>), and for amounts the
/// <c>{session,weekly,monthly,alltime}-top-{donation,donator}</c> leaderboards. Types are the alert listeners without "-latest" ("follower", "subscriber", "tip", "cheer", "raid",
/// "superchat", "kicks", ...). Amounts of different currencies are added as they are.
/// <para>
/// The session resets <see cref="ResetDelay"/> after the last live platform goes offline (or on <see cref="ResetSession"/>);
/// week (ISO, local time) and month totals roll over by the calendar; totals and goals persist (session-data.json, in
/// memory when <c>paths</c> is null). Every change is sent to overlay pages as <c>onSessionUpdate</c>.
/// </para>
/// </summary>
public sealed class SessionDataStore : IPeriodicService, IDisposable
{
    public static readonly TimeSpan ResetDelay = TimeSpan.FromMinutes(15);
    public const int RecentSize = 25;
    public const int SessionUsersSize = 1000;

    /// <summary>Types whose amount is money or currency-like (bits, Kicks, Jewels), with leaderboards.</summary>
    public static readonly IReadOnlySet<string> AmountTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "tip", "cheer", "superchat", "kicks", "jewels", "charityCampaignDonation", "merch",
    };

    private static readonly string[] Periods = ["session", "weekly", "monthly", "alltime"];

    private readonly object _gate = new();
    private readonly JsonFile<SessionDataDocument>? _file;
    private readonly SessionDataDocument _memory = new();
    private readonly OverlayFeed _feed;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _subscription;
    private readonly OverlayEventBridge? _bridge;

    public SessionDataStore(AppPaths? paths, AppLog log, EventBus bus, OverlayFeed feed, OverlayEventBridge? bridge, TimeProvider? time = null)
    {
        _feed = feed;
        _time = time ?? TimeProvider.System;
        if (paths is not null) _file = new JsonFile<SessionDataDocument>(paths.File("session-data.json"), log);
        lock (_gate) Roll(_time.GetLocalNow());
        _subscription = bus.Subscribe(OnEvent);
        _bridge = bridge;
        if (bridge is not null) bridge.AlertSent += Record;
    }

    /// <summary>Raised (on the caller's thread) after any change.</summary>
    public event Action? Changed;

    private SessionDataDocument Data => _file?.Value ?? _memory;

    /// <summary>StreamElements' <c>session.data</c>.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            Roll(_time.GetLocalNow());
            var data = new JsonObject();
            foreach (var (type, t) in Data.Types.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                data[$"{type}-latest"] = t.Latest?.DeepClone() ?? new JsonObject { ["name"] = "", ["amount"] = 0 };
                data[$"{type}-session"] = t.Session.ToJson();
                data[$"{type}-week"] = t.Week.ToJson();
                data[$"{type}-month"] = t.Month.ToJson();
                data[$"{type}-total"] = t.Total.ToJson();
                data[$"{type}-count"] = new JsonObject { ["count"] = t.Total.Count };
                data[$"{type}-goal"] = new JsonObject { ["amount"] = OverlayAlerts.ToNode(t.Goal) };
                data[$"{type}-recent"] = new JsonArray([.. t.Recent.Select(r => r.DeepClone())]);
                data[$"{type}-session-users"] = new JsonArray([.. t.SessionUsers.Select(u => (JsonNode)new JsonObject
                {
                    ["name"] = u.Key,
                    ["amount"] = OverlayAlerts.ToNode(Math.Round(u.Value, 2)),
                })]);
                if (AmountTypes.Contains(type))
                {
                    foreach (var period in Periods)
                    {
                        data[$"{type}-{period}-top-donation"] = t.TopDonation.GetValueOrDefault(period)?.DeepClone() ?? Empty();
                        data[$"{type}-{period}-top-donator"] = TopDonator(t, period);
                    }
                }
            }
            if (Data.Types.TryGetValue("subscriber", out var subs))
            {
                foreach (var kind in (string[])["new", "resub", "gifted"])
                    data[$"subscriber-{kind}-session"] = new JsonObject { ["count"] = subs.SubscriberKinds.GetValueOrDefault(kind) };
            }
            return data;
        }
    }

    /// <summary>The most recent alerts of every type, newest first (StreamElements' <c>recents</c>).</summary>
    public JsonArray Recents()
    {
        lock (_gate) return new JsonArray([.. Data.Recents.Select(r => r.DeepClone())]);
    }

    /// <summary>Starts a new session: session totals, session leaderboards and subscriber kinds go back to zero.</summary>
    public void ResetSession()
    {
        lock (_gate) ResetSessionCore(_time.GetUtcNow());
        Notify();
    }

    /// <summary>Sets a type's goal (e.g. "follower", 100); 0 clears it.</summary>
    public void SetGoal(string type, double amount)
    {
        lock (_gate)
        {
            Type(type).Goal = Math.Max(0, amount);
            Save();
        }
        Notify();
    }

    /// <summary>Counts an alert the overlay bridge sent.</summary>
    public void Record(OverlayAlert alert, string service, DateTimeOffset time)
    {
        if (!alert.Listener.EndsWith("-latest", StringComparison.Ordinal)) return;
        var type = alert.Listener[..^"-latest".Length];
        var e = alert.Event;
        var name = e["name"]?.GetValue<string>() ?? "";
        var amount = OverlayAlerts.NumberOf(e["amount"], fallback: 1);
        lock (_gate)
        {
            Roll(time.ToLocalTime());
            var t = Type(type);
            var latest = e.DeepClone().AsObject();
            latest["service"] = service;
            t.Latest = latest;

            var counted = AmountTypes.Contains(type) ? amount : alert.Count;
            t.Session.Add(alert.Count, counted);
            t.Week.Add(alert.Count, counted);
            t.Month.Add(alert.Count, counted);
            t.Total.Add(alert.Count, counted);

            var recent = new JsonObject
            {
                ["type"] = e["type"]?.DeepClone() ?? type,
                ["name"] = name,
                ["amount"] = e["amount"]?.DeepClone(),
                ["tier"] = e["tier"]?.DeepClone(),
                ["message"] = e["message"]?.DeepClone(),
                ["currency"] = e["currency"]?.DeepClone(),
                ["gifted"] = e["gifted"]?.DeepClone(),
                ["bulkGifted"] = e["bulkGifted"]?.DeepClone(),
                ["sender"] = e["sender"]?.DeepClone(),
                ["redemption"] = e["redemption"]?.DeepClone(),
                ["service"] = service,
                ["createdAt"] = time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            };
            Push(t.Recent, recent);
            Push(Data.Recents, recent.DeepClone().AsObject());

            if (AmountTypes.Contains(type) && name.Length > 0 && amount > 0)
            {
                foreach (var period in Periods)
                {
                    if (t.TopDonation.GetValueOrDefault(period) is not { } top || amount > OverlayAlerts.NumberOf(top["amount"]))
                        t.TopDonation[period] = new JsonObject { ["name"] = name, ["amount"] = OverlayAlerts.ToNode(amount) };
                    var donators = t.Donators.TryGetValue(period, out var map) ? map : t.Donators[period] = new(StringComparer.OrdinalIgnoreCase);
                    donators[name] = donators.GetValueOrDefault(name) + amount;
                }
            }

            if (name.Length > 0 && (t.SessionUsers.ContainsKey(name) || t.SessionUsers.Count < SessionUsersSize))
                t.SessionUsers[name] = t.SessionUsers.GetValueOrDefault(name) + counted;

            if (type == "subscriber" && alert.Count > 0)
            {
                var gifted = e["gifted"]?.GetValue<bool>() == true || e["bulkGifted"]?.GetValue<bool>() == true;
                var kind = gifted ? "gifted" : amount > 1 ? "resub" : "new";
                t.SubscriberKinds[kind] = t.SubscriberKinds.GetValueOrDefault(kind) + alert.Count;
            }
            Data.SessionStarted ??= time;
            Save();
        }
        Notify();
    }

    /// <summary>Resets the session once it has been offline long enough, and rolls week and month over.</summary>
    public void Tick(DateTimeOffset now)
    {
        bool changed;
        lock (_gate)
        {
            changed = Roll(now.ToLocalTime());
            if (Data.OfflineSince is { } since && now - since >= ResetDelay && _live.Count == 0)
            {
                ResetSessionCore(now);
                changed = true;
            }
        }
        if (changed) Notify();
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.ActivityEvent) return;
        var kind = OverlayAlerts.Str(e.Args, "kind");
        var platform = OverlayAlerts.Str(e.Args, "platform");
        var now = _time.GetUtcNow();
        bool changed = false;
        lock (_gate)
        {
            if (kind == nameof(FeedKind.StreamOnline))
            {
                _live.Add(platform);
                if (Data.OfflineSince is { } since && now - since >= ResetDelay)
                {
                    ResetSessionCore(now);
                    changed = true;
                }
                Data.OfflineSince = null;
                Save();
            }
            else if (kind == nameof(FeedKind.StreamOffline))
            {
                _live.Remove(platform);
                if (_live.Count == 0) Data.OfflineSince = now;
                Save();
            }
        }
        if (changed) Notify();
    }

    private void ResetSessionCore(DateTimeOffset now)
    {
        foreach (var t in Data.Types.Values)
        {
            t.Session.Clear();
            t.TopDonation.Remove("session");
            t.Donators.Remove("session");
            t.SubscriberKinds.Clear();
            t.SessionUsers.Clear();
        }
        Data.OfflineSince = null;
        Data.SessionStarted = now;
        Save();
    }

    /// <summary>Clears week or month totals when the calendar moved on; returns whether anything changed.</summary>
    private bool Roll(DateTimeOffset local)
    {
        var week = $"{ISOWeek.GetYear(local.DateTime)}-W{ISOWeek.GetWeekOfYear(local.DateTime):00}";
        var month = local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var changed = false;
        if (Data.WeekKey != week)
        {
            if (Data.WeekKey.Length > 0)
            {
                foreach (var t in Data.Types.Values)
                {
                    t.Week.Clear();
                    t.TopDonation.Remove("weekly");
                    t.Donators.Remove("weekly");
                }
            }
            Data.WeekKey = week;
            changed = true;
        }
        if (Data.MonthKey != month)
        {
            if (Data.MonthKey.Length > 0)
            {
                foreach (var t in Data.Types.Values)
                {
                    t.Month.Clear();
                    t.TopDonation.Remove("monthly");
                    t.Donators.Remove("monthly");
                }
            }
            Data.MonthKey = month;
            changed = true;
        }
        if (changed) Save();
        return changed;
    }

    private SessionTypeData Type(string type) =>
        Data.Types.TryGetValue(type, out var t) ? t : Data.Types[type] = new SessionTypeData();

    private static JsonObject TopDonator(SessionTypeData t, string period)
    {
        if (t.Donators.GetValueOrDefault(period) is not { Count: > 0 } map) return Empty();
        var (name, amount) = map.MaxBy(p => p.Value);
        return new JsonObject { ["name"] = name, ["amount"] = OverlayAlerts.ToNode(Math.Round(amount, 2)) };
    }

    private static JsonObject Empty() => new() { ["name"] = "", ["amount"] = 0 };

    private static void Push(List<JsonObject> list, JsonObject item)
    {
        list.Insert(0, item);
        if (list.Count > RecentSize) list.RemoveRange(RecentSize, list.Count - RecentSize);
    }

    private void Save() => _file?.ScheduleSave();

    private void Notify()
    {
        Changed?.Invoke();
        _feed.Publish(OverlayMessage.Session(Snapshot()));
    }

    public void Dispose()
    {
        _subscription.Dispose();
        if (_bridge is not null) _bridge.AlertSent -= Record;
        _file?.Dispose();
    }
}
