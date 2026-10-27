namespace Cuebot.Twitch;

/// <summary>
/// A pending device-code sign-in: show <see cref="UserCode"/> and send the user to <see cref="VerificationUri"/>,
/// then pass this object to <see cref="TwitchService.CompleteAuthorizationAsync"/>.
/// </summary>
public sealed record DeviceAuthorization(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt)
{
    internal string ClientId { get; init; } = "";
    internal string DeviceCode { get; init; } = "";
    internal IReadOnlyList<string> Scopes { get; init; } = [];
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}
