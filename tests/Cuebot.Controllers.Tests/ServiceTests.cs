using Cuebot.Controllers.Device;
using Cuebot.Controllers.Tests.Support;
using Cuebot.Core;

namespace Cuebot.Controllers.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task Watches_only_while_something_needs_it()
    {
        await using var env = new Env(triggers: false);
        env.Service.Sync();
        Assert.False(env.Service.IsRunning);
        Assert.Equal(0, env.Backend.Starts);
        Assert.Contains("Idle", env.Service.StatusMessage);

        env.TriggersWanted = true;
        env.Service.Sync();
        await WaitAsync(() => env.Service.IsRunning);
        Assert.Equal(ConnectionState.Disconnected, env.Service.State);
        Assert.Equal("No controllers connected", env.Service.StatusMessage);
        Assert.Equal("Fake 1.0", env.Service.BackendVersion);

        env.TriggersWanted = false;
        env.Service.Sync();
        Assert.False(env.Service.IsRunning);
        Assert.Equal(1, env.Backend.Stops);

        // A page hold keeps it running until released.
        var hold = env.Service.Hold();
        await WaitAsync(() => env.Service.IsRunning);
        hold.Dispose();
        env.Service.Sync();
        Assert.False(env.Service.IsRunning);

        // A sub-action/script lease lasts LeaseTime.
        await env.Service.EnsureRunningAsync();
        env.Time.Advance(ControllerService.LeaseTime - TimeSpan.FromSeconds(1));
        env.Service.Sync();
        Assert.True(env.Service.IsRunning);
        env.Time.Advance(TimeSpan.FromSeconds(2));
        env.Service.Sync();
        Assert.False(env.Service.IsRunning);
    }

    [Fact]
    public async Task Always_and_off_modes()
    {
        await using var env = new Env(triggers: false, mode: ControllerWatchMode.Always);
        env.Service.Sync();
        await WaitAsync(() => env.Service.IsRunning);
        env.Settings.Value.Mode = ControllerWatchMode.Off;
        env.Service.Sync();
        Assert.False(env.Service.IsRunning);
        Assert.Contains("Turned off", env.Service.StatusMessage);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.EnsureRunningAsync());
        Assert.Contains("turned off", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.RumbleAsync("Any", 50, 50, 100));
    }

    [Fact]
    public async Task A_missing_native_library_is_an_error_status_not_a_crash()
    {
        await using var env = new Env();
        env.Backend.FailWith = "the SDL3 library was not found";
        env.Service.Sync();
        await WaitAsync(() => env.Service.State == ConnectionState.Error);
        Assert.Contains("SDL3 library was not found", env.Service.StatusMessage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.EnsureRunningAsync());

        // It does not retry on every tick, only on Restart.
        env.Service.Sync();
        Assert.Equal(1, env.Backend.Starts);
        env.Backend.FailWith = null;
        env.Service.Restart();
        await WaitAsync(() => env.Service.IsRunning);
        Assert.Equal(2, env.Backend.Starts);
    }

    [Fact]
    public async Task Hot_plug_assigns_the_lowest_free_slot_and_publishes_connect_events()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.Xbox(10));
        await env.StartAsync();
        var first = Assert.Single(env.Of(ControllerEvents.Connected));
        Assert.Equal(1, first.Args["controllerIndex"]);
        Assert.Equal("Xbox Series X Controller", first.Args["controllerName"]);
        Assert.Equal("Xbox One", first.Args["controllerType"]);
        Assert.Equal("045e", first.Args["controllerVendorId"]);
        Assert.Equal(ConnectionState.Connected, env.Service.State);

        env.Backend.Raise(new DeviceAddedEvent(FakeBackend.DualSense(11)));
        env.Backend.Raise(new DeviceRemovedEvent(10));
        var gone = Assert.Single(env.Of(ControllerEvents.Disconnected));
        Assert.Equal(1, gone.Args["controllerIndex"]);
        Assert.Equal(1, gone.Args["controllerCount"]);
        env.Backend.Raise(new DeviceAddedEvent(FakeBackend.SwitchPro(12)));
        Assert.Equal([1, 2], env.Service.Controllers.Select(c => c.Slot));
        Assert.Equal("#1 Nintendo Switch Pro Controller", env.Service.Controllers[0].DisplayName);
        Assert.Contains("2 controllers", env.Service.StatusMessage);

        // A remap (same id) updates in place, without another connect event.
        env.Backend.Raise(new DeviceAddedEvent(FakeBackend.DualSense(11) with { Name = "DualSense (remapped)" }));
        Assert.Equal(3, env.Of(ControllerEvents.Connected).Count);
        Assert.Contains(env.Service.Controllers, c => c.Name == "DualSense (remapped)");

        env.Backend.Raise(new BatteryEvent(11, ControllerPower.Charging, 80));
        Assert.Equal(80, env.Service.Resolve("PS5").Single().Device.BatteryPercent);

        // Events for unknown devices are ignored.
        env.Backend.Raise(new ButtonEvent(999, 0, true));
        env.Backend.Raise(new DeviceRemovedEvent(999));
        Assert.Empty(env.Of(ControllerEvents.Button));
    }

    [Fact]
    public async Task Buttons_carry_names_labels_held_buttons_and_hold_time()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.SwitchPro(3));
        await env.StartAsync();
        env.Backend.Raise(new ButtonEvent(3, 9, true));    // L
        env.Backend.Raise(new ButtonEvent(3, 1, true));    // East = A on Nintendo
        env.Backend.Raise(new ButtonEvent(3, 1, true));    // repeat: ignored
        env.Time.Advance(TimeSpan.FromMilliseconds(250));
        env.Backend.Raise(new ButtonEvent(3, 1, false));

        var events = env.Of(ControllerEvents.Button);
        Assert.Equal(3, events.Count);
        var press = events[1];
        Assert.Equal("East", press.Args["button"]);
        Assert.Equal("A", press.Args["buttonLabel"]);
        Assert.Equal(1, press.Args["buttonIndex"]);
        Assert.Equal(true, press.Args["pressed"]);
        Assert.Equal("Pressed", press.Args["buttonState"]);
        Assert.Equal("East,LeftShoulder", press.Args["heldButtons"]);
        var release = events[2];
        Assert.Equal("Released", release.Args["buttonState"]);
        Assert.Equal(250L, release.Args["holdDurationMs"]);
        Assert.Equal("LeftShoulder", release.Args["heldButtons"]);
        Assert.Equal(["East", "LeftShoulder"], env.Service.RecentButtons);
        Assert.Equal(["LeftShoulder"], env.Service.Controllers.Single().HeldButtons);
    }

    [Fact]
    public async Task The_dpad_is_also_reported_as_hat1()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.Xbox());
        await env.StartAsync();
        env.Backend.Raise(new ButtonEvent(1, ControllerNames.DPadUp, true));
        env.Backend.Raise(new ButtonEvent(1, ControllerNames.DPadLeft, true));
        env.Backend.Raise(new ButtonEvent(1, ControllerNames.DPadUp, false));
        env.Backend.Raise(new ButtonEvent(1, ControllerNames.DPadLeft, false));
        var hats = env.Of(ControllerEvents.Hat);
        Assert.Equal(["Up", "UpLeft", "Left", "Centered"], hats.Select(h => (string)h.Args["hatDirection"]!));
        Assert.All(hats, h => Assert.Equal(true, h.Args["isDPad"]));
        Assert.Equal("Hat1", hats[0].Args["hat"]);
        Assert.Equal("UpLeft", hats[2].Args["hatPreviousDirection"]);

        env.Settings.Value.DPadAsHat = false;
        env.Backend.Raise(new ButtonEvent(1, ControllerNames.DPadDown, true));
        Assert.Equal(4, env.Of(ControllerEvents.Hat).Count);
    }

    [Fact]
    public async Task Joystick_hats_buttons_and_axes_use_raw_numbers()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.FlightStick(4));
        await env.StartAsync();
        env.Backend.Raise(new HatEvent(4, 0, 2));
        env.Backend.Raise(new HatEvent(4, 0, 2));   // unchanged: ignored
        env.Backend.Raise(new ButtonEvent(4, 0, true));
        env.Backend.Raise(new ButtonEvent(4, 20, true));   // beyond the announced count: grows
        env.Backend.Raise(new AxisEvent(4, 2, short.MinValue));
        var hat = Assert.Single(env.Of(ControllerEvents.Hat));
        Assert.Equal("Right", hat.Args["hatDirection"]);
        Assert.Equal(false, hat.Args["isDPad"]);
        Assert.Equal(["Button1", "Button21"], env.Of(ControllerEvents.Button).Select(b => (string)b.Args["button"]!));
        Assert.Equal("Button1", env.Of(ControllerEvents.Button)[0].Args["buttonLabel"]);
        var axis = Assert.Single(env.Of(ControllerEvents.Axis));
        Assert.Equal("Axis3", axis.Args["axis"]);
        Assert.Equal(-1.0, axis.Args["axisValue"]);
        Assert.Equal(-100, axis.Args["axisPercent"]);
        Assert.Equal("Negative", axis.Args["axisDirection"]);
        Assert.Equal(["Right"], env.Service.Controllers.Single().Hats.Select(ControllerNames.HatDirection));
    }

    [Fact]
    public async Task Axes_apply_deadzones_skip_noise_and_throttle_with_a_trailing_value()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.Xbox());
        await env.StartAsync();

        env.Backend.Raise(new AxisEvent(1, 0, 3000));          // inside the 15 % deadzone
        Assert.Empty(env.Of(ControllerEvents.Axis));

        env.Backend.Raise(new AxisEvent(1, 0, 20000));          // published
        env.Time.Advance(TimeSpan.FromMilliseconds(10));
        env.Backend.Raise(new AxisEvent(1, 0, 26000));          // held back (throttle 50 ms)
        env.Time.Advance(TimeSpan.FromMilliseconds(10));
        env.Backend.Raise(new AxisEvent(1, 0, 32767));          // held back, latest wins
        Assert.Single(env.Of(ControllerEvents.Axis));
        env.Time.Advance(TimeSpan.FromMilliseconds(40));
        env.Service.FlushAxes();
        var axes = env.Of(ControllerEvents.Axis);
        Assert.Equal(2, axes.Count);
        Assert.Equal(1.0, axes[1].Args["axisValue"]);
        Assert.Equal((double)axes[0].Args["axisValue"]!, axes[1].Args["axisPrevious"]);
        Assert.Equal(32767, axes[1].Args["axisRaw"]);
        Assert.Equal(false, axes[1].Args["isTrigger"]);
        Assert.Equal(1.0, env.Service.Controllers.Single().Axes[0].Value);

        // Tiny jitter is ignored; returning to rest is always published.
        env.Time.Advance(TimeSpan.FromSeconds(1));
        env.Backend.Raise(new AxisEvent(1, 0, 32600));
        Assert.Equal(2, env.Of(ControllerEvents.Axis).Count);
        env.Backend.Raise(new AxisEvent(1, 0, 100));
        Assert.Equal(0.0, env.Of(ControllerEvents.Axis)[2].Args["axisValue"]);

        // Triggers are 0..1 with their own deadzone.
        env.Backend.Raise(new AxisEvent(1, ControllerNames.RightTrigger, 32767));
        var trigger = env.Of(ControllerEvents.Axis).Last();
        Assert.Equal("RightTrigger", trigger.Args["axis"]);
        Assert.Equal(true, trigger.Args["isTrigger"]);
        Assert.Equal(100, trigger.Args["axisPercent"]);
        Assert.Equal(["RightTrigger", "LeftX"], env.Service.RecentAxes);
    }

    [Fact]
    public async Task Learn_returns_the_next_input_with_trigger_settings()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.DualSense(2));
        await env.StartAsync();
        var learn = env.Service.LearnAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20);
        env.Backend.Raise(new ButtonEvent(2, 3, true));
        var input = await learn;
        Assert.NotNull(input);
        Assert.Equal("Button", input.Kind);
        Assert.Equal("North", input.Name);
        Assert.Equal("Triangle", input.Label);
        Assert.Equal(ControllerEvents.Button, input.TriggerType);
        Assert.Equal("#1", input.TriggerSettings["controller"]);
        Assert.Equal("Button North (Triangle) on #1 DualSense Wireless Controller", input.Description);
        Assert.Equal("controller = #1, button = North, state = Pressed", input.SettingsText);

        var axis = env.Service.LearnAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20);
        env.Backend.Raise(new AxisEvent(2, 1, -30000));
        var learned = await axis;
        Assert.Equal("Axis", learned!.Kind);
        Assert.Equal("LeftY", learned.Name);
        Assert.Equal("Negative", learned.TriggerSettings["direction"]);

        Assert.Null(await env.Service.LearnAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Rumble_and_led_reach_matching_controllers_only()
    {
        await using var env = new Env(triggers: false);
        env.Backend.Present.AddRange([FakeBackend.Xbox(1), FakeBackend.DualSense(2)]);
        var rumbled = await env.Service.RumbleAsync("Any", 100, 50, 300);   // starts watching on demand
        Assert.Equal(2, rumbled.Count);
        Assert.Contains((1u, (ushort)65535, (ushort)32768, 300u), env.Backend.Rumbles);

        var lit = await env.Service.SetLedAsync("DualSense", 255, 0, 0);
        Assert.Equal("DualSense Wireless Controller", Assert.Single(lit).Name);
        Assert.Equal((2u, (byte)255, (byte)0, (byte)0), Assert.Single(env.Backend.Leds));
        Assert.Empty(await env.Service.RumbleAsync("#7", 50, 50, 100));
    }

    [Fact]
    public async Task Mappings_file_comes_from_settings_or_the_data_folder()
    {
        await using var env = new Env();
        Assert.Null(env.Service.ResolveMappingsFile());
        File.WriteAllText(env.Paths.File("gamecontroller.txt"), "# test\n");
        Assert.Equal(env.Paths.File("gamecontroller.txt"), env.Service.ResolveMappingsFile());
        File.WriteAllText(env.Paths.File("custom.txt"), "# test\n");
        env.Settings.Value.MappingsFile = "custom.txt";
        Assert.Equal(env.Paths.File("custom.txt"), env.Service.ResolveMappingsFile());
        env.Settings.Value.MappingsFile = "missing.txt";
        Assert.Null(env.Service.ResolveMappingsFile());
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.ReloadMappingsAsync());

        env.Settings.Value.MappingsFile = "custom.txt";
        await env.StartAsync();
        Assert.Equal(env.Paths.File("custom.txt"), env.Backend.Options!.MappingsFile);
        Assert.Equal(2, await env.Service.ReloadMappingsAsync());
        Assert.Equal([env.Paths.File("custom.txt")], env.Backend.MappingFiles);
    }

    [Fact]
    public async Task A_backend_fault_becomes_an_error_status()
    {
        await using var env = new Env();
        env.Backend.Present.Add(FakeBackend.Xbox());
        await env.StartAsync();
        env.Backend.Fault("device thread crashed");
        Assert.Equal(ConnectionState.Error, env.Service.State);
        Assert.Empty(env.Service.Controllers);
        Assert.Contains("device thread crashed", env.Service.StatusMessage);
    }

    [Fact]
    public async Task Status_changes_are_raised()
    {
        await using var env = new Env();
        var changes = 0;
        var controllerChanges = 0;
        env.Service.StatusChanged += () => Interlocked.Increment(ref changes);
        env.Service.ControllersChanged += () => Interlocked.Increment(ref controllerChanges);
        await env.StartAsync();
        env.Backend.Raise(new DeviceAddedEvent(FakeBackend.Xbox()));
        var version = env.Service.InputVersion;
        env.Backend.Raise(new ButtonEvent(1, 0, true));
        Assert.True(env.Service.InputVersion > version);
        Assert.True(changes >= 2);
        Assert.True(controllerChanges >= 2);
    }

    internal static async Task WaitAsync(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5)) Assert.Fail("Timed out waiting for the condition");
            await Task.Delay(10);
        }
    }
}
