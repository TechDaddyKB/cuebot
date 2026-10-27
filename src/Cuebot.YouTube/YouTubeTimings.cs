namespace Cuebot.YouTube;

/// <summary>Bounds of the polling loops; tests shorten them.</summary>
internal sealed record YouTubeTimings(
    TimeSpan MinimumChatPoll,
    TimeSpan MinimumBroadcastPoll,
    TimeSpan MinimumLivePoll,
    TimeSpan MinimumSubscriberPoll,
    TimeSpan ErrorBackoff,
    TimeSpan MaximumErrorBackoff,
    TimeSpan TokenRefreshMargin)
{
    public static YouTubeTimings Default { get; } = new(
        MinimumChatPoll: TimeSpan.FromSeconds(2),
        MinimumBroadcastPoll: TimeSpan.FromSeconds(30),
        MinimumLivePoll: TimeSpan.FromSeconds(15),
        MinimumSubscriberPoll: TimeSpan.FromSeconds(60),
        ErrorBackoff: TimeSpan.FromSeconds(5),
        MaximumErrorBackoff: TimeSpan.FromMinutes(5),
        TokenRefreshMargin: TimeSpan.FromSeconds(60));

    /// <summary>The configured seconds, raised to <paramref name="minimum"/>.</summary>
    public static TimeSpan Clamp(int seconds, TimeSpan minimum)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return configured > minimum ? configured : minimum;
    }

    /// <summary>
    /// Delay before the next chat poll: YouTube's suggested interval, but never below the configured minimum (quota)
    /// nor the hard minimum.
    /// </summary>
    public TimeSpan ChatDelay(TimeSpan suggested, int minimumSeconds)
    {
        var floor = Clamp(minimumSeconds, MinimumChatPoll);
        return suggested > floor ? suggested : floor;
    }

    /// <summary>Exponential backoff after an error, starting at <see cref="ErrorBackoff"/>.</summary>
    public TimeSpan NextBackoff(TimeSpan current)
    {
        if (current <= TimeSpan.Zero) return ErrorBackoff;
        var doubled = current * 2;
        return doubled > MaximumErrorBackoff ? MaximumErrorBackoff : doubled;
    }
}
