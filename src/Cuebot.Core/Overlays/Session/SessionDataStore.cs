using System.Globalization;
using System.Text.Json;
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

    /// <summary>Takes an event back out; never goes below zero (totals from before the event list may be smaller).</summary>
    public void Remove(long count, double amount)
    {
        Count = Math.Max(0, Count - count);
        Amount = Math.Max(0, Math.Round(Amount - amount, 6));
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

/// <summary>When this stream's totals start again.</summary>
public enum SessionResetMode
{
    /// <summary><see cref="SessionDataStore.ResetDelay"/> after the last live platform goes offline (or on going live after that).</summary>
    AfterOffline,

    /// <summary>Only with the reset button, the API, the assistant or a sub-action.</summary>
    Manual,
}

/// <summary>
/// One event the totals counted, kept so it can be looked at, corrected or taken back out later. Totals from before
/// Cuebot kept this list (or older than <see cref="SessionDataStore.EventLogSize"/> events) cannot be taken apart, only
/// overridden.
/// </summary>
public sealed class StoredStreamEvent
{
    public string Id { get; set; } = "";

    /// <summary>The totals type: "follower", "subscriber", "tip", "cheer"...</summary>
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";
    public double Amount { get; set; }

    /// <summary>How many the event added to counts (gift bundles: see <see cref="OverlayAlert.Count"/>).</summary>
    public int Count { get; set; } = 1;

    /// <summary>The platform or service ("twitch", "kick", "StreamElements"...).</summary>
    public string Service { get; set; } = "";

    public DateTimeOffset Time { get; set; }

    /// <summary>A test or emulated event.</summary>
    public bool IsTest { get; set; }

    /// <summary>Counted in the week, month and all-time totals and in the leaderboards (false for test events by default).</summary>
    public bool InTotals { get; set; } = true;

    /// <summary>The stream (session number) it counted in, and its ISO week and month.</summary>
    public int Session { get; set; }
    public string WeekKey { get; set; } = "";
    public string MonthKey { get; set; } = "";

    /// <summary>The event as overlays received it (what <c>{type}-latest</c> shows).</summary>
    public JsonObject Event { get; set; } = [];
}

/// <summary>Adds its own keys to the overlay session data (e.g. the top spenders), and says when they change.</summary>
public interface ISessionDataSource
{
    void AddTo(JsonObject data);

    event Action? Changed;
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

    /// <summary>Goes up by one each time the session resets; events remember the one they counted in.</summary>
    public int SessionNumber { get; set; }

    /// <summary>The counted events, oldest first, at most <see cref="SessionDataStore.EventLogSize"/>.</summary>
    public List<StoredStreamEvent> Events { get; set; } = [];

    /// <summary>Count test and emulated events in the week, month and all-time totals and leaderboards too.</summary>
    public bool CountTestEvents { get; set; }

    public SessionResetMode ResetMode { get; set; } = SessionResetMode.AfterOffline;
}

/// <summary>Which stored events to list or remove; every part is optional.</summary>
public sealed record StreamEventFilter(string? Type = null, string? Service = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    bool TestOnly = false, string? Name = null)
{
    public bool Matches(StoredStreamEvent e) =>
        (string.IsNullOrWhiteSpace(Type) || e.Type.Equals(Type.Trim(), StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(Service) || e.Service.Equals(Service.Trim(), StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(Name) || e.Name.Contains(Name.Trim(), StringComparison.OrdinalIgnoreCase))
        && (From is not { } from || e.Time >= from)
        && (To is not { } to || e.Time <= to)
        && (!TestOnly || e.IsTest);
}

/// <summary>A change to one stored event; null parts stay as they are.</summary>
public sealed record StreamEventEdit(string? Name = null, double? Amount = null, bool? IsTest = null);

/// <summary>
/// Stream totals across every platform, in StreamElements' session data shape, for goal bars, labels and event lists:
/// <c>{type}-latest</c>, <c>-session</c>, <c>-week</c>, <c>-month</c>, <c>-total</c> (<c>{count, amount}</c>),
/// <c>-goal</c>, <c>-recent</c>, <c>-session-users</c> (everyone this session, <c>[{name, amount}]</c> in the order they
/// first appeared, at most <see cref="SessionUsersSize"/>), and for amounts the
/// <c>{session,weekly,monthly,alltime}-top-{donation,donator}</c> leaderboards. Types are the alert listeners without "-latest" ("follower", "subscriber", "tip", "cheer", "raid",
/// "superchat", "kicks", ...). Amounts of different currencies are added as they are.
/// <para>
/// The session resets <see cref="ResetDelay"/> after the last live platform goes offline (unless
/// <see cref="SessionResetMode.Manual"/>) or on <see cref="ResetSession"/>; week (ISO, local time) and month totals roll
/// over by the calendar; totals and goals persist (session-data.json, in memory when <c>paths</c> is null). Every change
/// is sent to overlay pages as <c>onSessionUpdate</c>.
/// </para>
/// <para>
/// Each counted event is also kept (the last <see cref="EventLogSize"/>), so one can be edited or removed and every
/// total, list and leaderboard it touched follows. Test and emulated events count for this stream only (latest, recent
/// lists, session totals, so overlays can be tried out) unless <see cref="SessionDataDocument.CountTestEvents"/>; they
/// are cleared when the next stream starts.
/// </para>
/// </summary>
public sealed class SessionDataStore : IPeriodicService, IDisposable
{
    public static readonly TimeSpan ResetDelay = TimeSpan.FromMinutes(15);
    public const int RecentSize = 25;
    public const int SessionUsersSize = 1000;
    public const int EventLogSize = 5000;

    /// <summary>Tally periods as the API names them, and leaderboard periods.</summary>
    public static readonly IReadOnlyList<string> TallyPeriods = ["session", "week", "month", "total"];
    public static readonly IReadOnlyList<string> Periods = ["session", "weekly", "monthly", "alltime"];

    /// <summary>Types whose amount is money or currency-like (bits, Kicks, Jewels), with leaderboards.</summary>
    public static readonly IReadOnlySet<string> AmountTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "tip", "cheer", "superchat", "kicks", "jewels", "charityCampaignDonation", "merch",
    };

    /// <summary>
    /// Types whose session users (the Credits roll) add up the event's amount rather than counting events: the amount
    /// types, plus raids, whose amount is the number of viewers that came along.
    /// </summary>
    private static readonly IReadOnlySet<string> UserAmountTypes = new HashSet<string>(AmountTypes, StringComparer.Ordinal) { "raid" };

    private static readonly JsonSerializerOptions ExportOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly JsonFile<SessionDataDocument>? _file;
    private readonly SessionDataDocument _memory = new();
    private readonly OverlayFeed _feed;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _subscription;
    private readonly OverlayEventBridge? _bridge;
    private readonly ISessionDataSource[] _sources;

    public SessionDataStore(AppPaths? paths, AppLog log, EventBus bus, OverlayFeed feed, OverlayEventBridge? bridge, TimeProvider? time = null,
        IEnumerable<ISessionDataSource>? sources = null)
    {
        _sources = sources?.ToArray() ?? [];
        foreach (var source in _sources) source.Changed += Notify;
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

    /// <summary>StreamElements' <c>session.data</c>, with the keys of every <see cref="ISessionDataSource"/>.</summary>
    public JsonObject Snapshot()
    {
        var data = Own();
        foreach (var source in _sources) source.AddTo(data);
        return data;
    }

    private JsonObject Own()
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

    // ---- Status and settings -----------------------------------------------------------------------------------

    /// <summary>The types that have totals ("follower", "tip"...), sorted.</summary>
    public IReadOnlyList<string> Types()
    {
        lock (_gate) return [.. Data.Types.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>When this stream started, how the totals reset, how many events are kept, and the test-event setting.</summary>
    public JsonObject Info()
    {
        lock (_gate)
        {
            var oldest = Data.Events.Count > 0 ? Data.Events[0].Time : (DateTimeOffset?)null;
            DateTimeOffset? resetsAt = Data.ResetMode == SessionResetMode.AfterOffline && Data.OfflineSince is { } off ? off + ResetDelay : null;
            return new JsonObject
            {
                ["sessionStarted"] = Data.SessionStarted?.ToString("o", CultureInfo.InvariantCulture),
                ["resetMode"] = ResetModeName(Data.ResetMode),
                ["resetDelayMinutes"] = ResetDelay.TotalMinutes,
                ["offlineSince"] = Data.OfflineSince?.ToString("o", CultureInfo.InvariantCulture),
                ["resetsAt"] = resetsAt?.ToString("o", CultureInfo.InvariantCulture),
                ["countTestEvents"] = Data.CountTestEvents,
                ["storedEvents"] = Data.Events.Count,
                ["storedEventsLimit"] = EventLogSize,
                ["oldestStoredEvent"] = oldest?.ToString("o", CultureInfo.InvariantCulture),
                ["testEvents"] = Data.Events.Count(e => e.IsTest),
                ["types"] = new JsonArray([.. Data.Types.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode)k)]),
            };
        }
    }

    public SessionResetMode ResetMode
    {
        get { lock (_gate) return Data.ResetMode; }
    }

    public bool CountTestEvents
    {
        get { lock (_gate) return Data.CountTestEvents; }
    }

    public DateTimeOffset? SessionStarted
    {
        get { lock (_gate) return Data.SessionStarted; }
    }

    public static string ResetModeName(SessionResetMode mode) => mode == SessionResetMode.Manual ? "manual" : "afterOffline";

    public static SessionResetMode ParseResetMode(string text) => text.Trim().ToLowerInvariant() switch
    {
        "manual" or "never" => SessionResetMode.Manual,
        "afteroffline" or "auto" or "automatic" or "golive" or "going live" => SessionResetMode.AfterOffline,
        _ => throw new ArgumentException($"Unknown reset mode '{text}' (afterOffline or manual)"),
    };

    /// <summary>When this stream's totals reset by themselves: after going offline, or only by hand.</summary>
    public void SetResetMode(SessionResetMode mode)
    {
        lock (_gate)
        {
            Data.ResetMode = mode;
            Save();
        }
        Notify();
    }

    /// <summary>
    /// Whether test and emulated events count in the week, month and all-time totals and the leaderboards. Applies to
    /// new events; use <see cref="EditEvent"/> or <see cref="RemoveEvents"/> for stored ones.
    /// </summary>
    public void SetCountTestEvents(bool count)
    {
        lock (_gate)
        {
            Data.CountTestEvents = count;
            Save();
        }
        Notify();
    }

    // ---- Recording -----------------------------------------------------------------------------------------------

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
            var id = Guid.NewGuid().ToString("N")[..12];
            var latest = e.DeepClone().AsObject();
            latest["service"] = service;
            latest["id"] = id;
            if (alert.IsTest) latest["isTest"] = true;
            t.Latest = latest;

            var stored = new StoredStreamEvent
            {
                Id = id,
                Type = type,
                Name = name,
                Amount = amount,
                Count = alert.Count,
                Service = service,
                Time = time,
                IsTest = alert.IsTest,
                InTotals = !alert.IsTest || Data.CountTestEvents,
                Session = Data.SessionNumber,
                WeekKey = Data.WeekKey,
                MonthKey = Data.MonthKey,
                Event = latest.DeepClone().AsObject(),
            };
            Apply(stored, +1);

            var recent = new JsonObject
            {
                ["id"] = id,
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
            if (alert.IsTest) recent["isTest"] = true;
            Push(t.Recent, recent);
            Push(Data.Recents, recent.DeepClone().AsObject());

            Data.Events.Add(stored);
            if (Data.Events.Count > EventLogSize) Data.Events.RemoveRange(0, Data.Events.Count - EventLogSize);
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
            if (Data.ResetMode == SessionResetMode.AfterOffline && Data.OfflineSince is { } since && now - since >= ResetDelay && _live.Count == 0)
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
                if (Data.ResetMode == SessionResetMode.AfterOffline && Data.OfflineSince is { } since && now - since >= ResetDelay)
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
        Data.SessionNumber++;
        // Test events only counted for the stream that just ended: take them out of the latest entries and recent lists.
        foreach (var test in Data.Events.Where(e => e.IsTest && !e.InTotals).ToList()) Forget(test);
        foreach (var t in Data.Types.Values) t.Recent.RemoveAll(IsTestEntry);
        Data.Recents.RemoveAll(IsTestEntry);
        foreach (var (type, t) in Data.Types)
            if (t.Latest is { } latest && IsTestEntry(latest)) t.Latest = NewestLatest(type, null);
        Data.OfflineSince = null;
        Data.SessionStarted = now;
        Save();
    }

    private static bool IsTestEntry(JsonObject entry) => entry["isTest"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Clears week or month totals when the calendar moved on; returns whether anything changed.</summary>
    private bool Roll(DateTimeOffset local)
    {
        var week = WeekKeyOf(local);
        var month = MonthKeyOf(local);
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

    private static string WeekKeyOf(DateTimeOffset local) => $"{ISOWeek.GetYear(local.DateTime)}-W{ISOWeek.GetWeekOfYear(local.DateTime):00}";
    private static string MonthKeyOf(DateTimeOffset local) => local.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // ---- An event's share of the totals -------------------------------------------------------------------------

    private static double Counted(StoredStreamEvent e) => AmountTypes.Contains(e.Type) ? e.Amount : e.Count;

    /// <summary>Whether the event belongs to a leaderboard period (or tally period) as things stand now.</summary>
    private bool In(StoredStreamEvent e, string period) => period switch
    {
        "session" => e.Session == Data.SessionNumber,
        "weekly" or "week" => e.InTotals && e.WeekKey == Data.WeekKey,
        "monthly" or "month" => e.InTotals && e.MonthKey == Data.MonthKey,
        _ => e.InTotals,
    };

    /// <summary>Adds (+1) or takes back (-1) everything an event counted for; <paramref name="exclude"/> is left out of recalculated leaderboards.</summary>
    private void Apply(StoredStreamEvent e, int sign, string? exclude = null)
    {
        var t = Type(e.Type);
        var counted = Counted(e);
        void Tally(SessionTally tally)
        {
            if (sign > 0) tally.Add(e.Count, counted);
            else tally.Remove(e.Count, counted);
        }
        if (In(e, "session")) Tally(t.Session);
        if (In(e, "week")) Tally(t.Week);
        if (In(e, "month")) Tally(t.Month);
        if (In(e, "total")) Tally(t.Total);

        if (AmountTypes.Contains(e.Type) && e.Name.Length > 0 && e.Amount > 0 && e.InTotals)
        {
            foreach (var period in Periods.Where(p => In(e, p)))
            {
                var donators = t.Donators.TryGetValue(period, out var map) ? map : t.Donators[period] = new(StringComparer.OrdinalIgnoreCase);
                var sum = donators.GetValueOrDefault(e.Name) + sign * e.Amount;
                if (sum > 0.004) donators[e.Name] = sum;
                else donators.Remove(e.Name);

                var top = t.TopDonation.GetValueOrDefault(period);
                if (sign > 0)
                {
                    if (top is null || e.Amount > OverlayAlerts.NumberOf(top["amount"]))
                        t.TopDonation[period] = new JsonObject { ["name"] = e.Name, ["amount"] = OverlayAlerts.ToNode(e.Amount) };
                }
                else if (top is not null && string.Equals(top["name"]?.GetValue<string>(), e.Name, StringComparison.OrdinalIgnoreCase)
                         && Math.Abs(OverlayAlerts.NumberOf(top["amount"]) - e.Amount) < 0.005)
                {
                    RecomputeTop(t, e.Type, period, exclude ?? e.Id);
                }
            }
        }

        if (In(e, "session") && e.Name.Length > 0)
        {
            var share = UserAmountTypes.Contains(e.Type) ? e.Amount : counted;
            if (sign > 0)
            {
                if (t.SessionUsers.ContainsKey(e.Name) || t.SessionUsers.Count < SessionUsersSize)
                    t.SessionUsers[e.Name] = t.SessionUsers.GetValueOrDefault(e.Name) + share;
            }
            else if (t.SessionUsers.TryGetValue(e.Name, out var had))
            {
                if (had - share > 0.004) t.SessionUsers[e.Name] = had - share;
                else t.SessionUsers.Remove(e.Name);
            }
        }

        if (In(e, "session") && e.Type == "subscriber" && e.Count > 0)
        {
            var gifted = Flag(e.Event, "gifted") || Flag(e.Event, "bulkGifted");
            var kind = gifted ? "gifted" : e.Amount > 1 ? "resub" : "new";
            var value = t.SubscriberKinds.GetValueOrDefault(kind) + sign * e.Count;
            if (value > 0) t.SubscriberKinds[kind] = value;
            else t.SubscriberKinds.Remove(kind);
        }
    }

    private static bool Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>The largest stored event of a type in a period, as the new top donation (none when nothing is left).</summary>
    private void RecomputeTop(SessionTypeData t, string type, string period, string? exclude)
    {
        var best = Data.Events
            .Where(e => e.Id != exclude && e.Type == type && e.InTotals && e.Name.Length > 0 && e.Amount > 0 && In(e, period))
            .MaxBy(e => e.Amount);
        if (best is null) t.TopDonation.Remove(period);
        else t.TopDonation[period] = new JsonObject { ["name"] = best.Name, ["amount"] = OverlayAlerts.ToNode(best.Amount) };
    }

    /// <summary>The newest stored event of a type as its latest entry, else the newest recent entry, else none.</summary>
    private JsonObject? NewestLatest(string type, string? exclude)
    {
        var stored = Data.Events.LastOrDefault(e => e.Type == type && e.Id != exclude && !(e.IsTest && !e.InTotals && e.Session != Data.SessionNumber));
        if (stored is not null) return stored.Event.DeepClone().AsObject();
        return Data.Types.TryGetValue(type, out var t) && t.Recent.FirstOrDefault(r => Id(r) != exclude) is { } r ? r.DeepClone().AsObject() : null;
    }

    private static string? Id(JsonObject entry) => entry["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Removes a stored event from the list, the recent lists and the latest entry, without touching totals.</summary>
    private void Forget(StoredStreamEvent e)
    {
        Data.Events.Remove(e);
        if (!Data.Types.TryGetValue(e.Type, out var t)) return;
        t.Recent.RemoveAll(r => Id(r) == e.Id);
        Data.Recents.RemoveAll(r => Id(r) == e.Id);
        if (t.Latest is { } latest && Id(latest) == e.Id) t.Latest = NewestLatest(e.Type, e.Id);
    }

    // ---- Browsing and maintenance --------------------------------------------------------------------------------

    /// <summary>Stored events matching <paramref name="filter"/>, newest first.</summary>
    public IReadOnlyList<StoredStreamEvent> Events(StreamEventFilter? filter = null, int limit = 500)
    {
        lock (_gate)
        {
            IEnumerable<StoredStreamEvent> list = Enumerable.Reverse(Data.Events);
            if (filter is not null) list = list.Where(filter.Matches);
            return [.. list.Take(Math.Clamp(limit, 1, EventLogSize)).Select(Copy)];
        }
    }

    /// <summary>Whether a stored event counts in this stream's totals now (for showing it).</summary>
    public bool IsThisStream(StoredStreamEvent e)
    {
        lock (_gate) return e.Session == Data.SessionNumber;
    }

    private static StoredStreamEvent Copy(StoredStreamEvent e) => new()
    {
        Id = e.Id, Type = e.Type, Name = e.Name, Amount = e.Amount, Count = e.Count, Service = e.Service, Time = e.Time, IsTest = e.IsTest,
        InTotals = e.InTotals, Session = e.Session, WeekKey = e.WeekKey, MonthKey = e.MonthKey, Event = e.Event.DeepClone().AsObject(),
    };

    /// <summary>Removes one stored event; every total, list and leaderboard it counted in is recalculated. False when there is no such event.</summary>
    public bool RemoveEvent(string id)
    {
        lock (_gate)
        {
            if (Data.Events.FirstOrDefault(e => e.Id == id) is not { } e) return false;
            Forget(e);
            Apply(e, -1);
            Save();
        }
        Notify();
        return true;
    }

    /// <summary>Removes every stored event matching <paramref name="filter"/> (e.g. all test events, or those between two dates); returns how many.</summary>
    public int RemoveEvents(StreamEventFilter filter)
    {
        int removed;
        lock (_gate)
        {
            var doomed = Data.Events.Where(filter.Matches).ToList();
            foreach (var e in doomed)
            {
                Forget(e);
                Apply(e, -1);
            }
            removed = doomed.Count;
            if (removed > 0) Save();
        }
        if (removed > 0) Notify();
        return removed;
    }

    /// <summary>Changes a stored event's name, amount or test mark; the totals follow. Null when there is no such event.</summary>
    public StoredStreamEvent? EditEvent(string id, StreamEventEdit edit)
    {
        StoredStreamEvent result;
        lock (_gate)
        {
            if (Data.Events.FirstOrDefault(e => e.Id == id) is not { } e) return null;
            if (edit.Amount is < 0) throw new ArgumentException("The amount cannot be negative");
            Apply(e, -1, exclude: e.Id);
            if (edit.Name is { } name) e.Name = name.Trim();
            if (edit.Amount is { } amount) e.Amount = amount;
            if (edit.IsTest is { } test)
            {
                e.IsTest = test;
                e.InTotals = !test || Data.CountTestEvents;
            }
            e.Event["name"] = e.Name;
            e.Event["amount"] = OverlayAlerts.ToNode(e.Amount);
            if (e.IsTest) e.Event["isTest"] = true;
            else e.Event.Remove("isTest");
            Apply(e, +1);
            if (Data.Types.TryGetValue(e.Type, out var t))
            {
                foreach (var r in t.Recent.Concat(Data.Recents).Where(r => Id(r) == e.Id))
                {
                    r["name"] = e.Name;
                    r["amount"] = OverlayAlerts.ToNode(e.Amount);
                    if (e.IsTest) r["isTest"] = true;
                    else r.Remove("isTest");
                }
                if (t.Latest is { } latest && Id(latest) == e.Id) t.Latest = e.Event.DeepClone().AsObject();
            }
            Save();
            result = Copy(e);
        }
        Notify();
        return result;
    }

    /// <summary>Sets a type's count and/or amount for "session", "week", "month" or "total" by hand.</summary>
    public void SetTally(string type, string period, long? count, double? amount)
    {
        if (count is < 0 || amount is < 0) throw new ArgumentException("Counts and amounts cannot be negative");
        lock (_gate)
        {
            var t = Type(Clean(type));
            var tally = period.Trim().ToLowerInvariant() switch
            {
                "session" or "stream" => t.Session,
                "week" or "weekly" => t.Week,
                "month" or "monthly" => t.Month,
                "total" or "alltime" or "all-time" => t.Total,
                _ => throw new ArgumentException($"Unknown period '{period}' (session, week, month or total)"),
            };
            if (count is { } c) tally.Count = c;
            if (amount is { } a) tally.Amount = a;
            else if (count is { } c2 && !AmountTypes.Contains(type)) tally.Amount = c2;
            Save();
        }
        Notify();
    }

    /// <summary>Sets a type's latest entry by hand, or clears it (empty <paramref name="name"/>).</summary>
    public void SetLatest(string type, string? name, double amount = 0)
    {
        lock (_gate)
        {
            var t = Type(Clean(type));
            t.Latest = string.IsNullOrWhiteSpace(name)
                ? null
                : new JsonObject { ["name"] = name.Trim(), ["amount"] = OverlayAlerts.ToNode(amount), ["type"] = Clean(type) };
            Save();
        }
        Notify();
    }

    /// <summary>Sets the top donation of a type for "session", "weekly", "monthly" or "alltime" by hand, or clears it (empty name).</summary>
    public void SetTopDonation(string type, string period, string? name, double amount = 0)
    {
        lock (_gate)
        {
            var t = Type(Clean(type));
            var p = LeaderboardPeriod(period);
            if (string.IsNullOrWhiteSpace(name)) t.TopDonation.Remove(p);
            else t.TopDonation[p] = new JsonObject { ["name"] = name.Trim(), ["amount"] = OverlayAlerts.ToNode(amount) };
            Save();
        }
        Notify();
    }

    /// <summary>
    /// Sets one person's summed amount on a type's donator board for a period by hand (0 removes them), which is what
    /// the top donator is picked from.
    /// </summary>
    public void SetDonator(string type, string period, string name, double amount)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A name is needed");
        lock (_gate)
        {
            var t = Type(Clean(type));
            var p = LeaderboardPeriod(period);
            var map = t.Donators.TryGetValue(p, out var m) ? m : t.Donators[p] = new(StringComparer.OrdinalIgnoreCase);
            if (amount > 0) map[name.Trim()] = amount;
            else map.Remove(name.Trim());
            Save();
        }
        Notify();
    }

    /// <summary>Removes one entry from a type's recent list (or the list of every type when <paramref name="type"/> is empty) by position, 0 = newest.</summary>
    public bool RemoveRecent(string? type, int index)
    {
        lock (_gate)
        {
            var list = string.IsNullOrWhiteSpace(type) ? Data.Recents : Data.Types.TryGetValue(Clean(type), out var t) ? t.Recent : null;
            if (list is null || index < 0 || index >= list.Count) return false;
            var id = Id(list[index]);
            list.RemoveAt(index);
            if (id is not null)
            {
                Data.Recents.RemoveAll(r => Id(r) == id);
                foreach (var other in Data.Types.Values) other.Recent.RemoveAll(r => Id(r) == id);
            }
            Save();
        }
        Notify();
        return true;
    }

    /// <summary>Everything this store keeps, as JSON, for a backup before maintenance.</summary>
    public JsonObject Export()
    {
        lock (_gate)
        {
            var node = JsonSerializer.SerializeToNode(Data, ExportOptions)!.AsObject();
            node["cuebotStreamTotals"] = 1;
            return node;
        }
    }

    /// <summary>Replaces everything with an <see cref="Export"/>.</summary>
    public void Import(JsonObject data)
    {
        if (data["cuebotStreamTotals"] is null) throw new ArgumentException("This is not a Cuebot stream totals export");
        SessionDataDocument doc;
        try
        {
            doc = data.Deserialize<SessionDataDocument>(ExportOptions) ?? throw new ArgumentException("The export is empty");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The export could not be read: " + ex.Message, ex);
        }
        lock (_gate)
        {
            Data.Types = doc.Types;
            Data.Recents = doc.Recents;
            Data.WeekKey = doc.WeekKey;
            Data.MonthKey = doc.MonthKey;
            Data.OfflineSince = doc.OfflineSince;
            Data.SessionStarted = doc.SessionStarted;
            Data.SessionNumber = doc.SessionNumber;
            Data.Events = doc.Events;
            Data.CountTestEvents = doc.CountTestEvents;
            Data.ResetMode = doc.ResetMode;
            Roll(_time.GetLocalNow());
            Save();
        }
        Notify();
    }

    private static string LeaderboardPeriod(string period) => period.Trim().ToLowerInvariant() switch
    {
        "session" or "stream" => "session",
        "week" or "weekly" => "weekly",
        "month" or "monthly" => "monthly",
        "total" or "alltime" or "all-time" => "alltime",
        _ => throw new ArgumentException($"Unknown period '{period}' (session, weekly, monthly or alltime)"),
    };

    private static string Clean(string type) =>
        string.IsNullOrWhiteSpace(type) ? throw new ArgumentException("A type is needed (follower, subscriber, tip, cheer...)") : type.Trim();

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
        foreach (var source in _sources) source.Changed -= Notify;
        _file?.Dispose();
    }
}
