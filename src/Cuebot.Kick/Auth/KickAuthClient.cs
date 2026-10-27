using System.Net;
using System.Text.Json;

namespace Cuebot.Kick.Auth;

/// <summary>Tokens returned by id.kick.com.</summary>
internal sealed record KickTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);

/// <summary>
/// Kick's OAuth 2.1 endpoints on id.kick.com: authorize URL (authorization code + PKCE S256), code exchange,
/// refresh and revoke. Kick requires the app's client secret on the token endpoint.
/// </summary>
internal sealed class KickAuthClient(HttpClient http, KickEndpoints endpoints, TimeProvider time)
{
    public Uri BuildAuthorizeUrl(string clientId, string redirectUri, IEnumerable<string> scopes, PkceRequest pkce)
    {
        var query = Form(
            ("client_id", clientId),
            ("response_type", "code"),
            ("redirect_uri", redirectUri),
            ("scope", string.Join(' ', scopes)),
            ("state", pkce.State),
            ("code_challenge", pkce.Challenge),
            ("code_challenge_method", PkceRequest.Method));
        return new Uri(endpoints.Id, "oauth/authorize?" + query);
    }

    public Task<KickTokens> ExchangeCodeAsync(string clientId, string clientSecret, string redirectUri, string code,
        string verifier, CancellationToken ct) =>
        TokenRequestAsync(ct,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("client_id", clientId),
            ("client_secret", clientSecret),
            ("redirect_uri", redirectUri),
            ("code_verifier", verifier));

    public Task<KickTokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct) =>
        TokenRequestAsync(ct,
            ("grant_type", "refresh_token"),
            ("refresh_token", refreshToken),
            ("client_id", clientId),
            ("client_secret", clientSecret));

    /// <summary>Revokes a token. Best effort: failures are reported as false, never thrown.</summary>
    public async Task<bool> RevokeAsync(string token, string hint, CancellationToken ct)
    {
        try
        {
            var url = new Uri(endpoints.Id, "oauth/revoke?" + Form(("token", token), ("token_hint_type", hint)));
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent([]) };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<KickTokens> TokenRequestAsync(CancellationToken ct, params (string Key, string Value)[] fields)
    {
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var response = await http.PostAsync(new Uri(endpoints.Id, "oauth/token"), content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new KickApiException(TokenError(response.StatusCode, body), (int)response.StatusCode);
        return ParseTokens(body, time.GetUtcNow());
    }

    internal static KickTokens ParseTokens(string body, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.Str("access_token");
        if (access.Length == 0) throw new KickApiException("Kick returned no access token");
        var expiresIn = root.Long("expires_in");
        var scopes = root.Str("scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new KickTokens(access, root.Str("refresh_token"),
            now + TimeSpan.FromSeconds(expiresIn > 0 ? expiresIn : 3600), scopes);
    }

    /// <summary>A readable message for a failed token request; never includes the request (secrets).</summary>
    private static string TokenError(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            detail = doc.RootElement.Str("error_description") is { Length: > 0 } d ? d
                : doc.RootElement.Str("message") is { Length: > 0 } m ? m
                : doc.RootElement.Str("error");
        }
        catch (JsonException)
        {
        }
        var reason = detail.Length > 0 ? detail : status.ToString();
        return status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
            ? $"Kick rejected the sign-in ({reason}). Check the Client ID, Client Secret and redirect URL, then sign in again."
            : $"Kick sign-in failed ({(int)status} {reason})";
    }

    private static string Form(params (string Key, string Value)[] fields) =>
        string.Join('&', fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));
}
