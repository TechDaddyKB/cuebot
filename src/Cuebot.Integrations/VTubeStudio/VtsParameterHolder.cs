namespace Cuebot.Integrations.VTubeStudio;

/// <summary>
/// Parameters Cuebot keeps at a value ("Set parameter"): VTube Studio forgets injected values after about a second,
/// so they are re-sent together twice a second until they are stopped, run out or the connection ends.
/// </summary>
internal sealed class VtsParameterHolder(VTubeStudioService vts)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _held = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loop;

    private sealed record Entry(VtsParameterValue Value, DateTimeOffset? Until);

    public int Count
    {
        get { lock (_gate) return _held.Count; }
    }

    /// <summary>Sets the value now (so errors such as an unknown parameter surface here), then keeps it.</summary>
    public async Task SetAsync(string name, double value, double? weight, TimeSpan? duration, CancellationToken ct)
    {
        var entry = new Entry(new VtsParameterValue(name, value, weight), duration is { } d ? DateTimeOffset.UtcNow + d : null);
        await vts.SendHeldParametersAsync([entry.Value], ct).ConfigureAwait(false);
        lock (_gate)
        {
            _held[name] = entry;
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                _ = RunAsync(_loop.Token);
            }
        }
    }

    public int Stop(string name)
    {
        lock (_gate)
        {
            if (!_held.Remove(name)) return 0;
            vts.ReleaseHeldParameter(name);
            StopLoopIfIdle();
            return 1;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            var count = _held.Count;
            foreach (var name in _held.Keys) vts.ReleaseHeldParameter(name);
            _held.Clear();
            StopLoopIfIdle();
            return count;
        }
    }

    private void StopLoopIfIdle()
    {
        if (_held.Count > 0 || _loop is null) return;
        _loop.Cancel();
        _loop.Dispose();
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            VtsParameterValue[] values;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var expired in _held.Where(h => h.Value.Until <= now).Select(h => h.Key).ToArray())
                {
                    _held.Remove(expired);
                    vts.ReleaseHeldParameter(expired);
                }
                values = _held.Values.Select(e => e.Value).ToArray();
                if (values.Length == 0)
                {
                    StopLoopIfIdle();
                    return;
                }
            }
            try
            {
                await vts.SendHeldParametersAsync(values, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A parameter vanished (model changed) or the connection is ending: the next round tries again.
            }
        }
    }
}
