using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Cuebot.Donations.DonorDrive;

public sealed class DonorDriveApiException(string message, int status = 0, TimeSpan? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>The server's Retry-After, when it sent one (with 429/503).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A conditional GET: <see cref="Body"/> is null when the server answered 304 Not Modified.</summary>
internal sealed record DonorDriveResponse(JsonElement? Body, string ETag)
{
    public bool NotModified => Body is null;
}

/// <summary>
/// The DonorDrive Public API (https://github.com/DonorDrive/PublicAPI): unauthenticated GETs on
/// <c>https://&lt;program site&gt;/api/…</c>, conditional with <c>If-None-Match</c>. DonorDrive asks clients to make at
/// most one request every 15 seconds; the caller paces requests.
/// </summary>
internal sealed class DonorDriveApi(HttpClient http)
{
    public static readonly Uri ExtraLife = new("https://dd.extra-life.org/api/");
    public static readonly Uri TestDrive = new("https://testdrive.donordrive.com/api/");

    /// <summary>The API root for the configured program, always ending in "/api/".</summary>
    public static Uri BaseFor(DonorDriveSettings s) => s.Program switch
    {
        DonorDriveProgram.ExtraLife => ExtraLife,
        DonorDriveProgram.TestDrive => TestDrive,
        _ => Normalize(s.CustomBaseUrl),
    };

    /// <summary>"www.charity.org", "https://www.charity.org/" and "https://www.charity.org/api" all become "https://www.charity.org/api/".</summary>
    public static Uri Normalize(string site)
    {
        var text = site.Trim();
        if (text.Length == 0) throw new DonorDriveApiException("Enter the DonorDrive site of your charity program first");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new DonorDriveApiException($"'{site}' is not a web address");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) path += "/api";
        return new UriBuilder(uri) { Path = path + "/", Query = "" }.Uri;
    }

    public static string ProfilePath(DonorDriveProfileType type, string id) =>
        $"{(type == DonorDriveProfileType.Team ? "teams" : "participants")}/{Uri.EscapeDataString(id.Trim())}";

    public async Task<DonorDriveResponse> GetAsync(Uri baseUri, string path, string etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation("User-Agent", "Cuebot (DonorDrive integration)");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (etag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var newTag = response.Headers.ETag?.ToString() ?? (response.Headers.TryGetValues("ETag", out var tags) ? tags.FirstOrDefault() ?? "" : "");
        if (response.StatusCode == HttpStatusCode.NotModified) return new DonorDriveResponse(null, newTag.Length > 0 ? newTag : etag);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter is { } r
                ? r.Delta ?? (r.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            throw new DonorDriveApiException(status switch
            {
                404 => "DonorDrive has no such participant or team (check the id and the charity program)",
                429 => "DonorDrive rate-limited Cuebot",
                _ => $"DonorDrive API error {status.ToString(CultureInfo.InvariantCulture)}",
            }, status, retry);
        }
        // Unknown ids come back as an HTML page on some sites; only JSON counts.
        // 204 = an empty page (e.g. no donations yet).
        var json = JsonReaders.TryParse(status == 204 || body.Length == 0 ? "[]" : body);
        if (json is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
            throw new DonorDriveApiException("DonorDrive returned something other than JSON", status);
        return new DonorDriveResponse(json, newTag);
    }
}
