using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Spending;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Subathon;

/// <summary>Bus events of subathon timers and goal ladders (triggers in the Subathon timer and Goals categories).</summary>
public static class SubathonEvents
{
    public const string Started = "subathon.started";
    public const string Paused = "subathon.paused";
    public const string Resumed = "subathon.resumed";
    public const string Ended = "subathon.ended";
    public const string TimeAdded = "subathon.timeAdded";
    public const string PointsAdded = "goals.pointsAdded";
    public const string StepReached = "goals.stepReached";
    public const string GoalCompleted = "goals.completed";
}

/// <summary>
/// Subathon / donothon timers and tiered points goals, fed by stream events. Purchases come from the spending ledger's
/// "spending.recorded" events (money already in the streamer's currency at the stream's locked rate; bits, Kicks,
/// Jewels, subs and gift subs as counts), follows, raids and channel point redemptions from the activity feed. Each
/// timer and goal has its own <see cref="EventValues"/> and multiplier, which can change at any time and only affect
/// what comes in afterwards.
/// <para>
/// A running timer is frozen where it was when Cuebot closes: it comes back paused with the time it had (saved every
/// <see cref="SaveEvery"/>), and resumes on go-live when <see cref="SubathonTimer.StartOnStreamOnline"/> is on. Timers
/// can start and pause with the stream; goals can reset when a new stream starts (going live
/// <see cref="NewStreamGap"/> or more after the last platform went offline). Persisted in subathon.json; thread-safe.
/// </para>
/// </summary>
public sealed class SubathonService : IPeriodicService, IDisposable
{
    public static readonly TimeSpan NewStreamGap = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(10);

    /// <summary>How many history entries are kept (all timers and goals together); the oldest go first.</summary>
    public const int HistoryLimit = 50_000;

    private readonly JsonFile<SubathonFile>? _file;
    private readonly SubathonFile _memory = new();
    private readonly JsonFile<SubathonHistoryFile>? _historyFile;
    private readonly SubathonHistoryFile _historyMemory = new();
    private readonly ChatHub? _chat;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BotEvent> _outbox = [];
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    public SubathonService(AppPaths? paths, ChatHub? chat, EventBus bus, AppLog log, TimeProvider? time = null)
    {
        _bus = bus;
        _log = log;
        _time = time ?? TimeProvider.System;
        if (paths is not null)
        {
            _file = new JsonFile<SubathonFile>(paths.File("subathon.json"), log);
            _historyFile = new JsonFile<SubathonHistoryFile>(paths.File("subathon-history.json"), log);
        }
        lock (_gate)
        {
            foreach (var t in Data.Timers.Where(t => t.State == TimerState.Running))
            {
                // Frozen where it was when Cuebot closed (the last save), so a crash never eats time.
                t.State = TimerState.Paused;
                t.EndsAt = null;
                t.SavedAt = null;
                _log.Info("Subathon", $"'{t.Name}' was running when Cuebot closed; it is paused at {Format(t.RemainingSeconds)}");
            }
            Save();
        }
        _chat = chat;
        if (chat is not null) chat.FeedItemAdded += Record;
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>Raised after any change (on the caller's thread).</summary>
    public event Action? Changed;

    private SubathonFile Data => _file?.Value ?? _memory;

    private List<SubathonHistoryEntry> Log => (_historyFile?.Value ?? _historyMemory).Entries;

    // ---------------------------------------------------------------- reading

    /// <summary>Copies of every timer, with the time left brought up to now.</summary>
    public IReadOnlyList<SubathonTimer> Timers()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return Data.Timers.Select(t => Live(Json.Clone(t), now)).ToList();
        }
    }

    public IReadOnlyList<GoalLadder> Goals()
    {
        lock (_gate) return Data.Goals.Select(Json.Clone).ToList();
    }

    /// <summary>A timer by name or id (empty: the first one), as a copy brought up to now; null when there is none.</summary>
    public SubathonTimer? Timer(string? nameOrId)
    {
        lock (_gate) return FindTimer(nameOrId) is { } t ? Live(Json.Clone(t), _time.GetUtcNow()) : null;
    }

    public GoalLadder? Goal(string? nameOrId)
    {
        lock (_gate) return FindGoal(nameOrId) is { } g ? Json.Clone(g) : null;
    }

    // ---------------------------------------------------------------- setting up

    public SubathonTimer AddTimer(string name = "")
    {
        SubathonTimer timer;
        lock (_gate)
        {
            timer = new SubathonTimer { Name = UniqueName(name.Trim().Length > 0 ? name.Trim() : "Subathon", Data.Timers.Select(t => t.Name)) };
            timer.RemainingSeconds = timer.InitialSeconds;
            Data.Timers.Add(timer);
            Save();
        }
        Changed?.Invoke();
        return Json.Clone(timer);
    }

    public GoalLadder AddGoal(string name = "")
    {
        GoalLadder goal;
        lock (_gate)
        {
            goal = new GoalLadder
            {
                Name = UniqueName(name.Trim().Length > 0 ? name.Trim() : "Sub goal", Data.Goals.Select(g => g.Name)),
                Steps = [new() { Name = "First goal", Points = 10 }, new() { Name = "Second goal", Points = 20 }],
            };
            Data.Goals.Add(goal);
            Save();
        }
        Changed?.Invoke();
        return Json.Clone(goal);
    }

    /// <summary>Deletes a timer and its history.</summary>
    public bool RemoveTimer(string id) => Remove(Data.Timers, t => t.Id == id, id);

    /// <summary>Deletes a goal and its history.</summary>
    public bool RemoveGoal(string id) => Remove(Data.Goals, g => g.Id == id, id);

    /// <summary>
    /// Changes a timer's settings (name, starting length, limit, multiplier, values, automation). Its state and time left
    /// are not touched; a stopped timer's time left follows a new starting length.
    /// </summary>
    public void UpdateTimer(string id, Action<SubathonTimer> change)
    {
        lock (_gate)
        {
            var t = Data.Timers.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("No such timer");
            var (state, remaining, endsAt, elapsed, savedAt, added) = (t.State, t.RemainingSeconds, t.EndsAt, t.ElapsedSeconds, t.SavedAt, t.AddedSeconds);
            change(t);
            (t.State, t.RemainingSeconds, t.EndsAt, t.ElapsedSeconds, t.SavedAt, t.AddedSeconds) = (state, remaining, endsAt, elapsed, savedAt, added);
            t.Name = t.Name.Trim().Length > 0 ? t.Name.Trim() : "Subathon";
            t.InitialSeconds = Math.Max(1, t.InitialSeconds);
            t.MaxSeconds = Math.Max(0, t.MaxSeconds);
            t.Multiplier = ClampMultiplier(t.Multiplier);
            if (t.State == TimerState.Stopped) t.RemainingSeconds = t.InitialSeconds;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Changes a goal's settings; its steps are put in ascending order and its points are not touched.</summary>
    public void UpdateGoal(string id, Action<GoalLadder> change)
    {
        lock (_gate)
        {
            var g = Data.Goals.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("No such goal");
            var (points, reached) = (g.Points, g.ReachedThrough);
            change(g);
            (g.Points, g.ReachedThrough) = (points, reached);
            g.Name = g.Name.Trim().Length > 0 ? g.Name.Trim() : "Goal";
            g.Multiplier = ClampMultiplier(g.Multiplier);
            g.Steps = SortSteps(g.Steps);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Steps without a name dropped, in ascending order of points (ties keep their order).</summary>
    public static List<GoalStep> SortSteps(IEnumerable<GoalStep> steps) =>
        steps.Where(s => s.Name.Trim().Length > 0 && double.IsFinite(s.Points))
            .Select(s => new GoalStep { Name = s.Name.Trim(), Points = Math.Max(0, s.Points) })
            .OrderBy(s => s.Points)
            .ToList();

    // ---------------------------------------------------------------- timer control

    /// <summary>Starts a stopped or ended timer from its starting length, or resumes a paused one.</summary>
    public void Start(string? timer) => Control(timer, (t, now) => StartCore(t, now));

    public void Pause(string? timer) => Control(timer, (t, now) =>
    {
        if (t.State != TimerState.Running) return;
        Sync(t, now);
        t.State = TimerState.Paused;
        t.EndsAt = null;
        t.SavedAt = null;
        Emit(SubathonEvents.Paused, TimerArgs(t));
    });

    public void Resume(string? timer) => Control(timer, (t, now) =>
    {
        if (t.State != TimerState.Paused) return;
        Run(t, now);
        Emit(SubathonEvents.Resumed, TimerArgs(t));
    });

    /// <summary>Pauses a running timer, resumes a paused one, starts a stopped one.</summary>
    public void Toggle(string? timer) => Control(timer, (t, now) =>
    {
        if (t.State == TimerState.Running)
        {
            Sync(t, now);
            t.State = TimerState.Paused;
            t.EndsAt = null;
            t.SavedAt = null;
            Emit(SubathonEvents.Paused, TimerArgs(t));
        }
        else StartCore(t, now);
    });

    /// <summary>Back to stopped, with the starting length and nothing added or run yet.</summary>
    public void Reset(string? timer) => Control(timer, (t, _) =>
    {
        t.Run++;
        t.State = TimerState.Stopped;
        t.RemainingSeconds = t.InitialSeconds;
        t.ElapsedSeconds = 0;
        t.AddedSeconds = 0;
        t.EndsAt = null;
        t.SavedAt = null;
    });

    /// <summary>
    /// Adds (or with a negative number removes) time by hand. The multiplier is used only when <paramref name="useMultiplier"/>
    /// is set; the maximum length always applies. Adding to an ended timer brings it back paused. Returns the seconds added.
    /// </summary>
    public double AddTime(string? timer, double seconds, bool useMultiplier = false, string user = "", string reason = "Manual")
    {
        double added = 0;
        Control(timer, (t, now) =>
        {
            var multiplier = useMultiplier ? t.Multiplier : 1;
            added = AddCore(t, now, seconds * multiplier, user, "", reason);
            Note(t, now, HistoryKind.Manual, new Cause(user, "", reason), multiplier, added);
        });
        return added;
    }

    /// <summary>Sets the time left (the maximum length still applies). Setting time on an ended timer brings it back paused.</summary>
    public void SetTime(string? timer, double seconds) => Control(timer, (t, now) =>
    {
        Sync(t, now);
        var value = Math.Max(0, seconds);
        if (t.MaxSeconds > 0) value = Math.Min(value, Math.Max(0, t.MaxSeconds - t.ElapsedSeconds));
        Note(t, now, HistoryKind.Set, new Cause("", "", "Set time left"), 1, Math.Round(value - t.RemainingSeconds, 3));
        t.RemainingSeconds = value;
        if (t.State == TimerState.Ended && value > 0) t.State = TimerState.Paused;
        if (t.State == TimerState.Running) After(t, now);
    });

    public void SetTimerMultiplier(string? timer, double multiplier) => Control(timer, (t, _) => t.Multiplier = ClampMultiplier(multiplier));

    // ---------------------------------------------------------------- goal control

    /// <summary>Adds (or removes) points by hand; returns the points added.</summary>
    public double AddPoints(string? goal, double points, bool useMultiplier = false, string user = "", string reason = "Manual")
    {
        double added = 0;
        GoalControl(goal, g =>
        {
            var multiplier = useMultiplier ? g.Multiplier : 1;
            added = AddPointsCore(g, points * multiplier, user, "", reason);
            Note(g, _time.GetUtcNow(), HistoryKind.Manual, new Cause(user, "", reason), multiplier, added);
        });
        return added;
    }

    /// <summary>Sets the points; steps reached on the way up are announced once, as with any other change.</summary>
    public void SetPoints(string? goal, double points) => GoalControl(goal, g =>
        Note(g, _time.GetUtcNow(), HistoryKind.Set, new Cause("", "", "Set points"), 1, AddPointsCore(g, Math.Max(0, points) - g.Points, "", "", "Set")));

    /// <summary>Back to 0 points, with every step ready to be reached again.</summary>
    public void ResetGoal(string? goal) => GoalControl(goal, g =>
    {
        g.Run++;
        g.Points = 0;
        g.ReachedThrough = null;
    });

    public void SetGoalMultiplier(string? goal, double multiplier) => GoalControl(goal, g => g.Multiplier = ClampMultiplier(multiplier));

    // ---------------------------------------------------------------- ticking

    void IPeriodicService.Tick(DateTimeOffset now) => Tick(now);

    /// <summary>Ends timers that ran out, and saves running timers' time left every <see cref="SaveEvery"/>.</summary>
    public void Tick(DateTimeOffset now)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var t in Data.Timers.Where(t => t.State == TimerState.Running))
            {
                if (t.EndsAt is { } end && now >= end)
                {
                    Sync(t, now);
                    End(t);
                    changed = true;
                }
            }
            if (now - _lastSave >= SaveEvery && Data.Timers.Any(t => t.State == TimerState.Running))
            {
                foreach (var t in Data.Timers.Where(t => t.State == TimerState.Running)) Sync(t, now);
                _lastSave = now;
                Save();
            }
            else if (changed) Save();
        }
        Flush(changed);
    }

    // ---------------------------------------------------------------- events in

    /// <summary>Handles an activity item (subscribed to the chat hub; public for tests).</summary>
    public void Record(FeedItem item)
    {
        if (item.IsTest && !item.IsEmulated) return;   // emulated events count, so timers and goals can be tried out
        switch (item.Kind)
        {
            case FeedKind.StreamOnline:
                OnLive(item.Platform, live: true);
                return;
            case FeedKind.StreamOffline:
                OnLive(item.Platform, live: false);
                return;
            case FeedKind.Follow:
                Apply(new Cause(item.User, item.Platform, "Follow", "Follow", 1, IsTest: item.IsTest), v => v.Follow);
                return;
            case FeedKind.Raid:
                var viewers = ExtrasText.FirstNumber(item.Title) ?? 0;
                Apply(new Cause(item.User, item.Platform, "Raid", "Raid", viewers, IsTest: item.IsTest), v => v.RaidEach + viewers * v.RaidPerViewer);
                return;
            case FeedKind.Redemption:
                var marker = item.Title.IndexOf(" redeemed ", StringComparison.OrdinalIgnoreCase);
                var reward = (marker >= 0 ? item.Title[(marker + " redeemed ".Length)..] : item.Title).Trim();
                Apply(new Cause(item.User, item.Platform, reward, "Redemption", 1, IsTest: item.IsTest), v => RewardValue(v, reward));
                return;
        }
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type != SpendingEvents.Recorded) return;
        var a = e.Args;
        var platform = a.GetString("platform");
        var unit = Enum.TryParse<SpendUnit>(a.GetString("unit"), out var u) ? u : SpendUnit.Money;
        var quantity = Number(a, "quantity");
        var amount = Number(a, "amount");
        var tier = a.GetString("tier");
        var cause = new Cause(a.GetString("user"), platform, a.GetString("label"), unit.ToString(), quantity, amount, a.GetString("currency"),
            a.GetValueOrDefault("isTest") is true);
        Apply(cause, v => Worth(v, unit, quantity, amount, platform, tier));
    }

    /// <summary>What a purchase is worth to one timer or goal.</summary>
    public static double Worth(EventValues v, SpendUnit unit, double quantity, double amount, string platform, string tier) => unit switch
    {
        SpendUnit.Money => amount * v.MoneyValue(platform),
        SpendUnit.Bits => quantity / 100 * v.Per100Bits,
        SpendUnit.Kicks => quantity / 100 * v.Per100Kicks,
        SpendUnit.Jewels => quantity / 100 * v.Per100Jewels,
        SpendUnit.Subscription => quantity * SubValue(v, platform, tier),
        SpendUnit.GiftSubscription => v.CountGiftSubs ? quantity * SubValue(v, platform, tier) : 0,
        _ => 0,
    };

    /// <summary>A reward's value, matched ignoring capitals (saved lists lose their case-insensitive lookup).</summary>
    public static double RewardValue(EventValues v, string reward) =>
        v.Redemptions.FirstOrDefault(r => string.Equals(r.Key.Trim(), reward.Trim(), StringComparison.OrdinalIgnoreCase)).Value;

    private static double SubValue(EventValues v, string platform, string tier) => platform.ToLowerInvariant() switch
    {
        "twitch" => tier switch { "2" => v.TwitchTier2, "3" => v.TwitchTier3, _ => v.TwitchTier1 },
        "kick" => v.KickSub,
        "youtube" => v.YouTubeMembership,
        _ => v.OtherSub,
    };

    /// <summary>What caused a change, for its history entry.</summary>
    private sealed record Cause(string User, string Source, string Reason, string Unit = "", double Quantity = 0, double Amount = 0,
        string Currency = "", bool IsTest = false);

    private void Apply(Cause cause, Func<EventValues, double> worth)
    {
        var changed = false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var t in Data.Timers)
            {
                var accepts = t.State == TimerState.Running || t.State == TimerState.Paused && t.AddWhilePaused;
                var seconds = worth(t.Values) * t.Multiplier;
                if (!accepts || !(seconds > 0) || !double.IsFinite(seconds)) continue;
                var added = AddCore(t, now, seconds, cause.User, cause.Source, cause.Reason);
                if (added == 0) continue;
                Note(t, now, HistoryKind.Event, cause, t.Multiplier, added);
                changed = true;
            }
            foreach (var g in Data.Goals)
            {
                var points = worth(g.Values) * g.Multiplier;
                if (!(points > 0) || !double.IsFinite(points)) continue;
                var added = AddPointsCore(g, points, cause.User, cause.Source, cause.Reason);
                if (added == 0) continue;
                Note(g, now, HistoryKind.Event, cause, g.Multiplier, added);
                changed = true;
            }
            if (changed) Save();
        }
        Flush(changed);
    }

    private void OnLive(string platform, bool live)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (live)
            {
                var newStream = _live.Count == 0 && !Data.WasLive && (Data.OfflineSince is not { } off || now - off >= NewStreamGap);
                _live.Add(platform);
                Data.WasLive = true;
                Data.OfflineSince = null;
                if (newStream)
                {
                    foreach (var g in Data.Goals.Where(g => g.ResetOnNewStream))
                    {
                        g.Run++;
                        g.Points = 0;
                        g.ReachedThrough = null;
                    }
                }
                foreach (var t in Data.Timers.Where(t => t.StartOnStreamOnline && t.State is TimerState.Stopped or TimerState.Paused))
                    StartCore(t, now);
            }
            else
            {
                _live.Remove(platform);
                if (_live.Count > 0) return;
                Data.WasLive = false;
                Data.OfflineSince = now;
                foreach (var t in Data.Timers.Where(t => t.PauseOnStreamOffline && t.State == TimerState.Running))
                {
                    Sync(t, now);
                    t.State = TimerState.Paused;
                    t.EndsAt = null;
                    t.SavedAt = null;
                    Emit(SubathonEvents.Paused, TimerArgs(t));
                }
            }
            Save();
        }
        Flush(true);
    }

    // ---------------------------------------------------------------- history

    /// <summary>The newest entries matching <paramref name="filter"/> first, as copies.</summary>
    public IReadOnlyList<SubathonHistoryEntry> History(HistoryFilter filter, int limit = 500)
    {
        lock (_gate) return Matching(filter).Reverse().Take(Math.Max(1, limit)).Select(Json.Clone).ToList();
    }

    /// <summary>One entry by id (a copy), or null.</summary>
    public SubathonHistoryEntry? HistoryEntry(string id)
    {
        lock (_gate) return Log.FirstOrDefault(e => e.Id == id.Trim()) is { } e ? Json.Clone(e) : null;
    }

    /// <summary>What the entries matching <paramref name="filter"/> add up to, by source; removed entries are counted apart.</summary>
    public HistorySummary Summarize(HistoryFilter filter)
    {
        lock (_gate)
        {
            var all = Matching(filter with { IncludeRemoved = true }).ToList();
            var counted = all.Where(e => !e.Removed).ToList();
            var bySource = counted
                .GroupBy(e => SourceName(e), StringComparer.OrdinalIgnoreCase)
                .Select(g => new HistorySourceTotal(g.Key, g.Count(), Math.Round(g.Sum(e => e.Amount), 2), Math.Round(g.Sum(e => e.Value), 3)))
                .OrderByDescending(x => x.Value)
                .ToList();
            return new HistorySummary(counted.Count, all.Count - counted.Count, Math.Round(counted.Sum(e => e.Value), 3),
                Math.Round(counted.Sum(e => e.Amount), 2), bySource);
        }
    }

    /// <summary>The name of the timer or goal an entry belongs to ("" when it was deleted).</summary>
    public string OwnerName(SubathonHistoryEntry e)
    {
        lock (_gate)
            return e.IsGoal ? Data.Goals.FirstOrDefault(g => g.Id == e.OwnerId)?.Name ?? "" : Data.Timers.FirstOrDefault(t => t.Id == e.OwnerId)?.Name ?? "";
    }

    /// <summary>Whether correcting the entry changes its timer or goal (it belongs to the current run, which was not reset since).</summary>
    public bool IsCurrent(SubathonHistoryEntry e)
    {
        lock (_gate)
            return e.IsGoal ? Data.Goals.Any(g => g.Id == e.OwnerId && g.Run == e.Run) : Data.Timers.Any(t => t.Id == e.OwnerId && t.Run == e.Run);
    }

    /// <summary>
    /// Takes an entry back: its seconds or points come off its timer or goal (as far as they still can: time that has
    /// already run cannot be taken back) and it stops counting in the totals. An entry from before the last reset is only
    /// marked. Removing an entry twice does nothing.
    /// </summary>
    public SubathonHistoryEntry RemoveEntry(string id, string? note = null) => Correct(id, (e, now) =>
    {
        RemoveCore(e, now);
        if (note is not null) e.Note = note.Trim();
    });

    /// <summary>Puts a removed entry back: what removing it took off is added again.</summary>
    public SubathonHistoryEntry RestoreEntry(string id) => Correct(id, (e, now) =>
    {
        if (!e.Removed) return;
        var expected = e.Reversed;
        var (applied, live) = Adjust(e, expected, now);
        e.Removed = false;
        e.Reversed = 0;
        if (live && Math.Abs(applied - expected) > 0.001) e.Note = Join(e.Note, $"Only {Change(e, applied)} could be put back (the limit)");
    });

    /// <summary>
    /// Corrects an entry: a new value (seconds or points), or a new money amount (the value follows at the rate the entry
    /// was counted with), and/or a note. The difference is added to or taken off its timer or goal.
    /// </summary>
    public SubathonHistoryEntry ChangeEntry(string id, double? value = null, double? amount = null, string? note = null) => Correct(id, (e, now) =>
    {
        if (e.Removed && (value is not null || amount is not null)) throw new ArgumentException("This entry was removed; put it back first.");
        double? target = value;
        if (amount is { } newAmount)
        {
            if (!(e.Amount > 0)) throw new ArgumentException("This entry has no money amount to change; change its value instead.");
            if (!(newAmount >= 0) || !double.IsFinite(newAmount)) throw new ArgumentException("The amount must be 0 or more.");
            target ??= e.Value * newAmount / e.Amount;
            e.OriginalAmount ??= e.Amount;
            e.Amount = Math.Round(newAmount, 4);
        }
        if (target is { } wanted)
        {
            if (!double.IsFinite(wanted)) throw new ArgumentException("The value must be a number.");
            wanted = Math.Round(wanted, 3);
            var delta = wanted - e.Value;
            var (applied, live) = Adjust(e, delta, now);
            e.OriginalValue ??= e.Value;
            e.Value = live ? Math.Round(e.Value + applied, 3) : wanted;
            if (live && Math.Abs(applied - delta) > 0.001)
                e.Note = Join(e.Note, $"Only {Change(e, applied)} could be applied; the rest had already run or was past the limit");
        }
        if (note is not null) e.Note = note.Trim();
    });

    /// <summary>Removes every entry matching <paramref name="filter"/> (like <see cref="RemoveEntry"/>); returns how many.</summary>
    public int RemoveEntries(HistoryFilter filter)
    {
        int removed;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var matching = Matching(filter with { IncludeRemoved = false }).ToList();
            foreach (var e in matching)
            {
                RemoveCore(e, now);
                e.ChangedAt = now;
            }
            removed = matching.Count;
            if (removed > 0)
            {
                SaveHistory();
                Save();
            }
        }
        Flush(removed > 0);
        return removed;
    }

    /// <summary>The entries matching <paramref name="filter"/> as CSV (oldest first), for a spreadsheet or the streamer's records.</summary>
    public string HistoryCsv(HistoryFilter filter)
    {
        List<(SubathonHistoryEntry Entry, string Owner)> rows;
        lock (_gate)
        {
            var names = Data.Timers.ToDictionary(t => t.Id, t => t.Name);
            foreach (var g in Data.Goals) names[g.Id] = g.Name;
            rows = Matching(filter).Select(e => (Json.Clone(e), names.GetValueOrDefault(e.OwnerId, ""))).ToList();
        }
        var csv = new System.Text.StringBuilder("Time,Timer or goal,Kind,Name,Source,What,Unit,Quantity,Amount,Currency,Added,Unit added,Removed,Test,Note\n");
        foreach (var (e, owner) in rows)
        {
            string[] cells =
            [
                e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), owner, e.Kind.ToString(), e.User, e.Source,
                e.Reason, e.Unit, Num(e.Quantity), Num(e.Amount), e.Currency, Num(e.Value), e.IsGoal ? "points" : "seconds",
                e.Removed ? "yes" : "", e.IsTest ? "yes" : "", e.Note,
            ];
            csv.AppendJoin(',', cells.Select(Cell)).Append('\n');
        }
        return csv.ToString();

        static string Num(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
        static string Cell(string text) => text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>"+5:00:00"-style text for a timer entry's change, "+2 points" for a goal's.</summary>
    public static string Change(SubathonHistoryEntry e, double? value = null)
    {
        var v = value ?? e.Value;
        var sign = v < 0 ? "-" : "+";
        return e.IsGoal
            ? sign + Math.Abs(v).ToString("0.###", CultureInfo.InvariantCulture) + (Math.Abs(v) == 1 ? " point" : " points")
            : sign + Format(Math.Abs(v), roundUp: false);
    }

    /// <summary>Where an entry came from, for lists and totals ("By hand" for manual changes).</summary>
    public static string SourceName(SubathonHistoryEntry e) => e.Source.Trim().Length > 0 ? e.Source.Trim() : "By hand";

    private void Note(SubathonTimer t, DateTimeOffset now, HistoryKind kind, Cause cause, double multiplier, double value) =>
        Note(t.Id, false, t.Run, now, kind, cause, multiplier, value);

    private void Note(GoalLadder g, DateTimeOffset now, HistoryKind kind, Cause cause, double multiplier, double value) =>
        Note(g.Id, true, g.Run, now, kind, cause, multiplier, value);

    private void Note(string owner, bool isGoal, int run, DateTimeOffset now, HistoryKind kind, Cause c, double multiplier, double value)
    {
        if (value == 0 || !double.IsFinite(value)) return;
        var log = Log;
        log.Add(new SubathonHistoryEntry
        {
            OwnerId = owner, IsGoal = isGoal, Run = run, Time = now, Kind = kind,
            User = c.User.Trim().TrimStart('@'), Source = c.Source.Trim(), Reason = c.Reason.Trim(), Unit = c.Unit,
            Quantity = c.Quantity, Amount = Math.Round(c.Amount, 4), Currency = c.Currency, Multiplier = multiplier,
            Value = Math.Round(value, 3), IsTest = c.IsTest,
        });
        if (log.Count > HistoryLimit) log.RemoveRange(0, log.Count - HistoryLimit);
        SaveHistory();
    }

    private IEnumerable<SubathonHistoryEntry> Matching(HistoryFilter f)
    {
        string? owner = null;
        if (!string.IsNullOrWhiteSpace(f.Owner)) owner = FindTimer(f.Owner)?.Id ?? FindGoal(f.Owner)?.Id ?? "\0none";
        var user = f.User?.Trim().TrimStart('@') ?? "";
        var source = f.Source?.Trim() ?? "";
        return Log.Where(e =>
            (owner is null || e.OwnerId == owner)
            && (f.From is not { } from || e.Time >= from)
            && (f.To is not { } to || e.Time <= to)
            && (user.Length == 0 || e.User.Contains(user, StringComparison.OrdinalIgnoreCase))
            && (source.Length == 0 || string.Equals(SourceName(e), source, StringComparison.OrdinalIgnoreCase))
            && (!f.TestOnly || e.IsTest)
            && (f.IncludeRemoved || !e.Removed));
    }

    private void RemoveCore(SubathonHistoryEntry e, DateTimeOffset now)
    {
        if (e.Removed) return;
        var (applied, live) = Adjust(e, -e.Value, now);
        e.Removed = true;
        e.Reversed = Math.Round(-applied, 3);
        if (live && Math.Abs(applied + e.Value) > 0.001)
            e.Note = Join(e.Note, $"Only {Change(e, -applied).TrimStart('+')} could be taken back; the rest had already run");
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the entry's timer or goal when it still belongs to the current run; returns what
    /// was really applied (limits and time already run can make it less) and whether the timer or goal was changed.
    /// </summary>
    private (double Applied, bool Live) Adjust(SubathonHistoryEntry e, double delta, DateTimeOffset now)
    {
        delta = Math.Round(delta, 3);
        if (e.IsGoal)
        {
            if (Data.Goals.FirstOrDefault(g => g.Id == e.OwnerId) is not { } g || g.Run != e.Run) return (0, false);
            return (delta == 0 ? 0 : AddPointsCore(g, delta, e.User, e.Source, "Correction"), true);
        }
        if (Data.Timers.FirstOrDefault(t => t.Id == e.OwnerId) is not { } t || t.Run != e.Run) return (0, false);
        if (delta == 0) return (0, true);
        var applied = AddCore(t, now, delta, e.User, e.Source, "Correction");
        if (applied < 0) t.AddedSeconds = Math.Max(0, t.AddedSeconds + applied);   // a taken-back event was never really added
        return (applied, true);
    }

    private SubathonHistoryEntry Correct(string id, Action<SubathonHistoryEntry, DateTimeOffset> change)
    {
        SubathonHistoryEntry copy;
        lock (_gate)
        {
            var e = Log.FirstOrDefault(x => x.Id == id.Trim()) ?? throw new ArgumentException($"No history entry '{id}'");
            var now = _time.GetUtcNow();
            change(e, now);
            e.ChangedAt = now;
            SaveHistory();
            Save();
            copy = Json.Clone(e);
        }
        Flush(true);
        return copy;
    }

    private static string Join(string note, string add) => note.Trim().Length == 0 ? add : $"{note.Trim()}. {add}";

    // ---------------------------------------------------------------- the maths

    private void StartCore(SubathonTimer t, DateTimeOffset now)
    {
        switch (t.State)
        {
            case TimerState.Running:
                return;
            case TimerState.Paused:
                Run(t, now);
                Emit(SubathonEvents.Resumed, TimerArgs(t));
                return;
            case TimerState.Ended:
                t.Run++;
                t.RemainingSeconds = t.InitialSeconds;
                t.ElapsedSeconds = 0;
                t.AddedSeconds = 0;
                break;
        }
        Run(t, now);
        Emit(SubathonEvents.Started, TimerArgs(t));
    }

    private static void Run(SubathonTimer t, DateTimeOffset now)
    {
        t.State = TimerState.Running;
        t.SavedAt = now;
        t.EndsAt = now.AddSeconds(t.RemainingSeconds);
    }

    /// <summary>Brings a running timer's time left and time run up to <paramref name="now"/>.</summary>
    private static void Sync(SubathonTimer t, DateTimeOffset now)
    {
        if (t.State != TimerState.Running || t.SavedAt is not { } saved) return;
        var passed = Math.Clamp((now - saved).TotalSeconds, 0, t.RemainingSeconds);
        t.RemainingSeconds -= passed;
        t.ElapsedSeconds += passed;
        t.SavedAt = now;
    }

    /// <summary>A copy for reading: a running timer's time left as of now, without changing the stored one.</summary>
    private static SubathonTimer Live(SubathonTimer copy, DateTimeOffset now)
    {
        Sync(copy, now);
        return copy;
    }

    private double AddCore(SubathonTimer t, DateTimeOffset now, double seconds, string user, string platform, string reason)
    {
        Sync(t, now);
        if (t.MaxSeconds > 0 && seconds > 0) seconds = Math.Min(seconds, Math.Max(0, t.MaxSeconds - t.ElapsedSeconds - t.RemainingSeconds));
        if (seconds < 0) seconds = Math.Max(seconds, -t.RemainingSeconds);
        seconds = Math.Round(seconds, 3);
        if (seconds == 0) return 0;
        t.RemainingSeconds += seconds;
        if (seconds > 0) t.AddedSeconds += seconds;
        if (t.State == TimerState.Ended && t.RemainingSeconds > 0) t.State = TimerState.Paused;
        var args = TimerArgs(t);
        args["seconds"] = seconds;
        args["formattedSeconds"] = (seconds < 0 ? "-" : "+") + Format(Math.Abs(seconds));
        args["user"] = user;
        args["platform"] = platform;
        args["reason"] = reason;
        Emit(SubathonEvents.TimeAdded, args);
        if (t.State == TimerState.Running) After(t, now);
        return seconds;
    }

    /// <summary>After a change to a running timer: a new end time, or the end when nothing is left.</summary>
    private void After(SubathonTimer t, DateTimeOffset now)
    {
        if (t.RemainingSeconds <= 0) End(t);
        else t.EndsAt = now.AddSeconds(t.RemainingSeconds);
    }

    private void End(SubathonTimer t)
    {
        t.RemainingSeconds = 0;
        t.State = TimerState.Ended;
        t.EndsAt = null;
        t.SavedAt = null;
        _log.Info("Subathon", $"'{t.Name}' ended after {Format(t.ElapsedSeconds, roundUp: false)}");
        Emit(SubathonEvents.Ended, TimerArgs(t));
    }

    private double AddPointsCore(GoalLadder g, double points, string user, string platform, string reason)
    {
        points = Math.Round(points, 3);
        if (points < 0) points = Math.Max(points, -g.Points);
        if (points == 0) return 0;
        g.Points = Math.Round(g.Points + points, 3);
        var args = GoalArgs(g);
        args["added"] = points;
        args["user"] = user;
        args["platform"] = platform;
        args["reason"] = reason;
        Emit(SubathonEvents.PointsAdded, args);
        var reachedAny = false;
        for (var i = 0; i < g.Steps.Count; i++)
        {
            var step = g.Steps[i];
            if (step.Points > g.Points || g.ReachedThrough is { } through && step.Points <= through) continue;
            g.ReachedThrough = step.Points;
            reachedAny = true;
            var reached = GoalArgs(g);
            reached["step"] = step.Name;
            reached["stepPoints"] = step.Points;
            reached["stepNumber"] = i + 1;
            reached["stepCount"] = g.Steps.Count;
            reached["user"] = user;
            reached["platform"] = platform;
            Emit(SubathonEvents.StepReached, reached);
        }
        if (reachedAny && g.Steps.Count > 0 && g.ReachedThrough >= g.Steps[^1].Points) Emit(SubathonEvents.GoalCompleted, GoalArgs(g));
        return points;
    }

    // ---------------------------------------------------------------- arguments

    private static Dictionary<string, object?> TimerArgs(SubathonTimer t) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["timer"] = t.Name,
        ["timerId"] = t.Id,
        ["state"] = t.State.ToString(),
        ["remaining"] = Math.Round(t.RemainingSeconds),
        ["formattedRemaining"] = Format(t.RemainingSeconds),
        ["elapsed"] = Math.Round(t.ElapsedSeconds),
        ["formattedElapsed"] = Format(t.ElapsedSeconds, roundUp: false),
        ["multiplier"] = t.Multiplier,
    };

    internal static Dictionary<string, object?> GoalArgs(GoalLadder g)
    {
        var next = g.Steps.FirstOrDefault(s => s.Points > g.Points);
        var reached = g.ReachedThrough is { } through ? g.Steps.Count(s => s.Points <= through) : 0;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["goal"] = g.Name,
            ["goalId"] = g.Id,
            ["points"] = g.Points,
            ["nextStep"] = next?.Name ?? "",
            ["nextStepPoints"] = next?.Points ?? 0,
            ["pointsToNext"] = next is null ? 0 : Math.Round(next.Points - g.Points, 3),
            ["stepsReached"] = reached,
            ["stepCount"] = g.Steps.Count,
            ["multiplier"] = g.Multiplier,
        };
    }

    /// <summary>
    /// Seconds as H:MM:SS (hours can pass 24, like 26:03:10). Time left rounds up (a clock never shows 0:00:00 while
    /// time is left); time already run rounds down (<paramref name="roundUp"/> false), so the two add up.
    /// </summary>
    public static string Format(double seconds, bool roundUp = true)
    {
        var value = Math.Max(0, seconds);
        var total = roundUp ? (long)Math.Ceiling(value - 0.0005) : (long)Math.Floor(value + 0.0005);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}");
    }

    // ---------------------------------------------------------------- helpers

    private void Control(string? name, Action<SubathonTimer, DateTimeOffset> action)
    {
        lock (_gate)
        {
            var t = FindTimer(name) ?? throw new ArgumentException(string.IsNullOrWhiteSpace(name) ? "There is no subathon timer yet" : $"No subathon timer named '{name}'");
            action(t, _time.GetUtcNow());
            Save();
        }
        Flush(true);
    }

    private void GoalControl(string? name, Action<GoalLadder> action)
    {
        lock (_gate)
        {
            var g = FindGoal(name) ?? throw new ArgumentException(string.IsNullOrWhiteSpace(name) ? "There is no goal yet" : $"No goal named '{name}'");
            action(g);
            Save();
        }
        Flush(true);
    }

    private SubathonTimer? FindTimer(string? name) => Find(Data.Timers, name, t => t.Id, t => t.Name);

    private GoalLadder? FindGoal(string? name) => Find(Data.Goals, name, g => g.Id, g => g.Name);

    private static T? Find<T>(List<T> list, string? name, Func<T, string> id, Func<T, string> title) where T : class
    {
        var key = name?.Trim() ?? "";
        if (key.Length == 0) return list.FirstOrDefault();
        return list.FirstOrDefault(x => id(x) == key) ?? list.FirstOrDefault(x => string.Equals(title(x), key, StringComparison.OrdinalIgnoreCase));
    }

    private bool Remove<T>(List<T> list, Predicate<T> match, string id)
    {
        int removed;
        lock (_gate)
        {
            removed = list.RemoveAll(match);
            if (removed > 0)
            {
                if (Log.RemoveAll(e => e.OwnerId == id) > 0) SaveHistory();
                Save();
            }
        }
        if (removed > 0) Changed?.Invoke();
        return removed > 0;
    }

    private static string UniqueName(string name, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!used.Contains($"{name} {i}")) return $"{name} {i}";
    }

    private static double ClampMultiplier(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 1;

    private static double Number(IReadOnlyDictionary<string, object?> a, string key) =>
        a.GetValueOrDefault(key) switch
        {
            double d => d,
            int i => i,
            long l => l,
            decimal m => (double)m,
            var other => double.TryParse(Convert.ToString(other, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0,
        };

    private void Emit(string type, Dictionary<string, object?> args) => _outbox.Add(new BotEvent(type, args));

    /// <summary>Publishes what the last change produced (outside the lock) and raises <see cref="Changed"/>.</summary>
    private void Flush(bool changed)
    {
        BotEvent[] events;
        lock (_gate)
        {
            events = [.. _outbox];
            _outbox.Clear();
        }
        if (changed || events.Length > 0) Changed?.Invoke();
        foreach (var e in events) _bus.Publish(e);
    }

    private void Save() => _file?.ScheduleSave();

    private void SaveHistory() => _historyFile?.ScheduleSave();

    public void FlushFile()
    {
        _file?.Flush();
        _historyFile?.Flush();
    }

    public void Dispose()
    {
        if (_chat is not null) _chat.FeedItemAdded -= Record;
        _subscription.Dispose();
        lock (_gate)
        {
            // A clean shutdown freezes running timers at this very second (a crash keeps the last save instead).
            var now = _time.GetUtcNow();
            foreach (var t in Data.Timers.Where(t => t.State == TimerState.Running)) Sync(t, now);
            Save();
        }
        _file?.Dispose();
        _historyFile?.Dispose();
    }
}
