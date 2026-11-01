using Cuebot.Core.Storage;

namespace Cuebot.Integrations.Spotify;

/// <summary>
/// Spotify (integrations.json "Spotify"): the streamer's own Spotify app and its sign-in. The tokens are secrets (kept
/// in the encrypted secret store, never logged).
/// </summary>
public sealed class SpotifySettings
{
    /// <summary>The Client ID of the user's own app from developer.spotify.com (not a secret: PKCE needs no client secret).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The port of the redirect address http://127.0.0.1:PORT/callback (it must match the app's redirect URI).</summary>
    public int RedirectPort { get; set; } = SpotifyAuth.DefaultPort;

    [Secret]
    public string AccessToken { get; set; } = "";

    [Secret]
    public string RefreshToken { get; set; } = "";

    public DateTimeOffset AccessTokenExpires { get; set; }

    /// <summary>The ClientId the tokens belong to (tokens of another app do not work).</summary>
    public string TokenClientId { get; set; } = "";

    /// <summary>Seconds between "what is playing" checks while signed in (2 to 60).</summary>
    public int PollSeconds { get; set; } = 5;

    public bool IsSignedIn => RefreshToken.Length > 0 || AccessToken.Length > 0;

    public void ClearTokens()
    {
        AccessToken = "";
        RefreshToken = "";
        AccessTokenExpires = default;
        TokenClientId = "";
    }
}
