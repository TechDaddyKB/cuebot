namespace Cuebot.Twitch.Auth;

/// <summary>Tokens issued by the Twitch token endpoint.</summary>
internal sealed record TwitchTokens(string AccessToken, string RefreshToken, IReadOnlyList<string> Scopes);

/// <summary>What /oauth2/validate reports about an access token.</summary>
internal sealed record TokenValidation(string ClientId, string Login, string UserId, IReadOnlyList<string> Scopes, int ExpiresInSeconds);
