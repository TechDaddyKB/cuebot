namespace Cuebot.Rumble;

/// <summary>Intervals and limits of the Rumble connection; tests shorten them.</summary>
internal sealed record RumbleTimings(
    TimeSpan MinimumPollInterval,
    TimeSpan MaximumPollBackoff,
    TimeSpan ChatReconnectDelay,
    TimeSpan ChatMaximumReconnectDelay,
    TimeSpan ChatIdleTimeout,
    TimeSpan SendCooldown)
{
    /// <summary>
    /// Rumble asks for at most one Live Stream API request every 5 seconds. The chat stream sends keep-alives;
    /// 90 seconds of silence means a dead connection. Chat rejects messages sent less than 3 seconds apart.
    /// </summary>
    public static RumbleTimings Default { get; } = new(
        MinimumPollInterval: TimeSpan.FromSeconds(5),
        MaximumPollBackoff: TimeSpan.FromMinutes(2),
        ChatReconnectDelay: TimeSpan.FromSeconds(2),
        ChatMaximumReconnectDelay: TimeSpan.FromSeconds(30),
        ChatIdleTimeout: TimeSpan.FromSeconds(90),
        SendCooldown: TimeSpan.FromSeconds(3));

    /// <summary>The configured poll interval, raised to <see cref="MinimumPollInterval"/>.</summary>
    public TimeSpan PollInterval(int pollSeconds)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(pollSeconds, 0));
        return configured > MinimumPollInterval ? configured : MinimumPollInterval;
    }
}
