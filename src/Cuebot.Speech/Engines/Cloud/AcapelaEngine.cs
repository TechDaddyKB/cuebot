using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// Acapela Cloud: logs in with the account email and password for a token, then renders through the command
/// endpoint. Rate maps to "speed" and pitch to "shaping" (both 100 normal).
/// </summary>
internal sealed class AcapelaEngine(HttpClient http, Func<AcapelaSettings> settings, Func<DateTimeOffset>? clock = null) : ITtsEngine
{
    internal const string BaseUrl = "https://www.acapela-cloud.com/api/";
    private readonly TokenCache _tokens = new(TimeSpan.FromHours(12), clock);

    public string Id => "acapela";
    public string DisplayName => "Acapela Cloud";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason =>
        string.IsNullOrWhiteSpace(settings().Email) || string.IsNullOrWhiteSpace(settings().Password)
            ? "Enter the Acapela Cloud email and password in the speech settings."
            : null;

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct) =>
        CallAsync(async token =>
        {
            using var request = Create(HttpMethod.Get, "voices/", token);
            using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
            return ParseVoices(json.RootElement);
        }, ct);

    public Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(synthesis.VoiceId))
            throw new ArgumentException("An Acapela voice must be selected.", nameof(synthesis));
        return CallAsync(async token =>
        {
            using var request = Create(HttpMethod.Get, BuildCommandPath(synthesis), token);
            return AudioSniffer.Wrap(await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false));
        }, ct);
    }

    internal static string BuildCommandPath(SynthesisRequest synthesis)
    {
        var builder = new StringBuilder("command/?voice=").Append(Uri.EscapeDataString(synthesis.VoiceId.Trim()))
            .Append("&text=").Append(Uri.EscapeDataString(synthesis.Text))
            .Append("&output=stream&type=wav");
        if (synthesis.Rate != 0)
            builder.Append("&speed=").Append((100 + Prosody.RatePercent(synthesis.Rate)).ToString(CultureInfo.InvariantCulture));
        if (synthesis.Pitch != 0)
            builder.Append("&shaping=").Append((100 + Prosody.PitchPercent(synthesis.Pitch)).ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root.GetArray() : root.GetArray("voices");
        return items
            .Select(v => v.ValueKind == JsonValueKind.String
                ? new VoiceInfo(v.GetString()!, v.GetString()!, null)
                : (v.GetText("id") ?? v.GetText("name")) is { Length: > 0 } id
                    ? new VoiceInfo(id, v.GetText("name") ?? id, v.GetText("language"))
                    {
                        Gender = v.GetText("gender"),
                    }
                    : null)
            .OfType<VoiceInfo>()
            .ToArray();
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
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(BaseUrl), "login/"))
        {
            Content = JsonContent.Create(new JsonObject { ["email"] = s.Email.Trim(), ["password"] = s.Password }),
        };
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return json.RootElement.GetText("token") ?? throw new InvalidOperationException("Acapela Cloud did not return a token.");
    }

    private static HttpRequestMessage Create(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
        request.Headers.TryAddWithoutValidation("Authorization", "Token " + token);
        return request;
    }
}
