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

    private readonly JsonFile<SubathonFile>? _file;
    private readonly SubathonFile _memory = new();
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
        if (paths is not null) _file = new JsonFile<SubathonFile>(paths.File("subathon.json"), log);
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

    public bool RemoveTimer(string id) => Remove(Data.Timers, t => t.Id == id);

    public bool RemoveGoal(string id) => Remove(Data.Goals, g => g.Id == id);

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
        Control(timer, (t, now) => added = AddCore(t, now, useMultiplier ? seconds * t.Multiplier : seconds, user, "", reason));
        return added;
    }

    /// <summary>Sets the time left (the maximum length still applies). Setting time on an ended timer brings it back paused.</summary>
    public void SetTime(string? timer, double seconds) => Control(timer, (t, now) =>
    {
        Sync(t, now);
        var value = Math.Max(0, seconds);
        if (t.MaxSeconds > 0) value = Math.Min(value, Math.Max(0, t.MaxSeconds - t.ElapsedSeconds));
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
        GoalControl(goal, g => added = AddPointsCore(g, useMultiplier ? points * g.Multiplier : points, user, "", reason));
        return added;
    }

    /// <summary>Sets the points; steps reached on the way up are announced once, as with any other change.</summary>
    public void SetPoints(string? goal, double points) => GoalControl(goal, g => AddPointsCore(g, Math.Max(0, points) - g.Points, "", "", "Set"));

    /// <summary>Back to 0 points, with every step ready to be reached again.</summary>
    public void ResetGoal(string? goal) => GoalControl(goal, g =>
    {
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
        if (item.IsTest) return;
        switch (item.Kind)
        {
            case FeedKind.StreamOnline:
                OnLive(item.Platform, live: true);
                return;
            case FeedKind.StreamOffline:
                OnLive(item.Platform, live: false);
                return;
            case FeedKind.Follow:
                Apply(item.User, item.Platform, "Follow", v => v.Follow);
                return;
            case FeedKind.Raid:
                var viewers = ExtrasText.FirstNumber(item.Title) ?? 0;
                Apply(item.User, item.Platform, "Raid", v => v.RaidEach + viewers * v.RaidPerViewer);
                return;
            case FeedKind.Redemption:
                var marker = item.Title.IndexOf(" redeemed ", StringComparison.OrdinalIgnoreCase);
                var reward = (marker >= 0 ? item.Title[(marker + " redeemed ".Length)..] : item.Title).Trim();
                Apply(item.User, item.Platform, reward, v => RewardValue(v, reward));
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
        Apply(a.GetString("user"), platform, a.GetString("label"), v => Worth(v, unit, quantity, amount, platform, tier));
    }

    /// <summary>What a purchase is worth to one timer or goal.</summary>
    public static double Worth(EventValues v, SpendUnit unit, double quantity, double amount, string platform, string tier) => unit switch
    {
        SpendUnit.Money => amount * v.PerCurrencyUnit,
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

    private void Apply(string user, string platform, string reason, Func<EventValues, double> worth)
    {
        var changed = false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var t in Data.Timers)
            {
                var accepts = t.State == TimerState.Running || t.State == TimerState.Paused && t.AddWhilePaused;
                var seconds = worth(t.Values) * t.Multiplier;
                if (accepts && seconds > 0 && double.IsFinite(seconds) && AddCore(t, now, seconds, user, platform, reason) > 0) changed = true;
            }
            foreach (var g in Data.Goals)
            {
                var points = worth(g.Values) * g.Multiplier;
                if (points > 0 && double.IsFinite(points) && AddPointsCore(g, points, user, platform, reason) != 0) changed = true;
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
        _log.Info("Subathon", $"'{t.Name}' ended after {Format(t.ElapsedSeconds)}");
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
        ["formattedElapsed"] = Format(t.ElapsedSeconds),
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

    /// <summary>Seconds as H:MM:SS (hours can pass 24, like 26:03:10).</summary>
    public static string Format(double seconds)
    {
        var total = (long)Math.Ceiling(Math.Max(0, seconds) - 0.0005);
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

    private bool Remove<T>(List<T> list, Predicate<T> match)
    {
        int removed;
        lock (_gate)
        {
            removed = list.RemoveAll(match);
            if (removed > 0) Save();
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

    public void FlushFile() => _file?.Flush();

    public void Dispose()
    {
        if (_chat is not null) _chat.FeedItemAdded -= Record;
        _subscription.Dispose();
        _file?.Dispose();
    }
}
