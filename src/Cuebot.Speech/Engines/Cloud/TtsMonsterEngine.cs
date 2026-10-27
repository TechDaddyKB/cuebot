using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// TTS Monster: an API key (or the overlay URL that carries it). Generation returns a link to the audio, which is
/// then downloaded. Custom voices are listed alongside the stock ones. Rate and pitch are not supported.
/// </summary>
internal sealed class TtsMonsterEngine(HttpClient http, Func<TtsMonsterSettings> settings) : ITtsEngine
{
    internal const string BaseUrl = "https://api.console.tts.monster/";

    public string Id => "ttsmonster";
    public string DisplayName => "TTS Monster";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason =>
        string.IsNullOrWhiteSpace(ExtractKey(settings().ApiKey)) ? "Enter the TTS Monster API key (or overlay URL)." : null;

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = CreateRequest("voices");
        request.Content = JsonContent.Create(new JsonObject());
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(synthesis.VoiceId))
            throw new ArgumentException("A TTS Monster voice must be selected.", nameof(synthesis));
        using var request = CreateRequest("generate");
        request.Content = JsonContent.Create(new JsonObject
        {
            ["voice_id"] = synthesis.VoiceId.Trim(),
            ["message"] = synthesis.Text,
        });
        string url;
        using (var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false))
        {
            url = json.RootElement.GetText("url") ?? throw new InvalidOperationException("TTS Monster returned no audio link.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var audioUri) || audioUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("TTS Monster returned an invalid audio link.");
        using var download = new HttpRequestMessage(HttpMethod.Get, audioUri);
        return AudioSniffer.Wrap(await CloudHttp.GetBytesAsync(http, download, DisplayName, ct).ConfigureAwait(false));
    }

    /// <summary>Accepts a bare key, or an overlay/console URL carrying it as ?key=/?token= or its last path segment.</summary>
    internal static string ExtractKey(string value)
    {
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return trimmed;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] is "key" or "token" or "apikey") return Uri.UnescapeDataString(parts[1]);
        }
        return uri.Segments.LastOrDefault()?.Trim('/') ?? "";
    }

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root) =>
        root.GetArray("voices").Select(v => (Voice: v, Custom: false))
            .Concat(root.GetArray("customVoices").Select(v => (Voice: v, Custom: true)))
            .Select(v => (v.Voice, v.Custom, Id: v.Voice.GetText("voice_id")))
            .Where(v => !string.IsNullOrEmpty(v.Id))
            .Select(v => new VoiceInfo(v.Id!, v.Voice.GetText("name") ?? v.Id!, v.Voice.GetText("language"))
            {
                Tier = v.Custom ? "Custom" : null,
            })
            .ToArray();

    private HttpRequestMessage CreateRequest(string path)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(BaseUrl), path));
        request.Headers.TryAddWithoutValidation("Authorization", ExtractKey(settings().ApiKey));
        return request;
    }
}
