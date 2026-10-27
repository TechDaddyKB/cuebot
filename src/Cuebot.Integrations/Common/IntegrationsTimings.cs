namespace Cuebot.Integrations;

/// <summary>Timeouts and intervals of the integrations; tests use short ones.</summary>
public sealed record IntegrationsTimings(
    TimeSpan ConnectTimeout,
    TimeSpan RequestTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan AuthApprovalTimeout,
    TimeSpan HeartbeatInterval,
    TimeSpan InjectInterval,
    TimeSpan? KeepAliveInterval = null)
{
    /// <summary>Crowd Control PubSub: ping after 8 minutes (the documented limit is 9 minutes of inactivity).</summary>
    public TimeSpan PubSubPingInterval => KeepAliveInterval ?? TimeSpan.FromMinutes(8);

    public static IntegrationsTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(5),
        RequestTimeout: TimeSpan.FromSeconds(10),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(30),
        // VTube Studio shows an "allow plugin?" popup; give the streamer time to click it.
        AuthApprovalTimeout: TimeSpan.FromMinutes(2),
        // HypeRate (Phoenix) closes the socket without a heartbeat about every 10 s... we send one every 10 s.
        HeartbeatInterval: TimeSpan.FromSeconds(10),
        // VTube Studio drops injected parameter values after ~1 s without a new injection.
        InjectInterval: TimeSpan.FromMilliseconds(500));
}
