using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Meld;
using Cuebot.StreamApps.PolyPop;
using Cuebot.StreamApps.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Tests;

public sealed class MeldTriggerTests
{
    private static readonly TestEnvironment Env = new();

    private static ITriggerType Trigger(string id) =>
        new MeldTriggers(new MeldStudioService(Env.Settings, Env.Bus, Env.Log)).GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, string type, Dictionary<string, object?> args, Dictionary<string, string>? settings = null) =>
        trigger.EventTypes.Contains(type)
        && trigger.Matches(new BotEvent(type, args), new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    [Fact]
    public void Trigger_ids_are_unique_and_in_the_meld_category()
    {
        var triggers = new MeldTriggers(new MeldStudioService(Env.Settings, Env.Bus, Env.Log)).GetTriggers().ToArray();
        Assert.All(triggers, t =>
        {
            Assert.Equal("Meld Studio", t.Category);
            Assert.StartsWith("meldstudio.", t.TypeId);
        });
        Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        Assert.All(new PolyPopTriggers().GetTriggers(), t => Assert.StartsWith("polypop.", t.TypeId));
    }

    [Fact]
    public void Filters_match_scene_event_layer_and_state()
    {
        var scene = Trigger(MeldEvents.SceneChanged);
        var args = new Dictionary<string, object?> { ["meldStudio.sceneName"] = "Gaming", ["meldStudio.sceneId"] = "ID1" };
        Assert.True(Matches(scene, MeldEvents.SceneChanged, args));
        Assert.True(Matches(scene, MeldEvents.SceneChanged, args, new() { ["scene"] = "intro, gaming" }));
        Assert.False(Matches(scene, MeldEvents.SceneChanged, args, new() { ["scene"] = "Intro" }));

        var layer = new Dictionary<string, object?>
        {
            ["meldStudioEvent.event"] = MeldEvents.LayerVisibilityChanged, ["meldStudioEvent.layerName"] = "Camera",
            ["meldStudioEvent.layerId"] = "L1", ["meldStudioEvent.visible"] = false,
        };
        var any = Trigger(MeldEvents.Event);
        Assert.True(Matches(any, MeldEvents.Event, layer));
        Assert.True(Matches(any, MeldEvents.Event, layer, new() { ["event"] = MeldEvents.LayerVisibilityChanged }));
        Assert.False(Matches(any, MeldEvents.Event, layer, new() { ["event"] = MeldEvents.ObjectAdded }));

        var visibility = Trigger("meldstudio.layerVisibilityChanged");
        Assert.True(Matches(visibility, MeldEvents.Event, layer, new() { ["layer"] = "camera", ["stateFilter"] = "Hidden" }));
        Assert.False(Matches(visibility, MeldEvents.Event, layer, new() { ["stateFilter"] = "Visible" }));
        Assert.False(Matches(visibility, MeldEvents.Event, layer, new() { ["layer"] = "Webcam" }));
        Assert.False(Matches(Trigger("meldstudio.effectEnabledStateChanged"), MeldEvents.Event, layer));
    }
}

public sealed class ModuleRegistrationTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-streamapps-di-").FullName;
    private readonly ServiceProvider _services;

    public ModuleRegistrationTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddStreamApps().BuildServiceProvider();

    [Fact]
    public async Task Registers_services_statuses_triggers_sub_actions_and_script_functions()
    {
        var statuses = _services.GetServices<IConnectionStatus>().Select(s => s.Name).ToArray();
        Assert.Contains("Meld Studio", statuses);
        Assert.Contains("PolyPop", statuses);

        var registry = _services.GetRequiredService<Registry>();
        var set = _services.GetRequiredService<StreamAppsServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(set.All.Select(h => h.TypeId).Order(), StreamAppsServiceCollectionExtensions.SubActionSet.TypeIds.Order());
        foreach (var id in StreamAppsServiceCollectionExtensions.SubActionSet.TypeIds) Assert.NotNull(registry.GetSubAction(id));
        foreach (var id in new[] { "meldstudio.connected", "meldstudio.sceneChanged", "meldstudio.event", "meldstudio.recordingStarted",
                     "meldstudio.streamingStopped", "meldstudio.layerVisibilityChanged", "meldstudio.trackMutedStateChanged", "polypop.message" })
            Assert.NotNull(registry.GetTrigger(id));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "meldstudio.showScene", "meldstudio.showSceneByName", "meldstudio.showLayer", "meldstudio.hideLayer",
                     "meldstudio.isConnected", "meldstudio.connect", "meldstudio.invoke", "polypop.triggerAlert" })
            Assert.Contains(name, functions);

        var settings = _services.GetRequiredService<JsonFile<StreamAppsSettings>>().Value;
        Assert.False(settings.Meld.AutoConnect);
        Assert.False(settings.PolyPop.AutoStart);
        Assert.Equal(13376, settings.Meld.Port);
        Assert.Equal(9652, settings.PolyPop.Port);
        await _services.GetRequiredService<MeldStudioService>().StartAsync();
        await _services.GetRequiredService<PolyPopService>().StartAsync();
        Assert.False(_services.GetRequiredService<MeldStudioService>().IsRunning);
        Assert.False(_services.GetRequiredService<PolyPopService>().IsRunning);
    }

    [Fact]
    public async Task Sub_actions_report_status_and_fail_cleanly_when_disconnected()
    {
        var capture = new CaptureStep();
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "run")))
            .AddStreamApps().AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        var engine = services.GetRequiredService<ActionEngine>();
        var status = new ActionDefinition
        {
            Name = "status",
            SubActions =
            [
                new SubActionDefinition { Type = "meldstudio.getStatus" },
                new SubActionDefinition { Type = "meldstudio.takeScreenshot" },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        Assert.True(await engine.RunAsync(status));
        Assert.Equal(false, capture.Args!["meldStudio.isConnected"]);
        Assert.Equal(false, capture.Args["meldSutdio.isStreaming"]);
        Assert.Equal(false, capture.Args["screenshotSuccess"]);

        var show = new ActionDefinition { Name = "show", SubActions = [new SubActionDefinition { Type = "meldstudio.showScene", Parameters = new() { ["scene"] = "Gaming" } }] };
        await engine.RunAsync(show);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("Meld Studio is not connected") == true);
        var alert = new ActionDefinition { Name = "alert", SubActions = [new SubActionDefinition { Type = "polypop.triggerAlert", Parameters = new() { ["alertName"] = "Follow" } }] };
        await engine.RunAsync(alert);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("PolyPop is not connected") == true);
    }

    [Fact]
    public async Task Sub_actions_drive_a_connected_Meld()
    {
        var env = new TestEnvironment();
        await using var meld = new MeldStudioService(env.Settings, env.Bus, env.Log, env.Server.Create, TestTimings.Fast);
        var handlers = MeldSubActions.Create(meld).ToDictionary(h => h.TypeId);
        await meld.ConnectAsync();
        var fake = new FakeMeld(await env.Server.NextAsync(), FakeMeld.SampleSession());
        await Wait.UntilAsync(() => meld.IsConnected, "connected");

        var capture = new CaptureStep();
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "drive")))
            .AddSingleton(handlers["meldstudio.getCurrentScene"]).AddSingleton(handlers["meldstudio.setLayerVisibility"])
            .AddSingleton(handlers["meldstudio.takeScreenshot"]).AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        var engine = services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "drive",
            SubActions =
            [
                new SubActionDefinition { Type = "meldstudio.getCurrentScene" },
                new SubActionDefinition { Type = "meldstudio.setLayerVisibility", Parameters = new() { ["scene"] = "%currentScene%", ["layer"] = "Spoilers", ["state"] = "Hidden" } },
                new SubActionDefinition { Type = "meldstudio.takeScreenshot" },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        Assert.True(await engine.RunAsync(action));
        Assert.Equal("Just Chatting", capture.Args!["currentScene"]);
        Assert.Equal(false, capture.Args["meldStudio.layerVisible"]);
        Assert.Equal(true, capture.Args["screenshotSuccess"]);
        Assert.Equal(FakeMeld.Ids.Spoilers, (await fake.NextCallAsync("toggleLayer")).Args[1].GetString());
        Assert.Equal("meld.screenshot", (await fake.NextCallAsync("sendCommand")).Args[0].GetString());
        env.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
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
