using System.Text.Json;

namespace Cuebot.Donations.TipeeeStream;

public sealed class TipeeeStreamApiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The TipeeeStream REST calls Cuebot needs (https://api.tipeeestream.com): the socket server lookup and an API key check.
/// The key never appears in exceptions.
/// </summary>
internal sealed class TipeeeStreamApi(HttpClient http, Uri? baseUri = null)
{
    public static readonly Uri Production = new("https://api.tipeeestream.com/");

    /// <summary>Used when the lookup fails; it is what the lookup has returned for years.</summary>
    public static readonly Uri FallbackSocketServer = new("https://sso-cf.tipeeestream.com");

    private readonly Uri _base = baseUri ?? Production;

    /// <summary><c>GET /v2.0/site/socket</c> → <c>{"datas":{"host":"https://sso-cf.tipeeestream.com","port":"443"}}</c>.</summary>
    public async Task<Uri> GetSocketServerAsync(CancellationToken ct)
    {
        var json = await GetAsync("v2.0/site/socket", "", ct).ConfigureAwait(false);
        var datas = json.Child("datas");
        var host = datas.Str("host");
        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri)) throw new TipeeeStreamApiException("TipeeeStream did not return a socket server");
        var port = (int)datas.Number("port");
        return port is > 0 and < 65536 && port != uri.Port ? new UriBuilder(uri) { Port = port }.Uri : uri;
    }

    /// <summary>
    /// Checks the key with <c>GET /v1.0/events.json?apiKey=…&amp;limit=1</c> (the documented API-key endpoint); 401/403
    /// means the key is wrong.
    /// </summary>
    public async Task ValidateKeyAsync(string apiKey, CancellationToken ct) =>
        await GetAsync($"v1.0/events.json?apiKey={Uri.EscapeDataString(apiKey.Trim())}&limit=1", apiKey, ct).ConfigureAwait(false);

    private async Task<JsonElement> GetAsync(string path, string secret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path));
        request.Headers.TryAddWithoutValidation("User-Agent", "Cuebot");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            var text = status is 401 or 403 ? "TipeeeStream rejected the API key" : $"TipeeeStream API error {status}";
            throw new TipeeeStreamApiException(Secrets.Redact(text, secret), status);
        }
        return JsonReaders.TryParse(body.Length == 0 ? "{}" : body)
            ?? throw new TipeeeStreamApiException("TipeeeStream returned an unreadable response", status);
    }
}
