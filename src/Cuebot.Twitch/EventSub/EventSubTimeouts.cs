namespace Cuebot.Twitch.EventSub;

/// <summary>Timing policy of the EventSub client; tests shorten these.</summary>
internal sealed record EventSubTimeouts
{
    public static EventSubTimeouts Default { get; } = new();

    /// <summary>How long to wait for the welcome message after connecting.</summary>
    public TimeSpan Welcome { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Grace added to the server's keepalive_timeout_seconds before the connection counts as dead.</summary>
    public TimeSpan KeepaliveSlack { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Exponential backoff: 1s, 2s, 4s ... capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay(int consecutiveFailures) =>
        TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, InitialRetryDelay.Ticks << Math.Min(consecutiveFailures, 20)));
}
