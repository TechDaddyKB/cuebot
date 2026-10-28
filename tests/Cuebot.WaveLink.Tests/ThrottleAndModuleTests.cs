using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.WaveLink.Model;
using Cuebot.WaveLink.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.WaveLink.Tests;

public sealed class LevelMeterThrottleTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly List<IReadOnlyList<WaveLinkLevel>> _flushes = [];
    private double _rate = 10;

    private LevelMeterThrottle Create() => new(_time, () => _rate, _flushes.Add);

    private static WaveLinkLevel Level(string id, double left, double right = 0) => new("channel", id, "", id, left, right);

    [Fact]
    public void First_reading_flushes_at_once_then_coalesces_until_the_next_slot()
    {
        using var throttle = Create();
        throttle.Offer([Level("mic", 10)]);
        Assert.Single(_flushes);

        // Within the 100 ms slot: nothing goes out, the latest value wins.
        for (var i = 1; i <= 9; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            throttle.Offer([Level("mic", 10 + i * 5), Level("music", 3)]);
        }
        Assert.Single(_flushes);
        Assert.Equal(1, _time.PendingTimers);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(2, _flushes.Count);
        Assert.Equal(55, _flushes[1].Single(l => l.Id == "mic").Left);
        Assert.Equal(3, _flushes[1].Single(l => l.Id == "music").Left);
    }

    [Fact]
    public void Rate_caps_flushes_per_second_and_unchanged_meters_are_not_repeated()
    {
        using var throttle = Create();
        // 60 messages per second for two seconds, one meter rising and one standing still.
        for (var i = 0; i < 120; i++)
        {
            throttle.Offer([Level("mic", i % 100), Level("still", 42)]);
            _time.Advance(TimeSpan.FromMilliseconds(1000.0 / 60));
        }
        Assert.InRange(_flushes.Count, 19, 21);   // 10 per second
        Assert.Single(_flushes, f => f.Any(l => l.Id == "still"));   // sent once, then only when it moves

        _flushes.Clear();
        _rate = 2;
        for (var i = 0; i < 60; i++)
        {
            throttle.Offer([Level("mic", i % 100)]);
            _time.Advance(TimeSpan.FromMilliseconds(1000.0 / 60));
        }
        Assert.InRange(_flushes.Count, 2, 3);
    }

    [Fact]
    public void Tiny_changes_and_silence_produce_no_flush()
    {
        using var throttle = Create();
        throttle.Offer([Level("mic", 0)]);
        _time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
        {
            throttle.Offer([Level("mic", i % 2 * (LevelMeterThrottle.MinimumChange / 2))]);
            _time.Advance(TimeSpan.FromMilliseconds(150));
        }
        Assert.Single(_flushes);

        throttle.Reset();
        throttle.Offer([Level("mic", 0)]);
        Assert.Equal(2, _flushes.Count);   // after a reset the meter is new again
    }
}

public sealed class ModuleRegistrationTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-wavelink-di-").FullName;
    private readonly ServiceProvider _services;

    public ModuleRegistrationTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddWaveLink().BuildServiceProvider();

    [Fact]
    public async Task Registers_status_triggers_sub_actions_and_script_functions()
    {
        Assert.Contains("Wave Link", _services.GetServices<IConnectionStatus>().Select(s => s.Name));
        var registry = _services.GetRequiredService<Registry>();

        // Wave Link 3: Streamer.bot's 19 triggers; legacy: its 20 events (Connected/Disconnected are shared).
        var v3 = new[]
        {
            "wavelink.connected", "wavelink.disconnected", "wavelink.channelEffectsChanged", "wavelink.channelMuteChanged", "wavelink.channelUpdated",
            "wavelink.channelVolumeChanged", "wavelink.channelsChanged", "wavelink.inputDspEffectsChanged", "wavelink.inputDeviceUpdated",
            "wavelink.inputDevicesChanged", "wavelink.inputGainChanged", "wavelink.mixUpdated", "wavelink.mixesChanged", "wavelink.mainOutputChanged",
            "wavelink.outputDeviceUpdated", "wavelink.outputMixChanged", "wavelink.createProfileRequested", "wavelink.focusedAppChanged", "wavelink.levelMeter",
        };
        Assert.Equal(19, v3.Length);
        foreach (var id in v3) Assert.NotNull(registry.GetTrigger(id));
        Assert.Equal(18, WaveLinkTriggers.LegacyTriggers.Length);
        foreach (var t in WaveLinkTriggers.LegacyTriggers) Assert.NotNull(registry.GetTrigger(t.TypeId));
        var triggers = new WaveLinkTriggers(_services.GetRequiredService<WaveLinkService>()).GetTriggers().ToArray();
        Assert.Equal(triggers.Length, triggers.Select(t => t.TypeId).Distinct().Count());
        Assert.All(triggers, t => Assert.All(t.EventTypes, e => Assert.Contains(e, WaveLinkEvents.All)));

        // 21 + 2 Wave Link 3 sub-actions, 16 legacy ones.
        var set = _services.GetRequiredService<WaveLinkServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(set.All.Select(h => h.TypeId).Order(), WaveLinkSubActions.TypeIds.Order());
        Assert.Equal(23, set.All.Count(h => h.Category == "Wave Link"));
        Assert.Equal(16, set.All.Count(h => h.Category == "Wave Link (legacy)"));
        foreach (var id in WaveLinkSubActions.TypeIds) Assert.NotNull(registry.GetSubAction(id));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "wavelink.isConnected", "wavelink.connect", "wavelink.getStatus", "wavelink.getChannels", "wavelink.setChannelMute",
                     "wavelink.setChannelVolume", "wavelink.setMixVolume", "wavelink.setInputGain", "wavelink.setMonitoredMix", "wavelink.switchMonitoring" })
            Assert.Contains(name, functions);

        var settings = _services.GetRequiredService<JsonFile<WaveLinkSettings>>().Value;
        Assert.False(settings.AutoConnect);
        Assert.True(settings.AutoReconnect);
        Assert.False(settings.LevelMeters);
        Assert.Equal(10, settings.LevelMeterRate);
        Assert.Equal(0, settings.Port);
        await _services.GetRequiredService<WaveLinkService>().StartAsync();
        Assert.False(_services.GetRequiredService<WaveLinkService>().IsRunning);
    }

    [Fact]
    public async Task Sub_actions_fail_cleanly_when_disconnected()
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "mute",
            SubActions = [new SubActionDefinition { Type = "wavelink.muteChannel", Parameters = new() { ["channel"] = "Music", ["state"] = "Muted" } }],
        };
        await engine.RunAsync(action);
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("Wave Link is not connected") == true);
    }

    [Fact]
    public async Task Sub_actions_drive_a_connected_wave_link_and_set_variables()
    {
        var env = new TestEnvironment();
        env.Network.Listen(1884, FakeWaveLink.V3);
        await using var wl = env.Service();
        var handlers = WaveLinkSubActions.Create(wl).ToDictionary(h => h.TypeId);
        await wl.ConnectAsync();
        var app = await env.Network.AppAsync(1884);
        await Wait.UntilAsync(() => wl.IsConnected, "connected");

        var capture = new CaptureStep();
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(Path.Combine(_dir, "drive")))
            .AddSingleton(handlers["wavelink.getChannelInfo"]).AddSingleton(handlers["wavelink.setChannelMixMute"])
            .AddSingleton(handlers["wavelink.getMixInfo"]).AddSingleton(handlers["wavelink.getSelectedOutput"])
            .AddSingleton(handlers["wavelink.legacy.getOutputVolumes"]).AddSingleton(handlers["wavelink.getInputDeviceInfo"])
            .AddSingleton<ISubActionHandler>(capture).BuildServiceProvider();
        var engine = services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "drive",
            SubActions =
            [
                new SubActionDefinition { Type = "wavelink.getChannelInfo", Parameters = new() { ["channel"] = "Music" } },
                new SubActionDefinition { Type = "wavelink.setChannelMixMute", Parameters = new() { ["channel"] = "%waveLinkChannelId%", ["mix"] = "Stream Mix", ["state"] = "Toggle" } },
                new SubActionDefinition { Type = "wavelink.getMixInfo", Parameters = new() { ["mix"] = "Personal Mix" } },
                new SubActionDefinition { Type = "wavelink.getSelectedOutput" },
                new SubActionDefinition { Type = "wavelink.legacy.getOutputVolumes" },
                new SubActionDefinition { Type = "wavelink.getInputDeviceInfo" },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        Assert.True(await engine.RunAsync(action));
        var a = capture.Args!;
        Assert.Equal("Music", a["waveLinkChannelName"]);
        Assert.Equal(50.0, a["waveLinkChannelVolume"]);
        Assert.Equal("Stream Mix", a["waveLinkChannelMix.1.name"]);
        foreach (var field in new[] { "id", "name", "level", "muted" })   // Streamer.bot's waveLinkChannelMix.#.*
            Assert.True(a.ContainsKey($"waveLinkChannelMix.0.{field}"), field);
        Assert.IsType<double>(a["waveLinkChannelMix.0.level"]);
        Assert.IsType<bool>(a["waveLinkChannelMix.0.muted"]);
        Assert.Equal("Personal Mix", a["waveLinkMixName"]);
        Assert.Equal(100.0, a["waveLinkMixVolume"]);
        Assert.Equal("Headphones (Wave:3)", a["waveLinkOutputDeviceName"]);
        Assert.Equal(100, a["localVolume"]);
        Assert.Equal(85, a["streamVolume"]);
        Assert.Equal("Wave:3", a["waveLinkInputDeviceName"]);
        Assert.Equal(50.0, a["waveLinkInputDeviceGain"]);
        Assert.Equal("Wave:3", a["waveLinkInputChannelName"]);
        var call = await app.NextCallAsync("setChannel");
        Assert.False(call!["mixes"]![0]!["isMuted"]!.GetValue<bool>());   // was muted in the stream mix
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
