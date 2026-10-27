using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Meld;
using Cuebot.StreamApps.PolyPop;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against a running Meld Studio (WebChannel, "Allow remote connections" on) and PolyPop (community
/// WebSocket plugin). Nothing goes live: Meld only re-shows the current scene and, when a layer is named, toggles that
/// layer twice; PolyPop receives one test alert. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class StreamAppsLiveTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The PolyPop plugin retries every 5 s; give the streamer time to add the WebSocket source.</summary>
    private static readonly TimeSpan PluginTimeout = TimeSpan.FromSeconds(90);

    [LiveServiceFact("CUEBOT_LIVE_MELD", flag: true)]
    public async Task Meld_Studio_connects_lists_scenes_and_round_trips_a_layer_toggle()
    {
        await using var services = Build("meld");
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var meld = services.GetRequiredService<MeldStudioService>();
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_MELD_HOST") is { } host) meld.Settings.Host = host;
        await meld.ConnectAsync();
        await WaitAsync(() => meld.IsConnected, ConnectTimeout, () => $"Meld Studio did not connect ({meld.StatusMessage})");

        Assert.True(meld.ApiVersion >= 1);
        Assert.NotEmpty(meld.Session.Scenes);
        var current = meld.Session.CurrentScene ?? meld.Session.Scenes[0];
        await meld.ShowSceneAsync(current.Id);

        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_MELD_LAYER") is { } layerName)
        {
            var layer = meld.Session.FindLayer("", layerName);
            var visible = layer.Visible;
            await meld.SetLayerVisibilityAsync(meld.Session.Get(layer.Parent)?.Name ?? "", layer.Id, SwitchMode.Toggle);
            await WaitAsync(() => events.Any(e => e.Type == MeldEvents.Event && (e.Args.GetValueOrDefault("meldStudioEvent.layerId") as string) == layer.Id),
                ConnectTimeout, () => "no LayerVisibilityChanged event");
            await WaitAsync(() => meld.Session.Get(layer.Id)?.Visible == !visible, ConnectTimeout, () => "layer state did not change");
            await meld.SetLayerVisibilityAsync("", layer.Id, visible ? SwitchMode.On : SwitchMode.Off);
            await WaitAsync(() => meld.Session.Get(layer.Id)?.Visible == visible, ConnectTimeout, () => "layer state was not restored");
        }
        await meld.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_POLYPOP", flag: true)]
    public async Task PolyPop_plugin_connects_and_receives_a_test_alert()
    {
        await using var services = Build("polypop");
        var polyPop = services.GetRequiredService<PolyPopService>();
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_POLYPOP_PORT"), out var port)) polyPop.Settings.Port = port;
        await polyPop.ConnectAsync();
        await WaitAsync(() => polyPop.IsConnected, PluginTimeout,
            () => $"The PolyPop WebSocket plugin did not connect to port {polyPop.Settings.Port} ({polyPop.StatusMessage})");

        var alert = ServicesLiveEnvironment.Get("CUEBOT_LIVE_POLYPOP_ALERT") ?? "Cuebot Test";
        Assert.True(await polyPop.TriggerAlertAsync(alert, new Dictionary<string, string> { ["message"] = "dev testing" }) >= 1);
        await polyPop.DisconnectAsync();
    }

    private static ServiceProvider Build(string name)
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, name);
        Directory.CreateDirectory(dataDirectory);
        return new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddStreamApps().BuildServiceProvider();
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure());
            await Task.Delay(200);
        }
    }
}
