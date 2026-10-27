namespace Cuebot.Kick;

/// <summary>Timeouts and delays; tests use much shorter values.</summary>
internal sealed record KickTimings(
    TimeSpan ConnectTimeout,
    TimeSpan EstablishTimeout,
    TimeSpan PongTimeout,
    TimeSpan MaxActivityTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan MinimumPoll,
    TimeSpan DedupeWindow,
    TimeSpan SignInTimeout)
{
    public static KickTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(15),
        EstablishTimeout: TimeSpan.FromSeconds(15),
        PongTimeout: TimeSpan.FromSeconds(30),
        MaxActivityTimeout: TimeSpan.FromSeconds(120),
        ReconnectDelay: TimeSpan.FromSeconds(1),
        MaxReconnectDelay: TimeSpan.FromSeconds(60),
        MinimumPoll: TimeSpan.FromSeconds(15),
        DedupeWindow: TimeSpan.FromMinutes(2),
        SignInTimeout: TimeSpan.FromMinutes(5));
}
