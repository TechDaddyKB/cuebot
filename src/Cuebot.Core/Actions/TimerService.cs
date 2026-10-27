using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Actions;

/// <summary>Core service that needs a periodic tick (about once a second), driven by <see cref="TimerService"/>.</summary>
public interface IPeriodicService
{
    void Tick(DateTimeOffset now);
}

/// <summary>Effective runtime state of a timer (profile values plus changes made by sub-actions).</summary>
public sealed record TimerState(Guid Id, string Name, bool Enabled, int IntervalSeconds, bool Repeat,
    DateTimeOffset? NextFire, int ChatLines);

/// <summary>
/// Publishes "core.timer" events for each enabled timer: fixed interval, random interval or cron schedule, with
/// optional minimum chat lines and one-shot (non-repeating) timers. "Set timer state" changes are runtime
/// overrides (not saved to actions.json) and are dropped when the timer is edited in the profile.
/// Also ticks registered <see cref="IPeriodicService"/>s.
/// </summary>
public sealed class TimerService : IDisposable
{
    public const string EventType = "core.timer";

    private readonly ProfileService _profile;
    private readonly EventBus _bus;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<IPeriodicService> _periodic;
    private readonly AppLog? _log;
    private readonly Random _random;
    private readonly Dictionary<Guid, Runtime> _state = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _subscription;
    private Task? _loop;
    private bool _disposed;

    public TimerService(ProfileService profile, EventBus bus, TimeProvider? time = null,
        IEnumerable<IPeriodicService>? periodic = null, AppLog? log = null, Random? random = null)
    {
        _profile = profile;
        _bus = bus;
        _time = time ?? TimeProvider.System;
        _periodic = periodic?.ToArray() ?? [];
        _log = log;
        _random = random ?? new Random();
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>Raised when a timer's runtime state changes: through <see cref="SetState"/>, or when it fires and re-arms.</summary>
    public event Action<Guid>? StateChanged;

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Fires due timers and ticks periodic services. Separate from the loop so it can be tested deterministically.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var due = new List<BotEvent>();
        var fired = new List<Guid>();
        lock (_gate)
        {
            var timers = _profile.Snapshot.Timers;
            foreach (var stale in _state.Keys.Where(id => timers.All(t => t.Id != id)).ToArray()) _state.Remove(stale);
            foreach (var timer in timers)
            {
                if (Evaluate(timer, now) is not { } e) continue;
                due.Add(e);
                fired.Add(timer.Id);
            }
        }
        foreach (var e in due) _bus.Publish(e);
        foreach (var id in fired) StateChanged?.Invoke(id);

        foreach (var service in _periodic)
        {
            try { service.Tick(now); }
            catch (Exception ex) { _log?.Error("Timers", $"{service.GetType().Name} tick failed", ex); }
        }
    }

    /// <summary>Find a timer by id (as text) or name, case-insensitively.</summary>
    public TimerDefinition? FindTimer(string idOrName)
    {
        idOrName = idOrName.Trim();
        var timers = _profile.Snapshot.Timers;
        return (Guid.TryParse(idOrName, out var id) ? timers.FirstOrDefault(t => t.Id == id) : null)
               ?? timers.FirstOrDefault(t => string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public TimerState? GetState(string idOrName)
    {
        var timer = FindTimer(idOrName);
        if (timer is null) return null;
        lock (_gate)
        {
            var rt = RuntimeFor(timer);
            return new TimerState(timer.Id, timer.Name, rt.EnabledOverride ?? timer.Enabled,
                rt.IntervalOverride ?? timer.IntervalSeconds, timer.Repeat, rt.Done ? null : rt.Due, rt.Lines);
        }
    }

    public IReadOnlyList<TimerState> States() =>
        _profile.Snapshot.Timers.Select(t => GetState(t.Id.ToString())).OfType<TimerState>().ToArray();

    /// <summary>
    /// Enables/disables a timer and/or changes its interval (null leaves a value unchanged). Enabling, or changing
    /// the interval, re-arms the timer from now. Returns false when there is no such timer.
    /// </summary>
    public bool SetState(string idOrName, bool? enabled, int? intervalSeconds = null)
    {
        var timer = FindTimer(idOrName);
        if (timer is null) return false;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var rt = RuntimeFor(timer);
            var wasEnabled = rt.EnabledOverride ?? timer.Enabled;
            if (enabled is { } en) rt.EnabledOverride = en;
            if (intervalSeconds is { } iv) rt.IntervalOverride = Math.Max(0, iv);
            var isEnabled = rt.EnabledOverride ?? timer.Enabled;
            if (isEnabled && (!wasEnabled || intervalSeconds is not null))
            {
                rt.Done = false;
                Arm(timer, rt, now);
            }
            else if (!isEnabled)
            {
                rt.Due = null;
                rt.Done = false;
            }
        }
        StateChanged?.Invoke(timer.Id);
        return true;
    }

    private BotEvent? Evaluate(TimerDefinition timer, DateTimeOffset now)
    {
        var rt = RuntimeFor(timer);
        var enabled = rt.EnabledOverride ?? timer.Enabled;
        if (!enabled)
        {
            rt.Due = null;
            rt.Done = false;
            return null;
        }
        if (rt.Done) return null;
        if (rt.Due is null)
        {
            Arm(timer, rt, now);
            return null;
        }
        if (now < rt.Due) return null;

        var lines = rt.Lines;
        if (timer.MinChatLines > 0 && lines < timer.MinChatLines)
        {
            Arm(timer, rt, now, resetLines: false); // skip this cycle; keep counting
            return null;
        }

        rt.Lines = 0;
        if (timer.Repeat)
        {
            Arm(timer, rt, now);
        }
        else
        {
            rt.Done = true;
            rt.Due = null;
        }
        var interval = rt.IntervalOverride ?? timer.IntervalSeconds;
        return new BotEvent(EventType, new Dictionary<string, object?>
        {
            ["timerName"] = timer.Name,
            ["timerId"] = timer.Id.ToString(),
            ["interval"] = interval,
            ["timerInterval"] = interval,
            ["timerCron"] = timer.Cron ?? "",
            ["chatLines"] = lines,
        });
    }

    private void Arm(TimerDefinition timer, Runtime rt, DateTimeOffset now, bool resetLines = true)
    {
        if (resetLines) rt.Lines = 0;
        rt.Due = NextDue(timer, rt, now);
    }

    private DateTimeOffset? NextDue(TimerDefinition timer, Runtime rt, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(timer.Cron))
        {
            if (rt.Cron is null || rt.Cron.Text != timer.Cron.Trim())
            {
                if (!CronExpression.TryParse(timer.Cron, out var cron, out var error))
                {
                    if (!rt.CronErrorLogged) _log?.Warn("Timers", $"Timer '{timer.Name}': {error}");
                    rt.CronErrorLogged = true;
                    return null;
                }
                rt.Cron = cron;
            }
            return rt.Cron.Next(now, _time.LocalTimeZone);
        }
        if (timer.RandomInterval)
        {
            var min = Math.Max(1, Math.Min(timer.MinIntervalSeconds, timer.MaxIntervalSeconds));
            var max = Math.Max(min, Math.Max(timer.MinIntervalSeconds, timer.MaxIntervalSeconds));
            return now + TimeSpan.FromSeconds(_random.Next(min, max + 1));
        }
        var interval = rt.IntervalOverride ?? timer.IntervalSeconds;
        return interval > 0 ? now + TimeSpan.FromSeconds(interval) : null;
    }

    /// <summary>Runtime entry for a timer; resets it when the timer's definition changed in the profile. Caller holds the lock.</summary>
    private Runtime RuntimeFor(TimerDefinition timer)
    {
        var signature = Signature(timer);
        if (_state.TryGetValue(timer.Id, out var rt) && rt.Signature == signature) return rt;
        var lines = rt?.Lines ?? 0;
        rt = new Runtime { Signature = signature, Lines = lines };
        _state[timer.Id] = rt;
        return rt;
    }

    private static string Signature(TimerDefinition t) =>
        FormattableString.Invariant(
            $"{t.Enabled}|{t.IntervalSeconds}|{t.Repeat}|{t.RandomInterval}|{t.MinIntervalSeconds}|{t.MaxIntervalSeconds}|{t.Cron}");

    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.MessageEvent) return;
        lock (_gate)
        {
            foreach (var rt in _state.Values) rt.Lines++;
        }
    }

    private async Task LoopAsync()
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await ticker.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try { Tick(); }
                catch (Exception ex) { _log?.Error("Timers", "Timer tick failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed class Runtime
    {
        public string Signature = "";
        public DateTimeOffset? Due;
        public int Lines;
        public bool Done;
        public bool? EnabledOverride;
        public int? IntervalOverride;
        public CronExpression? Cron;
        public bool CronErrorLogged;
    }
}
