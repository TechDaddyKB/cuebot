using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;
using Cuebot.Speech;
using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.VoiceControl.Tests;

public sealed class ModuleRegistrationTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-voice-di-").FullName;
    private readonly FakeMicrophone _mic = new();
    private readonly FakeEngine _engine = new();
    private readonly CaptureStep _capture = new();
    private readonly ServiceProvider _services;

    public ModuleRegistrationTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddVoiceControl()
            .AddSingleton<IAudioInput>(_mic)
            .AddSingleton<ISpeechRecognizerEngine>(_engine)
            .AddSingleton<ISubActionHandler>(_capture)
            .BuildServiceProvider();

    private VoiceControlService Voice => _services.GetRequiredService<VoiceControlService>();

    [Fact]
    public void Registers_status_engines_triggers_sub_actions_and_script_functions()
    {
        Assert.Contains(_services.GetServices<IConnectionStatus>(), s => s.Name == "Voice Control");
        Assert.Equal(["whisper", "windows", "fake"], Voice.Engines.Select(e => e.Id));
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), Voice.Engines[0].IsAvailable);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(Voice.Engines[1].IsAvailable);
            Assert.Contains("only exists on Windows", Voice.Engines[1].UnavailableReason);
        }

        var registry = _services.GetRequiredService<Registry>();
        Assert.All(VoiceControlSubActions.TypeIds, id => Assert.Equal("Voice Control", registry.GetSubAction(id)!.Category));
        Assert.Equal(["Command", "Dictation"], registry.Triggers.Where(t => t.Category == "Voice Control").Select(t => t.DisplayName));
        Assert.Equal([VoiceControlEvents.Command], registry.GetTrigger("voiceControl.command")!.EventTypes);

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        Assert.Contains("voiceControl.startListening", functions);
        Assert.Contains("voiceControl.setCommandState", functions);
        Assert.Contains("voiceControl.dictationLog", functions);
        Assert.EndsWith(Path.Combine("models", "whisper"), Voice.Models.Directory);
        Assert.True(Directory.Exists(Voice.Models.Directory));
    }

    [Fact]
    public async Task Disposing_the_container_disposes_the_service_once_without_throwing()
    {
        // The service is registered directly and as an IConnectionStatus, so the container disposes it twice.
        _ = _services.GetServices<IConnectionStatus>().ToList();
        await Voice.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact]
    public void Trigger_filters_match_any_or_named_commands()
    {
        var trigger = _services.GetRequiredService<Registry>().GetTrigger("voiceControl.command")!;
        var e = new BotEvent(VoiceControlEvents.Command, new Dictionary<string, object?> { ["voiceCommandName"] = "Countdown", ["voiceCommandId"] = "abc" });
        bool Matches(string filter) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["command"] = filter }), new Dictionary<string, object?>());
        Assert.True(Matches("Any"));
        Assert.True(Matches(""));
        Assert.True(Matches("other, countdown"));
        Assert.True(Matches("abc"));
        Assert.False(Matches("Other"));
        var dictationWithoutCommand = new BotEvent(VoiceControlEvents.Dictation, new Dictionary<string, object?> { ["voiceCommandName"] = "" });
        Assert.False(VoiceControlTriggers.MatchesCommand(dictationWithoutCommand, "Countdown"));
        Assert.True(VoiceControlTriggers.MatchesCommand(dictationWithoutCommand, "any"));
    }

    [Fact]
    public async Task Sub_actions_change_commands_input_and_listening()
    {
        Voice.Settings.Engine = "fake";
        Voice.AddCommand("Countdown", "start the countdown");
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "Voice",
            SubActions =
            [
                new SubActionDefinition { Type = "voiceControl.setListening", Parameters = new() { ["state"] = "Start" } },
                new SubActionDefinition { Type = "voiceControl.setCommandState", Parameters = new() { ["command"] = "Countdown", ["state"] = "Disabled" } },
                new SubActionDefinition
                {
                    Type = "voiceControl.setCommand",
                    Parameters = new() { ["command"] = "Countdown", ["phrase"] = "begin the countdown", ["location"] = "Start" },
                },
                new SubActionDefinition { Type = "voiceControl.setInput", Parameters = new() { ["device"] = "Mic B" } },
                new SubActionDefinition { Type = "test.capture" },
            ],
        };
        Assert.True(await engine.RunAsync(action));

        Assert.True(Voice.IsListening);
        var command = Voice.FindCommand("countdown")!;
        Assert.False(command.Enabled);
        Assert.Equal("begin the countdown", command.Phrase);
        Assert.Equal(CommandLocation.Start, command.Location);
        Assert.Equal("Mic B", _mic.Opened[^1]);
        Assert.Equal(false, _capture.Args!["voiceCommandEnabled"]);
        Assert.Equal("begin the countdown", _capture.Args["voiceCommandPhrase"]);
        Assert.Equal(true, _capture.Args["voiceControlListening"]);

        action.SubActions = [new SubActionDefinition { Type = "voiceControl.setInput", Parameters = new() { ["device"] = "Default" } }];
        Assert.True(await engine.RunAsync(action));
        Assert.Equal("", Voice.Settings.InputDevice);

        action.SubActions = [new SubActionDefinition { Type = "voiceControl.setCommandState", Parameters = new() { ["command"] = "missing" } }];
        Assert.False(await engine.RunAsync(action));
    }

    [Fact]
    public async Task Script_functions_drive_the_service()
    {
        Voice.Settings.Engine = "fake";
        var api = _services.GetRequiredService<ScriptApi>();
        var engine = _services.GetRequiredService<ActionEngine>();
        ActionRunContext? context = null;
        // A context is only created by the engine; borrow one from a test step.
        _capture.OnRun = c => context = c;
        await engine.RunAsync(new ActionDefinition { Name = "ctx", SubActions = [new SubActionDefinition { Type = "test.capture" }] });

        Assert.Equal(true, await api.CallAsync(context!, "voiceControl.startListening", null, CancellationToken.None));
        await api.CallAsync(context!, "voiceControl.setCommand", new { command = "Hi", phrase = "hello there", create = true }, CancellationToken.None);
        Assert.Equal(false, await api.CallAsync(context!, "voiceControl.setCommandState", new { command = "Hi", state = "Toggle" }, CancellationToken.None));
        Assert.Single((Array)(await api.CallAsync(context!, "voiceControl.commands", null, CancellationToken.None))!);
        Assert.Equal(2, ((Array)(await api.CallAsync(context!, "voiceControl.inputDevices", null, CancellationToken.None))!).Length);
        _engine.Session!.Hear("anything", 0.9);
        Assert.Single((Array)(await api.CallAsync(context!, "voiceControl.dictationLog", null, CancellationToken.None))!);
        Assert.Equal(true, await api.CallAsync(context!, "voiceControl.stopListening", null, CancellationToken.None));
        Assert.Equal(false, await api.CallAsync(context!, "voiceControl.isListening", null, CancellationToken.None));
    }

    internal sealed class CaptureStep : ISubActionHandler
    {
        public Dictionary<string, object?>? Args { get; private set; }
        public Action<ActionRunContext>? OnRun { get; set; }
        public string TypeId => "test.capture";
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            OnRun?.Invoke(context);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
