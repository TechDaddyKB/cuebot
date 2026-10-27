namespace Cuebot.YouTube.Auth;

/// <summary>
/// A pending device-code sign-in: show <see cref="UserCode"/> and send the user to <see cref="VerificationUrl"/>,
/// then pass this object to <see cref="YouTubeService.CompleteDeviceSignInAsync"/>.
/// </summary>
public sealed record DeviceAuthorization(YouTubeAccountKind Account, string UserCode, string VerificationUrl, DateTimeOffset ExpiresAt)
{
    internal string DeviceCode { get; init; } = "";
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Tokens returned by Google's token endpoint. A refresh response carries no new refresh token.</summary>
internal sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);
