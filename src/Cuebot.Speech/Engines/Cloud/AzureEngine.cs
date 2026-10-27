using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cuebot.Speech;

/// <summary>Microsoft Azure AI Speech (REST): subscription key plus region; SSML with prosody and speaking styles.</summary>
internal sealed class AzureEngine(HttpClient http, Func<AzureSettings> settings) : ITtsEngine
{
    internal const string OutputFormat = "riff-24khz-16bit-mono-pcm";
    private const string KeyHeader = "Ocp-Apim-Subscription-Key";

    public string Id => "azure";
    public string DisplayName => "Microsoft Azure";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason
    {
        get
        {
            var s = settings();
            if (string.IsNullOrWhiteSpace(s.Key)) return "Enter an Azure Speech key in the speech settings.";
            return string.IsNullOrWhiteSpace(s.Region) || !s.Region.Trim().All(char.IsAsciiLetterOrDigit)
                ? "Enter the Azure Speech region (e.g. eastus)."
                : null;
        }
    }

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "cognitiveservices/voices/list");
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, "cognitiveservices/v1");
        request.Headers.Add("X-Microsoft-OutputFormat", OutputFormat);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Cuebot", "1.0"));
        request.Content = new StringContent(BuildSsml(synthesis), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml");
        var audio = await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return AudioSniffer.Wrap(audio);
    }

    internal static string BuildSsml(SynthesisRequest synthesis)
    {
        var voice = synthesis.VoiceId.Trim();
        var body = Prosody.ProsodyElement(synthesis.Text, synthesis.Rate, synthesis.Pitch);
        if (!string.IsNullOrWhiteSpace(synthesis.Style))
            body = $"<mstts:express-as style=\"{Ssml.Escape(synthesis.Style.Trim())}\">{body}</mstts:express-as>";
        return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" "
               + "xmlns:mstts=\"https://www.w3.org/2001/mstts\" xml:lang=\"" + Ssml.Escape(LocaleOf(voice)) + "\">"
               + $"<voice name=\"{Ssml.Escape(voice)}\">{body}</voice></speak>";
    }

    /// <summary>"en-US-JennyNeural" → "en-US".</summary>
    internal static string LocaleOf(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 3 ? $"{parts[0]}-{parts[1]}" : "en-US";
    }

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root) =>
        root.GetArray()
            .Select(v => (Voice: v, ShortName: v.GetText("ShortName")))
            .Where(v => !string.IsNullOrEmpty(v.ShortName))
            .Select(v => new VoiceInfo(v.ShortName!, v.Voice.GetText("DisplayName") ?? v.ShortName!, v.Voice.GetText("Locale"))
            {
                Gender = v.Voice.GetText("Gender"),
                Tier = v.Voice.GetText("VoiceType"),
                Styles = v.Voice.GetArray("StyleList").Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToArray(),
            })
            .ToArray();

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var s = settings();
        var region = s.Region.Trim().ToLowerInvariant();
        var request = new HttpRequestMessage(method, new Uri($"https://{region}.tts.speech.microsoft.com/{path}"));
        request.Headers.Add(KeyHeader, s.Key.Trim());
        return request;
    }
}
