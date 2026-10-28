using Cuebot.WaveLink.Model;

namespace Cuebot.WaveLink;

/// <summary>
/// Coalesces Wave Link's level meter stream (tens of messages a second) into at most <c>rate</c> flushes per second.
/// Every reading replaces the previous one for the same meter; a flush hands over the meters that moved by at least
/// <see cref="MinimumChange"/> points since they were last flushed (silence stays silent: no flush, no event). The first
/// reading after a quiet period flushes at once; later ones wait for the next slot (leading + trailing edge).
/// </summary>
internal sealed class LevelMeterThrottle : IDisposable
{
    public const double MinimumChange = 1.0;

    private readonly TimeProvider _time;
    private readonly Func<double> _rate;
    private readonly Action<IReadOnlyList<WaveLinkLevel>> _flush;
    private readonly object _gate = new();
    private readonly Dictionary<string, WaveLinkLevel> _latest = [];
    private readonly Dictionary<string, WaveLinkLevel> _sent = [];
    private ITimer? _timer;
    private long _lastFlush = long.MinValue;
    private bool _disposed;

    public LevelMeterThrottle(TimeProvider time, Func<double> rate, Action<IReadOnlyList<WaveLinkLevel>> flush)
    {
        _time = time;
        _rate = rate;
        _flush = flush;
    }

    /// <summary>Flushes handed over so far (for diagnostics and tests).</summary>
    public int FlushCount { get; private set; }

    private TimeSpan Interval => TimeSpan.FromSeconds(1 / Math.Clamp(_rate(), 1, 30));

    public void Offer(IReadOnlyList<WaveLinkLevel> levels)
    {
        if (levels.Count == 0) return;
        TimeSpan wait;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var level in levels) _latest[level.Key] = level;
            if (_timer is not null) return;   // a flush is already scheduled
            var now = _time.GetTimestamp();
            var since = _lastFlush == long.MinValue ? TimeSpan.MaxValue : _time.GetElapsedTime(_lastFlush, now);
            wait = since >= Interval ? TimeSpan.Zero : Interval - since;
            if (wait > TimeSpan.Zero)
            {
                _timer = _time.CreateTimer(_ => Flush(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        Flush();
    }

    /// <summary>Forgets the meters (on disconnect) so the next session starts fresh.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _latest.Clear();
            _sent.Clear();
            _lastFlush = long.MinValue;
        }
    }

    private void Flush()
    {
        List<WaveLinkLevel> moved;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed) return;
            moved = [];
            foreach (var (key, level) in _latest)
            {
                if (_sent.TryGetValue(key, out var previous)
                    && Math.Abs(previous.Left - level.Left) < MinimumChange && Math.Abs(previous.Right - level.Right) < MinimumChange) continue;
                _sent[key] = level;
                moved.Add(level);
            }
            _latest.Clear();
            if (moved.Count == 0) return;
            _lastFlush = _time.GetTimestamp();
            FlushCount++;
        }
        _flush(moved);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
