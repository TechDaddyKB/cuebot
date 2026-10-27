using Cuebot.Core;
using Cuebot.Integrations;
using Cuebot.Integrations.CrowdControl;
using Cuebot.Integrations.StreamLoots;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in, read-only connection tests against the real StreamLoots alerts stream and Crowd Control PubSub. Each one connects
/// with your own URL, waits for the connection, and disconnects; nothing is sent. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class InteractiveLiveTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>StreamLoots opens the stream for any id, so this proves the endpoint and the event-stream handling.</summary>
    [LiveServiceFact("CUEBOT_LIVE_STREAMLOOTS_ALERT_URL")]
    public async Task StreamLoots_alerts_stream_opens()
    {
        await using var services = Build("streamloots");
        var streamLoots = services.GetRequiredService<StreamLootsService>();
        streamLoots.Settings.AlertsUrl = ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMLOOTS_ALERT_URL")!;
        await streamLoots.ConnectAsync();
        await WaitConnectedAsync(streamLoots);
        // Stay through one keep-alive (StreamLoots sends a comment every 30 s) to prove the stream stays open.
        await Task.Delay(TimeSpan.FromSeconds(35));
        Assert.Equal(ConnectionState.Connected, streamLoots.State);
        await streamLoots.DisconnectAsync();
    }

    /// <summary>Crowd Control answers the subscription only for a valid overlay key (the private topic must succeed).</summary>
    [LiveServiceFact("CUEBOT_LIVE_CROWDCONTROL_OVERLAY_URL")]
    public async Task Crowd_Control_accepts_the_overlay_key()
    {
        await using var services = Build("crowdcontrol");
        var cc = services.GetRequiredService<CrowdControlService>();
        cc.Settings.OverlayUrl = ServicesLiveEnvironment.Get("CUEBOT_LIVE_CROWDCONTROL_OVERLAY_URL")!;
        await cc.ConnectAsync();
        await WaitConnectedAsync(cc);
        await cc.DisconnectAsync();
    }

    private static ServiceProvider Build(string name)
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, name);
        Directory.CreateDirectory(dataDirectory);
        return new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddIntegrations().BuildServiceProvider();
    }

    private static async Task WaitConnectedAsync(IConnectionStatus service)
    {
        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (service.State != ConnectionState.Connected)
        {
            if (service.State == ConnectionState.Error || DateTime.UtcNow > deadline)
                throw new TimeoutException($"{service.Name} did not connect (status: {service.State} {service.StatusMessage})");
            await Task.Delay(200);
        }
    }
}
