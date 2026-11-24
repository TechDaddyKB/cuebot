using System.Globalization;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>Which program speaks a library voice.</summary>
public enum VoiceBackend
{
    None,

    /// <summary>sherpa-onnx in Cuebot's voice engine (<see cref="VoiceEngine"/>): every format, every desktop system.</summary>
    VoiceEngine,

    /// <summary>The Piper 2023.11.14-2 program Cuebot 1.0.6 and before downloaded (Windows and Linux; classic Piper voices only).</summary>
    PiperProgram,
}

/// <summary>
/// Speaks the voices of the shared <see cref="VoiceLibrary"/>: Piper voices old and new, sherpa-onnx Piper packages
/// (such as GLaDOS), Kokoro, Kitten and Matcha. Each voice is spoken by the best program that can speak it (<see cref="ChooseBackend"/>).
/// The id stays "piper", the id of the Piper engine this replaces, so voice aliases made before keep working: library
/// voice ids are folder names, and a migrated Piper voice keeps its name. Voices with several speakers are
/// "folder#speaker". Rate maps to length_scale (Piper) or speed (voice engine); Cuebot shifts the pitch.
/// </summary>
public sealed class VoiceLibraryEngine(VoiceLibrary library, VoiceEngine engine, PiperStore store, Func<PiperSettings> settings) : ITtsEngine
{
    public string Id => "piper";

    public string DisplayName => "Voice library (Piper, Kokoro and more, free and offline)";

    public PitchSupport PitchSupport => PitchSupport.Cuebot;

    public VoiceLibrary Library => library;

    public VoiceEngine Engine => engine;

    public PiperStore Store => store;

    private string? PiperProgram => store.FindProgram(settings().Executable);

    public bool IsAvailable => library.Voices().Any(v => Choose(v).Backend != VoiceBackend.None);

    public string? UnavailableReason
    {
        get
        {
            var voices = library.Voices();
            if (voices.Count == 0) return "Download a voice below, or import one.";
            if (IsAvailable) return null;
            return voices.Select(v => Choose(v).Why).FirstOrDefault(w => w is not null) ?? "None of your voices can speak yet.";
        }
    }

    /// <summary>Which backend speaks <paramref name="voice"/> here, or why none can.</summary>
    public (VoiceBackend Backend, string? Why) Choose(LibraryVoice voice) =>
        ChooseBackend(voice, engine.IsInstalled, PiperProgram is not null);

    /// <summary>
    /// The voice engine when it is installed: in CI's Whisper checks it speaks every format on Windows, macOS and Linux
    /// at least as accurately as the Piper program (classic Piper voices: word error rate 0.00-0.04 for both), keeps the
    /// voice loaded between lines, and is the only one for retrained Piper, sherpa-onnx and Kokoro voices. Without it,
    /// classic Piper voices fall back to the Piper program, if there is one.
    /// </summary>
    internal static (VoiceBackend Backend, string? Why) ChooseBackend(LibraryVoice voice, bool engineInstalled, bool piperProgram)
    {
        if (voice.Problem is not null) return (VoiceBackend.None, voice.Problem);
        if (engineInstalled) return (VoiceBackend.VoiceEngine, null);
        if (voice.Format == VoiceFormat.Piper && piperProgram) return (VoiceBackend.PiperProgram, null);
        if (!VoiceEngine.CanInstall) return (VoiceBackend.None, "Cuebot's voice engine has no build for this computer.");
        return (VoiceBackend.None, $"{voice.Id} needs Cuebot's voice engine: press \"Get voice engine\" on the Engines tab (about {VoiceEngine.DownloadMegabytes} MB, once).");
    }

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        var voices = new List<VoiceInfo>();
        foreach (var voice in library.Voices().Where(v => Choose(v).Backend != VoiceBackend.None))
        {
            if (voice.Speakers.Count <= 1) voices.Add(new VoiceInfo(voice.Id, voice.Id, voice.LanguageCode));
            else voices.AddRange(voice.Speakers.Select(s => new VoiceInfo($"{voice.Id}#{s.Name}", $"{voice.Id} ({s.Name})", LanguageOf(s, voice))));
        }
        return Task.FromResult<IReadOnlyList<VoiceInfo>>(voices);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        var (key, speaker) = SplitVoiceId(request.VoiceId);
        var voices = library.Voices();
        var voice = voices.FirstOrDefault(v => string.Equals(v.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => Choose(v).Backend != VoiceBackend.None)
            ?? throw new InvalidOperationException(UnavailableReason ?? "There is no voice in the voice library.");
        var (backend, why) = Choose(voice);
        var text = OneLine(request.Text);
        switch (backend)
        {
            case VoiceBackend.VoiceEngine:
                return SynthesizedAudio.Wav(await engine.SpeakAsync(EngineRequest(voice, voice.Speaker(speaker), text, request.Rate, library), ct).ConfigureAwait(false));
            case VoiceBackend.PiperProgram:
                var program = PiperProgram!;
                // The downloaded program is Piper 2023.11.14-2, which refuses multi-character phonemes some voices list;
                // a program the user chose may be newer, so it gets the voice as it is.
                var config = store.IsDownloadedProgram(program) ? PiperStore.PrepareConfig(voice) : null;
                var result = await ProcessRunner.RunAsync(program, BuildArguments(voice, speaker, request.Rate, config), text, ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(DescribeFailure(result.ExitCode, result.Error, voice.Id, OperatingSystem.IsWindows()));
                return SynthesizedAudio.Wav(Wav.FromPcm16(result.Output, voice.SampleRate, channels: 1));
            default:
                throw new InvalidOperationException(why);
        }
    }

    /// <summary>"es" for Kokoro's Spanish "ef_dora"; the voice's own language for everything else.</summary>
    private static string LanguageOf(VoiceSpeaker speaker, LibraryVoice voice) =>
        speaker.EspeakVoice == voice.EspeakVoice ? voice.LanguageCode : speaker.EspeakVoice.Split('-')[0];

    /// <summary>The voice engine request for one line (see cuebot_voice_host.py).</summary>
    internal static JsonObject EngineRequest(LibraryVoice voice, VoiceSpeaker? speaker, string text, int rate, VoiceLibrary library)
    {
        var request = new JsonObject
        {
            ["kind"] = voice.Format switch
            {
                VoiceFormat.Kokoro => "kokoro",
                VoiceFormat.Kitten => "kitten",
                VoiceFormat.Matcha => "matcha",
                _ => "vits",
            },
            ["model"] = voice.ModelPath,
            ["frontend"] = voice.PhonemeType == "text" ? "text" : "espeak",
            ["espeak_voice"] = speaker?.EspeakVoice ?? voice.EspeakVoice,
            ["text"] = text,
            ["sid"] = speaker?.Id ?? 0,
            ["speed"] = Math.Round(1 / LengthScale(rate), 3),
        };
        if (voice.Format is VoiceFormat.Piper or VoiceFormat.PiperClusters)
        {
            var prepared = library.PrepareForSherpa(voice);
            request["tokens"] = prepared.Tokens;
            request["clusters"] = new JsonArray([.. prepared.Clusters.Select(c => (JsonNode)new JsonArray(new JsonArray([.. c.Phonemes.Select(p => (JsonNode)p)]), c.Symbol))]);
        }
        else request["tokens"] = voice.TokensPath;
        if (voice.Format is VoiceFormat.Kokoro or VoiceFormat.Kitten) request["voices"] = voice.VoicesPath;
        if (voice.Format == VoiceFormat.Matcha) request["vocoder"] = voice.VocoderPath;
        return request;
    }

    /// <summary>
    /// What to tell the user when the Piper program exits with an error: what happened and what to try, with Piper's
    /// own error line when it wrote one, instead of a bare exit code such as -1073740791.
    /// </summary>
    internal static string DescribeFailure(int exitCode, string error, string voiceKey, bool windows)
    {
        var code = windows ? $"0x{unchecked((uint)exitCode):X8}" : exitCode.ToString(CultureInfo.InvariantCulture);
        var detail = string.Join(" ", error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("[error]", StringComparison.Ordinal) || l.Contains("[critical]", StringComparison.Ordinal)
                || l.StartsWith("what():", StringComparison.Ordinal))
            .Select(l => l[(l.LastIndexOf("] ", StringComparison.Ordinal) + 1)..].Trim()));
        const string UseEngine = "Press \"Get voice engine\" on the Engines tab and Cuebot speaks your voices with its voice engine instead.";
        var message = unchecked((uint)exitCode) switch
        {
            // STATUS_DLL_NOT_FOUND, STATUS_INVALID_IMAGE_FORMAT, STATUS_ENTRYPOINT_NOT_FOUND
            0xC0000135 or 0xC000007B or 0xC0000139 when windows =>
                $"Piper could not start because one of its files is missing or damaged (error {code}). {UseEngine}",
            // STATUS_STACK_BUFFER_OVERRUN (also raised by abort()), STATUS_ACCESS_VIOLATION, STATUS_HEAP_CORRUPTION
            0xC0000409 or 0xC0000005 or 0xC0000374 when windows => Crashed(),
            // 128 + SIGABRT, SIGSEGV, SIGBUS
            134 or 139 or 135 when !windows => Crashed(),
            _ => $"Piper stopped with error {code} while speaking with the voice \"{voiceKey}\". Try another voice. {UseEngine}",
        };
        return detail.Length > 0 ? $"{message} Piper said: {detail}" : message;

        string Crashed() =>
            $"Piper crashed while speaking with the voice \"{voiceKey}\" (error {code}). Try another voice, or delete this "
            + $"voice and download it again. {UseEngine}";
    }

    internal static (string Key, string? Speaker) SplitVoiceId(string id)
    {
        var hash = id.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (id, null) : (id[..hash], id[(hash + 1)..]);
    }

    /// <summary>The Piper program's arguments. No <c>--quiet</c>: Piper's error lines are wanted for <see cref="DescribeFailure"/>.</summary>
    internal static List<string> BuildArguments(LibraryVoice voice, string? speaker, int rate, string? config = null)
    {
        List<string> arguments =
        [
            "--model", voice.ModelPath!,
            "--output_raw",
            "--length_scale", LengthScale(rate).ToString("0.##", CultureInfo.InvariantCulture),
        ];
        if (config is not null && config != voice.ConfigPath) arguments.AddRange(["--config", config]);
        if (speaker is not null && voice.Speakers.FirstOrDefault(s => string.Equals(s.Name, speaker, StringComparison.OrdinalIgnoreCase)) is { } chosen)
            arguments.AddRange(["--speaker", chosen.Id.ToString(CultureInfo.InvariantCulture)]);
        return arguments;
    }

    /// <summary>Maps the -10..10 rate scale to Piper's length_scale (1.6 slow .. 0.5 fast, 1 normal).</summary>
    internal static double LengthScale(int rate)
    {
        var clamped = Math.Clamp(rate, -10, 10);
        return Math.Round(clamped < 0 ? 1.0 - clamped * 0.06 : 1.0 - clamped * 0.05, 2);
    }

    /// <summary>Piper speaks each input line as a separate utterance; one line keeps a message together.</summary>
    internal static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
