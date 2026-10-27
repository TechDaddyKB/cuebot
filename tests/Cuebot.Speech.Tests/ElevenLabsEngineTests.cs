using System.Net;
using System.Text;
using System.Text.Json;

namespace Cuebot.Speech.Tests;

public class ElevenLabsEngineTests
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

    private static (ElevenLabsEngine Engine, FakeHandler Handler) Create(HttpResponseMessage response, string key = "secret")
    {
        var handler = new FakeHandler(_ => response);
        return (new ElevenLabsEngine(new HttpClient(handler), () => key), handler);
    }

    [Fact]
    public async Task Lists_voices_with_api_key_header()
    {
        const string json = """
            {"voices":[
              {"voice_id":"abc","name":"Rachel","labels":{"language":"en","accent":"american"}},
              {"voice_id":"def","name":"Clyde"},
              {"name":"no id"}
            ]}
            """;
        var (engine, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        var voices = await engine.GetVoicesAsync(CancellationToken.None);

        Assert.Equal([new VoiceInfo("abc", "Rachel", "en"), new VoiceInfo("def", "Clyde", null)], voices);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/voices", handler.Request.RequestUri!.ToString());
        Assert.Equal("secret", handler.Request.Headers.GetValues("xi-api-key").Single());
    }

    [Fact]
    public async Task Synthesizes_pcm_and_wraps_it_in_wav()
    {
        var (engine, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 0, 2, 0, 3, 0]),
        });

        var wav = await engine.SynthesizeAsync("-hello", "voice 1", rate: 0, CancellationToken.None);

        var info = Wav.Parse(wav);
        Assert.Equal(new WavFormat(WavFormat.PcmFormat, 1, 22050, 16), info.Format);
        Assert.Equal(6, info.DataLength);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/voice%201?output_format=pcm_22050",
            handler.Request.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("-hello", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("eleven_flash_v2_5", body.RootElement.GetProperty("model_id").GetString());
        Assert.False(body.RootElement.TryGetProperty("voice_settings", out _));
    }

    [Fact]
    public async Task Sends_speed_when_rate_is_not_normal()
    {
        var (engine, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

        await engine.SynthesizeAsync("hi", "v", rate: 10, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1.2, body.RootElement.GetProperty("voice_settings").GetProperty("speed").GetDouble());
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-10, 0.7)]
    [InlineData(10, 1.2)]
    [InlineData(5, 1.1)]
    public void Maps_rate_to_speed(int rate, double expected) => Assert.Equal(expected, ElevenLabsEngine.RateToSpeed(rate));

    [Fact]
    public async Task Error_responses_throw_with_status_and_detail()
    {
        var (engine, _) = Create(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"detail":"invalid key"}""", Encoding.UTF8, "application/json"),
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => engine.GetVoicesAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("invalid key", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Is_unavailable_without_an_api_key()
    {
        var (engine, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK), key: " ");

        Assert.False(engine.IsAvailable);
        Assert.NotNull(engine.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetVoicesAsync(CancellationToken.None));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Pitch_maps_to_stability_like_speakerbot()
    {
        var (engine, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

        await engine.SynthesizeAsync(new SynthesisRequest("hi", "v") { Pitch = -4 }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        var settings = body.RootElement.GetProperty("voice_settings");
        Assert.Equal(0.3, settings.GetProperty("stability").GetDouble());
        Assert.False(settings.TryGetProperty("speed", out _));
    }
}
