using System.Net;
using System.Text.Json;

namespace Cuebot.YouTube.Auth;

/// <summary>
/// Google OAuth 2.0 for installed apps: the device authorization grant ("TVs and Limited Input devices" clients),
/// the authorization-code exchange used by the loopback browser flow ("Desktop" clients, with PKCE), refresh and
/// revocation. Never logs tokens or the client secret.
/// </summary>
internal sealed class GoogleOAuthClient(HttpClient http, YouTubeEndpoints endpoints, TimeProvider time)
{
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    public async Task<DeviceAuthorization> StartDeviceFlowAsync(YouTubeAccountKind account, string clientId,
        IReadOnlyList<string> scopes, CancellationToken ct)
    {
        using var response = await PostFormAsync("device/code", new()
        {
            ["client_id"] = clientId,
            ["scope"] = string.Join(' ', scopes),
        }, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Error("Google refused to start sign-in", response, body);

        var url = body.Str("verification_url");
        if (url.Length == 0) url = body.Str("verification_uri");
        return new DeviceAuthorization(account, body.Str("user_code"), url, time.GetUtcNow().AddSeconds(body.Long("expires_in")))
        {
            DeviceCode = body.Str("device_code"),
            PollInterval = TimeSpan.FromSeconds(Math.Max(1, body.Long("interval") is var i && i > 0 ? i : 5)),
        };
    }

    /// <summary>Polls the token endpoint until the user approves, denies, or the code expires.</summary>
    public async Task<OAuthTokens> PollDeviceTokenAsync(DeviceAuthorization auth, string clientId, string clientSecret,
        CancellationToken ct)
    {
        var interval = auth.PollInterval;
        while (true)
        {
            if (time.GetUtcNow() >= auth.ExpiresAt)
                throw new YouTubeApiException("The Google sign-in code expired. Start sign-in again.", 0, "expired_token");

            await Task.Delay(interval, time, ct).ConfigureAwait(false);
            using var response = await PostFormAsync("token", new()
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["device_code"] = auth.DeviceCode,
                ["grant_type"] = DeviceGrantType,
            }, ct).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return ParseTokens(body, refreshFallback: "");

            switch (body.Str("error"))
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += SlowDownStep;
                    continue;
                case "access_denied":
                    throw new YouTubeApiException("Sign-in was denied in the browser.", (int)response.StatusCode, "access_denied");
                case "expired_token":
                    throw new YouTubeApiException("The Google sign-in code expired. Start sign-in again.", (int)response.StatusCode, "expired_token");
                default:
                    throw Error("Google sign-in failed", response, body);
            }
        }
    }

    /// <summary>Exchanges the code from the loopback redirect (PKCE verifier included) for tokens.</summary>
    public async Task<OAuthTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string codeVerifier,
        string redirectUri, CancellationToken ct)
    {
        using var response = await PostFormAsync("token", new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? ParseTokens(body, refreshFallback: "") : throw Error("Google sign-in failed", response, body);
    }

    public async Task<OAuthTokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        using var response = await PostFormAsync("token", new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? ParseTokens(body, refreshFallback: refreshToken)
            : throw Error("Refreshing the YouTube sign-in failed", response, body);
    }

    /// <summary>Revokes a refresh (or access) token; revoking the refresh token ends the whole grant.</summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var response = await PostFormAsync("revoke", new() { ["token"] = token }, ct).ConfigureAwait(false);
    }

    /// <summary>The browser URL that starts the loopback (authorization code + PKCE) sign-in.</summary>
    public Uri BuildAuthorizeUri(string clientId, string redirectUri, IReadOnlyList<string> scopes, string codeChallenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
        };
        var text = string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{endpoints.Authorize}?{text}");
    }

    private Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
        http.PostAsync(new Uri(endpoints.OAuth, path), new FormUrlEncodedContent(form), ct);

    private OAuthTokens ParseTokens(JsonElement body, string refreshFallback)
    {
        var refresh = body.Str("refresh_token");
        var scopes = body.Str("scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expiresIn = body.Long("expires_in");
        return new OAuthTokens(body.Str("access_token"), refresh.Length > 0 ? refresh : refreshFallback,
            time.GetUtcNow().AddSeconds(expiresIn > 0 ? expiresIn : 3600), scopes);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonElementExtensions.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

    private static YouTubeApiException Error(string prefix, HttpResponseMessage response, JsonElement body)
    {
        var error = body.Str("error");
        var description = body.Str("error_description");
        var detail = description.Length > 0 ? $"{error}: {description}" : error.Length > 0 ? error : $"HTTP {(int)response.StatusCode}";
        if (error == "invalid_grant")
            detail += response.StatusCode == HttpStatusCode.BadRequest
                ? " (sign in again; projects in Testing mode expire sign-ins after 7 days)"
                : "";
        return new YouTubeApiException($"{prefix}: {detail}", (int)response.StatusCode, error);
    }
}
