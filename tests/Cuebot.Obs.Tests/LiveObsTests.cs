using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs.Tests;

/// <summary>Runs only when CUEBOT_OBS_PASSWORD is set (CUEBOT_OBS_PORT optional, default 4455).</summary>
public sealed class LiveObsFactAttribute : FactAttribute
{
    public LiveObsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUEBOT_OBS_PASSWORD")))
            Skip = "Set CUEBOT_OBS_PASSWORD to run against a real OBS instance";
    }
}

/// <summary>
/// Integration tests against a real, running OBS Studio. They create temporary "Cuebot Test" scenes and
/// sources, restore the original scene and remove everything they created.
/// </summary>
public sealed class LiveObsTests : IAsyncLifetime
{
    private const string SceneA = "Cuebot Test A";
    private const string SceneB = "Cuebot Test B";
    private const string ColorSource = "Cuebot Test Color";
    private const string TextSource = "Cuebot Test Text";

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-live-obs-").FullName;
    private ServiceProvider _services = null!;
    private ObsService _obs = null!;
    private string? _originalScene;

    private static int Port => int.TryParse(Environment.GetEnvironmentVariable("CUEBOT_OBS_PORT"), out var p) ? p : 4455;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUEBOT_OBS_PASSWORD"))) return;
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddObs().BuildServiceProvider();
        var settings = _services.GetRequiredService<JsonFile<ObsSettings>>().Value;
        settings.Host = "127.0.0.1";
        settings.Port = Port;
        settings.Password = Environment.GetEnvironmentVariable("CUEBOT_OBS_PASSWORD")!;
        _obs = _services.GetRequiredService<ObsService>();
        await _obs.ConnectAsync();
        _originalScene = _obs.CurrentScene;

        await TryRequest("CreateScene", new { sceneName = SceneA });
        await TryRequest("CreateScene", new { sceneName = SceneB });
        await TryRequest("CreateInput", new
        {
            sceneName = SceneA, inputName = ColorSource, inputKind = "color_source_v3",
            inputSettings = new { color = 4278255360u, width = 640, height = 360 },
        });
        var textKind = OperatingSystem.IsWindows() ? "text_gdiplus_v3" : "text_ft2_source_v2";
        await TryRequest("CreateInput", new { sceneName = SceneA, inputName = TextSource, inputKind = textKind, inputSettings = new { text = "initial" } });
    }

    private async Task TryRequest(string type, object data)
    {
        try { await _obs.RequestAsync(type, data); }
        catch (ObsRequestException) { /* already exists from an interrupted earlier run */ }
    }

    [LiveObsFact]
    public async Task Connects_and_lists_scenes_and_inputs()
    {
        Assert.Equal(ConnectionState.Connected, _obs.State);
        var version = await _obs.RequestAsync("GetVersion");
        Assert.StartsWith("5.", version.GetProperty("obsWebSocketVersion").GetString());

        await WaitUntil(() => _obs.Scenes.Contains(SceneA) && _obs.Inputs.Contains(TextSource));
    }

    [LiveObsFact]
    public async Task Scene_switch_publishes_scene_changed_with_previous_scene()
    {
        using var recorder = new BusRecorder(_services.GetRequiredService<EventBus>());
        await _obs.SetCurrentSceneAsync(SceneA);
        await recorder.WaitForAsync(ObsEvents.SceneChanged);
        await _obs.SetCurrentSceneAsync(SceneB);
        var changed = await recorder.WaitForAsync(ObsEvents.SceneChanged, 2);

        Assert.Equal(SceneB, changed.Args["sceneName"]);
        Assert.Equal(SceneA, changed.Args["previousScene"]);
        Assert.Equal(SceneB, _obs.CurrentScene);
    }

    [LiveObsFact]
    public async Task Source_visibility_toggles_and_text_updates()
    {
        Assert.False(await _obs.SetSourceVisibilityAsync(SceneA, ColorSource, VisibilityMode.Hidden));
        Assert.True(await _obs.SetSourceVisibilityAsync(SceneA, ColorSource, VisibilityMode.Toggle));
        Assert.False(await _obs.SetSourceVisibilityAsync(SceneA, ColorSource, VisibilityMode.Toggle));
        Assert.True(await _obs.SetSourceVisibilityAsync(SceneA, ColorSource, VisibilityMode.Visible));

        await _obs.SetInputTextAsync(TextSource, "hello from cuebot");
        Assert.Equal("hello from cuebot", await ReadText());
    }

    [LiveObsFact]
    public async Task Scene_change_trigger_runs_an_action_that_updates_obs()
    {
        // End to end: OBS event -> trigger -> action engine -> sub-action -> OBS request.
        var profile = _services.GetRequiredService<ProfileService>();
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "Live OBS test",
            Triggers = [new TriggerDefinition { Type = ObsEvents.SceneChanged, Settings = { ["scene"] = SceneB } }],
            SubActions =
            [
                new SubActionDefinition { Type = "obs.setText", Parameters = { ["input"] = TextSource, ["text"] = "now on %sceneName%" } },
            ],
        });
        profile.NotifyChanged();
        _services.GetRequiredService<ActionEngine>().Start();

        await _obs.SetCurrentSceneAsync(SceneA);
        await _obs.SetCurrentSceneAsync(SceneB);

        await WaitUntil(async () => await ReadText() == $"now on {SceneB}");
    }

    private async Task<string?> ReadText()
    {
        var settings = await _obs.RequestAsync("GetInputSettings", new { inputName = TextSource });
        return settings.GetProperty("inputSettings").TryGetProperty("text", out var t) ? t.GetString() : null;
    }

    private static Task WaitUntil(Func<bool> condition) => WaitUntil(() => Task.FromResult(condition()));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 10 s");
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is null) return;
        if (_obs.State == ConnectionState.Connected)
        {
            if (_originalScene is not null && _originalScene is not SceneA and not SceneB)
                await TryRequest("SetCurrentProgramScene", new { sceneName = _originalScene });
            await TryRequest("RemoveScene", new { sceneName = SceneA });
            await TryRequest("RemoveScene", new { sceneName = SceneB });
            await TryRequest("RemoveInput", new { inputName = ColorSource });
            await TryRequest("RemoveInput", new { inputName = TextSource });
        }
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
