using Cuebot.Core.Events;

namespace Cuebot.Core.Actions;

/// <summary>Publishes "core.timer" events for each enabled timer at its interval.</summary>
public sealed class TimerService(ProfileService profile, EventBus bus, TimeProvider? time = null) : IDisposable
{
    public const string EventType = "core.timer";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _nextFire = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Fires due timers. Separate from the loop so it can be tested deterministically.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var timers = profile.Snapshot.Timers;
        foreach (var stale in _nextFire.Keys.Where(id => timers.All(t => t.Id != id)).ToArray()) _nextFire.Remove(stale);

        foreach (var timer in timers)
        {
            if (!timer.Enabled || timer.IntervalSeconds <= 0)
            {
                _nextFire.Remove(timer.Id);
                continue;
            }
            var interval = TimeSpan.FromSeconds(timer.IntervalSeconds);
            if (!_nextFire.TryGetValue(timer.Id, out var due))
            {
                _nextFire[timer.Id] = now + interval;
                continue;
            }
            if (now < due) continue;
            _nextFire[timer.Id] = now + interval;
            bus.Publish(new BotEvent(EventType, new Dictionary<string, object?>
            {
                ["timerName"] = timer.Name,
                ["timerId"] = timer.Id.ToString(),
                ["interval"] = timer.IntervalSeconds,
            }));
        }
    }

    private async Task LoopAsync()
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await ticker.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false)) Tick();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
