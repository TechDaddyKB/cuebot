using Cuebot.Core.Chat;

namespace Cuebot.Tests.Extras;

public sealed class StreamStartDetectorTests
{
    private readonly Cuebot.Tests.Commands.TestClock _clock = new();

    [Fact]
    public void Only_the_first_platform_after_a_real_break_starts_a_stream()
    {
        var d = new StreamStartDetector(_clock);
        Assert.True(d.IsNewStream("StreamOnline", "twitch"));
        Assert.False(d.IsNewStream("StreamOnline", "youtube"));     // joining a running stream
        Assert.False(d.IsNewStream("Follow", "twitch"));

        Assert.False(d.IsNewStream("StreamOffline", "twitch"));
        Assert.False(d.IsNewStream("StreamOffline", "youtube"));
        _clock.Advance(StreamStartDetector.DefaultGrace.TotalSeconds - 1);
        Assert.False(d.IsNewStream("StreamOnline", "twitch"));      // back within the grace time

        Assert.False(d.IsNewStream("StreamOffline", "twitch"));
        _clock.Advance(StreamStartDetector.DefaultGrace.TotalSeconds);
        Assert.True(d.IsNewStream("StreamOnline", "kick"));
    }

    [Fact]
    public void A_platform_that_never_said_offline_does_not_block_tomorrows_stream()
    {
        var d = new StreamStartDetector(_clock);
        Assert.True(d.IsNewStream("StreamOnline", "youtube"));
        _clock.Advance(StreamStartDetector.MaxStreamLength.TotalSeconds);
        Assert.True(d.IsNewStream("StreamOnline", "twitch"));
    }

    [Fact]
    public void Recent_activity_from_before_a_restart_counts_as_the_same_stream()
    {
        var d = new StreamStartDetector(_clock, lastLive: _clock.GetUtcNow().AddMinutes(-2));
        Assert.False(d.IsNewStream("StreamOnline", "youtube"));
    }
}
