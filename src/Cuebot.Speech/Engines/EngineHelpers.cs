using System.Globalization;
using System.Net;
using System.Security;
using System.Text.Json;

namespace Cuebot.Speech;

/// <summary>Shared HTTP plumbing for cloud engines. Error messages never contain credentials.</summary>
internal static class CloudHttp
{
    /// <summary>Sends and throws a readable <see cref="HttpRequestException"/> for non-success responses.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string engine,
        CancellationToken ct)
    {
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"{engine} returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail, 300)}",
                inner: null, response.StatusCode);
        }
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, HttpRequestMessage request, string engine,
        CancellationToken ct)
    {
        using var response = await SendAsync(http, request, engine, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> GetBytesAsync(HttpClient http, HttpRequestMessage request, string engine,
        CancellationToken ct)
    {
        using var response = await SendAsync(http, request, engine, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public static string? GetText(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static IEnumerable<JsonElement> GetArray(this JsonElement element, string? property = null)
    {
        var array = element;
        if (property is not null && (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out array)))
            return [];
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    }

    public static bool IsUnauthorized(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}

/// <summary>Maps Cuebot's -10..10 rate and pitch scales to SSML and engine units.</summary>
internal static class Prosody
{
    /// <summary>Rate as a relative SSML percentage: -10 is -50%, +10 is +100%.</summary>
    public static int RatePercent(int rate)
    {
        var r = Math.Clamp(rate, -10, 10);
        return r < 0 ? r * 5 : r * 10;
    }

    /// <summary>Pitch as a relative SSML percentage: ±10 is ±50%.</summary>
    public static int PitchPercent(int pitch) => Math.Clamp(pitch, -10, 10) * 5;

    /// <summary>Rate as a speed multiplier (0.5..2.0, 1 normal).</summary>
    public static double RateMultiplier(int rate) => Math.Round(1 + RatePercent(rate) / 100.0, 2);

    public static string Signed(int percent) =>
        (percent >= 0 ? "+" : "") + percent.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Wraps escaped text in a prosody element when rate or pitch differ from normal.</summary>
    public static string ProsodyElement(string text, int rate, int pitch, bool includePitch = true)
    {
        var escaped = Ssml.Escape(text);
        var attributes = new List<string>();
        if (rate != 0) attributes.Add($"rate=\"{Signed(RatePercent(rate))}\"");
        if (includePitch && pitch != 0) attributes.Add($"pitch=\"{Signed(PitchPercent(pitch))}\"");
        return attributes.Count == 0 ? escaped : $"<prosody {string.Join(' ', attributes)}>{escaped}</prosody>";
    }
}

internal static class Ssml
{
    /// <summary>Escapes text for an XML element body; user text is never treated as SSML.</summary>
    public static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    /// <summary>A plain SSML document with optional prosody.</summary>
    public static string Speak(string text, int rate, int pitch, bool includePitch = true) =>
        $"<speak>{Prosody.ProsodyElement(text, rate, pitch, includePitch)}</speak>";
}

/// <summary>Detects the container of downloaded audio.</summary>
internal static class AudioSniffer
{
    public static AudioFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[..4].SequenceEqual("RIFF"u8)) return AudioFormat.Wav;
        if (data.Length >= 4 && data[..4].SequenceEqual("OggS"u8)) return AudioFormat.Ogg;
        return AudioFormat.Mp3; // ID3 tag or MPEG frame sync
    }

    public static SynthesizedAudio Wrap(byte[] data) => new(data, Detect(data));
}
