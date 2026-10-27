using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cuebot.Donations.TreatStream;

public sealed class TreatStreamApiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Tokens from the TreatStream OAuth2 token or refresh endpoint.</summary>
public sealed record TreatStreamTokens(string AccessToken, string RefreshToken, TimeSpan ExpiresIn);

/// <summary>
/// TreatStream's OAuth2 and socket-token endpoints (https://treatstream.com/api/details). Secrets never appear in
/// exceptions.
/// </summary>
internal sealed class TreatStreamApi(HttpClient http, Uri? baseUri = null)
{
    public static readonly Uri Production = new("https://treatstream.com/");
    public static readonly Uri SocketServer = new("https://nodeapi.treatstream.com/");
    public const string Scope = "userinfo";

    private readonly Uri _base = baseUri ?? Production;

    public Uri AuthorizeUrl(string clientId, string redirectUri, string state) => new(_base,
        "Oauth2/Authorize?response_type=code" +
        $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}&scope={Scope}");

    public async Task<TreatStreamTokens> ExchangeCodeAsync(string clientId, string clientSecret, string redirectUri, string code, CancellationToken ct)
    {
        var json = await PostAsync("Oauth2/Authorize/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope,
        }, [clientSecret, code], ct).ConfigureAwait(false);
        return Tokens(json);
    }

    public async Task<TreatStreamTokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        var json = await PostAsync("Oauth2/RefreshToken", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }, [clientSecret, refreshToken], ct).ConfigureAwait(false);
        var tokens = Tokens(json);
        return tokens.RefreshToken.Length > 0 ? tokens : tokens with { RefreshToken = refreshToken };
    }

    /// <summary><c>POST Oauth2/Authorize/socketToken</c> (client_id, access_token) → <c>{"socket_token":"…"}</c>.</summary>
    public async Task<string> GetSocketTokenAsync(string clientId, string accessToken, CancellationToken ct)
    {
        var json = await PostAsync("Oauth2/Authorize/socketToken", new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["access_token"] = accessToken,
        }, [accessToken], ct).ConfigureAwait(false);
        return json.First("socket_token", "socketToken", "token") is { Length: > 0 } token
            ? token
            : throw new TreatStreamApiException("TreatStream did not return a socket token (check the client id and sign in again)", 401);
    }

    private static TreatStreamTokens Tokens(JsonElement json)
    {
        var access = json.Str("access_token");
        if (access.Length == 0) throw new TreatStreamApiException(json.First("error_description", "error", "message") is { Length: > 0 } e
            ? $"TreatStream refused the sign-in: {e}"
            : "TreatStream did not return an access token", 400);
        var seconds = json.Number("expires_in");
        return new TreatStreamTokens(access, json.Str("refresh_token"), TimeSpan.FromSeconds(seconds > 0 ? seconds : 864000));
    }

    /// <summary>
    /// Posts form fields (standard OAuth2); TreatStream's docs show JSON bodies, so a 400/415 without an access token
    /// is retried once as JSON.
    /// </summary>
    private async Task<JsonElement> PostAsync(string path, Dictionary<string, string> fields, string[] secrets, CancellationToken ct)
    {
        var (status, json) = await SendAsync(path, new FormUrlEncodedContent(fields), ct).ConfigureAwait(false);
        if (status is 400 or 415 && json.Str("access_token").Length == 0 && json.Str("socket_token").Length == 0)
            (status, json) = await SendAsync(path, JsonContent.Create(fields), ct).ConfigureAwait(false);
        if (status is < 200 or > 299)
        {
            var detail = json.First("error_description", "error", "message");
            var text = status is 401 or 403 ? "TreatStream rejected the credentials" : $"TreatStream API error {status.ToString(CultureInfo.InvariantCulture)}";
            if (detail.Length > 0) text += $": {detail}";
            foreach (var secret in secrets) text = Secrets.Redact(text, secret);
            throw new TreatStreamApiException(text, status);
        }
        return json;
    }

    private async Task<(int Status, JsonElement Json)> SendAsync(string path, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path)) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", "Cuebot");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonReaders.TryParse(body.Length == 0 ? "{}" : body) is { ValueKind: JsonValueKind.Object } j ? j : JsonReaders.TryParse("{}")!.Value;
        return ((int)response.StatusCode, json);
    }
}
