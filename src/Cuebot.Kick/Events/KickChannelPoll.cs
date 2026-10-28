using Cuebot.Kick.Api;

namespace Cuebot.Kick.Events;

/// <summary>
/// Turns successive official API channel snapshots into events: stream online/offline, viewer count changes
/// (while live) and title/category changes. The first snapshot only establishes the baseline.
/// </summary>
internal static class KickChannelPoll
{
    public const string Source = "api";

    public static IReadOnlyList<KickEvent> Diff(KickChannel? previous, KickChannel current)
    {
        if (previous is null) return [];
        var events = new List<KickEvent>();
        if (current.IsLive && !previous.IsLive)
            events.Add(KickEventFactory.StreamOnline(Source, current.Title, current.StartTime));
        else if (!current.IsLive && previous.IsLive)
            events.Add(KickEventFactory.StreamOffline(Source));

        if (current.Title != previous.Title || current.Category?.Id != previous.Category?.Id)
            events.Add(KickEventFactory.ChannelUpdate(Source, current.Title, current.Category?.Name ?? "", current.Category?.Id ?? 0,
                current.Language, current.IsMature, current.Category?.Thumbnail ?? ""));

        if (current.IsLive && current.ViewerCount != previous.ViewerCount)
            events.Add(KickEventFactory.ViewerCount(Source, current.ViewerCount));
        return events;
    }
}
