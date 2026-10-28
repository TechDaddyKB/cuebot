using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Storage;
using Cuebot.Obs;
using Cuebot.Obs.Streamlabs;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in test against a running Streamlabs Desktop (Settings > Remote Control; CUEBOT_LIVE_SLOBS_TOKEN, optional
/// CUEBOT_LIVE_SLOBS_HOST / _PORT). Nothing goes live: it adds a temporary scene, switches to it and back, toggles a
/// source twice and removes the scene again. CUEBOT_LIVE_SLOBS_RECORD=1 also records for a few seconds.
/// </summary>
public sealed class StreamlabsDesktopLiveTests
{
    private const string TestScene = "Cuebot live test";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [LiveServiceFact("CUEBOT_LIVE_SLOBS_TOKEN")]
    public async Task Streamlabs_Desktop_connects_switches_scenes_toggles_a_source_and_reports_events()
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "slobs");
        Directory.CreateDirectory(dataDirectory);
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddObs().BuildServiceProvider();
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        var settings = services.GetRequiredService<JsonFile<StreamlabsSettings>>().Value;
        settings.Token = ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_TOKEN")!;
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_HOST") is { } host) settings.Host = host;
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_PORT"), out var port)) settings.Port = port;
        var slobs = services.GetRequiredService<StreamlabsDesktopService>();

        await slobs.ConnectAsync();
        await WaitAsync(() => slobs.State == ConnectionState.Connected, () => $"Streamlabs Desktop did not connect ({slobs.StatusMessage})");
        Assert.Contains(events, e => e.Type == StreamlabsEvents.Connected);
        Assert.NotEmpty(slobs.SceneNames);
        var original = await slobs.GetCurrentSceneAsync();
        Assert.Equal(original, slobs.CurrentScene);

        // A scene of our own to switch to, removed again at the end.
        var created = await slobs.CallAsync("ScenesService", "createScene", [TestScene]);
        var testSceneId = created.GetProperty("id").GetString()!;
        try
        {
            await WaitAsync(() => slobs.SceneNames.Contains(TestScene), () => "the new scene did not show up in Cuebot's scene list");
            await slobs.SetSceneAsync(TestScene);
            await WaitAsync(() => events.Any(e => e.Type == StreamlabsEvents.SceneChanged && Equals(e.Args.GetValueOrDefault("sceneName"), TestScene)),
                () => "no scene changed event for the test scene");
            Assert.Equal(TestScene, await slobs.GetCurrentSceneAsync());

            // Toggle the first source of the original scene twice, so it ends as it started.
            var items = await slobs.CallAsync("ScenesService", "getScene", [SceneId(slobs, original)]);
            var nodes = await slobs.CallAsync(items.GetProperty("resourceId").GetString()!, "getItems");
            if (nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() > 0)
            {
                var source = nodes[0].GetProperty("name").GetString()!;
                var first = await slobs.SetSourceVisibilityAsync(original, source, VisibilityMode.Toggle);
                Assert.Equal(!first, await slobs.SetSourceVisibilityAsync(original, source, VisibilityMode.Toggle));
            }

            if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_RECORD") == "1")
            {
                await slobs.ControlRecordingAsync(OutputCommand.Start);
                await WaitAsync(() => events.Any(e => e.Type == StreamlabsEvents.RecordStarted), () => "no recording started event");
                await Task.Delay(TimeSpan.FromSeconds(3));
                await slobs.ControlRecordingAsync(OutputCommand.Stop);
                await WaitAsync(() => events.Any(e => e.Type == StreamlabsEvents.RecordStopped), () => "no recording stopped event");
            }
        }
        finally
        {
            await slobs.SetSceneAsync(original);
            await slobs.CallAsync("ScenesService", "removeScene", [testSceneId]);
        }
        await WaitAsync(() => !slobs.SceneNames.Contains(TestScene), () => "the test scene was not removed from Cuebot's scene list");
        Assert.Equal(original, await slobs.GetCurrentSceneAsync());
        await slobs.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_SLOBS_TOKEN")]
    public async Task Every_source_filter_and_group_command_works_on_a_scratch_scene()
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "slobs-commands");
        Directory.CreateDirectory(dataDirectory);
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddObs().BuildServiceProvider();
        var settings = services.GetRequiredService<JsonFile<StreamlabsSettings>>().Value;
        settings.Token = ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_TOKEN")!;
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_HOST") is { } host) settings.Host = host;
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_PORT"), out var port)) settings.Port = port;
        var slobs = services.GetRequiredService<StreamlabsDesktopService>();
        await slobs.ConnectAsync();
        await WaitAsync(() => slobs.State == ConnectionState.Connected, () => $"Streamlabs Desktop did not connect ({slobs.StatusMessage})");

        // Scratch scene: a colour source with a filter, and a folder holding two more colour sources.
        var scene = await slobs.CallAsync("ScenesService", "createScene", [TestScene]);
        var sceneId = scene.GetProperty("id").GetString()!;
        var sceneResource = scene.GetProperty("resourceId").GetString()!;
        var sourceIds = new List<string>();
        try
        {
            async Task<JsonElement> AddColour(string name)
            {
                var item = await slobs.CallAsync(sceneResource, "createAndAddSource", [name, "color_source", new { color = 4278190335L, width = 64, height = 64 }]);
                sourceIds.Add(item.GetProperty("sourceId").GetString()!);
                return item;
            }
            var box = await AddColour("Cuebot Box");
            var a = await AddColour("Cuebot A");
            var b = await AddColour("Cuebot B");
            var folder = await slobs.CallAsync(sceneResource, "createFolder", ["Cuebot Group"]);
            foreach (var child in new[] { a, b })
                await slobs.CallAsync(child.GetProperty("resourceId").GetString()!, "setParent", [folder.GetProperty("id").GetString()]);
            var boxSourceId = box.GetProperty("sourceId").GetString()!;
            await slobs.CallAsync("SourceFiltersService", "add", [boxSourceId, "color_filter", "Cuebot Filter", new { }]);
            await WaitAsync(() => slobs.SceneNames.Contains(TestScene), () => "the scratch scene did not show up");

            Assert.False(await slobs.SetSourceVisibilityAsync(TestScene, "Cuebot Box", VisibilityMode.Hidden));
            Assert.True(await slobs.SetSourceVisibilityAsync(TestScene, "Cuebot Box", VisibilityMode.Toggle));
            Assert.True(await slobs.HideAllSourcesAsync(TestScene) >= 1);
            Assert.True(await slobs.SetSourceVisibilityAsync(TestScene, "Cuebot Box", VisibilityMode.Visible));

            Assert.Contains(await slobs.GetFiltersAsync(boxSourceId), f => f.Name == "Cuebot Filter");
            await slobs.SetFilterStateAsync("Cuebot Box", "Cuebot Filter", enabled: false);
            Assert.False((await slobs.GetFiltersAsync(boxSourceId)).Single(f => f.Name == "Cuebot Filter").Visible);
            await slobs.SetFilterStateAsync("Cuebot Box", "Cuebot Filter", enabled: true);
            Assert.Equal("Cuebot Filter", await slobs.SetRandomFilterStateAsync(TestScene, "Cuebot Box", VisibilityMode.Hidden));
            Assert.True(await slobs.HideSourceFiltersAsync(TestScene, "Cuebot Box") >= 0);

            Assert.True(await slobs.SetSourceVisibilityAsync(TestScene, "Cuebot A", VisibilityMode.Visible));
            Assert.True(await slobs.SetSourceVisibilityAsync(TestScene, "Cuebot B", VisibilityMode.Visible));
            Assert.Equal(2, await slobs.HideGroupSourcesAsync(TestScene, "Cuebot Group"));   // only counts the ones it hid
            Assert.Contains(await slobs.SetRandomGroupSourceVisibleAsync(TestScene, "Cuebot Group"), new[] { "Cuebot A", "Cuebot B" });

            await slobs.FlipSourceAsync(TestScene, "Cuebot Box", FlipAxis.Horizontal);
            await slobs.RotateSourceAsync(TestScene, "Cuebot Box", 90);
            await slobs.UpdateSourceSettingsAsync("Cuebot Box", new { color = 4278255360L });

            // Mute toggles on an existing audio source, twice so it ends as it started.
            if (slobs.SourceNames.FirstOrDefault(n => n.Contains("Audio", StringComparison.OrdinalIgnoreCase)) is { } audio)
            {
                var muted = await slobs.SetSourceMuteAsync(audio, MuteMode.Toggle);
                Assert.Equal(!muted, await slobs.SetSourceMuteAsync(audio, MuteMode.Toggle));
            }
        }
        finally
        {
            await slobs.CallAsync("ScenesService", "removeScene", [sceneId]);
            foreach (var id in sourceIds)
            {
                try { await slobs.CallAsync("SourcesService", "removeSource", [id]); }
                catch (StreamlabsException) { } // already gone with the scene
            }
        }
        await slobs.DisconnectAsync();
    }

    private static string SceneId(StreamlabsDesktopService slobs, string name) =>
        slobs.Scenes.First(s => s.Name == name).Id;

    private static async Task WaitAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure());
            await Task.Delay(200);
        }
    }
}
