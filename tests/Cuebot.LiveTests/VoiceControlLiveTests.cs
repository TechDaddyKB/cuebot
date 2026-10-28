using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Speech;
using Cuebot.VoiceControl;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in end-to-end voice control test: the speech module's own text-to-speech (Windows SAPI or eSpeak NG) says a
/// command phrase, the audio is played into voice control as if it came from a microphone, and the real engine must
/// recognise it and fire the Voice Control Command trigger. Whisper downloads its model (default Tiny English, 32 MB)
/// into the live-test data folder on the first run. No real microphone is used. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class VoiceControlLiveTests(ITestOutputHelper output)
{
    private const string Phrase = "start the countdown";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [LiveServiceFact("CUEBOT_LIVE_VOICE", flag: true)]
    public Task Whisper_recognises_a_spoken_command() => RecogniseAsync("whisper");

    [LiveServiceFact("CUEBOT_LIVE_VOICE", flag: true)]
    public async Task Windows_speech_recognition_recognises_a_spoken_command()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Windows Speech Recognition only exists on Windows; skipped.");
            return;
        }
        await RecogniseAsync("windows");
    }

    private async Task RecogniseAsync(string engineId)
    {
        var data = Path.Combine(LiveEnvironment.DataDirectory, "voicecontrol-" + engineId);
        Directory.CreateDirectory(data);
        var microphone = new ClipMicrophone();
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(data)).AddSpeech().AddVoiceControl()
            .AddSingleton<IAudioInput>(microphone).BuildServiceProvider();
        var voice = services.GetRequiredService<VoiceControlService>();
        services.GetRequiredService<Cuebot.Core.Logging.AppLog>().EntryAdded += e => { try { output.WriteLine(e.ToString()); } catch (InvalidOperationException) { } };
        var engine = voice.Engines.Single(e => e.Id == engineId);
        if (!engine.IsAvailable)
        {
            output.WriteLine($"{engine.DisplayName} is not available: {engine.UnavailableReason}");
            return;
        }

        var tts = services.GetServices<ITtsEngine>().FirstOrDefault(e => e.Id is "windows" or "espeak" && e.IsAvailable)
            ?? throw new InvalidOperationException("No local text-to-speech engine (Windows SAPI or espeak-ng) is available to speak the phrase.");
        var voices = await tts.GetVoicesAsync(CancellationToken.None);
        var speaker = voices.FirstOrDefault(v => v.Language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true) ?? voices.First();
        var audio = await tts.SynthesizeAsync(new SynthesisRequest(Phrase, speaker.Id), CancellationToken.None);
        microphone.Clip = WavDecoder.Decode(audio.Data);
        output.WriteLine($"Spoke \"{Phrase}\" with {tts.DisplayName} / {speaker.Name}: {microphone.Clip.Duration.TotalSeconds:0.0} s at {microphone.Clip.SampleRate} Hz");

        voice.Settings.Engine = engineId;
        voice.Settings.Locale = "en-US";
        voice.Settings.ConfidenceThreshold = 30;
        if (engineId == "whisper")
        {
            voice.Settings.Whisper.Model = ServicesLiveEnvironment.Get("CUEBOT_LIVE_VOICE_MODEL") ?? "tiny.en-q5_1";
            var model = WhisperModelCatalog.Find(voice.Settings.Whisper.Model) ?? throw new InvalidOperationException("Unknown CUEBOT_LIVE_VOICE_MODEL");
            var started = DateTime.UtcNow;
            await voice.Models.DownloadAsync(model);
            output.WriteLine($"Model {model.DisplayName} ready in {(DateTime.UtcNow - started).TotalSeconds:0} s at {voice.Models.PathFor(model)}");
        }
        voice.Settings.Commands.Clear();   // the data folder is reused between runs
        voice.AddCommand("Countdown", Phrase);

        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);
        await voice.StartListeningAsync();
        output.WriteLine(voice.StatusMessage);
        try
        {
            microphone.Play();
            var deadline = DateTime.UtcNow + Timeout;
            while (!events.Any(e => e.Type == VoiceControlEvents.Command) && voice.State != ConnectionState.Error && DateTime.UtcNow < deadline) await Task.Delay(100);
            output.WriteLine($"Status: {voice.State} {voice.StatusMessage}");
            foreach (var line in services.GetRequiredService<Cuebot.Core.Logging.AppLog>().Recent().Where(l => l.Source == "Voice Control")) output.WriteLine(line.ToString());
            foreach (var entry in voice.DictationLog) output.WriteLine($"Heard \"{entry.Text}\" ({entry.ConfidencePercent}%) → {string.Join(", ", entry.Commands)}");
            var command = Assert.Single(events, e => e.Type == VoiceControlEvents.Command);
            output.WriteLine($"Command fired: spokenText \"{command.Args["spokenText"]}\", confidence {command.Args["spokenTextConfidencePercent"]}%");
            Assert.Equal(Phrase, command.Args["spokenCommand"]);
            Assert.Contains(events, e => e.Type == VoiceControlEvents.Dictation);
        }
        finally
        {
            await voice.StopListeningAsync();
        }
    }

    /// <summary>A "microphone" that plays a clip (with silence around it) in 20 ms blocks at real-time speed.</summary>
    private sealed class ClipMicrophone : IAudioInput
    {
        private Stream? _stream;

        public AudioClip? Clip { get; set; }

        public IReadOnlyList<AudioDevice> GetInputDevices() => [new("clip", "Synthesised speech", true)];

        public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb) => throw new NotSupportedException();

        public IAudioInputStream? CaptureSamples(string? deviceId, CaptureCallback onSamples) =>
            _stream = new Stream(Clip ?? throw new InvalidOperationException("No clip"), onSamples);

        public void Play() => _stream?.Start();

        private sealed class Stream(AudioClip clip, CaptureCallback callback) : IAudioInputStream
        {
            private readonly CancellationTokenSource _stop = new();

            public int SampleRate => clip.SampleRate;

            public void Start() => _ = Task.Run(async () =>
            {
                var mono = new float[clip.Frames];
                for (var i = 0; i < clip.Frames; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < clip.Channels; c++) sum += clip.Samples[i * clip.Channels + c];
                    mono[i] = sum / clip.Channels;
                }
                var silence = new float[clip.SampleRate * 2];
                var signal = silence.Concat(mono).Concat(silence).Concat(silence).ToArray();
                var block = clip.SampleRate / 50;
                var quiet = new float[block];
                // Like a real microphone the stream never ends: after the clip it keeps delivering silence.
                for (var i = 0; !_stop.IsCancellationRequested; i += block)
                {
                    callback(i < signal.Length ? signal.AsSpan(i, Math.Min(block, signal.Length - i)) : quiet);
                    await Task.Delay(20);
                }
            });

            public void Dispose() => _stop.Cancel();
        }
    }
}
