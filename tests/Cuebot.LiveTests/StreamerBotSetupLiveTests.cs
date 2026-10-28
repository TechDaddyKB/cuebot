using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Storage;
using Cuebot.Core.Transfer;
using Cuebot.Obs;
using Cuebot.Obs.Streamlabs;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// The OBS and Streamlabs Desktop parts of the real Streamer.bot setup (tests/Cuebot.UiTests/TestData/
/// streamerbot-exported-setup.sb; Cuebot.UiTests StreamerBotSetupBehaviourTests covers all of it against fakes), imported
/// through the Import page's path and run against the real apps: "E2E Follow alert" shows "Follow Alert" in "Alerts",
/// waits 3 s and hides it; "E2E Stream online" switches OBS and Streamlabs Desktop to "Starting Soon" and appends a line
/// to a file. Opt-in with CUEBOT_LIVE_E2E=1, plus CUEBOT_OBS_PASSWORD (OBS) and CUEBOT_LIVE_SLOBS_TOKEN (Streamlabs
/// Desktop). Scenes and the source are created when missing and removed afterwards, the current scenes are restored, and
/// nothing streams or records. Chat steps are captured instead of sent (Twitch is not connected).
/// </summary>
public sealed class StreamerBotSetupLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [LiveSetupFact]
    public async Task Imported_setup_drives_the_real_obs_and_streamlabs_desktop()
    {
        var slobsToken = ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_TOKEN");

        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "sb-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var chat = new ChatCapture();
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddObs();
        services.AddSingleton<ISubActionHandler>(chat);
        await using var provider = services.BuildServiceProvider();

        var obsSettings = provider.GetRequiredService<JsonFile<ObsSettings>>().Value;
        obsSettings.Host = LiveEnvironment.ObsHost;
        obsSettings.Port = LiveEnvironment.ObsPort;
        obsSettings.Password = LiveEnvironment.ObsPassword!;
        var slobsSettings = provider.GetRequiredService<JsonFile<StreamlabsSettings>>().Value;
        slobsSettings.Token = slobsToken!;
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_HOST") is { } host) slobsSettings.Host = host;
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_PORT"), out var port)) slobsSettings.Port = port;
        var obs = provider.GetRequiredService<ObsService>();
        var slobs = provider.GetRequiredService<StreamlabsDesktopService>();
        await obs.ConnectAsync();
        await WaitAsync(() => obs.State == ConnectionState.Connected, () => $"OBS did not connect ({obs.StatusMessage})");
        await slobs.ConnectAsync();
        await WaitAsync(() => slobs.State == ConnectionState.Connected, () => $"Streamlabs Desktop did not connect ({slobs.StatusMessage})");

        // Import exactly as the Import page does.
        var transfer = provider.GetRequiredService<TransferService>();
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "streamerbot-exported-setup.sb");
        await transfer.ApplyAsync(transfer.Preview(await File.ReadAllTextAsync(fixture)));
        var profile = provider.GetRequiredService<ProfileService>();
        var log = Path.Combine(dataDirectory, "stream-log.txt");
        await profile.EditAsync(p => p.Actions.Single(a => a.Name == "E2E Stream online").SubActions
            .Single(s => s.Type == "core.writeFile").Parameters["path"] = log);
        var engine = provider.GetRequiredService<ActionEngine>();

        var obsCleanup = new List<Func<Task>>();
        var slobsCleanup = new List<Func<Task>>();
        var originalObsScene = (await obs.RequestAsync("GetCurrentProgramScene")).GetProperty("currentProgramSceneName").GetString()!;
        var originalSlobsScene = await slobs.GetCurrentSceneAsync();
        try
        {
            var itemId = await PrepareObsAsync(obs, obsCleanup);
            await PrepareSlobsAsync(slobs, slobsCleanup);

            // Follow: shown, 3 s, hidden, then the thanks.
            var shownStates = new ConcurrentQueue<(DateTime At, bool Enabled)>();
            var watch = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                bool? last = null;
                while (DateTime.UtcNow < deadline)
                {
                    var enabled = (await obs.RequestAsync("GetSceneItemEnabled", new { sceneName = "Alerts", sceneItemId = itemId }))
                        .GetProperty("sceneItemEnabled").GetBoolean();
                    if (enabled != last) shownStates.Enqueue((DateTime.UtcNow, enabled));
                    last = enabled;
                    await Task.Delay(50);
                }
            });
            Assert.True(await engine.RunAsync(profile.Snapshot.FindAction("E2E Follow alert")!,
                new Dictionary<string, object?> { ["user"] = "Cool_User", ["userName"] = "cool_user" }, source: "Twitch Follow"));
            await watch;
            var states = shownStates.ToArray();
            Assert.Equal([false, true, false], states.Select(s => s.Enabled));
            Assert.InRange((states[2].At - states[1].At).TotalMilliseconds, 2700, 4000);
            Assert.Equal(["Thanks for the follow, Cool_User!"], chat.Sent);

            // Stream online: both apps on "Starting Soon", a line in the log.
            Assert.True(await engine.RunAsync(profile.Snapshot.FindAction("E2E Stream online")!,
                new Dictionary<string, object?> { ["user"] = "Streamer" }, source: "Twitch Stream Online"));
            Assert.Equal("Starting Soon", (await obs.RequestAsync("GetCurrentProgramScene")).GetProperty("currentProgramSceneName").GetString());
            Assert.Equal("Starting Soon", await slobs.GetCurrentSceneAsync());
            Assert.StartsWith("Live at ", Assert.Single(File.ReadAllLines(log)));
        }
        finally
        {
            await obs.SetCurrentSceneAsync(originalObsScene);
            await slobs.SetSceneAsync(originalSlobsScene);
            foreach (var undo in Enumerable.Reverse(obsCleanup)) await undo();
            foreach (var undo in Enumerable.Reverse(slobsCleanup)) await undo();
        }
        Assert.Equal(originalObsScene, (await obs.RequestAsync("GetCurrentProgramScene")).GetProperty("currentProgramSceneName").GetString());
        Assert.Equal(originalSlobsScene, await slobs.GetCurrentSceneAsync());
        await slobs.DisconnectAsync();
        await obs.DisconnectAsync();
        try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Makes sure OBS has "Alerts" holding a hidden "Follow Alert" and a "Starting Soon" scene; returns the item id.</summary>
    private static async Task<int> PrepareObsAsync(ObsService obs, List<Func<Task>> cleanup)
    {
        var scenes = (await obs.RequestAsync("GetSceneList")).GetProperty("scenes").EnumerateArray()
            .Select(s => s.GetProperty("sceneName").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var scene in new[] { "Alerts", "Starting Soon" })
        {
            if (scenes.Contains(scene)) continue;
            await obs.RequestAsync("CreateScene", new { sceneName = scene });
            cleanup.Add(() => obs.RequestAsync("RemoveScene", new { sceneName = scene }));
        }

        var items = (await obs.RequestAsync("GetSceneItemList", new { sceneName = "Alerts" })).GetProperty("sceneItems").EnumerateArray().ToArray();
        if (items.FirstOrDefault(i => i.GetProperty("sourceName").GetString() == "Follow Alert") is { ValueKind: JsonValueKind.Object } existing)
        {
            var id = existing.GetProperty("sceneItemId").GetInt32();
            var wasEnabled = existing.GetProperty("sceneItemEnabled").GetBoolean();
            cleanup.Add(() => obs.RequestAsync("SetSceneItemEnabled", new { sceneName = "Alerts", sceneItemId = id, sceneItemEnabled = wasEnabled }));
            await obs.RequestAsync("SetSceneItemEnabled", new { sceneName = "Alerts", sceneItemId = id, sceneItemEnabled = false });
            return id;
        }

        var inputs = (await obs.RequestAsync("GetInputList")).GetProperty("inputs").EnumerateArray()
            .Select(i => i.GetProperty("inputName").GetString()).ToHashSet(StringComparer.Ordinal);
        int itemId;
        if (inputs.Contains("Follow Alert"))
        {
            itemId = (await obs.RequestAsync("CreateSceneItem", new { sceneName = "Alerts", sourceName = "Follow Alert", sceneItemEnabled = false }))
                .GetProperty("sceneItemId").GetInt32();
            cleanup.Add(() => obs.RequestAsync("RemoveSceneItem", new { sceneName = "Alerts", sceneItemId = itemId }));
        }
        else
        {
            // A small colour source stands in for an alert.
            itemId = (await obs.RequestAsync("CreateInput", new
            {
                sceneName = "Alerts", inputName = "Follow Alert", inputKind = "color_source_v3",
                inputSettings = new { width = 64, height = 64 }, sceneItemEnabled = false,
            })).GetProperty("sceneItemId").GetInt32();
            cleanup.Add(() => obs.RequestAsync("RemoveInput", new { inputName = "Follow Alert" }));
        }
        return itemId;
    }

    private static async Task PrepareSlobsAsync(StreamlabsDesktopService slobs, List<Func<Task>> cleanup)
    {
        if (slobs.SceneNames.Contains("Starting Soon")) return;
        var created = await slobs.CallAsync("ScenesService", "createScene", ["Starting Soon"]);
        var id = created.GetProperty("id").GetString()!;
        cleanup.Add(() => slobs.CallAsync("ScenesService", "removeScene", [id]));
        await WaitAsync(() => slobs.SceneNames.Contains("Starting Soon"), () => "the new Streamlabs Desktop scene did not show up");
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure());
            await Task.Delay(200);
        }
    }

    /// <summary>Stands in for Twitch "Send Message": records the text it would have sent.</summary>
    private sealed class ChatCapture : ISubActionHandler
    {
        private readonly ConcurrentQueue<string> _sent = new();
        public string[] Sent => _sent.ToArray();
        public string TypeId => "twitch.sendMessage";
        public string Category => "Test";
        public string DisplayName => "Send message (captured)";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("message", "Message")];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            _sent.Enqueue(parameters.GetString("message"));
            return Task.FromResult(SubActionResult.Next);
        }
    }
}

/// <summary>Runs with CUEBOT_LIVE_E2E=1, CUEBOT_OBS_PASSWORD and CUEBOT_LIVE_SLOBS_TOKEN.</summary>
public sealed class LiveSetupFactAttribute : LiveFactAttribute
{
    public LiveSetupFactAttribute()
    {
        if (Skip is not null) return;
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_E2E") != "1")
            Skip = "Set CUEBOT_LIVE_E2E=1 to run the imported Streamer.bot setup against OBS and Streamlabs Desktop (see docs/LIVE_TESTING.md)";
        else if (LiveEnvironment.ObsPassword is null || ServicesLiveEnvironment.Get("CUEBOT_LIVE_SLOBS_TOKEN") is null)
            Skip = "Set CUEBOT_OBS_PASSWORD and CUEBOT_LIVE_SLOBS_TOKEN (see docs/LIVE_TESTING.md)";
    }
}
