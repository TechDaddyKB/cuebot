namespace Cuebot.Kick.Events;

/// <summary>
/// Something that delivers Kick events: the real-time websocket (<c>KickPusherEventSource</c>, works today) or
/// the webhook relay (<c>KickWebhookEventSource</c>, fed by the user-hosted relay later). The service merges all
/// sources and de-duplicates them with <see cref="KickEvent.DedupeKey"/>.
/// </summary>
public interface IKickEventSource
{
    /// <summary>Short name for status and logs, e.g. "Realtime chat".</summary>
    string Name { get; }

    /// <summary>True while events are flowing.</summary>
    bool IsConnected { get; }

    /// <summary>Raised from a background thread for every decoded event.</summary>
    event Action<KickEvent>? EventReceived;

    /// <summary>Raised when <see cref="IsConnected"/> changes; the string is a human-readable reason.</summary>
    event Action<bool, string>? ConnectionChanged;
}
