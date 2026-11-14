namespace Cuebot.Core.Chat;

/// <summary>
/// A platform module's own view of whether the channel is live right now, so features that follow stream online/offline
/// events (points' "Only while live") still notice a stream that was already live when Cuebot started.
/// </summary>
public interface IStreamLiveStatus
{
    /// <summary>The platform name, as on its chat events (for example "Twitch").</summary>
    string Platform { get; }

    /// <summary>True when live, false when offline, null when not known yet (not connected, not checked).</summary>
    bool? IsLive { get; }
}
