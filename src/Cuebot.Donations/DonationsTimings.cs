namespace Cuebot.Donations;

/// <summary>Timeouts and delays; tests use much shorter values.</summary>
internal sealed record DonationsTimings(
    TimeSpan ConnectTimeout,
    TimeSpan OpenTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan KeepAlive,
    TimeSpan RateLimitDelay = default,
    TimeSpan TipStateWait = default)
{
    /// <summary>First wait after Astro's <c>rate_limit_exceeded</c> (doubles per retry); the reconnect delay when unset.</summary>
    public TimeSpan RateLimitBackoff => RateLimitDelay > TimeSpan.Zero ? RateLimitDelay : ReconnectDelay;

    /// <summary>How long a StreamElements tip of unknown moderation state waits for its <c>channel.tips</c> document.</summary>
    public TimeSpan TipStateTimeout => TipStateWait > TimeSpan.Zero ? TipStateWait : ReconnectDelay;

    public static DonationsTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(15),
        OpenTimeout: TimeSpan.FromSeconds(15),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(60),
        KeepAlive: TimeSpan.FromSeconds(30),
        RateLimitDelay: TimeSpan.FromSeconds(5),
        TipStateWait: TimeSpan.FromSeconds(5));
}
