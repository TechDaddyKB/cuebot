using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Speech.Recognition;

namespace Cuebot.VoiceControl.Engines;

/// <summary>
/// Microsoft Speech Recognition (System.Speech, the engine Streamer.bot uses), Windows only. Exact commands become a
/// grammar, which gives them high confidence; a dictation grammar catches everything else. Audio comes from Cuebot's
/// own capture (so the device picker works), fed in as a 16 kHz 16-bit stream.
/// </summary>
internal sealed class WindowsSpeechEngine : ISpeechRecognizerEngine
{
    public const string EngineId = "windows";

    public string Id => EngineId;
    public string DisplayName => "Windows Speech Recognition (Windows only)";
    public bool NeedsModel => false;

    public bool IsAvailable => OperatingSystem.IsWindows() && GetLocales().Count > 0;

    public string? UnavailableReason => !OperatingSystem.IsWindows()
        ? "Windows Speech Recognition only exists on Windows. Use Whisper."
        : GetLocales().Count == 0
            ? "No Windows speech recognition language is installed (Settings → Time & language → Speech)."
            : null;

    public IReadOnlyList<string> GetLocales()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try { return InstalledCultures(); }
        catch (Exception) { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static string[] InstalledCultures() =>
        SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name).Distinct().ToArray();

    [SupportedOSPlatform("windows")]
    private static Task<IRecognitionSession> StartOnWindows(RecognitionOptions options, IReadOnlyList<string> installed, CancellationToken ct) =>
        Task.Run<IRecognitionSession>(() => new Session(options, installed), ct);

    public Task<IRecognitionSession> StartAsync(RecognitionOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(UnavailableReason);
        var installed = GetLocales();
        return StartOnWindows(options, installed, ct);
    }

    [SupportedOSPlatform("windows")]
    private sealed class Session : IRecognitionSession
    {
        private const int Rate = 16000;
        private readonly SpeechRecognitionEngine _engine;
        private readonly BlockingAudioStream _audio = new();
        private readonly object _gate = new();
        private Grammar? _commands;
        private long _utterance = 1;
        private bool _disposed;

        public Session(RecognitionOptions options, IReadOnlyList<string> installed)
        {
            var culture = PickCulture(options.Locale, installed);
            _engine = new SpeechRecognitionEngine(culture);
            Description = $"Windows Speech Recognition ({culture.Name})";
            _engine.LoadGrammar(new DictationGrammar { Name = "dictation" });
            LoadCommands(options);
            _engine.SetInputToAudioStream(_audio, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _engine.SpeechRecognized += (_, e) => Raise(e.Result, isFinal: true);
            _engine.SpeechHypothesized += (_, e) => Raise(e.Result, isFinal: false);
            _engine.SpeechRecognitionRejected += (_, _) => Interlocked.Increment(ref _utterance);
            _engine.RecognizeCompleted += (_, e) =>
            {
                if (e.Error is not null && !_disposed) Failed?.Invoke(e.Error);
            };
            _engine.RecognizeAsync(RecognizeMode.Multiple);
        }

        public int SampleRate => Rate;
        public string Description { get; }
        public event Action<RecognitionResult>? Recognized;
        public event Action<Exception>? Failed;

        public void Accept(ReadOnlySpan<float> samples) => _audio.Write(samples);

        public void UpdateOptions(RecognitionOptions options)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _engine.RequestRecognizerUpdate();
                LoadCommands(options);
            }
        }

        private void LoadCommands(RecognitionOptions options)
        {
            if (_commands is not null) _engine.UnloadGrammar(_commands);
            _commands = null;
            var phrases = options.ExactPhrases.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (phrases.Length == 0) return;
            _commands = new Grammar(new GrammarBuilder(new Choices(phrases))) { Name = "commands", Priority = 1 };
            _engine.LoadGrammar(_commands);
        }

        private void Raise(System.Speech.Recognition.RecognitionResult result, bool isFinal)
        {
            var id = isFinal ? Interlocked.Increment(ref _utterance) - 1 : Interlocked.Read(ref _utterance);
            var alternates = result.Alternates.Count > 0
                ? result.Alternates.Select(a => new RecognitionAlternate(a.Text, a.Confidence)).ToArray()
                : [new RecognitionAlternate(result.Text, result.Confidence)];
            Recognized?.Invoke(new RecognitionResult(id, result.Text, result.Confidence, isFinal)
            {
                Alternates = alternates,
                Language = _engine.RecognizerInfo.Culture.TwoLetterISOLanguageName,
                Duration = result.Audio?.Duration ?? TimeSpan.Zero,
            });
        }

        private static CultureInfo PickCulture(string locale, IReadOnlyList<string> installed)
        {
            var wanted = string.IsNullOrWhiteSpace(locale) || locale.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.CurrentUICulture.Name
                : locale.Trim();
            var match = installed.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(c => c.Split('-')[0].Equals(wanted.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault()
                ?? throw new InvalidOperationException("No Windows speech recognition language is installed.");
            return CultureInfo.GetCultureInfo(match);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate) _disposed = true;
            _audio.Complete();
            try { _engine.RecognizeAsyncCancel(); }
            catch (InvalidOperationException) { }
            _engine.Dispose();
            _audio.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A read-blocking PCM16 stream fed with float samples: System.Speech reads from it on its own thread and treats a
/// zero-byte read as the end of the audio, so reads wait for data until <see cref="Complete"/> is called.
/// </summary>
internal sealed class BlockingAudioStream : Stream
{
    private const int MaxBufferedBytes = 16000 * 2 * 10;
    private readonly object _gate = new();
    private readonly Queue<byte> _bytes = new();
    private bool _completed;

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_completed) return;
            foreach (var sample in samples)
            {
                var value = (short)Math.Clamp((int)MathF.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
                _bytes.Enqueue((byte)value);
                _bytes.Enqueue((byte)(value >> 8));
            }
            // Never fall far behind: drop the oldest audio if the recogniser stalls.
            while (_bytes.Count > MaxBufferedBytes) { _bytes.Dequeue(); _bytes.Dequeue(); }
            Monitor.PulseAll(_gate);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            // SAPI expects full reads (a short read looks like the end of the audio), so wait for the whole request.
            while (_bytes.Count < count && !_completed) Monitor.Wait(_gate);
            var n = Math.Min(count, _bytes.Count);
            for (var i = 0; i < n; i++) buffer[offset + i] = _bytes.Dequeue();
            return n;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // System.Speech asks for these when wrapping the stream; the audio is a live stream of unknown length.
    public override long Length => -1;
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => 0;
    public override void SetLength(long value) { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
