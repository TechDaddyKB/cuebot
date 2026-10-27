using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>UberDuck (paid plan): API key and secret as HTTP Basic credentials. Rate and pitch are not supported.</summary>
internal sealed class UberDuckEngine(HttpClient http, Func<UberDuckSettings> settings) : ITtsEngine
{
    internal const string BaseUrl = "https://api.uberduck.ai/";

    public string Id => "uberduck";
    public string DisplayName => "UberDuck";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason =>
        string.IsNullOrWhiteSpace(settings().ApiKey) || string.IsNullOrWhiteSpace(settings().ApiSecret)
            ? "Enter the UberDuck API key and secret in the speech settings."
            : null;

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "voices?mode=tts-basic");
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(synthesis.VoiceId))
            throw new ArgumentException("An UberDuck voice must be selected.", nameof(synthesis));
        using var request = CreateRequest(HttpMethod.Post, "speak-synchronous");
        request.Content = JsonContent.Create(new JsonObject { ["speech"] = synthesis.Text, ["voice"] = synthesis.VoiceId.Trim() });
        var audio = await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return AudioSniffer.Wrap(audio);
    }

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root.GetArray() : root.GetArray("voices");
        return items
            .Select(v => (Voice: v, Name: v.GetText("name")))
            .Where(v => !string.IsNullOrEmpty(v.Name))
            .Select(v => new VoiceInfo(v.Name!, v.Voice.GetText("display_name") ?? v.Name!, v.Voice.GetText("language"))
            {
                Tier = v.Voice.GetText("category"),
            })
            .ToArray();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var s = settings();
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.ApiKey.Trim()}:{s.ApiSecret.Trim()}")));
        return request;
    }
}
