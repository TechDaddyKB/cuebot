using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Kick.Auth;

/// <summary>Supplies access tokens for API calls and refreshes them when they expire or are rejected.</summary>
internal interface IKickTokenSource
{
    bool IsSignedIn(KickAccount account);

    /// <summary>A current access token, refreshed first when it expires within a minute.</summary>
    Task<string> GetAccessTokenAsync(KickAccount account, CancellationToken ct);

    /// <summary>Called after a 401 with the token Kick rejected. Returns true when a newer token is available.</summary>
    Task<bool> TryRefreshAsync(KickAccount account, string rejectedToken, CancellationToken ct);
}

/// <summary>Owns the tokens stored in <see cref="KickSettings"/>: storing, refreshing (single-flight per account), clearing.</summary>
internal sealed class KickTokenManager(JsonFile<KickSettings> settings, KickAuthClient auth, TimeProvider time, AppLog log)
    : IKickTokenSource
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private KickSettings Settings => settings.Value;

    public KickAccountTokens Tokens(KickAccount account) => account == KickAccount.Bot ? Settings.Bot : Settings.Broadcaster;

    public bool IsSignedIn(KickAccount account) => Tokens(account).IsSignedIn;

    public void Store(KickAccount account, KickTokens tokens)
    {
        var t = Tokens(account);
        t.AccessToken = tokens.AccessToken;
        if (tokens.RefreshToken.Length > 0) t.RefreshToken = tokens.RefreshToken;
        t.ExpiresAt = tokens.ExpiresAt;
        if (tokens.Scopes.Count > 0) t.Scopes = [.. tokens.Scopes];
        settings.ScheduleSave();
    }

    public void Clear(KickAccount account)
    {
        Tokens(account).Clear();
        settings.ScheduleSave();
    }

    public async Task<string> GetAccessTokenAsync(KickAccount account, CancellationToken ct)
    {
        var t = Tokens(account);
        if (!t.IsSignedIn) throw new KickApiException(account == KickAccount.Bot
            ? "Sign in the Kick bot account first (Kick settings)."
            : "Sign in to Kick first (Kick settings).", 401);
        if (t.AccessToken.Length > 0 && t.ExpiresAt - RefreshMargin > time.GetUtcNow()) return t.AccessToken;
        await TryRefreshAsync(account, t.AccessToken, ct).ConfigureAwait(false);
        if (t.AccessToken.Length == 0) throw new KickApiException("Kick sign-in has expired. Please sign in again.", 401);
        return t.AccessToken;
    }

    public async Task<bool> TryRefreshAsync(KickAccount account, string rejectedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var t = Tokens(account);
            if (t.AccessToken != rejectedToken && t.AccessToken.Length > 0) return true; // someone else refreshed already
            if (t.RefreshToken.Length == 0) return false;
            var refreshed = await auth.RefreshAsync(Settings.ClientId.Trim(), Settings.ClientSecret.Trim(), t.RefreshToken, ct)
                .ConfigureAwait(false);
            Store(account, refreshed);
            log.Info("Kick", $"{account} access token refreshed");
            return true;
        }
        catch (KickApiException ex)
        {
            log.Warn("Kick", $"Could not refresh the {account} token: {ex.Message}");
            if (ex.Status is 400 or 401)
            {
                // The refresh token is no longer valid; a fresh sign-in is required.
                Tokens(account).AccessToken = "";
                Tokens(account).RefreshToken = "";
                settings.ScheduleSave();
            }
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
