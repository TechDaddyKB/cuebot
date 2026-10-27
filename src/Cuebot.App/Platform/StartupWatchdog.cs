namespace Cuebot.App.Platform;

/// <summary>
/// Catches a display backend that hangs instead of failing: unless <see cref="Task"/> (the UI-ready signal) completes
/// within the timeout, <c>onTimeout</c> runs once on a background thread. Used for automatically chosen Wayland, where
/// a compositor Avalonia cannot use sometimes never answers rather than throwing.
/// </summary>
internal sealed class StartupWatchdog
{
    /// <summary>Seconds to wait for the window (default 20); mainly for tests and slow machines.</summary>
    public const string TimeoutVariable = "CUEBOT_WAYLAND_TIMEOUT";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly Task _ready;
    private readonly Task _watch;

    public StartupWatchdog(Task ready, TimeSpan timeout, Action onTimeout)
    {
        _ready = ready;
        _watch = Task.Run(async () =>
        {
            if (await Task.WhenAny(ready, Task.Delay(timeout)).ConfigureAwait(false) != ready) onTimeout();
        });
    }

    /// <summary>Completes when the UI became ready or <c>onTimeout</c> has returned.</summary>
    public Task Watch => _watch;

    public bool UiReady => _ready.IsCompleted;

    public static TimeSpan TimeoutFrom(Func<string, string?> env) =>
        double.TryParse(env(TimeoutVariable), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;
}
