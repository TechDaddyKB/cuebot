namespace Cuebot.Core.Chat;

/// <summary>
/// Answers "is the streamer live?" for the "Only while live" option on timers, commands and triggers, from every
/// platform's <see cref="IStreamLiveStatus"/>. A platform that has not reported yet (null) counts as not live.
/// </summary>
public sealed class StreamLiveState(Func<IEnumerable<IStreamLiveStatus>>? sources = null)
{
    /// <summary>The platforms the option can be narrowed to, as they name themselves.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["Twitch", "YouTube", "Kick", "Rumble"];

    // Resolved on first use, so that only profiles using the option touch the platform services.
    private readonly Lazy<IStreamLiveStatus[]> _sources = new(() => sources?.Invoke().ToArray() ?? []);

    /// <summary>True when any platform is live; with <paramref name="platforms"/>, any of those.</summary>
    public bool IsLive(IReadOnlyCollection<string>? platforms = null) =>
        _sources.Value.Any(s => s.IsLive == true &&
            (platforms is not { Count: > 0 } || platforms.Contains(s.Platform, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Whether something with this "Only while live" setting may run now.</summary>
    public bool Allows(bool onlyWhileLive, IReadOnlyCollection<string>? platforms) => !onlyWhileLive || IsLive(platforms);
}
