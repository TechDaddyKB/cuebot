using System.Collections.Concurrent;
using Cuebot.Controllers;
using Cuebot.Core;
using Cuebot.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in check of the real SDL3 device layer: SDL loads and starts on its own thread, the connected controllers are
/// listed, and (with CUEBOT_LIVE_CONTROLLER_WAIT_SECONDS) the first button press arrives as a controller.button event.
/// Nothing is asked interactively; nothing rumbles or changes colour. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class ControllerLiveTests(ITestOutputHelper output)
{
    [LiveServiceFact("CUEBOT_LIVE_CONTROLLER", flag: true)]
    public async Task Sdl_starts_lists_controllers_and_optionally_sees_a_button()
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "controllers");
        Directory.CreateDirectory(dataDirectory);
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddGameControllers().BuildServiceProvider();
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var controllers = services.GetRequiredService<ControllerService>();

        await controllers.EnsureRunningAsync();
        output.WriteLine($"{controllers.BackendVersion}: {controllers.StatusMessage}");
        Assert.True(controllers.IsRunning);
        Assert.NotEqual(ConnectionState.Error, controllers.State);

        // Bluetooth pads and Linux udev can report a moment after start.
        await Task.Delay(TimeSpan.FromSeconds(1));
        foreach (var c in controllers.Controllers)
            output.WriteLine($"{c.DisplayName}: type {c.Device.Type}, guid {c.Device.Guid}, {c.Device.VendorId:x4}:{c.Device.ProductId:x4}, "
                             + $"rumble {c.Device.CanRumble}, led {c.Device.HasRgbLed}, battery {c.Device.Power} {c.Device.BatteryPercent}%");
        if (controllers.Controllers.Count == 0) output.WriteLine("No controllers connected (plug one in to see it listed).");

        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_CONTROLLER_WAIT_SECONDS"), out var seconds) && seconds > 0)
        {
            output.WriteLine($"Waiting up to {seconds} s for any button on any controller...");
            var learned = await controllers.LearnAsync(TimeSpan.FromSeconds(seconds));
            Assert.True(learned is not null, $"No controller input within {seconds} s.");
            output.WriteLine($"{learned.Description} -> {learned.TriggerType}: {learned.SettingsText}");
            Assert.Contains(events, e => e.Type is ControllerEvents.Button or ControllerEvents.Axis or ControllerEvents.Hat);
        }
    }
}
