using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.Tests;

public sealed class AutoConnectRetryTests
{
    private static readonly TimeSpan[] Fast = [TimeSpan.FromMilliseconds(1)];
    private readonly AppLog _log = new();

    [Fact]
    public async Task A_failed_first_attempt_is_retried_in_the_background_until_it_succeeds()
    {
        using var retry = new AutoConnectRetry("Test", _log, TimeProvider.System, Fast);
        var attempts = 0;

        await retry.StartAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) < 3) throw new HttpRequestException("offline");
            return Task.CompletedTask;
        });
        await retry.Retrying!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, attempts);
        Assert.Single(_log.Recent(), e => e.Level == LogLevel.Error);   // later failures don't flood the log
        Assert.Contains(_log.Recent(), e => e.Message == "Connected after 3 attempts");
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried()
    {
        using var retry = new AutoConnectRetry("Test", _log, TimeProvider.System, Fast);
        var attempts = 0;

        await retry.StartAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException("Sign in first.");
        }, ex => ex is InvalidOperationException);

        Assert.Equal(1, attempts);
        Assert.Null(retry.Retrying);
    }

    [Fact]
    public async Task Cancel_stops_the_retries()
    {
        using var retry = new AutoConnectRetry("Test", _log, TimeProvider.System, [TimeSpan.FromMilliseconds(20)]);
        var attempts = 0;

        await retry.StartAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("offline");
        });
        retry.Cancel();
        await retry.Retrying!.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(60);

        Assert.True(attempts <= 2, $"{attempts} attempts after cancelling");
    }
}
