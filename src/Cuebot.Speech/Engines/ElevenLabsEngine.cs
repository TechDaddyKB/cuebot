using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// Speaks through the ElevenLabs REST API. Available once an API key is configured. Rate maps to the speed setting;
/// pitch maps to stability (as in Speaker.bot, which relabels its pitch slider for ElevenLabs voices).
/// </summary>
internal sealed class ElevenLabsEngine(HttpClient http, Func<string> apiKey) : ITtsEngine
{
    internal const string BaseUrl = "https://api.elevenlabs.io/v1/";
    internal const string Model = "eleven_flash_v2_5";
    internal const int SampleRate = 22050;
    private const string ApiKeyHeader = "xi-api-key";

    public string Id => "elevenlabs";
    public string DisplayName => "ElevenLabs";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey());
    public string? UnavailableReason => IsAvailable ? null : "Enter an ElevenLabs API key in the speech settings.";

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "voices");
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(synthesis.VoiceId))
            throw new ArgumentException("An ElevenLabs voice must be selected.", nameof(synthesis));
        using var request = CreateRequest(HttpMethod.Post,
            $"text-to-speech/{Uri.EscapeDataString(synthesis.VoiceId)}?output_format=pcm_{SampleRate}");
        request.Content = JsonContent.Create(BuildBody(synthesis.Text, synthesis.Rate, synthesis.Pitch));
        var pcm = await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return SynthesizedAudio.Wav(Wav.FromPcm16(pcm, SampleRate, channels: 1));
    }

    /// <summary>Maps the -10..10 rate scale to ElevenLabs' speed setting (0.7..1.2, 1 normal).</summary>
    internal static double RateToSpeed(int rate)
    {
        var clamped = Math.Clamp(rate, -10, 10);
        return Math.Round(1.0 + clamped * (clamped < 0 ? 0.03 : 0.02), 2);
    }

    /// <summary>Maps the -10..10 pitch scale to stability (0..1, 0.5 at 0).</summary>
    internal static double PitchToStability(int pitch) => Math.Round(0.5 + Math.Clamp(pitch, -10, 10) * 0.05, 2);

    private static JsonObject BuildBody(string text, int rate, int pitch)
    {
        var body = new JsonObject { ["text"] = text, ["model_id"] = Model };
        // Only send voice settings when needed: a partial object would override the voice's saved settings.
        var settings = new JsonObject();
        if (rate != 0) settings["speed"] = RateToSpeed(rate);
        if (pitch != 0) settings["stability"] = PitchToStability(pitch);
        if (settings.Count > 0) body["voice_settings"] = settings;
        return body;
    }

    private static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root)
    {
        var result = new List<VoiceInfo>();
        foreach (var voice in root.GetArray("voices"))
        {
            var id = voice.GetText("voice_id");
            if (string.IsNullOrEmpty(id)) continue;
            var language = voice.TryGetProperty("labels", out var labels) ? labels.GetText("language") : null;
            result.Add(new VoiceInfo(id, voice.GetText("name") ?? id, language));
        }
        return result;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(UnavailableReason);
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
        request.Headers.Add(ApiKeyHeader, key.Trim());
        return request;
    }
}
