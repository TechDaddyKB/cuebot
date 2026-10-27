using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.YouTube.Auth;

/// <summary>
/// Access tokens of one account (broadcaster or bot) stored in <see cref="YouTubeSettings"/>: hands out a valid
/// token, refreshing it shortly before it expires or when YouTube rejects it (single-flight).
/// </summary>
internal sealed class AccountTokens(
    JsonFile<YouTubeSettings> settings,
    YouTubeAccountKind kind,
    GoogleOAuthClient oauth,
    TimeProvider time,
    YouTubeTimings timings,
    AppLog log)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public YouTubeAccountKind Kind => kind;

    public YouTubeAccount Account => kind == YouTubeAccountKind.Bot ? settings.Value.Bot : settings.Value.Broadcaster;

    public bool IsSignedIn => Account.IsSignedIn;

    public void Store(OAuthTokens tokens)
    {
        var account = Account;
        account.AccessToken = tokens.AccessToken;
        if (tokens.RefreshToken.Length > 0) account.RefreshToken = tokens.RefreshToken;
        account.AccessTokenExpiresAt = tokens.ExpiresAt;
        if (tokens.Scopes.Count > 0) account.Scopes = [.. tokens.Scopes];
        settings.ScheduleSave();
    }

    /// <summary>A token valid for at least the refresh margin. Throws <see cref="YouTubeApiException"/> when the user must sign in.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var account = Account;
        if (!account.IsSignedIn)
            throw new YouTubeApiException($"The YouTube {Label} account is not signed in.", 401, "signInRequired");
        if (account.AccessToken.Length > 0 && account.AccessTokenExpiresAt - timings.TokenRefreshMargin > time.GetUtcNow())
            return account.AccessToken;
        await RefreshAsync(account.AccessToken, ct).ConfigureAwait(false);
        return Account.AccessToken;
    }

    /// <summary>
    /// Refreshes unless another caller already replaced <paramref name="rejectedToken"/>. Throws when there is no
    /// refresh token or Google rejects it.
    /// </summary>
    public async Task RefreshAsync(string rejectedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = Account;
            if (account.AccessToken != rejectedToken && account.AccessToken.Length > 0
                && account.AccessTokenExpiresAt - timings.TokenRefreshMargin > time.GetUtcNow())
                return;
            if (account.RefreshToken.Length == 0)
                throw new YouTubeApiException($"The YouTube {Label} sign-in has expired. Sign in again.", 401, "signInRequired");

            var s = settings.Value;
            Store(await oauth.RefreshAsync(s.ClientId.Trim(), s.ClientSecret.Trim(), account.RefreshToken, ct).ConfigureAwait(false));
            log.Info("YouTube", $"Access token of the {Label} account refreshed");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private string Label => kind == YouTubeAccountKind.Bot ? "bot" : "broadcaster";
}
