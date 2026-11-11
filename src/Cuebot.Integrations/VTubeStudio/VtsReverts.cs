using Cuebot.Core.Logging;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>
/// "For N seconds, then revert": one pending undo per target (an expression, the model, the tint, the visual effects,
/// an item, the model position). A second timed change of the same target restarts the timer but keeps the first
/// undo, so it goes back to how things were before either change. A change without a timer cancels the pending undo
/// (the latest explicit choice wins), and a lost connection drops them all (VTube Studio state is unknown then).
/// </summary>
internal sealed class VtsReverts(AppLog log, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Pending(Func<CancellationToken, Task> Undo, CancellationTokenSource Timer);

    public bool IsPending(string target)
    {
        lock (_gate) return _pending.ContainsKey(target);
    }

    /// <summary>
    /// Undoes the change of <paramref name="target"/> after <paramref name="after"/>. <paramref name="undo"/> is only used
    /// when nothing is pending for the target yet (it captured the state before the first change).
    /// </summary>
    public void Schedule(string target, TimeSpan after, Func<CancellationToken, Task> undo)
    {
        var timer = new CancellationTokenSource();
        Func<CancellationToken, Task> kept;
        lock (_gate)
        {
            if (_pending.TryGetValue(target, out var existing))
            {
                existing.Timer.Cancel();
                existing.Timer.Dispose();
                kept = existing.Undo;
            }
            else
            {
                kept = undo;
            }
            _pending[target] = new Pending(kept, timer);
        }
        _ = RunAsync(target, after, timer);
    }

    /// <summary>Forgets the pending undo of <paramref name="target"/> without running it.</summary>
    public void Cancel(string target)
    {
        lock (_gate)
        {
            if (!_pending.Remove(target, out var existing)) return;
            existing.Timer.Cancel();
            existing.Timer.Dispose();
        }
    }

    /// <summary>Forgets every pending undo whose target starts with <paramref name="prefix"/>.</summary>
    public void CancelAll(string prefix = "")
    {
        lock (_gate)
        {
            foreach (var key in _pending.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _pending.Remove(key, out var existing);
                existing!.Timer.Cancel();
                existing.Timer.Dispose();
            }
        }
    }

    /// <summary>How often, and how far apart, an undo is tried again while VTube Studio is busy (an open menu or window).</summary>
    internal int BusyAttempts { get; init; } = 15;
    internal TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the undo; while VTube Studio refuses because a menu or window is open (or a model is still loading), tries
    /// again every few seconds for about half a minute, so a timed hat is not left on the model.
    /// </summary>
    private async Task UndoWithRetriesAsync(Func<CancellationToken, Task> undo)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await undo(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (IntegrationRequestException ex) when (attempt < BusyAttempts && VtsProtocol.IsBusy(ex.ErrorId))
            {
                await Task.Delay(BusyWait, time).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(string target, TimeSpan after, CancellationTokenSource timer)
    {
        Func<CancellationToken, Task>? undo = null;
        try
        {
            await Task.Delay(after < TimeSpan.Zero ? TimeSpan.Zero : after, time, timer.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending.TryGetValue(target, out var current) && ReferenceEquals(current.Timer, timer))
                {
                    _pending.Remove(target);
                    undo = current.Undo;
                }
            }
            if (undo is not null) await UndoWithRetriesAsync(undo).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced or cancelled.
        }
        catch (ObjectDisposedException)
        {
            // Cancelled while starting.
        }
        catch (Exception ex)
        {
            log.Warn("VTube Studio", $"Could not undo the timed change ({target}): {ex.Message}");
        }
        finally
        {
            if (undo is not null) timer.Dispose();
        }
    }
}
