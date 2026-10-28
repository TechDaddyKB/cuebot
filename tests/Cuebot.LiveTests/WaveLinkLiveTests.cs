using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.WaveLink;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in test against a running Elgato Wave Link (3.x or 1.x/2.x) on this PC. Nothing audible changes for long: it
/// detects the API and port, reads the channels and mixes, and (with CUEBOT_LIVE_WAVELINK_CHANNEL) toggles that channel's
/// mute twice, waiting for Wave Link's notifications. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class WaveLinkLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [LiveServiceFact("CUEBOT_LIVE_WAVELINK", flag: true)]
    public async Task Wave_Link_is_detected_lists_channels_and_round_trips_a_mute()
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "wavelink");
        Directory.CreateDirectory(dataDirectory);
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddWaveLink().BuildServiceProvider();
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var wl = services.GetRequiredService<WaveLinkService>();
        Assert.True(wl.IsAvailable, wl.UnsupportedReason);
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_WAVELINK_PORT"), out var port)) wl.Settings.Port = port;
        await wl.ConnectAsync();
        await WaitAsync(() => wl.IsConnected, () => $"Wave Link was not found ({wl.StatusMessage})");

        Assert.NotEqual(WaveLinkApi.None, wl.Api);
        Assert.InRange(wl.Port, 1, 65535);
        Assert.NotEmpty(wl.Snapshot.Mixes);
        Assert.NotEmpty(wl.Snapshot.Channels);
        Assert.Contains(events, e => e.Type == WaveLinkEvents.Connected);
        Assert.True((await wl.PingAsync()) < TimeSpan.FromSeconds(5));

        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_WAVELINK_CHANNEL") is { } channelName)
        {
            var channel = wl.Snapshot.FindChannel(channelName);
            var muted = channel.Muted;
            Assert.Equal(!muted, await wl.SetChannelMuteAsync(channel.Id, "", SwitchMode.Toggle));
            await WaitAsync(() => wl.Snapshot.FindChannel(channel.Id).Muted == !muted, () => "the channel's mute state did not change");
            await wl.SetChannelMuteAsync(channel.Id, "", muted ? SwitchMode.On : SwitchMode.Off);
            await WaitAsync(() => wl.Snapshot.FindChannel(channel.Id).Muted == muted, () => "the channel's mute state was not restored");
            Assert.Contains(events, e => e.Type == WaveLinkEvents.ChannelMuteChanged);
        }
        await wl.DisconnectAsync();
        Assert.Contains(events, e => e.Type == WaveLinkEvents.Disconnected);
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure());
            await Task.Delay(200);
        }
    }
}
