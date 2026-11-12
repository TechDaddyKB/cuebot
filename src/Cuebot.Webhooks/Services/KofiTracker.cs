using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Session;
using Cuebot.Core.Storage;

namespace Cuebot.Webhooks.Services;

/// <summary>kofi.json: what Cuebot has counted from Ko-fi webhooks.</summary>
public sealed class KofiData
{
    /// <summary>Added up from real (not test) payments that count toward the goal, plus amounts added by hand.</summary>
    public double Raised { get; set; }

    /// <summary>When the goal last reached its target (null until then, cleared by a reset or a higher target).</summary>
    public DateTimeOffset? GoalReachedAt { get; set; }

    /// <summary>Members by key: their Discord user id when linked, otherwise their Ko-fi name in lower case.</summary>
    public Dictionary<string, KofiMember> Members { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commissions in the order they were paid; done ones stay until cleared.</summary>
    public List<KofiCommission> Commissions { get; set; } = [];
}

public sealed class KofiMember
{
    /// <summary>The name as shown publicly ("Anonymous" for a private supporter).</summary>
    public string Name { get; set; } = "";

    /// <summary>The Ko-fi name of a private supporter (kept on this PC only, never shown in overlays).</summary>
    public string PrivateName { get; set; } = "";

    public string Tier { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DiscordUsername { get; set; } = "";
    public double Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset FirstPayment { get; set; }
    public DateTimeOffset LastPayment { get; set; }

    /// <summary>Payments Cuebot has seen since the last first payment (Ko-fi sends no month count).</summary>
    public int Payments { get; set; }

    /// <summary>True once <see cref="KofiEventTypes.MembershipLapsed"/> ran for the current lapse.</summary>
    public bool LapseReported { get; set; }
}

public sealed class KofiCommission
{
    /// <summary>Ko-fi's message_id (or a Cuebot id for one added by hand).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Message { get; set; } = "";
    public double Amount { get; set; }
    public string Currency { get; set; } = "";
    public string FormattedAmount { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
}

/// <summary>A copy of the goal's state.</summary>
public sealed record KofiGoalState(string Title, double Current, double Target, double Raised, double StartAmount, string Currency, bool Reached)
{
    /// <summary>Whole percent, 0 without a target (can pass 100).</summary>
    public int Percent => Target > 0 ? (int)Math.Floor(Current / Target * 100) : 0;
}

/// <summary>
/// Keeps what Ko-fi itself never reports back: a goal fed by webhooks (with a start amount typed in, since Ko-fi has
/// no API for the goal on your page), when each member last paid (Ko-fi sends no cancellation, so a lapse is an
/// estimate) and the open commissions. Only real, new payments count: test events and duplicates never change it
/// (the duplicate check runs first, see <see cref="WebhookServiceBase"/>). Also adds the goal and the open
/// commissions to overlay session data as <c>kofi-goal</c> and <c>kofi-commissions</c>.
/// </summary>
public sealed class KofiTracker : ISessionDataSource, IPeriodicService, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private readonly JsonFile<WebhooksSettings> _settings;
    private readonly JsonFile<KofiData>? _file;
    private readonly KofiData _memory = new();
    private readonly EventBus _bus;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <param name="path">kofi.json; null keeps everything in memory (tests).</param>
    public KofiTracker(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log, string? path = null, TimeProvider? time = null)
    {
        _settings = settings;
        _bus = bus;
        _time = time ?? TimeProvider.System;
        if (path is not null) _file = new JsonFile<KofiData>(path, log);
    }

    /// <summary>Raised after the goal, members or commissions change.</summary>
    public event Action? Changed;

    private KofiData Data => _file?.Value ?? _memory;
    private KofiOptions Options => _settings.Value.KofiOptions;
    private KofiGoalSettings GoalSettings => Options.Goal;

    // ---- events ----

    /// <summary>
    /// Counts a new Ko-fi event and adds the goal (and for memberships the payment count, for commissions the queue
    /// position) to its arguments. Returns the follow-up events (goal reached).
    /// </summary>
    internal IReadOnlyList<BotEvent> Record(string type, Dictionary<string, object?> a)
    {
        var isTest = a.TryGetValue("isTest", out var t) && t is true;
        var followUps = new List<BotEvent>();
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!isTest && Counts(type, Text(a, "currency")))
            {
                Data.Raised += Number(a, "amount");
                var after = GoalCore();
                if (after.Target > 0 && after.Current >= after.Target && Data.GoalReachedAt is null)
                {
                    Data.GoalReachedAt = now;
                    followUps.Add(new BotEvent(KofiEventTypes.GoalReached, GoalArgs(after, a)));
                }
            }
            if (type is KofiEventTypes.Subscription or KofiEventTypes.Resubscription)
                a["months"] = isTest ? (type == KofiEventTypes.Subscription ? 1 : 2) : Member(type, a, now);
            if (type == KofiEventTypes.Commission)
            {
                if (!isTest)
                {
                    Data.Commissions.Add(new KofiCommission
                    {
                        Id = Text(a, "messageId") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
                        Name = Text(a, "user"), Message = Text(a, "message"), Amount = Number(a, "amount"), Currency = Text(a, "currency"),
                        FormattedAmount = Text(a, "formattedAmount"), Time = now,
                    });
                }
                a["commissionsOpen"] = Data.Commissions.Count(c => !c.Done);
            }
            AddGoal(a, GoalCore());
            if (!isTest) Save();
        }
        if (!isTest) Changed?.Invoke();
        return followUps;
    }

    private bool Counts(string type, string currency)
    {
        var g = GoalSettings;
        var counted = type switch
        {
            KofiEventTypes.Donation => g.CountDonations,
            KofiEventTypes.Subscription or KofiEventTypes.Resubscription => g.CountMemberships,
            KofiEventTypes.Commission => g.CountCommissions,
            KofiEventTypes.ShopOrder => g.CountShopOrders,
            _ => false,
        };
        return counted && (g.Currency.Trim().Length == 0 || string.Equals(g.Currency.Trim(), currency, StringComparison.OrdinalIgnoreCase));
    }

    private int Member(string type, Dictionary<string, object?> a, DateTimeOffset now)
    {
        var privateName = Text(a, "privateName");
        var raw = privateName.Length > 0 ? privateName : Text(a, "from");
        var discordId = Text(a, "discordUserId");
        var key = discordId.Length > 0 ? "discord:" + discordId : "name:" + raw.Trim().ToLowerInvariant();
        if (discordId.Length == 0 && raw.Trim().Length == 0) return type == KofiEventTypes.Subscription ? 1 : 2;   // nobody to remember
        if (!Data.Members.TryGetValue(key, out var m) || type == KofiEventTypes.Subscription)
        {
            m = new KofiMember { FirstPayment = now };
            Data.Members[key] = m;
        }
        m.Name = Text(a, "user");
        m.PrivateName = privateName;
        m.Tier = Text(a, "tier");
        m.DiscordUserId = discordId;
        m.DiscordUsername = Text(a, "discordUsername");
        m.Amount = Number(a, "amount");
        m.Currency = Text(a, "currency");
        m.LastPayment = now;
        m.LapseReported = false;
        // A renewal Cuebot never saw the start of still means at least the second month.
        m.Payments = Math.Max(m.Payments + 1, type == KofiEventTypes.Resubscription ? 2 : 1);
        return m.Payments;
    }

    /// <summary>Reports members whose last payment is older than the lapse setting (checked once an hour).</summary>
    public void Tick(DateTimeOffset now)
    {
        if (now - _lastCheck < CheckEvery) return;
        _lastCheck = now;
        CheckLapses(now);
    }

    /// <summary>Publishes <see cref="KofiEventTypes.MembershipLapsed"/> once per lapse; returns how many.</summary>
    public int CheckLapses(DateTimeOffset now)
    {
        var days = Math.Max(1, Options.LapseDays);
        var lapsed = new List<BotEvent>();
        lock (_gate)
        {
            foreach (var m in Data.Members.Values.Where(m => !m.LapseReported && now - m.LastPayment > TimeSpan.FromDays(days)))
            {
                m.LapseReported = true;
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["service"] = "Ko-fi", ["user"] = m.Name, ["tier"] = m.Tier, ["amount"] = (decimal)m.Amount, ["currency"] = m.Currency,
                    ["lastPayment"] = m.LastPayment.LocalDateTime, ["daysSincePayment"] = (int)(now - m.LastPayment).TotalDays,
                    ["payments"] = m.Payments, ["isEstimate"] = true, ["isTest"] = false,
                };
                if (m.PrivateName.Length > 0) args["privateName"] = m.PrivateName;
                if (m.DiscordUserId.Length > 0) args["discordUserId"] = m.DiscordUserId;
                if (m.DiscordUsername.Length > 0) args["discordUsername"] = m.DiscordUsername;
                lapsed.Add(new BotEvent(KofiEventTypes.MembershipLapsed, args));
            }
            if (lapsed.Count > 0) Save();
        }
        foreach (var e in lapsed) _bus.Publish(e);
        if (lapsed.Count > 0) Changed?.Invoke();
        return lapsed.Count;
    }

    // ---- goal ----

    public KofiGoalState Goal()
    {
        lock (_gate) return GoalCore();
    }

    private KofiGoalState GoalCore()
    {
        var g = GoalSettings;
        return new KofiGoalState(g.Title, Math.Round(g.StartAmount + Data.Raised, 2), g.Target, Math.Round(Data.Raised, 2), g.StartAmount,
            g.Currency.Trim().ToUpperInvariant(), g.Target > 0 && Data.GoalReachedAt is not null);
    }

    /// <summary>Changes the title, target or start amount (null leaves one as it is). A higher target lets the goal be reached again.</summary>
    public KofiGoalState SetGoal(string? title = null, double? target = null, double? startAmount = null, string? currency = null)
    {
        lock (_gate)
        {
            var g = GoalSettings;
            if (title is not null) g.Title = title.Trim();
            if (target is { } t) g.Target = Math.Max(0, t);
            if (startAmount is { } s) g.StartAmount = Math.Max(0, s);
            if (currency is not null) g.Currency = currency.Trim().ToUpperInvariant();
            if (GoalCore() is var now && (now.Target <= 0 || now.Current < now.Target)) Data.GoalReachedAt = null;
            _settings.ScheduleSave();
            Save();
        }
        Changed?.Invoke();
        return Goal();
    }

    /// <summary>Adds (or with a negative number takes away) an amount by hand; may reach the goal.</summary>
    public KofiGoalState AddToGoal(double amount, string user = "")
    {
        BotEvent? reached = null;
        lock (_gate)
        {
            Data.Raised = Math.Max(-GoalSettings.StartAmount, Data.Raised + amount);
            var after = GoalCore();
            if (after.Target > 0 && after.Current >= after.Target && Data.GoalReachedAt is null)
            {
                Data.GoalReachedAt = _time.GetUtcNow();
                reached = new BotEvent(KofiEventTypes.GoalReached, GoalArgs(after, new Dictionary<string, object?> { ["user"] = user }));
            }
            else if (after.Current < after.Target) Data.GoalReachedAt = null;
            Save();
        }
        if (reached is not null) _bus.Publish(reached);
        Changed?.Invoke();
        return Goal();
    }

    /// <summary>Starts counting again from the start amount.</summary>
    public KofiGoalState ResetGoal(bool clearStartAmount = false)
    {
        lock (_gate)
        {
            Data.Raised = 0;
            Data.GoalReachedAt = null;
            if (clearStartAmount)
            {
                GoalSettings.StartAmount = 0;
                _settings.ScheduleSave();
            }
            Save();
        }
        Changed?.Invoke();
        return Goal();
    }

    private static Dictionary<string, object?> GoalArgs(KofiGoalState g, IReadOnlyDictionary<string, object?> from)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = "Ko-fi", ["user"] = Text(from, "user"), ["isTest"] = false,
        };
        AddGoal(args, g);
        return args;
    }

    internal static void AddGoal(Dictionary<string, object?> a, KofiGoalState g)
    {
        a["kofiGoalTitle"] = g.Title;
        a["kofiGoalCurrent"] = (decimal)g.Current;
        a["kofiGoalTarget"] = (decimal)g.Target;
        a["kofiGoalPercent"] = g.Percent;
        a["kofiGoalCurrency"] = g.Currency;
    }

    // ---- members ----

    public IReadOnlyList<KofiMember> Members()
    {
        lock (_gate) return Data.Members.Values.OrderByDescending(m => m.LastPayment).Select(Copy).ToArray();
    }

    private static KofiMember Copy(KofiMember m) => new()
    {
        Name = m.Name, PrivateName = m.PrivateName, Tier = m.Tier, DiscordUserId = m.DiscordUserId, DiscordUsername = m.DiscordUsername,
        Amount = m.Amount, Currency = m.Currency, FirstPayment = m.FirstPayment, LastPayment = m.LastPayment, Payments = m.Payments,
        LapseReported = m.LapseReported,
    };

    // ---- commissions ----

    public IReadOnlyList<KofiCommission> Commissions(bool includeDone = false)
    {
        lock (_gate) return Data.Commissions.Where(c => includeDone || !c.Done).Select(Copy).ToArray();
    }

    private static KofiCommission Copy(KofiCommission c) => new()
    {
        Id = c.Id, Name = c.Name, Message = c.Message, Amount = c.Amount, Currency = c.Currency, FormattedAmount = c.FormattedAmount,
        Time = c.Time, Done = c.Done, DoneAt = c.DoneAt,
    };

    /// <summary>Adds a commission by hand (e.g. one paid outside Ko-fi).</summary>
    public KofiCommission AddCommission(string name, string message, double amount = 0, string currency = "")
    {
        var c = new KofiCommission
        {
            Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Message = message.Trim(), Amount = amount, Currency = currency.Trim().ToUpperInvariant(),
            FormattedAmount = amount > 0 ? Money.Format(amount, currency) : "", Time = _time.GetUtcNow(),
        };
        lock (_gate)
        {
            Data.Commissions.Add(c);
            Save();
        }
        Changed?.Invoke();
        return Copy(c);
    }

    /// <summary>
    /// Marks a commission done: by id, by its position in the open list (1 = oldest), or the oldest open one when
    /// <paramref name="which"/> is empty. Returns it, or null when there is no such open commission.
    /// </summary>
    public KofiCommission? CompleteCommission(string which = "")
    {
        KofiCommission? found;
        lock (_gate)
        {
            var open = Data.Commissions.Where(c => !c.Done).ToList();
            which = which.Trim();
            found = which.Length == 0 ? open.FirstOrDefault()
                : open.FirstOrDefault(c => string.Equals(c.Id, which, StringComparison.OrdinalIgnoreCase))
                  ?? (int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= open.Count ? open[n - 1] : null);
            if (found is null) return null;
            found.Done = true;
            found.DoneAt = _time.GetUtcNow();
            Save();
        }
        Changed?.Invoke();
        return Copy(found);
    }

    /// <summary>Removes done commissions (or every commission); returns how many.</summary>
    public int ClearCommissions(bool all = false)
    {
        int removed;
        lock (_gate)
        {
            removed = Data.Commissions.RemoveAll(c => all || c.Done);
            Save();
        }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    // ---- overlay session data ----

    /// <summary><c>kofi-goal</c>: {title, current, target, percent, currency, reached}; <c>kofi-commissions</c>: the open ones.</summary>
    public void AddTo(JsonObject data)
    {
        var g = Goal();
        data["kofi-goal"] = new JsonObject
        {
            ["title"] = g.Title,
            ["current"] = g.Current,
            ["target"] = g.Target,
            ["percent"] = g.Percent,
            ["currency"] = g.Currency,
            ["reached"] = g.Reached,
        };
        data["kofi-commissions"] = new JsonArray([.. Commissions().Select((c, i) => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["position"] = i + 1,
            ["name"] = c.Name,
            ["message"] = c.Message,
            ["amount"] = c.Amount,
            ["currency"] = c.Currency,
            ["formattedAmount"] = c.FormattedAmount,
            ["createdAt"] = c.Time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        })]);
    }

    private void Save() => _file?.ScheduleSave();

    public void Dispose() => _file?.Dispose();

    private static string Text(IReadOnlyDictionary<string, object?> a, string key) => WebhookFilters.Text(a, key);

    private static double Number(IReadOnlyDictionary<string, object?> a, string key) => WebhookFilters.Number(a, key);
}
