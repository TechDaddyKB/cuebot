using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cuebot.Speech;

/// <summary>
/// CereProc CereVoice Cloud (REST v2): the account email and password buy an access token, then texts are posted
/// to the speak endpoint (as SSML when rate or pitch differ from normal).
/// </summary>
internal sealed class CereProcEngine(HttpClient http, Func<CereProcSettings> settings, Func<DateTimeOffset>? clock = null) : ITtsEngine
{
    internal const string BaseUrl = "https://api.cerevoice.com/v2/";
    private readonly TokenCache _tokens = new(TimeSpan.FromMinutes(50), clock);

    public string Id => "cereproc";
    public string DisplayName => "CereProc (CereVoice Cloud)";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason =>
        string.IsNullOrWhiteSpace(settings().Email) || string.IsNullOrWhiteSpace(settings().Password)
            ? "Enter the CereVoice Cloud email and password in the speech settings."
            : null;

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct) =>
        CallAsync(async token =>
        {
            using var request = Create(HttpMethod.Get, "voices", token);
            using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
            return ParseVoices(json.RootElement);
        }, ct);

    public Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(synthesis.VoiceId))
            throw new ArgumentException("A CereVoice voice must be selected.", nameof(synthesis));
        return CallAsync(async token =>
        {
            using var request = Create(HttpMethod.Post,
                $"speak?voice={Uri.EscapeDataString(synthesis.VoiceId.Trim())}&audio_format=wav", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
            request.Content = new StringContent(BuildBody(synthesis), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            return AudioSniffer.Wrap(await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false));
        }, ct);
    }

    /// <summary>Plain text, or an SSML document with prosody when rate or pitch are set.</summary>
    internal static string BuildBody(SynthesisRequest synthesis) =>
        synthesis.Rate == 0 && synthesis.Pitch == 0
            ? synthesis.Text
            : Ssml.Speak(synthesis.Text, synthesis.Rate, synthesis.Pitch);

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root) =>
        root.GetArray("voices")
            .Select(v => (Voice: v, Name: v.GetText("name")))
            .Where(v => !string.IsNullOrEmpty(v.Name))
            .Select(v => new VoiceInfo(v.Name!, v.Name!, Locale(v.Voice))
            {
                Gender = v.Voice.GetText("sex") switch
                {
                    "female" => "Female",
                    "male" => "Male",
                    _ => null,
                },
            })
            .ToArray();

    private static string? Locale(JsonElement voice)
    {
        var language = voice.GetText("language_ISO");
        var country = voice.GetText("country_ISO");
        return language is null ? null : country is null ? language : $"{language}-{country}";
    }

    private Task<T> CallAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var s = settings();
        return _tokens.WithTokenAsync(s.Email.Trim() + "\n" + s.Password, LoginAsync, call, ct);
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        var s = settings();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseUrl), "auth"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Email.Trim()}:{s.Password}")));
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return json.RootElement.GetText("access_token")
               ?? throw new InvalidOperationException("CereVoice Cloud did not return an access token.");
    }

    private static HttpRequestMessage Create(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
