using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.VoiceControl.Models;
using Cuebot.VoiceControl.Tests.Support;

namespace Cuebot.VoiceControl.Tests;

public sealed class VoiceControlServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-voice-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private readonly FakeMicrophone _mic = new();
    private readonly FakeEngine _engine = new();
    private readonly JsonFile<VoiceControlSettings> _settings;
    private readonly VoiceControlService _voice;

    public VoiceControlServiceTests()
    {
        _bus = new EventBus(_log);
        _bus.Subscribe(_events.Enqueue);
        _settings = new JsonFile<VoiceControlSettings>(Path.Combine(_dir, "voicecontrol.json"), _log);
        _settings.Value.Engine = "fake";
        _voice = Create(_mic);
    }

    private VoiceControlService Create(FakeMicrophone? mic, params Cuebot.VoiceControl.Engines.ISpeechRecognizerEngine[] engines) =>
        new(_settings, engines.Length > 0 ? engines : [_engine], mic, new ModelStore(Path.Combine(_dir, "models"), new HttpClient(), _log), _bus, _log);

    [Fact]
    public async Task Listening_opens_the_microphone_and_resamples_to_the_engine_rate()
    {
        _settings.Value.InputDevice = "Mic B";
        await _voice.StartListeningAsync();
        Assert.Equal(ConnectionState.Connected, _voice.State);
        Assert.Contains("Fake recogniser", _voice.StatusMessage);
        Assert.Contains("Mic B", _voice.StatusMessage);
        Assert.Equal("Mic B", Assert.Single(_mic.Opened));

        _mic.Stream!.Feed(new float[4800]);
        Assert.Equal(1600, _engine.Session!.Samples.Count);

        await _voice.StopListeningAsync();
        Assert.True(_mic.Stream.Disposed);
        Assert.True(_engine.Session.Disposed);
        Assert.Equal(ConnectionState.Disconnected, _voice.State);
        Assert.False(_voice.IsListening);
    }

    [Fact]
    public async Task A_recognised_command_publishes_command_and_dictation_events()
    {
        var command = _voice.AddCommand("Countdown", "start the countdown", CommandLocation.Start);
        await _voice.StartListeningAsync();
        _engine.Session!.Hear("Start the countdown for five minutes", 0.92);

        var fired = Assert.Single(_events, e => e.Type == VoiceControlEvents.Command);
        Assert.Equal("start the countdown", fired.Args["spokenCommand"]);
        Assert.Equal("for five minutes", fired.Args["spokenTextInput"]);
        Assert.Equal(92, fired.Args["spokenTextConfidencePercent"]);
        Assert.Equal(command.Id.ToString(), fired.Args["voiceCommandId"]);
        Assert.Equal("fake", fired.Args["voiceEngine"]);
        var dictation = Assert.Single(_events, e => e.Type == VoiceControlEvents.Dictation);
        Assert.Equal("Countdown", dictation.Args["voiceCommandName"]);

        var entry = Assert.Single(_voice.DictationLog);
        Assert.Equal(["Countdown"], entry.Commands);
        Assert.False(entry.Ignored);
        Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains("Heard"));
    }

    [Fact]
    public async Task Low_confidence_dictation_is_logged_as_ignored_and_fires_nothing()
    {
        _settings.Value.ConfidenceThreshold = 60;
        _settings.Value.LogDictation = true;
        _voice.AddCommand("Lights", "lights");
        await _voice.StartListeningAsync();
        _engine.Session!.Hear("lights", 0.4);
        Assert.Empty(_events);
        Assert.True(Assert.Single(_voice.DictationLog).Ignored);
        Assert.Contains(_log.Recent(), e => e.Message.Contains("Heard \"lights\" (40%, below threshold)"));
    }

    [Fact]
    public async Task Stop_after_commands_fire_from_interim_results_only_once()
    {
        _voice.AddCommand("Clip", "clip that", CommandLocation.Anywhere).StopAfter = true;
        _voice.CommandEdited();
        await _voice.StartListeningAsync();
        Assert.True(_engine.Session!.Options.InterimResults);
        var hearing = new List<string>();
        _voice.Hearing += hearing.Add;

        _engine.Session.Hear("oh clip that", 0.9, isFinal: false, id: 7);
        Assert.Single(_events, e => e.Type == VoiceControlEvents.Command);
        Assert.Equal(false, _events.Single().Args["spokenTextFinal"]);
        _engine.Session.Hear("oh clip that right now", 0.9, isFinal: true, id: 7);
        Assert.Single(_events, e => e.Type == VoiceControlEvents.Command);
        var dictation = Assert.Single(_events, e => e.Type == VoiceControlEvents.Dictation);
        Assert.Equal("clip that", dictation.Args["spokenCommand"]);
        Assert.Equal(["oh clip that", ""], hearing);
    }

    [Fact]
    public async Task Command_changes_reach_the_running_engine_and_are_saved()
    {
        await _voice.StartListeningAsync();
        var changed = 0;
        _voice.CommandsChanged += () => changed++;
        var command = _voice.AddCommand("Scene", "go to scene one");
        Assert.Equal(["go to scene one"], _engine.Session!.Options.ExactPhrases);
        Assert.False(_voice.SetCommandState("Scene", VoiceSwitch.Toggle));
        Assert.Empty(_engine.Session.Options.Phrases);
        Assert.True(_voice.SetCommandState(command.Id.ToString(), VoiceSwitch.On));
        _voice.SetCommand("scene", "switch scene", CommandLocation.Anywhere);
        Assert.Equal(["switch scene"], _engine.Session.Options.Phrases);
        Assert.Empty(_engine.Session.Options.ExactPhrases);
        Assert.Equal("Scene (2)", _voice.AddCommand("Scene", "x").Name);
        Assert.Throws<ArgumentException>(() => _voice.SetCommandState("missing", VoiceSwitch.On));
        Assert.Equal("Created", _voice.SetCommand("Created", "make it", create: true).Name);
        Assert.Equal(6, changed);

        _settings.SaveNow();
        var reloaded = new JsonFile<VoiceControlSettings>(Path.Combine(_dir, "voicecontrol.json"), _log).Value;
        Assert.Equal(["switch scene", "x", "make it"], reloaded.Commands.Select(c => c.Phrase));
        Assert.Equal(CommandLocation.Anywhere, reloaded.Commands[0].Location);
    }

    [Fact]
    public async Task Changing_the_input_device_while_listening_reopens_only_the_microphone()
    {
        await _voice.StartListeningAsync();
        var first = _mic.Stream!;
        var session = _engine.Session;
        _voice.SetInputDevice("Mic A");
        Assert.True(first.Disposed);
        Assert.Equal([null, "Mic A"], _mic.Opened);
        Assert.Same(session, _engine.Session);
        Assert.Equal(1, _engine.Starts);
        Assert.Equal("Mic A", _settings.Value.InputDevice);
        _voice.SetInputDevice("");
        Assert.Null(_mic.Opened[^1]);
    }

    [Fact]
    public async Task Missing_microphone_engine_or_capture_end_in_a_readable_error()
    {
        await using var noMic = Create(null);
        Assert.False(noMic.HasMicrophone);
        Assert.Contains("No audio input", (await Assert.ThrowsAsync<InvalidOperationException>(() => noMic.StartListeningAsync())).Message);
        Assert.Equal(ConnectionState.Error, noMic.State);

        await using var unavailable = Create(_mic, new FakeEngine("fake", available: false));
        Assert.Contains("unavailable", (await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.StartListeningAsync())).Message);

        _mic.CanCapture = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _voice.StartListeningAsync());
        Assert.True(_engine.Session!.Disposed);
        Assert.False(_voice.IsListening);
        Assert.Contains("microphone could not be opened", _voice.StatusMessage);
    }

    [Fact]
    public async Task Auto_start_listens_and_engine_failures_stop_listening()
    {
        _settings.Value.AutoStart = true;
        await _voice.StartAsync();
        Assert.True(_voice.IsListening);

        _engine.Session!.Fail(new InvalidOperationException("device lost"));
        await Wait.UntilAsync(() => _voice.State == ConnectionState.Error, "error state");
        Assert.False(_voice.IsListening);
        Assert.Contains("device lost", _voice.StatusMessage);
        Assert.True(_mic.Stream!.Disposed);

        _engine.StartError = new InvalidOperationException("model broken");
        await _voice.StartAsync();   // never throws at startup
        Assert.Contains(_log.Recent(), e => e.Level == LogLevel.Error && e.Message.Contains("model broken"));
    }

    [Fact]
    public async Task Listening_toggle_and_restart()
    {
        await _voice.SetListeningAsync(VoiceSwitch.Toggle);
        Assert.True(_voice.IsListening);
        await _voice.RestartAsync();
        Assert.Equal(2, _engine.Starts);
        await _voice.SetListeningAsync(VoiceSwitch.Toggle);
        Assert.False(_voice.IsListening);
        await _voice.SetListeningAsync(VoiceSwitch.Off);
        Assert.False(_voice.IsListening);
    }

    [Fact]
    public async Task Unknown_engine_ids_fall_back_to_an_available_engine()
    {
        _settings.Value.Engine = "nope";
        await using var voice = Create(_mic, new FakeEngine("fake", available: false), new FakeEngine("other"));
        Assert.Equal("other", voice.CurrentEngine.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await _voice.DisposeAsync();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
