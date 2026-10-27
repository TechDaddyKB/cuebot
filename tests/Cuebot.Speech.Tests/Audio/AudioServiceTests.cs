using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Speech.Tests.Audio;

public sealed class AudioServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cuebot-audio-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAudioBackend _backend = new();
    private readonly AudioService _audio;

    public AudioServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _audio = new AudioService(new AudioOutput(_backend));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private string WriteWav(string name, int frames = 500)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestAudio.Wav16(frames));
        return path;
    }

    [Fact]
    public async Task Start_reports_duration_and_tracks_the_sound()
    {
        var playback = await _audio.StartFileAsync(WriteWav("a.wav", 1500), new PlaybackOptions("Headset", 30), "alerts", default);
        Assert.Equal(TimeSpan.FromSeconds(1.5), playback.Duration);
        Assert.Equal("alerts", playback.Tag);
        Assert.Single(_audio.Active);

        var voice = (await TestAudio.VoicesAsync(_backend, 1))[0];
        Assert.Equal("Headset", voice.DeviceId);
        Assert.Equal(0.3f, voice.Gain, 3);
        voice.Finish();
        await TestAudio.Within(playback.Completion);
        Assert.Empty(_audio.Active);
    }

    [Fact]
    public async Task Stop_by_tag_stops_only_matching_sounds()
    {
        var a = await _audio.StartFileAsync(WriteWav("a.wav"), PlaybackOptions.Default, "Alerts", default);
        var b = await _audio.StartFileAsync(WriteWav("b.wav"), PlaybackOptions.Default, "music", default);
        var c = await _audio.StartFileAsync(WriteWav("c.wav"), PlaybackOptions.Default, null, default);
        var voices = await TestAudio.VoicesAsync(_backend, 3);

        Assert.Equal(1, _audio.Stop("alerts"));
        await TestAudio.Within(a.Completion); // stopping is a normal end, not a failure
        Assert.True(voices[0].Stopped);
        Assert.False(voices[1].Stopped);

        Assert.Equal(2, _audio.StopAll());
        await TestAudio.Within(Task.WhenAll(b.Completion, c.Completion));
        Assert.Empty(_audio.Active);
        Assert.Equal(0, _audio.StopAll());
    }

    [Fact]
    public async Task Waiting_play_is_cancelled_with_its_token()
    {
        using var cts = new CancellationTokenSource();
        var play = _audio.PlayFileAsync(WriteWav("a.wav"), PlaybackOptions.Default, null, cts.Token);
        var voice = (await TestAudio.VoicesAsync(_backend, 1))[0];
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAudio.Within(play));
        Assert.True(voice.Stopped);
    }

    [Fact]
    public async Task Missing_and_unsupported_files_fail_before_playing()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _audio.StartFileAsync(Path.Combine(_folder, "nope.wav"), PlaybackOptions.Default, null, default));
        var text = Path.Combine(_folder, "notes.mp3");
        await File.WriteAllTextAsync(text, "not audio");
        await Assert.ThrowsAsync<NotSupportedException>(() => _audio.StartFileAsync(text, PlaybackOptions.Default, null, default));
        Assert.Empty(_backend.Voices);
    }

    [Fact]
    public void Random_file_respects_extension_filter_and_subfolders()
    {
        WriteWav("one.wav");
        File.WriteAllText(Path.Combine(_folder, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(_folder, "two.MP3"), "x");
        WriteWav(Path.Combine("sub", "three.wav"));

        var seen = new HashSet<string>();
        var random = new Random(1);
        for (var i = 0; i < 50; i++) seen.Add(Path.GetFileName(AudioService.PickRandomFile(_folder, "", false, random)!));
        Assert.Equal(["one.wav", "two.MP3"], seen.Order());

        seen.Clear();
        for (var i = 0; i < 50; i++) seen.Add(Path.GetFileName(AudioService.PickRandomFile(_folder, "*.wav", true, random)!));
        Assert.Equal(["one.wav", "three.wav"], seen.Order());

        Assert.Null(AudioService.PickRandomFile(_folder, "flac", false));
        Assert.Throws<DirectoryNotFoundException>(() => AudioService.PickRandomFile(Path.Combine(_folder, "missing"), "", false));
    }

    [Theory]
    [InlineData("mp3, wav", new[] { ".mp3", ".wav" })]
    [InlineData("*.ogg;*.flac", new[] { ".flac", ".ogg" })]
    [InlineData(".WAV", new[] { ".WAV" })]
    public void Parses_extension_lists(string text, string[] expected) =>
        Assert.Equal(expected, AudioService.ParseExtensions(text).Order(StringComparer.Ordinal));

    [Fact]
    public void Empty_extension_list_means_every_supported_format() =>
        Assert.Equal(AudioFormatDetector.Extensions.Count, AudioService.ParseExtensions(" ").Count);

    [Fact]
    public async Task Sub_actions_play_wait_and_stop()
    {
        var log = new AppLog();
        var profile = new ProfileService(null, log);
        var capture = new CaptureArgs();
        var registry = new Registry(
            [new PlaySoundSubAction(_audio), new PlaySoundFromFolderSubAction(_audio, new Random(3)), new StopSoundsSubAction(_audio), capture], []);
        var engine = new ActionEngine(new EventBus(log), profile, registry, new GlobalVariables(null, log), log);
        var file = WriteWav("ding.wav", 250);

        // Not waiting: the action finishes while the sound plays.
        var start = new ActionDefinition
        {
            Name = "Start",
            Concurrent = true,
            SubActions =
            [
                Step("audio.playSound", ("file", file), ("wait", "false"), ("tag", "sfx"), ("volume", "150")),
                Step(CaptureArgs.Id),
            ],
        };
        Assert.True(await engine.RunAsync(start));
        Assert.Equal(file, capture.Args["soundFile"]);
        Assert.Equal(0.25, capture.Args["soundDuration"]);
        Assert.Equal(250L, capture.Args["soundDurationMs"]);
        var voice = (await TestAudio.VoicesAsync(_backend, 1))[0];
        Assert.Equal(1f, voice.Gain); // clamped to 100
        Assert.False(voice.Stopped);

        var stop = new ActionDefinition { Name = "Stop", SubActions = [Step("audio.stopSounds", ("tag", "SFX")), Step(CaptureArgs.Id)] };
        Assert.True(await engine.RunAsync(stop));
        Assert.Equal(1, capture.Args["soundsStopped"]);
        Assert.True(voice.Stopped);

        // Waiting from a folder: the action continues when the sound ends.
        var folder = new ActionDefinition
        {
            Name = "Folder",
            SubActions = [Step("audio.playSoundFromFolder", ("folder", _folder), ("device", "Speakers")), Step(CaptureArgs.Id)],
        };
        var run = engine.RunAsync(folder);
        var second = (await TestAudio.VoicesAsync(_backend, 2))[1];
        Assert.Equal("Speakers", second.DeviceId);
        Assert.False(run.IsCompleted);
        second.Finish();
        Assert.True(await TestAudio.Within(run));
        Assert.Equal(file, capture.Args["soundFile"]);
    }

    [Fact]
    public void Play_sound_suggests_output_devices()
    {
        var handler = new PlaySoundSubAction(_audio);
        var device = handler.Parameters.Single(p => p.Key == "device");
        Assert.Equal(["Speakers", "Headset"], device.Suggestions!());
    }

    private static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (key, value) in parameters) step.Parameters[key] = value;
        return step;
    }

    private sealed class CaptureArgs : ISubActionHandler
    {
        public const string Id = "test.capture";
        public Dictionary<string, object?> Args { get; private set; } = [];
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
}
