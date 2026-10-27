using Cuebot.App.Platform;
using Cuebot.App.Shell;
using Cuebot.Core;

namespace Cuebot.UiTests;

public class StartupWatchdogTests
{
    [Fact]
    public async Task Fires_when_the_ui_never_becomes_ready()
    {
        var fired = 0;
        var dog = new StartupWatchdog(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref fired));
        await dog.Watch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, fired);
        Assert.False(dog.UiReady);
    }

    [Fact]
    public async Task Stays_quiet_when_the_ui_is_ready_in_time()
    {
        var ready = new TaskCompletionSource();
        var fired = false;
        var dog = new StartupWatchdog(ready.Task, TimeSpan.FromSeconds(10), () => fired = true);
        ready.SetResult();
        await dog.Watch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(fired);
        Assert.True(dog.UiReady);
    }

    [Fact]
    public void Shell_context_signals_ui_ready_once()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "cuebot-watchdog-" + Guid.NewGuid().ToString("N")));
        var ctx = new ShellContext { Options = new StartupOptions(), Paths = paths, Settings = new AppSettingsStore(paths) };
        Assert.False(ctx.UiReady.IsCompleted);
        ctx.MarkUiReady();
        ctx.MarkUiReady();
        Assert.True(ctx.UiReady.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("", 20)]
    [InlineData("abc", 20)]
    [InlineData("-3", 20)]
    [InlineData("0", 20)]
    [InlineData("5", 5)]
    [InlineData("2.5", 2.5)]
    public void Timeout_comes_from_the_environment(string? value, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), StartupWatchdog.TimeoutFrom(k => k == StartupWatchdog.TimeoutVariable ? value : null));
}
