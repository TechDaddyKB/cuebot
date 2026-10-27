using System.Text.Json;
using Cuebot.Obs;
using Cuebot.Twitch;

namespace Cuebot.LiveTests;

/// <summary>
/// End-to-end tests against the real Twitch API, real streaming platforms and a real OBS: everything goes
/// through Cuebot's modules and action engine exactly as in the app. Opt-in only; see docs/LIVE_TESTING.md.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class LiveStreamingTests(LiveBot bot)
{
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamEventTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);

    [LiveTwitchFact]
    public async Task Twitch_chat_messages_round_trip_and_commands_run_actions()
    {
        var since = DateTimeOffset.Now;

        await bot.Twitch.SendChatMessageAsync("dev testing: Cuebot live test connected (automated, please ignore)");
        var echo = await bot.WaitForChatAsync("Cuebot live test connected", since, ChatTimeout);
        Assert.Equal(bot.Twitch.Settings.UserId, echo.Args["userId"]);
        Assert.Equal(true, echo.Args["isBroadcaster"]);

        await bot.Twitch.SendChatMessageAsync(LiveBot.TestCommand);
        await bot.WaitForChatAsync("Cuebot received !cuebottest", since, ChatTimeout);
    }

    /// <summary>
    /// Streams to Twitch and checks both sides: OBS output events and Twitch's EventSub stream.online /
    /// stream.offline, plus the stream-online trigger running its action.
    /// </summary>
    [LiveTwitchStreamFact]
    public async Task Twitch_going_live_fires_obs_and_eventsub_stream_events()
    {
        await using var target = await bot.UseStreamTargetAsync(LiveEnvironment.Target("twitch")!);
        var since = DateTimeOffset.Now;

        await StartAsync(since);
        await bot.WaitForEventAsync(TwitchEventTypes.StreamOnline, since, StreamEventTimeout);
        await bot.WaitForChatAsync("saw stream.online", since, ChatTimeout);
        await AssertHealthyUntilAsync(since + LiveEnvironment.StreamDuration);

        var stopping = await StopAsync();
        await bot.WaitForEventAsync(TwitchEventTypes.StreamOffline, stopping, StreamEventTimeout);
    }

    [LivePlatformStreamFact("youtube")]
    public Task YouTube_accepts_a_stream_started_and_stopped_by_cuebot() => StreamToAsync("youtube");

    [LivePlatformStreamFact("kick")]
    public Task Kick_accepts_a_stream_started_and_stopped_by_cuebot() => StreamToAsync("kick");

    /// <summary>
    /// Platform-agnostic check: the ingest accepts the stream (OBS connects, keeps sending, never reconnects,
    /// drops no frames) for the configured duration. These platforms have no Cuebot module yet.
    /// </summary>
    private async Task StreamToAsync(string platform)
    {
        await using var target = await bot.UseStreamTargetAsync(LiveEnvironment.Target(platform)!);
        var since = DateTimeOffset.Now;
        await StartAsync(since);
        await AssertHealthyUntilAsync(since + LiveEnvironment.StreamDuration);
        await StopAsync();
    }

    private async Task StartAsync(DateTimeOffset since)
    {
        await bot.Obs.SetCurrentSceneAsync(LiveEnvironment.StreamScene!);
        await bot.Obs.ControlStreamAsync(OutputCommand.Start);
        await bot.WaitForEventAsync(ObsEvents.StreamStarted, since, TimeSpan.FromSeconds(30));
    }

    private async Task<DateTimeOffset> StopAsync()
    {
        var stopping = DateTimeOffset.Now;
        await bot.Obs.ControlStreamAsync(OutputCommand.Stop);
        await bot.WaitForEventAsync(ObsEvents.StreamStopped, stopping, TimeSpan.FromSeconds(30));
        return stopping;
    }

    /// <summary>Samples OBS's output until <paramref name="until"/>: live, progressing, no reconnects or drops.</summary>
    private async Task AssertHealthyUntilAsync(DateTimeOffset until)
    {
        long lastBytes = -1;
        do
        {
            await Task.Delay(HealthInterval);
            var status = await bot.Obs.RequestAsync("GetStreamStatus");
            Assert.True(status.GetProperty("outputActive").GetBoolean(), "stream dropped");
            Assert.False(status.GetProperty("outputReconnecting").GetBoolean(), "OBS is reconnecting to the ingest");
            Assert.Equal(0, status.GetProperty("outputSkippedFrames").GetInt32());
            var bytes = status.GetProperty("outputBytes").GetInt64();
            Assert.True(bytes > lastBytes, "no data sent since the last sample");
            lastBytes = bytes;
        }
        while (DateTimeOffset.Now < until);
    }
}
