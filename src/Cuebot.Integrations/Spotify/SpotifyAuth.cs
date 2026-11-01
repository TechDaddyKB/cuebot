using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Cuebot.Integrations.Spotify;

/// <summary>
/// Spotify's Authorization Code flow with PKCE (no client secret), for the streamer's own app:
/// https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow
/// </summary>
public sealed partial class SpotifyAuth(HttpClient http, SpotifyEndpoints endpoints)
{
    /// <summary>The redirect port Cuebot suggests (any free port works if the app's redirect URI matches).</summary>
    public const int DefaultPort = 43117;

    public const string CallbackPath = "/callback";

    /// <summary>Read what plays, read the player, control the player (queue, skip, pause, volume).</summary>
    public static readonly string[] Scopes = ["user-read-currently-playing", "user-read-playback-state", "user-modify-playback-state"];

    /// <summary>
    /// The redirect URI to register in the Spotify app. Spotify accepts plain http only for loopback IP literals
    /// (127.0.0.1 or [::1]), never "localhost".
    /// </summary>
    public static string RedirectUri(int port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{CallbackPath}";

    /// <summary>Client IDs are 32 hexadecimal characters; this only rules out obvious paste mistakes.</summary>
    public static bool LooksLikeClientId(string clientId) => ClientIdPattern().IsMatch(clientId.Trim());

    public static Uri AuthorizeUri(SpotifyEndpoints endpoints, string clientId, string redirectUri, string challenge, string state)
    {
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["state"] = state,
            ["scope"] = string.Join(' ', Scopes),
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        return new Uri(endpoints.Accounts, "authorize?" + query);
    }

    public Task<SpotifyTokens> ExchangeCodeAsync(string clientId, string code, string verifier, string redirectUri, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, ct);

    /// <summary>
    /// A new access token. Spotify may also send a new refresh token (keep the old one when it does not).
    /// </summary>
    /// <exception cref="SpotifySignInRequiredException">The refresh token expired or was revoked (invalid_grant).</exception>
    public Task<SpotifyTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }, ct);

    private async Task<SpotifyTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoints.Accounts, "api/token"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return SpotifyJson.ParseTokens(body);
        var (message, error) = SpotifyJson.ParseError(body);
        throw error switch
        {
            "invalid_grant" when form["grant_type"] == "refresh_token" => new SpotifySignInRequiredException(
                "Your Spotify sign-in ran out (Spotify ends sign-ins after a while). Click Sign in on the Music page again.", "invalid_grant"),
            "invalid_grant" => new SpotifyException($"Spotify did not accept the sign-in{Detail(message)}. Try again.", (int)response.StatusCode, error),
            "invalid_client" => new SpotifySignInRequiredException(
                "Spotify does not know this Client ID. Copy it again from your app on developer.spotify.com.", error),
            "invalid_request" when message.Contains("redirect", StringComparison.OrdinalIgnoreCase) => new SpotifyException(
                "The redirect address does not match your Spotify app. Copy it from Cuebot into the app's Redirect URIs again.",
                (int)response.StatusCode, error),
            _ when response.StatusCode >= HttpStatusCode.InternalServerError => new SpotifyException(
                "Spotify is having trouble right now. Try again in a minute.", (int)response.StatusCode, error),
            _ => new SpotifyException($"Spotify sign-in failed{Detail(message.Length > 0 ? message : error)}.", (int)response.StatusCode, error),
        };
    }

    private static string Detail(string message) => message.Length > 0 ? $" (\"{message}\")" : "";

    [GeneratedRegex("^[A-Za-z0-9]{16,64}$")]
    private static partial Regex ClientIdPattern();
}
