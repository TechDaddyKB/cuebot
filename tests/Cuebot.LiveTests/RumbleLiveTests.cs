using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Storage;
using Cuebot.Obs;
using Cuebot.Rumble;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against the real Rumble Live Stream API and, when streaming is allowed, a real Rumble live stream
/// started through OBS. See docs/LIVE_TESTING.md.
/// </summary>
[Collection(RumbleLiveCollection.Name)]
public sealed class RumbleLiveTests(RumbleLiveBot bot)
{
    /// <summary>Rumble's API reports streams with a delay; give it several minutes.</summary>
    private static readonly TimeSpan StreamEventTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ChatAttachTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ObsTimeout = TimeSpan.FromSeconds(30);

    [LiveRumbleFact]
    public void Connects_and_reads_channel_info()
    {
        Assert.Equal(ConnectionState.Connected, bot.Rumble.State);
        Assert.False(string.IsNullOrWhiteSpace(bot.Rumble.ChannelName));
        Assert.True(bot.Rumble.FollowerCount >= 0);
    }

    [LiveRumbleStreamFact]
    public async Task Going_live_via_obs_fires_stream_online_attaches_chat_and_fires_stream_offline()
    {
        await using var target = await bot.UseStreamTargetAsync(LiveEnvironment.Target("rumble")!);
        var since = DateTimeOffset.Now;

        await bot.Obs.SetCurrentSceneAsync(LiveEnvironment.StreamScene!);
        await bot.Obs.ControlStreamAsync(OutputCommand.Start);
        await bot.WaitForEventAsync(ObsEvents.StreamStarted, since, ObsTimeout);

        BotEvent online;
        try
        {
            online = await bot.WaitForEventAsync(RumbleEventTypes.StreamOnline, since, StreamEventTimeout);
        }
        catch (TimeoutException ex)
        {
            // Seen in practice: Rumble accepted the stream (dashboard showed it LIVE) but the Live Stream API never
            // listed it. The API omits streams it doesn't consider part of the key's scope or visibility.
            throw new TimeoutException(ex.Message + ". OBS streamed, but Rumble's Live Stream API never listed a live stream. " +
                "Check that the stream setup is Public (unlisted streams appear to be omitted) and that the API key's " +
                "scope (user vs channel) matches the profile or channel the stream key belongs to.", ex);
        }
        Assert.False(string.IsNullOrEmpty(online.Args["streamId"] as string));
        await RumbleLiveBot.WaitUntilAsync(() => bot.Rumble.IsChatAttached, ChatAttachTimeout, "the Rumble live chat to attach");
        await Task.Delay(LiveEnvironment.StreamDuration);

        var stopping = DateTimeOffset.Now;
        await bot.Obs.ControlStreamAsync(OutputCommand.Stop);
        await bot.WaitForEventAsync(ObsEvents.StreamStopped, stopping, ObsTimeout);
        await bot.WaitForEventAsync(RumbleEventTypes.StreamOffline, stopping, StreamEventTimeout);
        Assert.False(bot.Rumble.IsLive);
    }
}

/// <summary>A real Cuebot with the Rumble and OBS modules, shared by the Rumble live tests.</summary>
public sealed class RumbleLiveBot : IAsyncLifetime
{
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private ServiceProvider? _services;

    public RumbleService Rumble => Get<RumbleService>();
    public ObsService Obs => Get<ObsService>();

    private T Get<T>() where T : notnull =>
        (_services ?? throw new InvalidOperationException("Rumble live tests are not configured")).GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        if (!LiveEnvironment.RunRequested || RumbleLiveEnvironment.ApiUrl is not { } apiUrl) return;   // every test skips

        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "rumble");
        Directory.CreateDirectory(dataDirectory);
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddRumble().AddObs().BuildServiceProvider();
        Get<EventBus>().Subscribe(_events.Enqueue);

        Rumble.Settings.LiveStreamApiUrl = apiUrl;
        Rumble.Settings.AutoConnect = false;
        Rumble.Settings.PollSeconds = 5;
        await Rumble.ConnectAsync();

        if (LiveEnvironment.ObsPassword is { } password)
        {
            var obs = Get<JsonFile<ObsSettings>>().Value;
            obs.Host = LiveEnvironment.ObsHost;
            obs.Port = LiveEnvironment.ObsPort;
            obs.Password = password;
            obs.AutoConnect = false;
            await Obs.ConnectAsync();
        }
    }

    /// <summary>Points OBS at <paramref name="target"/> until disposed, then restores OBS's previous stream settings.</summary>
    public async Task<IAsyncDisposable> UseStreamTargetAsync(StreamTarget target)
    {
        var previous = await Obs.RequestAsync("GetStreamServiceSettings");
        await Obs.RequestAsync("SetStreamServiceSettings", new
        {
            streamServiceType = "rtmp_custom",
            streamServiceSettings = new { server = target.Url, key = target.Key },
        });
        return new Restore(async () =>
        {
            await StopStreamIfActiveAsync();   // OBS refuses to change stream settings while live
            await Obs.RequestAsync("SetStreamServiceSettings", new
            {
                streamServiceType = previous.GetProperty("streamServiceType").GetString(),
                streamServiceSettings = previous.GetProperty("streamServiceSettings"),
            });
        });
    }

    /// <summary>Stops the stream (if live) and waits until OBS reports it inactive.</summary>
    public async Task StopStreamIfActiveAsync()
    {
        if (!Obs.IsConnected || !(await Obs.RequestAsync("GetStreamStatus")).GetProperty("outputActive").GetBoolean()) return;
        await Obs.ControlStreamAsync(OutputCommand.Stop);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await Obs.RequestAsync("GetStreamStatus")).GetProperty("outputActive").GetBoolean() && DateTime.UtcNow < deadline)
            await Task.Delay(500);
    }

    public async Task<BotEvent> WaitForEventAsync(string type, DateTimeOffset since, TimeSpan timeout)
    {
        BotEvent? hit = null;
        await WaitUntilAsync(() => (hit = _events.FirstOrDefault(e => e.Type == type && e.Time >= since)) is not null, timeout, type);
        return hit!;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}");
            await Task.Delay(500);
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is null) return;
        await StopStreamIfActiveAsync();
        await _services.DisposeAsync();
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}

[CollectionDefinition(Name)]
public sealed class RumbleLiveCollection : ICollectionFixture<RumbleLiveBot>
{
    public const string Name = "Live Rumble";
}

/// <summary>Rumble settings for the live tests, read from the environment or the live.env file.</summary>
internal static class RumbleLiveEnvironment
{
    /// <summary>The private Live Stream API URL (rumble.com → Account → Live Stream API). Never printed.</summary>
    public static string? ApiUrl
    {
        get
        {
            // LiveEnvironment's static constructor loads live.env into the environment.
            RuntimeHelpers.RunClassConstructor(typeof(LiveEnvironment).TypeHandle);
            return Environment.GetEnvironmentVariable("CUEBOT_LIVE_RUMBLE_API_URL") is { Length: > 0 } url ? url : null;
        }
    }
}

/// <summary>Runs only with CUEBOT_LIVE_RUMBLE_API_URL set. Read-only: nothing is posted to Rumble.</summary>
public sealed class LiveRumbleFactAttribute : LiveFactAttribute
{
    public LiveRumbleFactAttribute()
    {
        if (Skip is not null) return;
        if (RumbleLiveEnvironment.ApiUrl is null)
            Skip = "Set CUEBOT_LIVE_RUMBLE_API_URL to run against the real Rumble Live Stream API (see docs/LIVE_TESTING.md)";
    }
}

/// <summary>
/// Goes live on Rumble: needs the API URL, the Rumble ingest URL and stream key, OBS, and CUEBOT_LIVE_STREAM=1 plus
/// CUEBOT_LIVE_OBS_SCENE, because it is public.
/// </summary>
public sealed class LiveRumbleStreamFactAttribute : LiveFactAttribute
{
    public LiveRumbleStreamFactAttribute()
    {
        if (Skip is not null) return;
        if (RumbleLiveEnvironment.ApiUrl is null || LiveEnvironment.ObsPassword is null)
            Skip = "Set CUEBOT_LIVE_RUMBLE_API_URL and CUEBOT_OBS_PASSWORD (see docs/LIVE_TESTING.md)";
        else if (!LiveEnvironment.StreamingAllowed || LiveEnvironment.StreamScene is null)
            Skip = "Going live is public: set CUEBOT_LIVE_STREAM=1 and CUEBOT_LIVE_OBS_SCENE to allow it";
        else if (LiveEnvironment.Target("rumble") is null)
            Skip = "Set CUEBOT_LIVE_RUMBLE_RTMP_URL and CUEBOT_LIVE_RUMBLE_STREAM_KEY to stream to Rumble";
    }
}
