using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Spending;

/// <summary>Events the ledger publishes on the bus (triggers in the Spending category).</summary>
public static class SpendingEvents
{
    /// <summary>A purchase was counted: user, platform, label, amount (in the chosen currency), currency, streamTotal, rank.</summary>
    public const string Recorded = "spending.recorded";

    /// <summary>Someone new leads this stream's list: user, platform, total, currency, previousUser, previousTotal, scope.</summary>
    public const string TopSpenderChanged = "spending.topSpenderChanged";
}

/// <summary>
/// Records what viewers spend on every platform (the <see cref="FeedSpend"/> of each activity item: tips, Super Chats,
/// rants, bits, Kicks, Jewels, subscriptions, gift subs, shop orders...) and ranks top spenders per stream, day, week,
/// month, year or all time, per platform or across all of them.
/// <para>
/// Everything is valued in the streamer's chosen currency. Each stream fetches exchange rates once, when it starts,
/// from free APIs (<see cref="IExchangeRateSource"/>), and keeps them: a purchase keeps the value it had that day, and
/// lists are ordered by that value. Purchases made before the rates arrive wait and are valued as soon as they do; when
/// no provider can be reached, the newest earlier stream's rates are reused. Platform units (bits, Kicks, Jewels,
/// subscriptions) are priced in dollars by <see cref="SpendingSettings"/> on the day they are bought.
/// </para>
/// A stream starts when a platform goes live (or with the first purchase when none reported it, or on request) and
/// ends <see cref="RestartGap"/> after the last live platform goes offline. Test events are never counted. Persisted in
/// spending.json; thread-safe. Ignored names are still recorded (and announced, so anonymous gifts add subathon
/// time); they are only left out of the lists.
/// </summary>
public sealed class SpendingLedger : IPeriodicService, IDisposable
{
    /// <summary>Going live again within this long after going offline continues the same stream.</summary>
    public static readonly TimeSpan RestartGap = TimeSpan.FromMinutes(15);

    /// <summary>A purchase this long after the stream's last activity starts a new stream when no platform said it ended.</summary>
    public static readonly TimeSpan IdleGap = TimeSpan.FromHours(6);

    /// <summary>How often a stream without exchange rates tries again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private readonly JsonFile<SpendingFile>? _file;
    private readonly SpendingFile _memory = new();
    private readonly ChatHub? _chat;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IExchangeRateSource _rates;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Emulated purchases (never saved) waiting for their stream's exchange rates.</summary>
    private readonly List<SpendRecord> _emulatedPending = [];
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private Task _fetch = Task.CompletedTask;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public SpendingLedger(AppPaths? paths, ChatHub? chat, EventBus bus, AppLog log, IExchangeRateSource rates, TimeProvider? time = null)
    {
        _bus = bus;
        _log = log;
        _rates = rates;
        _time = time ?? TimeProvider.System;
        if (paths is not null) _file = new JsonFile<SpendingFile>(paths.File("spending.json"), log);
        _chat = chat;
        if (chat is not null) chat.FeedItemAdded += Record;
        lock (_gate)
        {
            if (Current is { Rates.Count: 0 } open && open.EndedAt is null) StartFetch(open);
        }
    }

    /// <summary>Raised after any change (a purchase, new rates, a new stream, settings).</summary>
    public event Action? Changed;

    /// <summary>Linked accounts, so <c>combineLinked</c> lists count one viewer's platforms together.</summary>
    public Users.AccountLinks? Links { get; set; }

    private SpendingFile Data => _file?.Value ?? _memory;

    private SpendingSession? Current => Data.Sessions.Count > 0 ? Data.Sessions[^1] : null;

    /// <summary>The task fetching the current stream's rates (tests wait for it).</summary>
    public Task RatesTask
    {
        get { lock (_gate) return _fetch; }
    }

    public SpendingSettings Settings
    {
        get { lock (_gate) return Json.Clone(Data.Settings); }
    }

    public void UpdateSettings(Action<SpendingSettings> change)
    {
        lock (_gate)
        {
            change(Data.Settings);
            Data.Settings.Currency = NormalizeCurrency(Data.Settings.Currency);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>The current (or most recent) stream, as a copy; null before anything was recorded.</summary>
    public SpendingSession? CurrentSession
    {
        get { lock (_gate) return Current is { } s ? Json.Clone(s) : null; }
    }

    /// <summary>Starts a new stream now (its own list and freshly fetched rates).</summary>
    public SpendingSession StartSession()
    {
        SpendingSession session;
        lock (_gate) session = NewSession(_time.GetUtcNow());
        _log.Info("Spending", "Started a new stream for top spenders");
        Changed?.Invoke();
        return Json.Clone(session);
    }

    /// <summary>Counts an activity item (subscribed to the chat hub; public for tests).</summary>
    public void Record(FeedItem item)
    {
        if (item.IsTest && !item.IsEmulated) return;
        switch (item.Kind)
        {
            case FeedKind.StreamOnline:
                OnLive(item.Platform, live: true);
                return;
            case FeedKind.StreamOffline:
                OnLive(item.Platform, live: false);
                return;
        }
        if (item.Spend is not { } spend) return;
        Add(item.Platform, item.User, spend, item.Time, emulated: item.IsTest);
    }

    /// <summary>Adds a purchase by hand (a cash gift, a sale outside a connected service...).</summary>
    public void AddManual(string platform, string user, double amount, string currency = "", string label = "Manual")
    {
        user = user.Trim().TrimStart('@');
        if (user.Length == 0) throw new ArgumentException("No user given");
        if (!(amount > 0) || !double.IsFinite(amount)) throw new ArgumentException("The amount must be more than 0");
        string chosen;
        lock (_gate) chosen = Data.Settings.Currency;
        var spend = FeedSpend.Money(amount, currency.Trim().Length > 0 ? currency : chosen, label.Trim().Length > 0 ? label.Trim() : "Manual")!;
        Add(platform.Trim().Length > 0 ? platform.Trim() : "Manual", user, spend, _time.GetUtcNow());
    }

    /// <summary>Removes everything a person bought in a period (a refund, a chargeback, a test).</summary>
    public int RemoveUser(string platform, string user, SpendingPeriod period = SpendingPeriod.AllTime)
    {
        int removed;
        lock (_gate)
        {
            var inPeriod = InPeriod(period);
            removed = Data.Entries.RemoveAll(e => inPeriod(e) && Same(e.User, user.Trim().TrimStart('@'))
                                                 && (platform.Trim().Length == 0 || Same(e.Platform, platform.Trim())));
            if (removed > 0) Save();
        }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    /// <summary>Forgets every purchase and stream (settings are kept).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            Data.Entries.Clear();
            Data.Sessions.Clear();
            Save();
        }
        _log.Info("Spending", "Cleared all top spender history");
        Changed?.Invoke();
    }

    /// <summary>The platforms anything was bought on, in order of first purchase.</summary>
    public IReadOnlyList<string> Platforms()
    {
        lock (_gate) return Data.Entries.Select(e => e.Platform).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The top spenders of a period, highest first, in the chosen currency. <paramref name="platform"/> empty means
    /// every platform together (each person is listed per platform: the same name on two platforms is two people).
    /// </summary>
    /// <remarks>
    /// With <paramref name="combineLinked"/> (and no platform) a viewer's linked accounts are one person; their
    /// <see cref="TopSpender.Platform"/> then lists the platforms ("Twitch + Kick").
    /// </remarks>
    public SpendingList Top(SpendingPeriod period, string platform = "", int count = 10, bool combineLinked = false)
    {
        var links = combineLinked && platform.Trim().Length == 0 ? LinkKeys() : null;
        lock (_gate) return TopCore(period, platform.Trim(), count, links);
    }

    /// <summary>
    /// What one viewer has given (all their linked accounts together unless <paramref name="combineLinked"/> is off):
    /// the total in the chosen currency and, per kind (bits, gifted subs, Super Chats...), how many and their value.
    /// </summary>
    public SupportTally Tally(string platform, string user, SpendingPeriod period = SpendingPeriod.AllTime, bool combineLinked = true)
    {
        var links = combineLinked ? LinkKeys() : null;
        var self = Key(platform, user);
        var key = links?.GetValueOrDefault(self) ?? self;
        lock (_gate)
        {
            var inPeriod = InPeriod(period);
            var mine = Data.Entries.Where(e => e.Value is not null && inPeriod(e))
                .Where(e => (links?.GetValueOrDefault(Key(e.Platform, e.User)) ?? Key(e.Platform, e.User)) == key)
                .ToList();
            var items = mine.GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SupportItem(g.Key, g.First().Unit, g.Sum(e => e.Quantity), Math.Round(g.Sum(e => Shown(e.Value!.Value, e.Currency)), 2)))
                .OrderByDescending(i => i.Value)
                .ToList();
            return new SupportTally(Data.Settings.Currency, Math.Round(items.Sum(i => i.Value), 2), items);
        }
    }

    private static string Key(string platform, string user) => platform.Trim().ToLowerInvariant() + ":" + user.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>"platform:name" → link group id for every linked account.</summary>
    private Dictionary<string, string>? LinkKeys()
    {
        if (Links is not { } links) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in links.Groups)
            foreach (var a in g.Accounts)
                if (a.UserName.Length > 0) map[Key(a.Platform, a.UserName)] = "link:" + g.Id;
        return map;
    }

    void IPeriodicService.Tick(DateTimeOffset now) => Tick(now);

    /// <summary>Retries a stream's exchange rates that could not be fetched.</summary>
    public void Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Current is { Rates.Count: 0 } session && _fetch.IsCompleted && now - _lastFetch >= RetryDelay) StartFetch(session);
        }
    }

    // ---------------------------------------------------------------- recording

    private void OnLive(string platform, bool live)
    {
        var now = _time.GetUtcNow();
        var changed = false;
        lock (_gate)
        {
            if (live)
            {
                _live.Add(platform);
                if (Current is not { } current || current.EndedAt is { } ended && now - ended >= RestartGap)
                {
                    NewSession(now);
                    changed = true;
                }
                else if (current.EndedAt is not null)
                {
                    current.EndedAt = null;   // back within the gap: the same stream
                    Save();
                }
            }
            else
            {
                _live.Remove(platform);
                if (_live.Count == 0 && Current is { EndedAt: null } current)
                {
                    current.EndedAt = now;
                    Save();
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <param name="emulated">
    /// A sample from Cuebot's emulator: converted and announced (<see cref="SpendingEvents.Recorded"/>, so "Money spent"
    /// actions and subathon timers and goals see it) but never saved, so top spenders stay real.
    /// </param>
    private void Add(string platform, string user, FeedSpend spend, DateTimeOffset time, bool emulated = false)
    {
        user = user.Trim().TrimStart('@');
        if (user.Length == 0) return;
        Dictionary<string, object?>? recorded;
        List<Dictionary<string, object?>> leaders = [];
        lock (_gate)
        {
            var settings = Data.Settings;
            if (spend.IsCharity && !settings.IncludeCharity) return;
            var session = SessionFor(time);
            var before = Leaders(platform);
            var record = new SpendRecord
            {
                Time = time,
                SessionId = session.Id,
                Platform = platform,
                User = user,
                Label = spend.Label,
                Unit = spend.Unit,
                Quantity = spend.Quantity,
                Tier = spend.Tier,
                Currency = session.Currency,
            };
            if (spend.Unit == SpendUnit.Money)
            {
                record.PaidCurrency = spend.Currency.Length > 0 ? spend.Currency : "USD";
                record.PaidAmount = spend.Quantity;
            }
            else
            {
                record.PaidCurrency = "USD";
                record.PaidAmount = Math.Round(spend.Quantity * UnitPriceUsd(settings, platform, spend), 4);
            }
            record.Value = Convert(record, session);
            if (emulated)
            {
                recorded = Announce(record, session, emulated: true);
                if (recorded is null)
                {
                    if (_emulatedPending.Count >= 100) _emulatedPending.RemoveAt(0);
                    _emulatedPending.Add(record);
                }
            }
            else
            {
                Data.Entries.Add(record);
                Save();
                recorded = Announce(record, session);
                if (record.Value is not null) leaders = LeaderChanges(before, Leaders(platform));
            }
        }
        if (!emulated) Changed?.Invoke();
        if (recorded is not null) _bus.Publish(new BotEvent(SpendingEvents.Recorded, recorded));
        foreach (var args in leaders) _bus.Publish(new BotEvent(SpendingEvents.TopSpenderChanged, args));
    }

    /// <summary>The stream a purchase belongs to: the open one, or a new one when the last has ended or gone quiet.</summary>
    private SpendingSession SessionFor(DateTimeOffset time)
    {
        if (Current is { } current)
        {
            var ended = current.EndedAt is { } end && time - end >= RestartGap;
            var last = Data.Entries.LastOrDefault(e => e.SessionId == current.Id)?.Time ?? current.StartedAt;
            var idle = current.EndedAt is null && _live.Count == 0 && time - last >= IdleGap;
            if (!ended && !idle) return current;
        }
        return NewSession(time);
    }

    private SpendingSession NewSession(DateTimeOffset time)
    {
        if (Current is { EndedAt: null } previous) previous.EndedAt = time;
        var session = new SpendingSession { StartedAt = time, Currency = Data.Settings.Currency };
        Data.Sessions.Add(session);
        Save();
        StartFetch(session);
        return session;
    }

    private static double UnitPriceUsd(SpendingSettings s, string platform, FeedSpend spend) => spend.Unit switch
    {
        SpendUnit.Bits => s.BitUsd,
        SpendUnit.Kicks => s.KickUsd,
        SpendUnit.Jewels => s.JewelUsd,
        SpendUnit.Subscription or SpendUnit.GiftSubscription => platform.ToLowerInvariant() switch
        {
            "twitch" => spend.Tier switch { "2" => s.TwitchTier2Usd, "3" => s.TwitchTier3Usd, _ => s.TwitchTier1Usd },
            "kick" => s.KickSubscriptionUsd,
            "youtube" => s.YouTubeMembershipUsd,
            _ => s.OtherSubscriptionUsd,
        },
        _ => 1,
    };

    /// <summary>The record's value in its stream's currency with the stream's locked rates; null until they arrive.</summary>
    private double? Convert(SpendRecord record, SpendingSession session)
    {
        if (Same(record.PaidCurrency, session.Currency)) return Math.Round(record.PaidAmount, 2);
        if (session.Rates.Count == 0) return null;
        if (session.Rates.TryGetValue(record.PaidCurrency, out var rate) && rate > 0) return Math.Round(record.PaidAmount / rate, 2);
        if (_warned.Add(record.PaidCurrency))
            _log.Warn("Spending", $"No exchange rate for '{record.PaidCurrency}'; counting {record.PaidAmount} {record.PaidCurrency} as {session.Currency} as it is");
        return Math.Round(record.PaidAmount, 2);
    }

    private Dictionary<string, object?>? Announce(SpendRecord record, SpendingSession session, bool emulated = false)
    {
        if (record.Value is not { } value) return null;
        var currency = Data.Settings.Currency;
        var amount = Shown(value, record.Currency);
        var list = TopCore(SpendingPeriod.Stream, "", int.MaxValue);
        var me = list.Spenders.FirstOrDefault(s => Same(s.User, record.User) && Same(s.Platform, record.Platform));
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = record.User,
            ["platform"] = record.Platform,
            ["label"] = record.Label,
            ["amount"] = amount,
            ["currency"] = currency,
            ["formattedAmount"] = SpendingText.Money(amount, currency),
            ["paidAmount"] = record.Unit == SpendUnit.Money ? record.PaidAmount : record.Quantity,
            ["paidCurrency"] = record.Unit == SpendUnit.Money ? record.PaidCurrency : record.Unit.ToString(),
            ["unit"] = record.Unit.ToString(),
            ["quantity"] = record.Quantity,
            ["tier"] = record.Tier,
            ["streamTotal"] = me?.Total ?? amount,
            ["rank"] = me?.Rank ?? 0,
            ["sessionId"] = session.Id,
            ["isTest"] = emulated,
            ["isEmulated"] = emulated,
        };
    }

    // ---------------------------------------------------------------- leaders

    private (TopSpender? All, TopSpender? Platform) Leaders(string platform) =>
        (TopCore(SpendingPeriod.Stream, "", 1).Spenders.FirstOrDefault(), TopCore(SpendingPeriod.Stream, platform, 1).Spenders.FirstOrDefault());

    private List<Dictionary<string, object?>> LeaderChanges((TopSpender? All, TopSpender? Platform) before, (TopSpender? All, TopSpender? Platform) after)
    {
        var list = new List<Dictionary<string, object?>>();
        void Check(TopSpender? old, TopSpender? now, string scope)
        {
            if (now is null || old is not null && Same(old.User, now.User) && Same(old.Platform, now.Platform)) return;
            list.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["user"] = now.User,
                ["platform"] = now.Platform,
                ["total"] = now.Total,
                ["currency"] = Data.Settings.Currency,
                ["formattedTotal"] = SpendingText.Money(now.Total, Data.Settings.Currency),
                ["previousUser"] = old?.User ?? "",
                ["previousPlatform"] = old?.Platform ?? "",
                ["previousTotal"] = old?.Total ?? 0,
                ["scope"] = scope,
            });
        }
        Check(before.All, after.All, "All platforms");
        Check(before.Platform, after.Platform, after.Platform?.Platform ?? "");
        return list;
    }

    // ---------------------------------------------------------------- lists

    private SpendingList TopCore(SpendingPeriod period, string platform, int count, Dictionary<string, string>? links = null)
    {
        var currency = Data.Settings.Currency;
        var inPeriod = InPeriod(period);
        var ignored = new HashSet<string>(Data.Settings.IgnoredUsers.Select(u => u.Trim().TrimStart('@')), StringComparer.OrdinalIgnoreCase);
        var entries = Data.Entries.Where(e => inPeriod(e) && (platform.Length == 0 || Same(e.Platform, platform)) && !ignored.Contains(e.User)).ToList();
        var pending = entries.Count(e => e.Value is null);
        var groups = entries
            .Where(e => e.Value is not null)
            .GroupBy(e => links?.GetValueOrDefault(Key(e.Platform, e.User)) ?? Key(e.Platform, e.User))
            .Select(g =>
            {
                var values = g.Select(e => (e, Value: Shown(e.Value!.Value, e.Currency))).ToList();
                var breakdown = values.GroupBy(v => v.e.Label, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(b => b.Key, b => Math.Round(b.Sum(v => v.Value), 2), StringComparer.OrdinalIgnoreCase);
                var last = g.Last();
                var platforms = g.Select(e => e.Platform).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return (User: last.User, Platform: platforms.Count > 1 ? string.Join(" + ", platforms) : last.Platform, Total: Math.Round(values.Sum(v => v.Value), 2), Count: g.Count(),
                    First: g.Min(e => e.Time), Breakdown: breakdown);
            })
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.First)   // ties: whoever got there first
            .ToList();
        var total = Math.Round(groups.Sum(g => g.Total), 2);
        var spenders = groups.Take(Math.Max(0, count))
            .Select((g, i) => new TopSpender(i + 1, g.User, g.Platform, g.Total, g.Count, g.Breakdown))
            .ToList();
        return new SpendingList(period, platform, currency, total, spenders) { Pending = pending };
    }

    private Func<SpendRecord, bool> InPeriod(SpendingPeriod period)
    {
        if (period == SpendingPeriod.Stream)
        {
            var id = Current?.Id;
            return e => e.SessionId == id;
        }
        if (period == SpendingPeriod.AllTime) return _ => true;
        var start = PeriodStart(period, _time.GetLocalNow(), Data.Settings.WeekStart, _time.LocalTimeZone);
        return e => e.Time >= start;
    }

    /// <summary>When the calendar day, week, month or year containing <paramref name="now"/> (local time) began.</summary>
    public static DateTimeOffset PeriodStart(SpendingPeriod period, DateTimeOffset now, DayOfWeek weekStart, TimeZoneInfo zone)
    {
        var today = now.Date;
        var start = period switch
        {
            SpendingPeriod.Day => today,
            SpendingPeriod.Week => today.AddDays(-(((int)today.DayOfWeek - (int)weekStart + 7) % 7)),
            SpendingPeriod.Month => new DateTime(today.Year, today.Month, 1),
            SpendingPeriod.Year => new DateTime(today.Year, 1, 1),
            _ => DateTime.MinValue,
        };
        return start == DateTime.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(start, zone.GetUtcOffset(start));
    }

    /// <summary>
    /// A stored value in the chosen currency: as is when the stream used it, otherwise with that stream's locked rate
    /// to the chosen currency (or, when it has none, the newest stream in the chosen currency that has a rate back).
    /// </summary>
    private double Shown(double value, string currency)
    {
        var chosen = Data.Settings.Currency;
        if (Same(currency, chosen)) return value;
        var direct = Data.Sessions.LastOrDefault(s => Same(s.Currency, currency) && s.Rates.ContainsKey(chosen));
        if (direct is not null) return Math.Round(value * direct.Rates[chosen], 2);
        var back = Data.Sessions.LastOrDefault(s => Same(s.Currency, chosen) && s.Rates.TryGetValue(currency, out var r) && r > 0);
        return back is not null ? Math.Round(value / back.Rates[currency], 2) : value;
    }

    // ---------------------------------------------------------------- rates

    private void StartFetch(SpendingSession session)
    {
        if (!_fetch.IsCompleted) return;
        _lastFetch = _time.GetUtcNow();
        var id = session.Id;
        var currency = session.Currency;
        _fetch = Task.Run(async () =>
        {
            ExchangeRateTable? table = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                table = await _rates.FetchAsync(currency, cts.Token);
            }
            catch (Exception ex)
            {
                _log.Warn("Spending", $"Could not fetch exchange rates: {ex.Message}");
            }
            ApplyRates(id, table);
        });
    }

    private void ApplyRates(string sessionId, ExchangeRateTable? table)
    {
        var announcements = new List<Dictionary<string, object?>>();
        lock (_gate)
        {
            var session = Data.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session is null || session.Rates.Count > 0) return;
            if (table is { Rates.Count: > 0 } && Same(table.Base, session.Currency))
            {
                session.Rates = new Dictionary<string, double>(table.Rates, StringComparer.OrdinalIgnoreCase);
                session.RatesSource = table.Source;
                session.RatesDate = table.Date;
                _log.Info("Spending", $"Exchange rates for this stream locked ({table.Source}, {table.Date})");
            }
            else if (Data.Sessions.LastOrDefault(s => s.Id != sessionId && Same(s.Currency, session.Currency) && s.Rates.Count > 0) is { } earlier)
            {
                session.Rates = new Dictionary<string, double>(earlier.Rates, StringComparer.OrdinalIgnoreCase);
                session.RatesSource = $"{earlier.RatesSource} (reused from an earlier stream: no exchange rate service could be reached)";
                session.RatesDate = earlier.RatesDate;
                _log.Warn("Spending", $"No exchange rate service could be reached; reusing the rates of {earlier.RatesDate}");
            }
            else
            {
                _log.Warn("Spending", "No exchange rate service could be reached; purchases in other currencies wait until one can");
                return;
            }
            foreach (var record in Data.Entries.Where(e => e.SessionId == sessionId && e.Value is null))
            {
                record.Value = Convert(record, session);
                if (Announce(record, session) is { } args) announcements.Add(args);
            }
            foreach (var record in _emulatedPending.Where(e => e.SessionId == sessionId).ToList())
            {
                record.Value = Convert(record, session);
                if (Announce(record, session, emulated: true) is not { } args) continue;
                announcements.Add(args);
                _emulatedPending.Remove(record);
            }
            Save();
        }
        Changed?.Invoke();
        // Purchases that waited for the rates trigger "Money spent" now that they have a value.
        foreach (var args in announcements) _bus.Publish(new BotEvent(SpendingEvents.Recorded, args));
    }

    // ---------------------------------------------------------------- helpers

    internal static string NormalizeCurrency(string? code) =>
        code?.Trim().ToUpperInvariant() is { Length: 3 } c && c.All(char.IsAsciiLetterUpper) ? c : "USD";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Save() => _file?.ScheduleSave();

    public void Flush() => _file?.Flush();

    public void Dispose()
    {
        if (_chat is not null) _chat.FeedItemAdded -= Record;
        _file?.Dispose();
    }
}

/// <summary>Money as text: "$12.50", "€5.00", "12.50 CHF" (the currency's own symbol where .NET knows one).</summary>
public static class SpendingText
{
    public static string Money(double amount, string currency)
    {
        var symbol = Symbol(currency);
        var number = amount.ToString("0.00", CultureInfo.InvariantCulture);
        return symbol is null ? $"{number} {currency}" : symbol + number;
    }

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥", ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$",
        ["BRL"] = "R$", ["MXN"] = "MX$", ["INR"] = "₹", ["KRW"] = "₩", ["CNY"] = "CN¥", ["PHP"] = "₱", ["TRY"] = "₺",
        ["PLN"] = "zł", ["ILS"] = "₪", ["THB"] = "฿", ["VND"] = "₫", ["UAH"] = "₴", ["RUB"] = "₽",
    };

    private static string? Symbol(string currency) => Symbols.GetValueOrDefault(currency);
}
