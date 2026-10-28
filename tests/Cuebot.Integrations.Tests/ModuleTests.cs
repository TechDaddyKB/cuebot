using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.Tests.Support;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Integrations.Tests;

public sealed class TriggerFilterTests
{
    private static readonly TestEnvironment Env = new();

    private static ITriggerType Trigger(ITriggerProvider provider, string id) => provider.GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, string type, Dictionary<string, object?> args, Dictionary<string, string>? settings = null) =>
        trigger.EventTypes.Contains(type)
        && trigger.Matches(new BotEvent(type, args), new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    private static VTubeStudioTriggers Vts() => new(new VTubeStudioService(Env.Settings, Env.Bus, Env.Log));
    private static VoicemodTriggers Voicemod() => new(new VoicemodService(Env.Settings, Env.Bus, Env.Log));

    [Fact]
    public void Every_trigger_has_a_unique_id_in_its_app_category()
    {
        foreach (var (provider, category, prefix) in new (ITriggerProvider, string, string)[]
                 { (Vts(), "VTube Studio", "vtubestudio."), (Voicemod(), "Voicemod", "voicemod.") })
        {
            var triggers = provider.GetTriggers().ToArray();
            Assert.All(triggers, t =>
            {
                Assert.Equal(category, t.Category);
                Assert.StartsWith(prefix, t.TypeId);
                Assert.StartsWith(prefix, Assert.Single(t.EventTypes));
            });
            Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        }
    }

    [Fact]
    public void Vts_filters()
    {
        var hotkey = Trigger(Vts(), VTubeStudioEvents.HotkeyTriggered);
        var wave = new Dictionary<string, object?> { ["hotkeyName"] = "Wave", ["hotkeyId"] = "hk-1", ["hotkeyAction"] = "TriggerAnimation", ["triggeredByApi"] = true };
        Assert.True(Matches(hotkey, VTubeStudioEvents.HotkeyTriggered, wave));
        Assert.True(Matches(hotkey, VTubeStudioEvents.HotkeyTriggered, wave, new() { ["hotkeyName"] = "Jump, wave" }));
        Assert.True(Matches(hotkey, VTubeStudioEvents.HotkeyTriggered, wave, new() { ["hotkeyName"] = "hk-1" }));
        Assert.False(Matches(hotkey, VTubeStudioEvents.HotkeyTriggered, wave, new() { ["hotkeyName"] = "Jump" }));
        Assert.False(Matches(hotkey, VTubeStudioEvents.HotkeyTriggered, wave, new() { ["ignoreApi"] = "true" }));
        Assert.False(Matches(hotkey, VTubeStudioEvents.HotkeyTriggered, wave, new() { ["hotkeyAction"] = "ToggleExpression" }));

        var added = Trigger(Vts(), "vtubestudio.itemAdded");
        var item = new Dictionary<string, object?> { ["itemEventType"] = "Added", ["itemFileName"] = "hat.png" };
        Assert.True(Matches(added, VTubeStudioEvents.ItemEvent, item));
        Assert.False(Matches(Trigger(Vts(), "vtubestudio.itemRemoved"), VTubeStudioEvents.ItemEvent, item));
        Assert.False(Matches(added, VTubeStudioEvents.ItemEvent, item, new() { ["itemFileName"] = "cat.gif" }));
        Assert.True(Matches(Trigger(Vts(), VTubeStudioEvents.ItemEvent), VTubeStudioEvents.ItemEvent, item, new() { ["itemEventType"] = "Added" }));

        var animation = Trigger(Vts(), VTubeStudioEvents.ModelAnimation);
        var idle = new Dictionary<string, object?> { ["animationEventType"] = "Start", ["animationName"] = "idle", ["isIdleAnimation"] = true };
        Assert.False(Matches(animation, VTubeStudioEvents.ModelAnimation, idle));
        Assert.True(Matches(animation, VTubeStudioEvents.ModelAnimation, idle, new() { ["ignoreIdle"] = "false", ["animationEventType"] = "Start" }));

        var click = Trigger(Vts(), VTubeStudioEvents.ModelClicked);
        var miss = new Dictionary<string, object?> { ["mouseButton"] = "Left", ["modelWasClicked"] = false };
        Assert.False(Matches(click, VTubeStudioEvents.ModelClicked, miss));
        Assert.True(Matches(click, VTubeStudioEvents.ModelClicked, miss, new() { ["onlyOnModel"] = "false", ["mouseButton"] = "Left" }));
    }

    [Fact]
    public void Voicemod_filters()
    {
        var voice = Trigger(Voicemod(), VoicemodEvents.VoiceChanged);
        var baby = new Dictionary<string, object?> { ["voiceName"] = "Baby", ["voiceId"] = "baby" };
        Assert.True(Matches(voice, VoicemodEvents.VoiceChanged, baby, new() { ["voice"] = "BABY" }));
        Assert.False(Matches(voice, VoicemodEvents.VoiceChanged, baby, new() { ["voice"] = "Cave" }));

        var toggled = Trigger(Voicemod(), VoicemodEvents.VoiceChangerToggled);
        var off = new Dictionary<string, object?> { ["enabled"] = false };
        Assert.True(Matches(toggled, VoicemodEvents.VoiceChangerToggled, off));
        Assert.True(Matches(toggled, VoicemodEvents.VoiceChangerToggled, off, new() { ["stateFilter"] = "Off" }));
        Assert.False(Matches(toggled, VoicemodEvents.VoiceChangerToggled, off, new() { ["stateFilter"] = "On" }));
        var mute = Trigger(Voicemod(), VoicemodEvents.MuteToggled);
        Assert.True(Matches(mute, VoicemodEvents.MuteToggled, new() { ["muted"] = true, ["enabled"] = true }, new() { ["stateFilter"] = "On" }));
    }
}

public sealed class SecretsTests
{
    [Fact]
    public void Mask_and_redact()
    {
        Assert.Equal("", Secrets.Mask(""));
        Assert.Equal("••••••••", Secrets.Mask("short"));
        Assert.Equal("••••••••WXYZ", Secrets.Mask("0123456789abcdefWXYZ"));
        const string secret = "a+b/c&d";
        var text = $"url ?token={Uri.EscapeDataString(secret)} and {secret} end";
        Assert.Equal("url ?token=*** and *** end", Secrets.Redact(text, secret));
        Assert.Equal("nothing", Secrets.Redact("nothing", null, ""));
    }
}

public sealed class ModuleRegistrationTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-integrations-di-").FullName;
    private readonly ServiceProvider _services;

    public ModuleRegistrationTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddIntegrations().BuildServiceProvider();

    [Fact]
    public async Task Registers_services_statuses_triggers_sub_actions_and_script_functions()
    {
        var statuses = _services.GetServices<IConnectionStatus>().Select(s => s.Name).ToArray();
        Assert.Contains("VTube Studio", statuses);
        Assert.Contains("Voicemod", statuses);
        Assert.Contains("Pulsoid", statuses);
        Assert.Contains("HypeRate", statuses);
        Assert.Contains("StreamLoots", statuses);
        Assert.Contains("Crowd Control", statuses);
        Assert.Contains(_services.GetServices<Cuebot.Core.Chat.IChatEventAdapter>(), a => a.Platform == "StreamLoots");
        Assert.Contains(_services.GetServices<Cuebot.Core.Chat.IChatEventAdapter>(), a => a.Platform == "Crowd Control");
        Assert.Same(_services.GetRequiredService<HeartRateHub>(), _services.GetRequiredService<PulsoidService>().Hub);
        Assert.Same(_services.GetRequiredService<HeartRateHub>(), _services.GetRequiredService<HypeRateService>().Hub);

        var registry = _services.GetRequiredService<Registry>();
        var set = _services.GetRequiredService<IntegrationsServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(set.All.Select(h => h.TypeId).Order(), IntegrationsServiceCollectionExtensions.SubActionSet.TypeIds.Order());
        foreach (var id in IntegrationsServiceCollectionExtensions.SubActionSet.TypeIds) Assert.NotNull(registry.GetSubAction(id));
        Assert.All(set.All, h => Assert.Contains(h.Category, new[] { "VTube Studio", "Voicemod", "Heart Rate", "StreamLoots", "Crowd Control" }));
        foreach (var id in new[] { "vtubestudio.hotkeyTriggered", "vtubestudio.modelLoaded", "vtubestudio.trackingLost", "vtubestudio.itemAdded",
                     "voicemod.voiceChanged", "voicemod.voiceChangerToggled", "pulsoid.heartRate", "pulsoid.heartRateAbove",
                     "hyperate.heartRateChanged", "hyperate.heartRateBelow", "hyperate.clipCreated",
                     "streamloots.cardRedeemed", "streamloots.packGifted", "crowdcontrol.effectSuccess", "crowdcontrol.coinExchange" })
            Assert.NotNull(registry.GetTrigger(id));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "vtubestudio.triggerHotkey", "vtubestudio.loadModel", "vtubestudio.injectParameters", "voicemod.selectVoice",
                     "voicemod.playSound", "voicemod.setVoiceChanger", "heartrate.get", "pulsoid.isConnected", "hyperate.isConnected",
                     "streamloots.isConnected", "crowdcontrol.getSession" })
            Assert.Contains(name, functions);

        // Defaults: auto-connect off, so StartAsync never connects.
        var settings = _services.GetRequiredService<JsonFile<IntegrationsSettings>>().Value;
        Assert.False(settings.VTubeStudio.AutoConnect);
        Assert.False(settings.Voicemod.AutoConnect);
        Assert.False(settings.Pulsoid.AutoConnect);
        Assert.False(settings.HypeRate.AutoConnect);
        Assert.False(settings.StreamLoots.AutoConnect);
        Assert.False(settings.CrowdControl.AutoConnect);
        Assert.Equal(8001, settings.VTubeStudio.Port);
        await _services.GetRequiredService<VTubeStudioService>().StartAsync();
        await _services.GetRequiredService<PulsoidService>().StartAsync();
        Assert.False(_services.GetRequiredService<VTubeStudioService>().IsRunning);
        Assert.Equal(ConnectionState.Disconnected, _services.GetRequiredService<PulsoidService>().State);
    }

    [Fact]
    public async Task Sub_actions_fail_cleanly_when_disconnected_and_get_heart_rate_reads_the_hub()
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        var capture = new CaptureStep();

        var action = new ActionDefinition
        {
            Name = "hr",
            SubActions =
            [
                new SubActionDefinition { Type = "heartrate.get", Parameters = new() { ["source"] = "HypeRate" } },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        // A container with a capture step, so the engine's final arguments can be inspected.
        await using var withCapture = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "run")))
            .AddIntegrations().AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        withCapture.GetRequiredService<HeartRateHub>().Report("HypeRate", 77);
        Assert.True(await withCapture.GetRequiredService<ActionEngine>().RunAsync(action));
        Assert.Equal(77, capture.Args!["heartRate"]);
        Assert.Equal("HypeRate", capture.Args["heartRateSource"]);
        Assert.Equal(true, capture.Args["heartRateAvailable"]);
        Assert.Equal("77", withCapture.GetRequiredService<GlobalVariables>().Get("hyperateHeartRate"));

        var hotkey = new ActionDefinition { Name = "vts", SubActions = [new SubActionDefinition { Type = "vtubestudio.triggerHotkey", Parameters = new() { ["hotkey"] = "Wave" } }] };
        await engine.RunAsync(hotkey);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("VTube Studio is not connected") == true);
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

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* settings flush */ }
    }
}

/// <summary>Sub-actions run through the real engine against fake apps.</summary>
public sealed class SubActionEngineTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-integrations-steps-").FullName;
    private readonly FakeServer _server = new();
    private readonly ServiceProvider _services;
    private readonly Capture _capture = new();

    public SubActionEngineTests()
    {
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddIntegrations();
        // Later registrations win: the services talk to in-memory fakes.
        collection.AddSingleton(sp => new VTubeStudioService(sp.GetRequiredService<JsonFile<IntegrationsSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), _server.Create, TestTimings.Fast));
        collection.AddSingleton(sp => new VoicemodService(sp.GetRequiredService<JsonFile<IntegrationsSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), _server.Create, TestTimings.Fast));
        collection.AddSingleton<ISubActionHandler>(_capture);
        _services = collection.BuildServiceProvider();
    }

    [Fact]
    public async Task Voicemod_get_current_voice_sets_streamer_bot_currentVoiceId()
    {
        _services.GetRequiredService<JsonFile<IntegrationsSettings>>().Value.Voicemod.ClientKey = FakeVoicemod.ValidKey;
        var voicemod = _services.GetRequiredService<VoicemodService>();
        await voicemod.ConnectAsync();
        _ = new FakeVoicemod(await _server.NextAsync());
        await Wait.UntilAsync(() => voicemod.State == ConnectionState.Connected, "connected");

        var args = await RunAsync("voicemod.getCurrentVoice", []);

        Assert.Equal("nofx", args["currentVoiceId"]);
        Assert.Equal("nofx", args["voicemodVoiceId"]);
    }

    private async Task<Dictionary<string, object?>> RunAsync(string type, Dictionary<string, string> parameters)
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        var ok = await engine.RunAsync(new ActionDefinition
        {
            Name = type,
            SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = Capture.Id }],
        });
        Assert.True(ok && _capture.Args is not null, engine.History.Entries.FirstOrDefault(e => e.Error is not null)?.Error);
        return _capture.Args!;
    }

    [Fact]
    public async Task Vts_sub_actions_store_their_results()
    {
        var vts = _services.GetRequiredService<VTubeStudioService>();
        await vts.ConnectAsync();
        var fake = new FakeVts(await _server.NextAsync());
        await Wait.UntilAsync(() => vts.IsConnected && vts.Hotkeys.Count > 0, "connected");

        var model = await RunAsync("vtubestudio.getCurrentModel", []);
        Assert.Equal("Akari", model["vtsModelName"]);
        Assert.Equal(-40.0, model["vtsSize"]);
        Assert.Equal("model-akari", model["model.id"]);
        Assert.Equal("Akari", model["model.name"]);
        Assert.Equal("Akari.vtube.json", model["model.vtsModelName"]);
        Assert.Equal("icon.png", model["model.vtsModelIconName"]);
        Assert.Equal("Akari.model3.json", model["model.live2DModelName"]);
        Assert.Equal(1680L, model["model.loadTime"]);
        Assert.Equal(419903L, model["model.timeSinceLoaded"]);
        Assert.Equal(29L, model["model.numberOfLive2DParameters"]);
        Assert.Equal(136L, model["model.numberOfLive2DArtmeshes"]);
        Assert.Equal(true, model["model.hasPhysicsFile"]);
        Assert.Equal(1L, model["model.numberOfTextures"]);
        Assert.Equal(1024L, model["model.textureResolution"]);
        Assert.Equal(0.1, model["model.posX"]);
        Assert.Equal(-0.2, model["model.posY"]);
        Assert.Equal(5.0, model["model.rotation"]);
        Assert.Equal(-40.0, model["model.size"]);
        var hotkeys = await RunAsync("vtubestudio.getHotkeys", []);
        Assert.Equal("Wave, Heart Eyes", hotkeys["vtsHotkeys"]);
        Assert.Equal("hk-wave", (await RunAsync("vtubestudio.triggerHotkey", new() { ["hotkey"] = "Wave" }))["vtsHotkeyId"]);
        Assert.Equal("item-new", (await RunAsync("vtubestudio.loadItem", new() { ["fileName"] = "cat.gif" }))["vtsItemInstanceId"]);
        await RunAsync("vtubestudio.injectParameters", new() { ["parameters"] = "FaceAngleX=5", ["holdSeconds"] = "0" });
        Assert.Single(fake.Of("InjectParameterDataRequest"));
        var raw = await RunAsync("vtubestudio.rawRequest", new() { ["messageType"] = "StatisticsRequest" });
        Assert.Equal("{}", raw["vtsResult"]);
        var rawModel = await RunAsync("vtubestudio.rawRequest", new() { ["messageType"] = "CurrentModelRequest", ["variablePrefix"] = "vts" });
        Assert.Equal("Akari", rawModel["vts.data.modelName"]);
        Assert.Equal(true, rawModel["vts.data.modelLoaded"]);
        Assert.Equal(-40.0, rawModel["vts.data.modelPosition.size"]);
        var rawModels = await RunAsync("vtubestudio.rawRequest", new() { ["messageType"] = "AvailableModelsRequest" });
        Assert.Equal("Hiyori", rawModels["vtsRaw.data.availableModels[1].modelName"]);

        // Suggestions come from live data.
        var registry = _services.GetRequiredService<Registry>();
        var trigger = registry.GetSubAction("vtubestudio.triggerHotkey")!.Parameters.Single(p => p.Key == "hotkey");
        Assert.Equal(["Wave", "Heart Eyes"], trigger.Suggestions!());
        var load = registry.GetSubAction("vtubestudio.loadModel")!.Parameters.Single(p => p.Key == "model");
        Assert.Contains("Hiyori", load.Suggestions!());
    }

    private sealed class Capture : ISubActionHandler
    {
        public const string Id = "test.capture";
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* settings flush */ }
    }
}
