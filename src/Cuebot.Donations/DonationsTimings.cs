namespace Cuebot.Donations;

/// <summary>Timeouts and delays; tests use much shorter values.</summary>
internal sealed record DonationsTimings(
    TimeSpan ConnectTimeout,
    TimeSpan OpenTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan KeepAlive)
{
    public static DonationsTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(15),
        OpenTimeout: TimeSpan.FromSeconds(15),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(60),
        KeepAlive: TimeSpan.FromSeconds(30));
}
