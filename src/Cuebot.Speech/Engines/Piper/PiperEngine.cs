using System.Globalization;

namespace Cuebot.Speech;

/// <summary>
/// Speaks with Piper, a free voice engine that runs on this computer (no account, no internet once a voice is
/// downloaded). Runs the piper program once per line with <c>--output_raw</c> (16-bit mono PCM at the voice's sample
/// rate). Voice ids are the voice's file name, plus "#speaker" for voices with several speakers. Rate maps to
/// length_scale; pitch is not supported.
/// </summary>
public sealed class PiperEngine(PiperStore store, Func<PiperSettings> settings) : ITtsEngine
{
    public string Id => "piper";
    public string DisplayName => "Piper (free, offline)";

    public PiperStore Store => store;

    public bool IsAvailable => store.FindProgram(settings().Executable) is not null && store.InstalledVoices().Count > 0;

    public string? UnavailableReason =>
        store.FindProgram(settings().Executable) is null ? "Press \"Get Piper\" to download it (about 25 MB)."
        : store.InstalledVoices().Count == 0 ? "Download at least one voice below."
        : null;

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        var voices = new List<VoiceInfo>();
        foreach (var voice in store.InstalledVoices())
        {
            if (voice.Speakers.Count <= 1) voices.Add(new VoiceInfo(voice.Key, voice.Key, voice.LanguageCode));
            else voices.AddRange(voice.Speakers.Select(s => new VoiceInfo($"{voice.Key}#{s}", $"{voice.Key} ({s})", voice.LanguageCode)));
        }
        return Task.FromResult<IReadOnlyList<VoiceInfo>>(voices);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        var program = store.FindProgram(settings().Executable) ?? throw new InvalidOperationException(UnavailableReason);
        var installed = store.InstalledVoices();
        var (key, speaker) = SplitVoiceId(request.VoiceId);
        var voice = installed.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault()
            ?? throw new InvalidOperationException(UnavailableReason);
        var result = await ProcessRunner.RunAsync(program, BuildArguments(voice, speaker, request.Rate), OneLine(request.Text), ct)
            .ConfigureAwait(false);
        return SynthesizedAudio.Wav(Wav.FromPcm16(result.EnsureSuccess("Piper").Output, voice.SampleRate, channels: 1));
    }

    internal static (string Key, string? Speaker) SplitVoiceId(string id)
    {
        var hash = id.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (id, null) : (id[..hash], id[(hash + 1)..]);
    }

    internal static List<string> BuildArguments(PiperInstalledVoice voice, string? speaker, int rate)
    {
        List<string> arguments =
        [
            "--model", voice.ModelPath,
            "--output_raw", "--quiet",
            "--length_scale", LengthScale(rate).ToString("0.##", CultureInfo.InvariantCulture),
        ];
        var index = speaker is null ? -1 : voice.Speakers.ToList().FindIndex(s => string.Equals(s, speaker, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) arguments.AddRange(["--speaker", index.ToString(CultureInfo.InvariantCulture)]);
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
