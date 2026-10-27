using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// Amazon Polly over REST with SigV4. Voice ids are "Name:engine" (e.g. "Joanna:neural"); a bare name uses the
/// configured engine. Pitch is only sent to standard voices (neural and generative ignore it).
/// </summary>
internal sealed class PollyEngine(HttpClient http, Func<PollySettings> settings, Func<DateTimeOffset>? clock = null) : ITtsEngine
{
    internal const int SampleRate = 16000;
    internal static readonly string[] Engines = ["standard", "neural", "long-form", "generative"];

    public string Id => "polly";
    public string DisplayName => "Amazon Polly";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason
    {
        get
        {
            var s = settings();
            if (string.IsNullOrWhiteSpace(s.AccessKeyId) || string.IsNullOrWhiteSpace(s.SecretAccessKey))
                return "Enter an AWS access key ID and secret access key in the speech settings.";
            return IsValidRegion(s.Region) ? null : "Enter the AWS region (e.g. us-east-1).";
        }
    }

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        var voices = new List<VoiceInfo>();
        string? nextToken = null;
        do
        {
            var query = nextToken is null ? "" : "?NextToken=" + Uri.EscapeDataString(nextToken);
            using var request = CreateRequest(HttpMethod.Get, "/v1/voices" + query, body: null);
            using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
            voices.AddRange(ParseVoices(json.RootElement));
            nextToken = json.RootElement.GetText("NextToken");
        } while (!string.IsNullOrEmpty(nextToken));
        return voices;
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(BuildBody(synthesis, settings().Engine).ToJsonString());
        using var request = CreateRequest(HttpMethod.Post, "/v1/speech", body);
        var pcm = await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return SynthesizedAudio.Wav(Wav.FromPcm16(pcm, SampleRate, channels: 1));
    }

    /// <summary>"Joanna:neural" → ("Joanna", "neural"); a bare name gets <paramref name="defaultEngine"/>.</summary>
    internal static (string Voice, string Engine) ParseVoiceId(string voiceId, string defaultEngine)
    {
        var parts = voiceId.Trim().Split(':', 2);
        var engine = parts.Length == 2 && Engines.Contains(parts[1].ToLowerInvariant()) ? parts[1].ToLowerInvariant() : null;
        engine ??= Engines.Contains(defaultEngine.Trim().ToLowerInvariant()) ? defaultEngine.Trim().ToLowerInvariant() : "standard";
        return (parts[0], engine);
    }

    internal static JsonObject BuildBody(SynthesisRequest synthesis, string defaultEngine)
    {
        var (voice, engine) = ParseVoiceId(synthesis.VoiceId, defaultEngine);
        if (voice.Length == 0) throw new ArgumentException("An Amazon Polly voice must be selected.", nameof(synthesis));
        var includePitch = engine == "standard";
        var useSsml = synthesis.Rate != 0 || (includePitch && synthesis.Pitch != 0);
        return new JsonObject
        {
            ["Engine"] = engine,
            ["OutputFormat"] = "pcm",
            ["SampleRate"] = SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Text"] = useSsml ? Ssml.Speak(synthesis.Text, synthesis.Rate, synthesis.Pitch, includePitch) : synthesis.Text,
            ["TextType"] = useSsml ? "ssml" : "text",
            ["VoiceId"] = voice,
        };
    }

    internal static IEnumerable<VoiceInfo> ParseVoices(JsonElement root)
    {
        foreach (var voice in root.GetArray("Voices"))
        {
            var id = voice.GetText("Id");
            if (string.IsNullOrEmpty(id)) continue;
            var name = voice.GetText("Name") ?? id;
            var engines = voice.GetArray("SupportedEngines").Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToArray();
            if (engines.Length == 0) engines = ["standard"];
            foreach (var engine in engines)
            {
                var tier = engine switch
                {
                    "neural" => "Neural",
                    "generative" => "Generative",
                    "long-form" => "Long-form",
                    _ => "Standard",
                };
                yield return new VoiceInfo($"{id}:{engine}", $"{name} ({tier})", voice.GetText("LanguageCode"))
                {
                    Gender = voice.GetText("Gender"),
                    Tier = tier,
                };
            }
        }
    }

    private static bool IsValidRegion(string region) =>
        region.Trim().Length > 0 && region.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery, byte[]? body)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var s = settings();
        var region = s.Region.Trim().ToLowerInvariant();
        var request = new HttpRequestMessage(method, new Uri($"https://polly.{region}.amazonaws.com{pathAndQuery}"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        AwsSigV4.Sign(request, body ?? [], s.AccessKeyId.Trim(), s.SecretAccessKey.Trim(), region, "polly",
            clock?.Invoke() ?? DateTimeOffset.UtcNow);
        return request;
    }
}
