using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Elgato;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Lights;
using Cuebot.Elgato.Studio;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against real Elgato hardware and apps. Nothing goes live and every change is restored: the light's
/// brightness moves by one step and back (power is left alone), the webcam's brightness is written back unchanged.
/// See docs/LIVE_TESTING.md.
/// </summary>
public sealed class ElgatoLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [LiveServiceFact("CUEBOT_LIVE_ELGATO_LIGHT_IP")]
    public async Task Light_answers_and_a_brightness_change_round_trips_through_polling()
    {
        await using var services = Build("elgato-light");
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var lights = services.GetRequiredService<ElgatoLightsService>();
        lights.Settings.Discovery = false;
        lights.Settings.PollIntervalMs = 500;

        var light = await lights.AddLightAsync(ServicesLiveEnvironment.Get("CUEBOT_LIVE_ELGATO_LIGHT_IP")!);
        output.WriteLine($"{light.Name} ({light.ProductName}, fw {light.Info?.FirmwareVersion}) state {light.State}, settings {light.SettingsJson}");
        Assert.True(light.IsOnline);
        Assert.NotNull(light.State);
        Assert.NotEmpty(light.Info!.SerialNumber);

        var original = light.State!.Brightness;
        var probe = original >= 100 ? 99 : original + 1;
        await lights.StartPollingAsync();
        try
        {
            await lights.SetBrightnessAsync(light.Id, probe);
            Assert.Equal(probe, light.State!.Brightness);
            var changed = Assert.Single(events, e => e.Type == LightEvents.BrightnessChanged);
            Assert.Equal(original, changed.Args["oldBrightness"]);
            await lights.RefreshAsync();
            Assert.Equal(probe, light.State!.Brightness);
        }
        finally
        {
            await lights.SetBrightnessAsync(light.Id, original);
            await lights.StopAsync();
        }

        // Best effort: mDNS may be blocked by the network or firewall, so only report it.
        try
        {
            var packetsFound = await lights.DiscoverAsync();
            output.WriteLine($"mDNS discovery added {packetsFound.Count} more light(s); known: {string.Join(", ", lights.Lights.Select(l => $"{l.Name}@{l.Address}"))}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"mDNS discovery failed: {ex.Message}");
        }
    }

    [LiveServiceFact("CUEBOT_LIVE_CAMERAHUB", flag: true)]
    public async Task Camera_hub_webcam_control_or_the_documented_limitation()
    {
        await using var services = Build("elgato-camerahub");
        var hub = services.GetRequiredService<CameraHubService>();
        if (!hub.IsAvailable)
        {
            // Windows/macOS: Camera Hub has no public API. Check that Cuebot at least sees the app (start it first).
            Assert.True(ElgatoAppPresence.IsRunning(ElgatoAppPresence.CameraHubProcesses),
                "Start Elgato Camera Hub: on this system Cuebot can only detect it (no public control API).");
            output.WriteLine(CameraHubService.UnavailableReason);
            return;
        }
        await hub.ConnectAsync();
        var start = DateTime.UtcNow;
        while (hub.Webcams.Count == 0 && DateTime.UtcNow - start < Timeout) await Task.Delay(200);
        var webcam = Assert.Single(hub.Resolve(""));
        output.WriteLine($"{webcam.Name}: {string.Join(", ", CameraHubScript.Describe(webcam).Select(p => $"{p.Key}={p.Value}"))}");
        if (webcam.Display(WebcamControl.Brightness) is { } brightness)
            Assert.Equal(brightness, await hub.SetAsync(webcam.Id, WebcamControl.Brightness, brightness));
        await hub.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_ELGATO_STUDIO", flag: true)]
    public void Elgato_studio_is_detected_while_it_runs()
    {
        // Elgato Studio has no public control API; this checks the process names Cuebot looks for.
        Assert.True(ElgatoAppPresence.IsRunning(ElgatoAppPresence.StudioProcesses),
            $"Start Elgato Studio first (looked for processes named {string.Join(", ", ElgatoAppPresence.StudioProcesses)}).");
    }

    private static ServiceProvider Build(string name)
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, name);
        Directory.CreateDirectory(dataDirectory);
        return new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddElgato().BuildServiceProvider();
    }
}
