namespace Cuebot.Twitch.Auth;

/// <summary>Supplies credentials to Helix requests and refreshes them when Twitch rejects one.</summary>
internal interface ITwitchTokenSource
{
    string ClientId { get; }
    string AccessToken { get; }

    /// <summary>
    /// Refreshes after <paramref name="rejectedToken"/> got a 401. Returns true when a newer token is available
    /// (possibly refreshed concurrently by another caller).
    /// </summary>
    Task<bool> TryRefreshAsync(string rejectedToken, CancellationToken ct);
}
