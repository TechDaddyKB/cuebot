using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Lights;
using Cuebot.Elgato.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Elgato.Tests;

public sealed class ModuleRegistrationTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-elgato-di-").FullName;
    private readonly ServiceProvider _services;

    public ModuleRegistrationTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddElgato().BuildServiceProvider();

    [Fact]
    public void Registers_statuses_triggers_sub_actions_and_script_functions()
    {
        var statuses = _services.GetServices<IConnectionStatus>().Select(s => s.Name).ToArray();
        Assert.Contains("Elgato Lights", statuses);
        Assert.Contains("Camera Hub", statuses);

        var registry = _services.GetRequiredService<Registry>();
        var set = _services.GetRequiredService<ElgatoServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(ElgatoServiceCollectionExtensions.SubActionSet.TypeIds.Order(), set.All.Select(h => h.TypeId).Order());
        Assert.All(ElgatoServiceCollectionExtensions.SubActionSet.TypeIds, id => Assert.NotNull(registry.GetSubAction(id)));

        var triggers = _services.GetServices<ITriggerProvider>().SelectMany(p => p.GetTriggers()).Where(t => t.TypeId.StartsWith("elgato.")).ToArray();
        Assert.Equal(12 + 26, triggers.Length);
        Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        Assert.All(triggers, t => Assert.Contains(t.Category, new[] { "Elgato Control Center", "Elgato Camera Hub" }));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        Assert.Contains("elgato.lights.setBrightness", functions);
        Assert.Contains("elgato.cameraHub.set", functions);
        Assert.Contains("elgato.studio.isRunning", functions);
    }

    [Fact]
    public void Device_filters_match_names_ids_and_addresses()
    {
        var trigger = _services.GetServices<ITriggerProvider>().SelectMany(p => p.GetTriggers()).Single(t => t.TypeId == LightEvents.BrightnessChanged);
        var args = new Dictionary<string, object?> { ["deviceId"] = "CW12", ["deviceName"] = "Left", ["ipAddress"] = "10.0.0.2" };
        bool Matches(string filter) => trigger.Matches(new BotEvent(LightEvents.BrightnessChanged, args), new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["device"] = filter }), new Dictionary<string, object?>());
        Assert.True(Matches(""));
        Assert.True(Matches("right, left"));
        Assert.True(Matches("10.0.0.2"));
        Assert.False(Matches("Right"));
    }

    [Fact]
    public async Task Sub_actions_run_against_a_fake_light()
    {
        await using var server = new FakeLightServer();
        var capture = new CaptureStep();
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "run")))
            .AddElgato().AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        await services.GetRequiredService<ElgatoLightsService>().AddLightAsync($"127.0.0.1:{server.Port}");
        var engine = services.GetRequiredService<ActionEngine>();

        var action = new ActionDefinition
        {
            Name = "Lights",
            SubActions =
            [
                new SubActionDefinition { Type = "elgato.controlCenter.setPowerState", Parameters = new() { ["device"] = "All", ["state"] = "On" } },
                new SubActionDefinition
                {
                    Type = "elgato.controlCenter.setTemperature", Parameters = new() { ["device"] = "Elgato Key Light", ["temperature"] = "3200" },
                },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        Assert.True(await engine.RunAsync(action));
        Assert.True(server.On);
        Assert.Equal(313, server.Temperature);
        Assert.Equal(3200, capture.Args!["temperature"]);
        Assert.Equal(1, capture.Args["elgatoLightCount"]);
        Assert.Equal("On", capture.Args["powerState"]);

        var missing = new ActionDefinition
        {
            Name = "missing",
            SubActions = [new SubActionDefinition { Type = "elgato.controlCenter.setBrightness", Parameters = new() { ["device"] = "Nope" } }],
        };
        await engine.RunAsync(missing);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("No Elgato light matches") == true);
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

    [Fact]
    public void Camera_hub_sub_actions_cover_the_uvc_controls()
    {
        Assert.Equal(14, CameraHubSubActions.TypeIds.Length);
        Assert.Contains("elgato.cameraHub.webcam.setZoom", CameraHubSubActions.TypeIds);
        Assert.Equal(6, LightSubActions.TypeIds.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
