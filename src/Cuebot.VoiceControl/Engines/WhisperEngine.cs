using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cuebot.VoiceControl.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Cuebot.VoiceControl.Engines;

/// <summary>
/// Local speech-to-text with whisper.cpp through Whisper.net, on Windows and Linux. CPU by default (AVX2 build with
/// the NoAvx build as fallback); a CUDA or Vulkan runtime placed next to Cuebot is used when "Use GPU" is on.
/// Utterances are cut at pauses (<see cref="UtteranceRecognitionSession"/>) because Whisper transcribes clips.
/// </summary>
internal sealed class WhisperEngine(Func<VoiceControlSettings> settings, ModelStore models) : ISpeechRecognizerEngine
{
    public const string EngineId = WhisperDefaults.EngineId;

    public string Id => EngineId;
    public string DisplayName => "Whisper (local, Windows and Linux)";
    public bool IsAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
    public string? UnavailableReason => IsAvailable ? null : "Whisper runtimes are only shipped for Windows, Linux and macOS.";
    public bool NeedsModel => true;

    public IReadOnlyList<string> GetLocales() => ["auto", .. WhisperLanguages.Codes];

    public async Task<IRecognitionSession> StartAsync(RecognitionOptions options, CancellationToken ct)
    {
        var s = settings();
        var model = WhisperModelCatalog.Find(s.Whisper.Model)
            ?? throw new InvalidOperationException($"Unknown Whisper model '{s.Whisper.Model}'. Pick one in Voice Control → Settings.");
        if (!models.IsDownloaded(model))
            throw new InvalidOperationException($"The Whisper model {model.DisplayName} ({model.SizeText}) is not downloaded yet. Download it in Voice Control → Settings.");

        RuntimeOptions.RuntimeLibraryOrder = s.Whisper.UseGpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        var transcriber = await Task.Run(() => WhisperTranscriber.Load(models.PathFor(model), model, s.Whisper), ct).ConfigureAwait(false);
        return new UtteranceRecognitionSession(transcriber, options, () => settings().Vad);
    }

    /// <summary>The whisper.cpp model and a processor per language/prompt combination.</summary>
    private sealed class WhisperTranscriber : IUtteranceTranscriber
    {
        // Whisper's stock outputs for silence and noise: "[BLANK_AUDIO]", "(music)", "*coughs*"...
        private static readonly Regex NonSpeech = new(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪+", RegexOptions.Compiled);
        private readonly WhisperFactory _factory;
        private readonly WhisperModel _model;
        private readonly WhisperSettings _settings;
        private WhisperProcessor? _processor;
        private string _processorKey = "";

        private WhisperTranscriber(WhisperFactory factory, WhisperModel model, WhisperSettings settings, string runtime)
        {
            _factory = factory;
            _model = model;
            _settings = settings;
            Description = $"Whisper {model.DisplayName} ({runtime})";
        }

        public string Description { get; }

        public static WhisperTranscriber Load(string path, WhisperModel model, WhisperSettings settings)
        {
            WhisperFactory factory;
            try
            {
                factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = settings.UseGpu });
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                           || ex.GetType().Name.Contains("NativeLibrary", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The whisper.cpp native library could not be loaded. " + (OperatingSystem.IsWindows()
                        ? "Install the Microsoft Visual C++ 2015-2022 Redistributable (x64)."
                        : "It needs glibc 2.34 or newer, libstdc++ and libgomp (GCC's OpenMP runtime, e.g. the libgomp1 or gcc-libs package).")
                    + $" ({ex.Message})", ex);
            }
            var runtime = RuntimeOptions.LoadedLibrary?.ToString() ?? "CPU";
            return new WhisperTranscriber(factory, model, settings, runtime);
        }

        public async Task<Transcription?> TranscribeAsync(float[] samples, RecognitionOptions options, CancellationToken ct)
        {
            var processor = ProcessorFor(options);
            var text = new StringBuilder();
            double weighted = 0, weight = 0;
            string? language = null;
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                var clean = NonSpeech.Replace(segment.Text, " ").Trim();
                if (clean.Length == 0) continue;
                text.Append(text.Length > 0 ? " " : "").Append(clean);
                weighted += segment.Probability * clean.Length;
                weight += clean.Length;
                language ??= segment.Language;
            }
            return text.Length == 0 ? null : new Transcription(text.ToString(), weight > 0 ? weighted / weight : 0, language);
        }

        private WhisperProcessor ProcessorFor(RecognitionOptions options)
        {
            var language = _model.EnglishOnly ? "en" : WhisperLanguages.FromLocale(options.Locale);
            var prompt = _settings.PromptWithCommands && options.Phrases.Count > 0 ? string.Join(", ", options.Phrases.Distinct()) + "." : "";
            var key = language + "|" + prompt;
            if (_processor is not null && key == _processorKey) return _processor;
            _processor?.Dispose();
            var threads = _settings.Threads > 0 ? _settings.Threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            var builder = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithThreads(threads)
                .WithNoContext()
                .WithSingleSegment()
                .WithProbabilities();
            if (prompt.Length > 0) builder = builder.WithPrompt(prompt);
            _processor = builder.Build();
            _processorKey = key;
            return _processor;
        }

        public async ValueTask DisposeAsync()
        {
            if (_processor is not null) await _processor.DisposeAsync().ConfigureAwait(false);
            _factory.Dispose();
        }
    }
}

/// <summary>Languages Whisper understands (ISO 639-1 codes, plus "haw", "yue").</summary>
internal static class WhisperLanguages
{
    public static readonly string[] Codes =
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi", "fi", "vi",
        "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la", "mi", "ml", "cy", "sk",
        "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq", "sw",
        "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be", "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo",
        "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su", "yue",
    ];

    /// <summary>"en-US" → "en"; "nb-NO" → "no"; empty → the system language; "auto" stays "auto".</summary>
    public static string FromLocale(string? locale)
    {
        if (string.Equals(locale?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        CultureInfo culture;
        try { culture = string.IsNullOrWhiteSpace(locale) ? CultureInfo.CurrentUICulture : CultureInfo.GetCultureInfo(locale.Trim()); }
        catch (CultureNotFoundException) { return "auto"; }
        var code = culture.TwoLetterISOLanguageName switch
        {
            "nb" => "no",
            "jv" => "jw",
            "fil" => "tl",
            var c => c,
        };
        if (culture.Name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)) code = "zh";
        return Codes.Contains(code) ? code : "auto";
    }
}
