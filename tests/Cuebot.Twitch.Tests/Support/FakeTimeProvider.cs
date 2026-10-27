namespace Cuebot.Twitch.Tests.Support;

/// <summary>
/// Controllable clock. One-shot timers (Task.Delay) complete immediately and advance the clock by their due time,
/// recording it; periodic timers never fire.
/// </summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;
    private readonly List<TimeSpan> _delays = [];

    public FakeTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public IReadOnlyList<TimeSpan> Delays
    {
        get { lock (_gate) return _delays.ToArray(); }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _now += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period == Timeout.InfiniteTimeSpan && dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_gate)
            {
                _delays.Add(dueTime);
                _now += dueTime;
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        return new NeverTimer();
    }

    private sealed class NeverTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
