using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Speech.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Speech.Tests;

public class ActionTests
{
    [Theory]
    [InlineData("Pause", false, true)]
    [InlineData("pause", true, true)]
    [InlineData("Resume", true, false)]
    [InlineData("Toggle", true, false)]
    [InlineData("Toggle", false, true)]
    [InlineData("bogus", true, true)]
    public void Pause_modes(string mode, bool paused, bool expected) =>
        Assert.Equal(expected, PauseSpeechSubAction.ApplyMode(mode, paused));

    [Theory]
    [InlineData("Enable", false, true)]
    [InlineData("Disable", true, false)]
    [InlineData("Toggle", true, false)]
    [InlineData("?", true, true)]
    public void Switch_modes(string mode, bool current, bool expected) => Assert.Equal(expected, SwitchMode.Apply(mode, current));

    [Theory]
    [InlineData("Main", "", true)]
    [InlineData("Main", "main", true)]
    [InlineData("Main", "Other", false)]
    [InlineData(null, "Main", false)]
    public void Triggers_filter_by_alias(string? spoken, string filter, bool expected) =>
        Assert.Equal(expected, SpeechTriggers.MatchesAlias(spoken, filter));

    [Fact]
    public async Task AddSpeech_registers_everything()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuebot-speech-tests", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(root)).AddSpeech();
        await using (var provider = services.BuildServiceProvider())
        {
            var registry = provider.GetRequiredService<Registry>();
            var speech = provider.GetRequiredService<SpeechService>();

            Assert.Equal(["windows", "espeak", "elevenlabs", "azure", "polly", "google", "watson", "uberduck", "ttsmonster", "acapela", "cereproc"],
                speech.Engines.Select(e => e.Id));
            foreach (var id in new[]
                     {
                         "speech.speak", "speech.skip", "speech.clearQueue", "speech.pause", "speech.setEnabled", "speech.setEvents",
                         "speech.setAlias", "speech.setUserVoice", "speech.stopUser", "speech.activateVoiceGate", "audio.playSound",
                     })
                Assert.NotNull(registry.GetSubAction(id));
            foreach (var id in new[] { "speech.started", "speech.finished", "speech.stateChanged" })
                Assert.NotNull(registry.GetTrigger(id));
            var functions = provider.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
            foreach (var name in new[] { "speech.speak", "speech.stop", "speech.clear", "speech.pause", "speech.setEnabled", "speech.setEvents",
                         "speech.setUserVoice", "speech.stopUser", "speech.activateVoiceGate", "speech.state" })
                Assert.Contains(name, functions);
            Assert.NotNull(provider.GetRequiredService<AudioService>());
            Assert.NotNull(speech.Users);
        }
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Runs one speech sub-action in a real action engine and returns the action's final arguments.</summary>
    private static async Task<Dictionary<string, object?>> RunAsync(SpeechHarness h, string type, Dictionary<string, string> parameters)
    {
        var root = Path.Combine(h.Folder, "app");
        var capture = new CaptureSubAction();
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(root));
        services.AddSingleton(h.Service);
        services.AddSingleton<ISubActionHandler, SpeakSubAction>();
        services.AddSingleton<ISubActionHandler, SetSpeechEnabledSubAction>();
        services.AddSingleton<ISubActionHandler, SetEventSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, SetSpeechAliasSubAction>();
        services.AddSingleton<ISubActionHandler, SetUserVoiceSubAction>();
        services.AddSingleton<ISubActionHandler, StopUserSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, ActivateVoiceGateSubAction>();
        services.AddSingleton<ISubActionHandler, SkipSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, SpeakNextSubAction>();
        services.AddSingleton<ISubActionHandler, ClearSpeechQueueSubAction>();
        services.AddSingleton<ISubActionHandler, PauseSpeechSubAction>();
        services.AddSingleton<ISubActionHandler>(capture);
        await using var provider = services.BuildServiceProvider();
        var action = new ActionDefinition
        {
            Name = "test",
            SubActions =
            [
                new SubActionDefinition { Type = type, Parameters = parameters },
                new SubActionDefinition { Type = CaptureSubAction.Id },
            ],
        };
        Assert.True(await provider.GetRequiredService<ActionEngine>().RunAsync(action,
            new Dictionary<string, object?> { ["user"] = "Bob", ["userId"] = "b1", ["platform"] = "Twitch" }));
        return capture.Args!;
    }

    private sealed class CaptureSubAction : ISubActionHandler
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

    [Fact]
    public async Task Speak_silent_writes_speech_file_and_duration()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Engine.Samples = 3200;

        var args = await RunAsync(h, "speech.speak", new() { ["text"] = "render me", ["silent"] = "true", ["alias"] = "main" });

        try
        {
            Assert.Equal(true, args["success"]);
            Assert.Equal(200L, args["duration"]);
            Assert.True(File.Exists((string)args["speechFile"]!));
            Assert.Equal(0, h.Player.Pending);
        }
        finally
        {
            File.Delete((string)args["speechFile"]!);
        }
    }

    [Fact]
    public async Task Speak_wait_plays_and_reports_duration_and_filters_when_asked()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Engine.Samples = 1600;
        h.Settings.Filters.BadWords.Add("heck");
        h.Settings.Filters.BadWordAction = BadWordAction.Strip;

        var args = await RunAsync(h, "speech.speak", new() { ["text"] = "what the heck", ["wait"] = "true", ["badWordFilter"] = "true" });

        Assert.Equal(100L, args["duration"]);
        Assert.Equal("", args["speechFile"]);
        Assert.Equal(["what the"], h.Spoken);
        Assert.Equal(1, h.Player.Pending);
    }

    [Fact]
    public async Task Speak_without_wait_only_reports_success()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Service.Enabled = false;

        var args = await RunAsync(h, "speech.speak", new() { ["text"] = "x" });

        Assert.Equal(false, args["success"]);
        Assert.False(args.ContainsKey("duration"));
    }

    [Fact]
    public async Task Enabled_events_and_alias_sub_actions()
    {
        await using var h = await SpeechHarness.StartAsync();

        await RunAsync(h, "speech.setEnabled", new() { ["mode"] = "Disable" });
        Assert.False(h.Service.Enabled);
        await RunAsync(h, "speech.setEnabled", new() { ["mode"] = "Toggle" });
        Assert.True(h.Service.Enabled);
        await RunAsync(h, "speech.setEvents", new() { ["mode"] = "Enable" });
        Assert.True(h.Service.EventsEnabled);

        await RunAsync(h, "speech.setAlias", new() { ["target"] = "Chat", ["alias"] = "Main" });
        await RunAsync(h, "speech.setAlias", new() { ["target"] = "Events", ["alias"] = "E" });
        await RunAsync(h, "speech.setAlias", new() { ["target"] = "Default", ["alias"] = "D" });
        await RunAsync(h, "speech.setAlias", new() { ["target"] = "Highlight", ["alias"] = "H" });
        Assert.Equal(("Main", "E", "D", "H"),
            (h.Settings.SpeakChat.Alias, h.Settings.Events.Alias, h.Settings.DefaultAlias, h.Settings.SpeakChat.HighlightAlias));
    }

    [Fact]
    public async Task User_voice_and_stop_user_sub_actions_default_to_the_trigger_user()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Service.Paused = true;
        _ = h.Service.SpeakAsync("queued", new SpeakOptions { Platform = "Twitch", UserId = "b1", UserName = "Bob" });

        await RunAsync(h, "speech.setUserVoice", new() { ["alias"] = "Main" });
        var args = await RunAsync(h, "speech.stopUser", new());

        Assert.Equal("Main", h.Users.Find("Twitch", "b1", null)!.Alias);
        Assert.Equal(1, args["stoppedCount"]);
        Assert.Equal(0, h.Service.QueueLength);
    }

    [Fact]
    public async Task Speak_next_lets_one_queued_text_through_a_pause()
    {
        await using var h = await SpeechHarness.StartAsync();
        Assert.Equal(false, (await RunAsync(h, "speech.next", new()))["success"]);
        h.Service.Paused = true;
        _ = h.Service.SpeakAsync("first", new SpeakOptions());
        _ = h.Service.SpeakAsync("second", new SpeakOptions());

        Assert.Equal(true, (await RunAsync(h, "speech.next", new()))["success"]);
        await h.Player.NextAsync();
        Assert.Equal(1, h.Service.QueueLength);
    }

    [Fact]
    public async Task Activate_voicegate_reports_success()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.VoiceGate.Profiles.Add(new VoiceGateProfile { Name = "Mic" });

        Assert.Equal(true, (await RunAsync(h, "speech.activateVoiceGate", new() { ["profile"] = "Mic" }))["success"]);
        Assert.Equal(false, (await RunAsync(h, "speech.activateVoiceGate", new() { ["profile"] = "Nope" }))["success"]);
    }

    [Theory]
    [InlineData("%user%", "")]
    [InlineData(" bob ", "bob")]
    [InlineData("%", "%")]
    public void Unresolved_variables_count_as_empty(string value, string expected) =>
        Assert.Equal(expected, SetUserVoiceSubAction.Clean(value));

    [Fact]
    public async Task Script_functions()
    {
        await using var h = await SpeechHarness.StartAsync();
        var functions = new SpeechScriptFunctions(h.Service).GetFunctions().ToDictionary(f => f.Name);
        Task<object?> Call(string name, object args) =>
            functions[name].Invoke(null!, JsonSerializer.SerializeToElement(args), CancellationToken.None);

        var spoken = JsonSerializer.SerializeToElement(await Call("speech.speak", new { text = "hi", wait = true }));
        Assert.True(spoken.GetProperty("success").GetBoolean());
        Assert.Null(await Call("speech.speak", new { text = "later" }));

        await Call("speech.pause", new { });
        Assert.True(h.Service.Paused);
        await Call("speech.pause", new { paused = false });
        Assert.False(h.Service.Paused);
        await Call("speech.setEvents", new { enabled = true });
        Assert.True(h.Service.EventsEnabled);
        await Call("speech.setUserVoice", new { platform = "Twitch", user = "Ann", alias = "Main" });
        Assert.Equal("Main", h.Users.Find("Twitch", null, "ann")!.Alias);
        Assert.Equal(0, await Call("speech.stopUser", new { platform = "Twitch", user = "Ann" }));
        Assert.Equal(false, await Call("speech.activateVoiceGate", new { profile = "x" }));
        await Call("speech.clear", new { });
        await Call("speech.stop", new { });
        await Call("speech.setEnabled", new { enabled = false });
        var state = JsonSerializer.SerializeToElement(await Call("speech.state", new { }));
        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, state.GetProperty("queueLength").GetInt32());
    }
}
