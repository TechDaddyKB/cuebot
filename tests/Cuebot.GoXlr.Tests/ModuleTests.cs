using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.GoXlr.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.GoXlr.Tests;

public sealed class ModuleTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-goxlr-di-").FullName;
    private readonly ServiceProvider _services;

    public ModuleTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddGoXlr().BuildServiceProvider();

    [Fact]
    public void Registers_status_triggers_sub_actions_and_script_functions()
    {
        Assert.Contains("GoXLR", _services.GetServices<IConnectionStatus>().Select(s => s.Name));
        var registry = _services.GetRequiredService<Registry>();
        foreach (var type in GoXlrEvents.All) Assert.NotNull(registry.GetTrigger(type));
        var triggers = new GoXlrTriggers(_services.GetRequiredService<GoXlrService>()).GetTriggers().ToArray();
        Assert.Equal(GoXlrEvents.All.Order(), triggers.Select(t => t.TypeId).Order());
        Assert.All(triggers, t => Assert.Equal("GoXLR", t.Category));

        var set = _services.GetRequiredService<GoXlrServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(GoXlrSubActions.TypeIds.Order(), set.All.Select(h => h.TypeId).Order());
        foreach (var id in GoXlrSubActions.TypeIds) Assert.NotNull(registry.GetSubAction(id));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "goxlr.isConnected", "goxlr.connect", "goxlr.getStatus", "goxlr.setVolume", "goxlr.setFaderMute", "goxlr.loadProfile",
                     "goxlr.setEffect", "goxlr.playSample", "goxlr.command", "goxlr.request" })
            Assert.Contains(name, functions);

        var settings = _services.GetRequiredService<JsonFile<GoXlrSettings>>().Value;
        Assert.False(settings.AutoConnect);
        Assert.True(settings.AutoReconnect);
        Assert.Equal("localhost", settings.Host);
    }

    [Fact]
    public async Task Sub_actions_fail_cleanly_when_disconnected()
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "mute",
            SubActions = [new SubActionDefinition { Type = "goxlr.setFaderMute", Parameters = new() { ["target"] = "A", ["state"] = "Mute" } }],
        };
        await engine.RunAsync(action);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("GoXLR Utility is not connected") == true);
    }

    [Fact]
    public async Task Triggers_filter_and_sub_actions_drive_the_utility_through_the_engine()
    {
        await using var fake = FakeGoXlrUtility.Start();
        var settings = _services.GetRequiredService<JsonFile<GoXlrSettings>>();
        settings.Value.Port = fake.Port;
        var goXlr = _services.GetRequiredService<GoXlrService>();
        await goXlr.ConnectAsync();
        await Wait.UntilAsync(() => goXlr.IsConnected, "connected");

        var engine = _services.GetRequiredService<ActionEngine>();
        var profiles = _services.GetRequiredService<ProfileService>();
        var capture = new CaptureStep();
        var fired = new List<string>();
        profiles.Profile.Actions.Add(new ActionDefinition
        {
            Name = "on music mute",
            Triggers = [new TriggerDefinition { Type = GoXlrEvents.FaderMuteChanged, Settings = new() { ["channel"] = "music", ["stateFilter"] = "Muted" } }],
            SubActions = [new SubActionDefinition { Type = "goxlr.getStatus" }],
        });
        profiles.NotifyChanged();
        engine.RunCompleted += r => { lock (fired) fired.Add(r.ActionName); };
        engine.Start();

        var run = new ActionDefinition
        {
            Name = "drive",
            SubActions =
            [
                new SubActionDefinition { Type = "goxlr.setVolume", Parameters = new() { ["channel"] = "Line In", ["volume"] = "20" } },
                new SubActionDefinition { Type = "goxlr.setFaderMute", Parameters = new() { ["target"] = "Music", ["state"] = "Mute" } },
                new SubActionDefinition { Type = "goxlr.setEffect", Parameters = new() { ["effect"] = "Robot", ["state"] = "On" } },
                new SubActionDefinition { Type = "goxlr.sendCommand", Parameters = new() { ["command"] = "{\"SetVolume\": [\"Chat\", 0]}" } },
                new SubActionDefinition { Type = "goxlr.sendCommand", Parameters = new() { ["command"] = "\"Ping\"", ["raw"] = "true" } },
            ],
        };
        Assert.True(await engine.RunAsync(run));
        Assert.Contains(fake.Requests, r => r.EndsWith("""{"SetVolume":["LineIn",51]}]}""", StringComparison.Ordinal));
        Assert.Contains(fake.Requests, r => r.EndsWith("""{"SetRobotEnabled":true}]}""", StringComparison.Ordinal));
        Assert.Contains(fake.Requests, r => r.EndsWith("""{"SetVolume":["Chat",0]}]}""", StringComparison.Ordinal));
        await Wait.UntilAsync(() => { lock (fired) return fired.Contains("on music mute"); }, "trigger fired");

        await Wait.UntilAsync(() => goXlr.Mixer().Faders["B"].Muted && goXlr.Mixer().Volume("LineIn") == 51, "state");
        await using var drive = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "drive")))
            .AddSingleton(_services.GetRequiredService<GoXlrServiceCollectionExtensions.SubActionSet>().Get("goxlr.getStatus"))
            .AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        Assert.True(await drive.GetRequiredService<ActionEngine>().RunAsync(new ActionDefinition
        {
            Name = "status",
            SubActions = [new SubActionDefinition { Type = "goxlr.getStatus" }, new SubActionDefinition { Type = "test.capture" }],
        }));
        var a = capture.Args!;
        Assert.Equal(FakeGoXlrUtility.Serial, a["goXlrSerial"]);
        Assert.Equal("Music", a["goXlrFaderBChannel"]);
        Assert.Equal(true, a["goXlrFaderBMuted"]);
        Assert.Equal(20, a["goXlrVolumeLineIn"]);
        Assert.Equal("Default", a["goXlrProfile"]);
        Assert.Equal(true, a["goXlrRobotEnabled"]);
        Assert.Equal("Default", a["goXlrPresetName"]);

        // Script functions.
        var api = _services.GetRequiredService<ScriptApi>();
        ActionRunContext ctx = null!;
        Assert.Equal(true, await api.CallAsync(ctx, "goxlr.isConnected", null, CancellationToken.None));
        Assert.Equal(100, await api.CallAsync(ctx, "goxlr.setVolume", new { channel = "mic", volume = 100 }, CancellationToken.None));
        Assert.Equal(true, await api.CallAsync(ctx, "goxlr.command", new { command = "{\"SetFXEnabled\": true}" }, CancellationToken.None));
        Assert.Equal("Ok", await api.CallAsync(ctx, "goxlr.request", new { request = "Ping" }, CancellationToken.None));
        await goXlr.DisconnectAsync();
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
