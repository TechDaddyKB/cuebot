namespace Cuebot.Speech;

/// <summary>An awaitable open/closed gate: <see cref="WaitAsync"/> completes immediately unless paused.</summary>
internal sealed class PauseGate
{
    private readonly object _gate = new();
    private TaskCompletionSource _resumed = CreateOpen();

    public bool IsPaused
    {
        get
        {
            lock (_gate) return !_resumed.Task.IsCompleted;
        }
    }

    /// <summary>Pauses or resumes; returns false when the state did not change.</summary>
    public bool Set(bool paused)
    {
        lock (_gate)
        {
            if (paused == !_resumed.Task.IsCompleted) return false;
            if (paused) _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else _resumed.TrySetResult();
            return true;
        }
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task resumed;
        lock (_gate) resumed = _resumed.Task;
        return resumed.WaitAsync(ct);
    }

    private static TaskCompletionSource CreateOpen()
    {
        var open = new TaskCompletionSource();
        open.SetResult();
        return open;
    }
}
