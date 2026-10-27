namespace Cuebot.Rumble.Tests.Support;

internal static class TestTimings
{
    /// <summary>Real-time but fast: tests poll every 50 ms and reconnect almost immediately.</summary>
    public static RumbleTimings Fast { get; } = new(
        MinimumPollInterval: TimeSpan.FromMilliseconds(50),
        MaximumPollBackoff: TimeSpan.FromMilliseconds(200),
        ChatReconnectDelay: TimeSpan.FromMilliseconds(20),
        ChatMaximumReconnectDelay: TimeSpan.FromMilliseconds(100),
        ChatIdleTimeout: TimeSpan.FromSeconds(5),
        SendCooldown: TimeSpan.Zero);
}
