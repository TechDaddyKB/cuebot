using System.Net;
using System.Text.Json;
using Cuebot.Core.Logging;

namespace Cuebot.Speech.Tests;

public class OpenAiEngineTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    [Fact]
    public async Task Synthesizes_wav_with_bearer_key_voice_speed_and_instructions()
    {
        var wav = Wav.FromPcm16([1, 0, 2, 0], 24000, channels: 1);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) });
        var settings = new OpenAiSpeechSettings { ApiKey = " sk-test ", Instructions = "Calm" };
        var engine = new OpenAiEngine(new HttpClient(handler), () => settings);

        var audio = await engine.SynthesizeAsync(new SynthesisRequest("Hello chat", "coral") { Rate = 5 }, CancellationToken.None);

        Assert.Equal(AudioFormat.Wav, audio.Format);
        Assert.Equal("https://api.openai.com/v1/audio/speech", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.Request.Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("gpt-4o-mini-tts", body.GetProperty("model").GetString());
        Assert.Equal("coral", body.GetProperty("voice").GetString());
        Assert.Equal("wav", body.GetProperty("response_format").GetString());
        Assert.Equal(1.5, body.GetProperty("speed").GetDouble());
        Assert.Equal("Calm", body.GetProperty("instructions").GetString());
    }

    [Fact]
    public void Style_wins_tts1_skips_instructions_and_long_text_is_cut()
    {
        var settings = new OpenAiSpeechSettings { Instructions = "Calm" };
        var styled = OpenAiEngine.BuildBody(new SynthesisRequest("hi", "nova") { Style = "Whisper like a secret" }, settings);
        Assert.Equal("Whisper like a secret", styled["instructions"]!.GetValue<string>());
        Assert.False(styled.ContainsKey("speed"));

        settings.Model = "tts-1";
        var old = OpenAiEngine.BuildBody(new SynthesisRequest(new string('a', 5000), "nobody"), settings);
        Assert.False(old.ContainsKey("instructions"));
        Assert.Equal("alloy", old["voice"]!.GetValue<string>());
        Assert.Equal(OpenAiEngine.MaxCharacters, old["input"]!.GetValue<string>().Length);
        Assert.Equal(0.5, OpenAiEngine.RateToSpeed(-10));
        Assert.Equal(2.0, OpenAiEngine.RateToSpeed(10));
    }

    [Fact]
    public async Task Needs_a_key_and_lists_the_fixed_voices()
    {
        var settings = new OpenAiSpeechSettings();
        var engine = new OpenAiEngine(new HttpClient(), () => settings);
        Assert.False(engine.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetVoicesAsync(CancellationToken.None));
        settings.ApiKey = "sk";
        var voices = await engine.GetVoicesAsync(CancellationToken.None);
        Assert.Contains(voices, v => v.Id == "marin" && v.Name == "Marin" && v.Styles.Contains("Cheerful"));
        Assert.Equal(13, voices.Count);
    }
}

public sealed class PiperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("piper-test").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Catalog = """
        {
          "en_US-lessac-medium": {"key":"en_US-lessac-medium","name":"lessac","language":{"code":"en_US","name_english":"English","country_english":"United States"},
            "quality":"medium","num_speakers":1,"speaker_id_map":{},
            "files":{"en/en_US/lessac/medium/en_US-lessac-medium.onnx":{"size_bytes":10,"md5_digest":"aa"},
                     "en/en_US/lessac/medium/en_US-lessac-medium.onnx.json":{"size_bytes":5,"md5_digest":"bb"},
                     "en/en_US/lessac/medium/MODEL_CARD":{"size_bytes":1,"md5_digest":"cc"}}},
          "de_DE-thorsten-high": {"name":"thorsten","language":{"code":"de_DE","name_english":"German","country_english":"Germany"},
            "quality":"high","num_speakers":2,
            "files":{"de/x.onnx":{"size_bytes":1,"md5_digest":"d"},"de/x.onnx.json":{"size_bytes":1,"md5_digest":"e"}}},
          "broken": {"name":"no files"}
        }
        """;

    [Fact]
    public void Catalog_is_parsed_and_sorted_by_what_the_picker_shows()
    {
        var voices = PiperStore.ParseCatalog(Catalog);
        Assert.Equal(["English (United States): Lessac, medium", "German (Germany): Thorsten, high, 2 voices"], voices.Select(v => v.Display));
        Assert.Equal("en/en_US/lessac/medium/en_US-lessac-medium.onnx", voices[0].Model.Path);
        Assert.Equal(15, voices[0].SizeBytes);
    }

    [Fact]
    public async Task Installed_voices_become_voices_and_arguments()
    {
        var store = new PiperStore(_dir, new HttpClient(), new AppLog());
        Directory.CreateDirectory(store.VoicesDirectory);
        File.WriteAllBytes(Path.Combine(store.VoicesDirectory, "en_US-lessac-medium.onnx"), [0]);
        File.WriteAllText(Path.Combine(store.VoicesDirectory, "en_US-lessac-medium.onnx.json"),
            """{"audio":{"sample_rate":22050},"language":{"code":"en_US"},"speaker_id_map":{}}""");
        File.WriteAllBytes(Path.Combine(store.VoicesDirectory, "en_GB-vctk-medium.onnx"), [0]);
        File.WriteAllText(Path.Combine(store.VoicesDirectory, "en_GB-vctk-medium.onnx.json"),
            """{"audio":{"sample_rate":16000},"language":{"code":"en_GB"},"speaker_id_map":{"p239":1,"p225":0}}""");
        File.WriteAllBytes(Path.Combine(store.VoicesDirectory, "half.onnx"), [0]); // no .json: skipped

        var settings = new PiperSettings();
        var engine = new PiperEngine(store, () => settings);
        Assert.False(engine.IsAvailable);
        Assert.Contains("Get Piper", engine.UnavailableReason);

        var voices = await engine.GetVoicesAsync(CancellationToken.None);
        Assert.Equal(["en_GB-vctk-medium#p225", "en_GB-vctk-medium#p239", "en_US-lessac-medium"], voices.Select(v => v.Id));

        var vctk = store.InstalledVoices().Single(v => v.Key == "en_GB-vctk-medium");
        Assert.Equal(16000, vctk.SampleRate);
        var args = PiperEngine.BuildArguments(vctk, "p239", rate: -10);
        Assert.Equal(["--model", vctk.ModelPath, "--output_raw", "--quiet", "--length_scale", "1.6", "--speaker", "1"], args);
        Assert.Equal(0.5, PiperEngine.LengthScale(10));
        Assert.Equal("one two three", PiperEngine.OneLine(" one\r\ntwo\n\nthree "));
        Assert.Equal(("a", "b"), PiperEngine.SplitVoiceId("a#b"));
        Assert.True(store.IsInstalled("en_US-lessac-medium"));
        store.DeleteVoice("en_US-lessac-medium");
        Assert.False(store.IsInstalled("en_US-lessac-medium"));
    }

    [Fact]
    public void Every_desktop_platform_has_a_pinned_program()
    {
        var release = PiperRelease.ForThisComputer();
        Assert.NotNull(release);
        Assert.Equal(64, release!.Sha256.Length);
        Assert.StartsWith("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/", release.Url.ToString());
    }

    /// <summary>Downloads Piper and a voice for real and speaks (set CUEBOT_LIVE_PIPER=1; about 90 MB).</summary>
    [Fact]
    public async Task Live_download_and_speak()
    {
        if (Environment.GetEnvironmentVariable("CUEBOT_LIVE_PIPER") != "1") return;
        using var http = new HttpClient();
        var store = new PiperStore(_dir, http, new AppLog());
        await store.InstallProgramAsync();
        var voice = (await store.GetCatalogAsync()).Single(v => v.Key == "en_US-lessac-low");
        await store.DownloadVoiceAsync(voice);
        var engine = new PiperEngine(store, () => new PiperSettings());
        Assert.True(engine.IsAvailable);
        var audio = await engine.SynthesizeAsync(new SynthesisRequest("Thanks for the follow,\nAnn!", "en_US-lessac-low"), CancellationToken.None);
        var info = Wav.Parse(audio.Data);
        Assert.Equal(16000, info.Format.SampleRate);
        Assert.True(info.DataLength > 16000, $"only {info.DataLength} bytes of audio");
    }
}

public class OpenAiEngineLiveTests
{
    /// <summary>Speaks one short line through OpenAI (set CUEBOT_LIVE_OPENAI_TTS=1 and CUEBOT_LIVE_OPENAI_API_KEY).</summary>
    [Fact]
    public async Task Live_speaks()
    {
        var key = Environment.GetEnvironmentVariable("CUEBOT_LIVE_OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("CUEBOT_LIVE_OPENAI_TTS") != "1" || string.IsNullOrEmpty(key)) return;
        using var http = new HttpClient();
        var settings = new OpenAiSpeechSettings { ApiKey = key, Instructions = "Cheerful" };
        var audio = await new OpenAiEngine(http, () => settings)
            .SynthesizeAsync(new SynthesisRequest("Thanks for the raid!", "marin") { Rate = 2 }, CancellationToken.None);
        var info = Wav.Parse(audio.Data);
        Assert.True(info.DataLength > 10000, $"only {info.DataLength} bytes");
    }
}
