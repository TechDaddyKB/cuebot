using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.GoXlr;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in test against a running GoXLR Utility (Linux, Windows or macOS) with a GoXLR or GoXLR Mini plugged in. Nothing
/// audible changes for long: it loads the status, pings, and (with CUEBOT_LIVE_GOXLR_FADER) toggles that fader's mute
/// twice, waiting for the utility's status patches. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class GoXlrLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [LiveServiceFact("CUEBOT_LIVE_GOXLR", flag: true)]
    public async Task GoXlr_Utility_connects_reports_the_device_and_round_trips_a_mute()
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "goxlr");
        Directory.CreateDirectory(dataDirectory);
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddGoXlr().BuildServiceProvider();
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var g = services.GetRequiredService<GoXlrService>();
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_GOXLR_HOST") is { } host) g.Settings.Host = host;
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_GOXLR_PORT"), out var port)) g.Settings.Port = port;
        await g.ConnectAsync();
        await WaitAsync(() => g.IsConnected, () => $"The GoXLR Utility was not found ({g.StatusMessage})");

        Assert.NotEmpty(g.Status.DaemonVersion);
        var mixer = g.Mixer();
        Assert.Equal(4, mixer.Faders.Count);
        Assert.NotEmpty(mixer.Volumes);
        Assert.Contains(events, e => e.Type == GoXlrEvents.Connected);
        Assert.True((await g.PingAsync()) < TimeSpan.FromSeconds(5));

        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_GOXLR_FADER") is { } fader)
        {
            var f = g.ResolveFader(fader);
            var wasMuted = f.Muted;
            await g.SetFaderMuteAsync(f.Name, GoXlrMuteAction.Toggle);
            await WaitAsync(() => g.Mixer().Faders[f.Name].Muted == !wasMuted, () => "the fader's mute state did not change");
            await g.SetFaderMuteAsync(f.Name, wasMuted ? GoXlrMuteAction.Mute : GoXlrMuteAction.Unmute);
            await WaitAsync(() => g.Mixer().Faders[f.Name].Muted == wasMuted, () => "the fader's mute state was not restored");
            Assert.Contains(events, e => e.Type == GoXlrEvents.FaderMuteChanged);
        }
        await g.DisconnectAsync();
        Assert.Contains(events, e => e.Type == GoXlrEvents.Disconnected);
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
