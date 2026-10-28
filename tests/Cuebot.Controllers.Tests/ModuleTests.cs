using System.Text.Json;
using Cuebot.Controllers.Device;
using Cuebot.Controllers.Device.Sdl;
using Cuebot.Controllers.Tests.Support;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Controllers.Tests;

public sealed class ModuleTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-controllers-di-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly ServiceProvider _services;

    public ModuleTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddGameControllers()
            .AddSingleton<IControllerBackend>(_backend)   // last registration wins: no SDL, no devices
            .BuildServiceProvider();

    [Fact]
    public void Registers_status_triggers_sub_actions_and_script_functions()
    {
        var status = Assert.Single(_services.GetServices<IConnectionStatus>(), s => s.Name == "Game Controllers");
        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.Contains(_services.GetServices<IPeriodicService>(), p => p is ControllerService);

        var registry = _services.GetRequiredService<Registry>();
        Assert.All(ControllerTriggers.TriggerIds, id => Assert.NotNull(registry.GetTrigger(id)));
        Assert.All(ControllerSubActions.TypeIds, id => Assert.Equal("Game Controller", registry.GetSubAction(id)!.Category));
        var set = _services.GetRequiredService<ControllersServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(ControllerSubActions.TypeIds.Order(), set.All.Select(h => h.TypeId).Order());

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "controllers.status", "controllers.list", "controllers.get", "controllers.rumble", "controllers.setLed",
                     "controllers.learn", "controllers.reloadMappings" })
            Assert.Contains(name, functions);

        // Building the module touches no device.
        Assert.Equal(0, _backend.Starts);
    }

    [Fact]
    public async Task Sub_actions_rumble_light_and_report_state()
    {
        _backend.Present.AddRange([FakeBackend.Xbox(1), FakeBackend.DualSense(2)]);
        var capture = new CaptureStep();
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "run"))).AddGameControllers()
            .AddSingleton<IControllerBackend>(_backend).AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        var engine = services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "Pads",
            SubActions =
            [
                new SubActionDefinition { Type = "controller.rumble", Parameters = new() { ["controller"] = "Any", ["low"] = "100", ["high"] = "0", ["duration"] = "250" } },
                new SubActionDefinition { Type = "controller.setLed", Parameters = new() { ["controller"] = "PS5", ["color"] = "#ff8000" } },
                new SubActionDefinition { Type = "controller.getList" },
                new SubActionDefinition { Type = "controller.getState", Parameters = new() { ["controller"] = "#2" } },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        Assert.True(await engine.RunAsync(action));
        Assert.Equal(2, _backend.Rumbles.Count);
        Assert.Contains((2u, (ushort)65535, (ushort)0, 250u), _backend.Rumbles);
        Assert.Equal((2u, (byte)255, (byte)128, (byte)0), Assert.Single(_backend.Leds));
        var args = capture.Args!;
        Assert.Equal(1, args["controllersAffected"]);
        Assert.Equal(2, args["controllerCount"]);
        Assert.Equal("Xbox Series X Controller, DualSense Wireless Controller", args["controllerNames"]);
        Assert.Equal("PS5", args["controller1Type"]);
        Assert.Equal(true, args["controllerConnected"]);
        Assert.Equal("DualSense Wireless Controller", args["controllerName"]);
        Assert.Equal(70, args["batteryPercent"]);
        Assert.Equal(0.0, args["axisLeftX"]);
        Assert.Equal("Centered", args["hat1"]);

        var bad = new ActionDefinition
        {
            Name = "bad",
            SubActions = [new SubActionDefinition { Type = "controller.setLed", Parameters = new() { ["color"] = "nope" } }],
        };
        await engine.RunAsync(bad);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("is not a colour") == true);
    }

    [Fact]
    public async Task Script_functions_list_and_rumble()
    {
        _backend.Present.Add(FakeBackend.Xbox(1));
        var api = _services.GetRequiredService<ScriptApi>();
        var list = (Dictionary<string, object?>[])(await api.CallAsync(null!, "controllers.list", JsonDocument.Parse("{}").RootElement, default))!;
        Assert.Equal("Xbox Series X Controller", Assert.Single(list)["controllerName"]);
        Assert.Equal(1, await api.CallAsync(null!, "controllers.rumble", JsonDocument.Parse("""{"low":20,"high":"40","durationMs":100}""").RootElement, default));
        var status = (Dictionary<string, object?>)(await api.CallAsync(null!, "controllers.status", JsonDocument.Parse("{}").RootElement, default))!;
        Assert.Equal(true, status["running"]);
        Assert.Equal("Fake", status["backend"]);
        Assert.Null(await api.CallAsync(null!, "controllers.get", JsonDocument.Parse("""{"controller":"#5"}""").RootElement, default));
        await Assert.ThrowsAsync<ScriptException>(() =>
            api.CallAsync(null!, "controllers.setLed", JsonDocument.Parse("""{"color":"??"}""").RootElement, default));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class CaptureStep : ISubActionHandler
    {
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => "test.capture";
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}

/// <summary>The SDL backend without SDL: only paths that never load the native library or open a device.</summary>
public sealed class SdlBackendTests
{
    [Fact]
    public async Task Start_fails_cleanly_when_the_library_is_missing()
    {
        using var log = new AppLog();
        using var backend = new SdlControllerBackend(log, probe: () => "the SDL3 library was not found");
        var result = await backend.StartAsync(new ControllerBackendOptions()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Ok);
        Assert.Contains("not found", result.Error);
        Assert.False(await backend.RumbleAsync(1, 1, 1, 1));
        Assert.False(await backend.SetLedAsync(1, 1, 1, 1));
        Assert.Equal(-1, await backend.AddMappingsAsync("x"));
        backend.Stop();
        backend.Stop();
    }

    [Theory]
    [InlineData(-1, ControllerPower.Error)]
    [InlineData(0, ControllerPower.Unknown)]
    [InlineData(1, ControllerPower.OnBattery)]
    [InlineData(2, ControllerPower.NoBattery)]
    [InlineData(3, ControllerPower.Charging)]
    [InlineData(4, ControllerPower.Charged)]
    public void Power_states_map(int sdl, ControllerPower power) => Assert.Equal(power, SdlControllerBackend.Power(sdl));

    [Fact]
    public void Event_layout_matches_sdl_events_h()
    {
        Assert.Equal(128, System.Runtime.InteropServices.Marshal.SizeOf<SdlNative.Event>());
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<SdlNative.Guid>());
        Assert.Equal(16, (int)System.Runtime.InteropServices.Marshal.OffsetOf<SdlNative.Event>(nameof(SdlNative.Event.Which)));
        Assert.Equal(24, (int)System.Runtime.InteropServices.Marshal.OffsetOf<SdlNative.Event>(nameof(SdlNative.Event.AxisValue)));
    }
}
