namespace Cuebot.UiTests;

/// <summary>Event names a service repeats because it does not reference the project that defines them.</summary>
public sealed class CrossServiceEventNameTests
{
    [Fact]
    public void YouTube_listens_for_OBS_stream_started_under_its_real_name() =>
        Assert.Equal(Cuebot.Obs.ObsEvents.StreamStarted, Cuebot.YouTube.YouTubeService.ObsStreamStarted);
}
