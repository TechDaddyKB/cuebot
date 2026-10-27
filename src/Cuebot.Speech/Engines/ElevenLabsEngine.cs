using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>Speaks through the ElevenLabs REST API. Available once an API key is configured.</summary>
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
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voiceId, int rate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("An ElevenLabs voice must be selected.", nameof(voiceId));
        using var request = CreateRequest(HttpMethod.Post,
            $"text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=pcm_{SampleRate}");
        request.Content = JsonContent.Create(BuildBody(text, rate));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Wav.FromPcm16(pcm, SampleRate, channels: 1);
    }

    /// <summary>Maps the -10..10 rate scale to ElevenLabs' speed setting (0.7..1.2, 1 normal).</summary>
    internal static double RateToSpeed(int rate)
    {
        var clamped = Math.Clamp(rate, -10, 10);
        return Math.Round(1.0 + clamped * (clamped < 0 ? 0.03 : 0.02), 2);
    }

    private static JsonObject BuildBody(string text, int rate)
    {
        var body = new JsonObject { ["text"] = text, ["model_id"] = Model };
        // Only send voice settings when needed: a partial object would override the voice's saved settings.
        if (rate != 0) body["voice_settings"] = new JsonObject { ["speed"] = RateToSpeed(rate) };
        return body;
    }

    private static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root)
    {
        if (!root.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array) return [];
        var result = new List<VoiceInfo>();
        foreach (var voice in voices.EnumerateArray())
        {
            var id = GetString(voice, "voice_id");
            if (string.IsNullOrEmpty(id)) continue;
            var language = voice.TryGetProperty("labels", out var labels) ? GetString(labels, "language") : null;
            result.Add(new VoiceInfo(id, GetString(voice, "name") ?? id, language));
        }
        return result;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(UnavailableReason);
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
        request.Headers.Add(ApiKeyHeader, key.Trim());
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"ElevenLabs returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail, 300)}",
                inner: null, response.StatusCode);
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
