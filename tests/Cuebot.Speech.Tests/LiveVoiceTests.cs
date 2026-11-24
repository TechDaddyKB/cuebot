using System.Runtime.InteropServices;
using Cuebot.Core.Logging;

namespace Cuebot.Speech.Tests;

/// <summary>
/// Real downloads, real speech. CI runs these on Windows, macOS and Linux and has Whisper check the words
/// (.github/workflows/voices.yml); WAVs go to CUEBOT_LIVE_VOICE_WAV_DIR when it is set.
/// </summary>
public sealed class LiveVoiceTests : IDisposable
{
    internal const string Line = "Thanks for the follow,\nAnn! I like my bright white kite.";
    private const string SherpaModels = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";

    private readonly string _dir = Directory.CreateTempSubdirectory("live-voices").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder; Windows may still hold a file of the voice engine for a moment.
        }
    }

    private static bool Enabled(string name) => Environment.GetEnvironmentVariable(name) == "1";

    internal const string Spanish = "Gracias por seguirme, Ana. Me gusta mucho mi cometa blanca.";
    // Whisper hears "cerf-volant blanc" as "cerveau l'ambulance" even though espeak-ng's phonemes for it are right
    // (sˈɛʁvolˈɑ̃ blˈɑ̃), so the French line sticks to everyday words.
    internal const string French = "Bonjour à tous, merci beaucoup pour votre soutien ce soir.";

    /// <summary>
    /// Installs the voice engine and speaks one voice of each format (set CUEBOT_LIVE_VOICES=1; about 550 MB):
    /// en_US-amy-medium as retrained for Piper 1.5 (vowel clusters; it crashed Piper in issue 61), en_US-lessac-low,
    /// and sherpa-onnx's GLaDOS, Kokoro (multi-language: English, Spanish and French speakers), Kitten and Matcha
    /// packages imported from their .tar.bz2 (Matcha gets its vocoder on import). The GLaDOS package carries Piper's
    /// .onnx.json as well, so it counts as a Piper voice; a copy without it ("glados-tokens-only") is spoken from
    /// sherpa-onnx's tokens.txt and model metadata alone. A line given up on half-way does not restart the engine.
    /// </summary>
    [Fact]
    public async Task Live_voice_engine_speaks_every_format()
    {
        if (!Enabled("CUEBOT_LIVE_VOICES")) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var log = new AppLog();
        var library = new VoiceLibrary(Path.Combine(_dir, "voices"), log, http);
        var store = new PiperStore(Path.Combine(_dir, "piper"), http, log, library);
        await using var voiceEngine = new VoiceEngine(Path.Combine(_dir, "voice-engine"), http, log);
        await voiceEngine.InstallAsync(new Progress<string>(Console.WriteLine));
        Assert.True(voiceEngine.IsInstalled);

        var catalog = await store.GetCatalogAsync();
        foreach (var key in new[] { "en_US-amy-medium", "en_US-lessac-low" })
            await store.DownloadVoiceAsync(catalog.Single(v => v.Key == key));
        foreach (var package in new[] { "vits-piper-en_US-glados-high", "kokoro-int8-multi-lang-v1_0", "kitten-nano-en-v0_8-int8", "matcha-icefall-en_US-ljspeech" })
        {
            var path = Path.Combine(_dir, package + ".tar.bz2");
            await using (var file = File.Create(path))
            await using (var body = await http.GetStreamAsync(SherpaModels + package + ".tar.bz2"))
                await body.CopyToAsync(file);
            await library.ImportAsync([path], move: true);
        }

        var glados = library.Find("vits-piper-en_US-glados-high")!.Folder;
        var tokensOnly = Path.Combine(_dir, "glados-tokens-only");
        CopyFolder(glados, tokensOnly, skip: name => name.EndsWith(".onnx.json", StringComparison.Ordinal));
        Directory.Move(tokensOnly, Path.Combine(library.Directory, "glados-tokens-only")); // copied in by hand
        library.Refresh();

        var formats = library.Voices().ToDictionary(v => v.Id, v => v.Format);
        Assert.Equal(VoiceFormat.PiperClusters, formats["en_US-amy-medium"]);
        Assert.Equal(VoiceFormat.Piper, formats["vits-piper-en_US-glados-high"]);
        Assert.Equal(VoiceFormat.SherpaVits, formats["glados-tokens-only"]);
        Assert.Equal(VoiceFormat.Kokoro, formats["kokoro-int8-multi-lang-v1_0"]);
        Assert.Equal(VoiceFormat.Kitten, formats["kitten-nano-en-v0_8-int8"]);
        Assert.Equal(VoiceFormat.Matcha, formats["matcha-icefall-en_US-ljspeech"]);
        Assert.Contains(formats["en_US-lessac-low"], new[] { VoiceFormat.Piper, VoiceFormat.PiperClusters });

        var engine = new VoiceLibraryEngine(library, voiceEngine, store, () => new PiperSettings());
        foreach (var voice in library.Voices().Where(v => v.Format != VoiceFormat.Kokoro))
        {
            Assert.Equal(VoiceBackend.VoiceEngine, engine.Choose(voice).Backend);
            await SpeakAndCheckAsync(engine, voice.Id, "engine");
        }
        const string kokoro = "kokoro-int8-multi-lang-v1_0";
        await SpeakAndCheckAsync(engine, $"{kokoro}#af_heart", "engine");
        await SpeakAndCheckAsync(engine, $"{kokoro}#ef_dora", "engine", Spanish);
        await SpeakAndCheckAsync(engine, $"{kokoro}#ff_siwis", "engine", French);

        // A line given up on (skipped) leaves the engine running with its voices loaded.
        var host = voiceEngine.HostProcessId;
        Assert.NotNull(host);
        using (var skip = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.SynthesizeAsync(new SynthesisRequest(Line + " " + Line + " " + Line, $"{kokoro}#af_heart"), skip.Token));
        await SpeakAndCheckAsync(engine, "kitten-nano-en-v0_8-int8", "after-skip");
        Assert.Equal(host, voiceEngine.HostProcessId);
    }

    /// <summary>
    /// The Piper 2023.11.14-2 program that older Cuebot versions installed still speaks classic Piper voices, and a
    /// retrained voice is refused with a reason instead of crashing it (set CUEBOT_LIVE_PIPER=1; Windows and Linux).
    /// </summary>
    [Fact]
    public async Task Live_piper_program_speaks_classic_voices()
    {
        if (!Enabled("CUEBOT_LIVE_PIPER") || PiperRelease.ForThisComputer() is null) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var log = new AppLog();
        var library = new VoiceLibrary(Path.Combine(_dir, "voices"), log);
        var store = new PiperStore(Path.Combine(_dir, "piper"), http, log, library);
        await store.InstallProgramAsync();
        var catalog = await store.GetCatalogAsync();
        await store.DownloadVoiceAsync(catalog.Single(v => v.Key == "en_US-lessac-low"));
        await store.DownloadVoiceAsync(catalog.Single(v => v.Key == "en_US-amy-medium"));

        var engine = new VoiceLibraryEngine(library, new VoiceEngine(Path.Combine(_dir, "voice-engine"), http, log), store, () => new PiperSettings());
        var lessac = library.Find("en_US-lessac-low")!;
        if (lessac.Format == VoiceFormat.Piper)
        {
            Assert.Equal(VoiceBackend.PiperProgram, engine.Choose(lessac).Backend);
            await SpeakAndCheckAsync(engine, lessac.Id, "program");
        }
        var amy = library.Find("en_US-amy-medium")!;
        Assert.Equal(VoiceFormat.PiperClusters, amy.Format);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.SynthesizeAsync(new SynthesisRequest(Line, amy.Id), CancellationToken.None));
        Assert.Contains("Get voice engine", ex.Message);
    }

    private static void CopyFolder(string from, string to, Func<string, bool> skip)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (skip(Path.GetFileName(file))) continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task SpeakAndCheckAsync(VoiceLibraryEngine engine, string id, string by, string line = Line)
    {
        var audio = await engine.SynthesizeAsync(new SynthesisRequest(line, id), CancellationToken.None);
        var info = Wav.Parse(audio.Data);
        var seconds = info.DataLength / 2.0 / info.Format.SampleRate;
        Assert.True(seconds is > 1.5 and < 10, $"{id}: {seconds:0.00} s of audio");
        var samples = MemoryMarshal.Cast<byte, short>(audio.Data.AsSpan(info.DataOffset, info.DataLength)).ToArray();
        var rms = Math.Sqrt(samples.Average(s => (double)s * s));
        Assert.True(rms > 300, $"{id}: nearly silent (RMS {rms:0})");
        Console.WriteLine($"{by} {id}: {seconds:0.00} s, {info.Format.SampleRate} Hz, RMS {rms:0}");
        var output = Environment.GetEnvironmentVariable("CUEBOT_LIVE_VOICE_WAV_DIR");
        if (!string.IsNullOrEmpty(output)) await File.WriteAllBytesAsync(Path.Combine(output, $"{by}-{id.Replace('#', '-')}.wav"), audio.Data);
    }
}
