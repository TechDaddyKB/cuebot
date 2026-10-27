using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// IBM Watson Text to Speech. Uses the API key and instance URL, entered directly or read from the
/// ibm-credentials.env file IBM Cloud offers for download. Rate and pitch use the rate/pitch percentage parameters.
/// </summary>
internal sealed class WatsonEngine(HttpClient http, Func<WatsonSettings> settings) : ITtsEngine
{
    public string Id => "watson";
    public string DisplayName => "IBM Watson";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason
    {
        get
        {
            var s = settings();
            if (!string.IsNullOrWhiteSpace(s.CredentialsFile) && !File.Exists(s.CredentialsFile.Trim())
                && (string.IsNullOrWhiteSpace(s.ApiKey) || string.IsNullOrWhiteSpace(s.Url)))
                return "The IBM credentials file was not found.";
            var (key, url) = Credentials();
            if (string.IsNullOrWhiteSpace(key)) return "Enter an IBM Watson API key or choose the ibm-credentials.env file.";
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? null
                : "Enter the IBM Watson service URL (https://...).";
        }
    }

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/voices");
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, BuildSynthesizePath(synthesis));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        request.Content = JsonContent.Create(new JsonObject { ["text"] = synthesis.Text });
        var audio = await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return SynthesizedAudio.Wav(TryNormalize(audio));
    }

    internal static string BuildSynthesizePath(SynthesisRequest synthesis)
    {
        if (string.IsNullOrWhiteSpace(synthesis.VoiceId))
            throw new ArgumentException("An IBM Watson voice must be selected.", nameof(synthesis));
        var path = new StringBuilder("v1/synthesize?voice=").Append(Uri.EscapeDataString(synthesis.VoiceId.Trim()));
        if (synthesis.Rate != 0)
            path.Append("&rate_percentage=").Append(Prosody.RatePercent(synthesis.Rate).ToString(CultureInfo.InvariantCulture));
        if (synthesis.Pitch != 0)
            path.Append("&pitch_percentage=").Append(Prosody.PitchPercent(synthesis.Pitch).ToString(CultureInfo.InvariantCulture));
        return path.ToString();
    }

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root) =>
        root.GetArray("voices")
            .Select(v => (Voice: v, Name: v.GetText("name")))
            .Where(v => !string.IsNullOrEmpty(v.Name))
            .Select(v => new VoiceInfo(v.Name!, v.Voice.GetText("description") is { Length: > 0 } d ? d : v.Name!, v.Voice.GetText("language"))
            {
                Gender = v.Voice.GetText("gender") switch
                {
                    "female" => "Female",
                    "male" => "Male",
                    _ => null,
                },
                Tier = v.Name!.Contains("Expressive", StringComparison.Ordinal) ? "Expressive"
                    : v.Name.Contains("Natural", StringComparison.Ordinal) ? "Natural"
                    : v.Name.Contains("V3", StringComparison.Ordinal) ? "Neural" : null,
            })
            .ToArray();

    /// <summary>Reads TEXT_TO_SPEECH_APIKEY and TEXT_TO_SPEECH_URL from an ibm-credentials.env file.</summary>
    internal static (string? Key, string? Url) ParseCredentialsFile(IEnumerable<string> lines)
    {
        string? key = null, url = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var name = line[..index].Trim().ToUpperInvariant();
            var value = line[(index + 1)..].Trim().Trim('"');
            if (name.EndsWith("_APIKEY", StringComparison.Ordinal)) key ??= value;
            else if (name.EndsWith("_URL", StringComparison.Ordinal)) url ??= value;
        }
        return (key, url);
    }

    private static byte[] TryNormalize(byte[] audio)
    {
        try
        {
            return Wav.Normalize(audio);
        }
        catch (InvalidDataException)
        {
            return audio;
        }
    }

    private (string? Key, string? Url) Credentials()
    {
        var s = settings();
        string? key = s.ApiKey.Trim(), url = s.Url.Trim();
        if ((key.Length == 0 || url.Length == 0) && !string.IsNullOrWhiteSpace(s.CredentialsFile) && File.Exists(s.CredentialsFile.Trim()))
        {
            var (fileKey, fileUrl) = ParseCredentialsFile(File.ReadAllLines(s.CredentialsFile.Trim()));
            if (key.Length == 0) key = fileKey;
            if (url.Length == 0) url = fileUrl;
        }
        return (key, url);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var (key, url) = Credentials();
        var request = new HttpRequestMessage(method, new Uri(new Uri(url!.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:" + key)));
        return request;
    }
}
