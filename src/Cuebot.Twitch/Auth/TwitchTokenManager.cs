using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Twitch.Auth;

/// <summary>
/// Owns the tokens of one account (broadcaster or bot) stored in <see cref="TwitchSettings"/>: storing,
/// refreshing (single-flight) and validating.
/// </summary>
internal sealed class TwitchTokenManager(
    JsonFile<TwitchSettings> settings,
    TwitchAuthClient auth,
    AppLog log,
    Func<string> clientId,
    Func<TwitchSettings, ITwitchAccount>? account = null,
    string label = "Twitch") : ITwitchTokenSource
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Func<TwitchSettings, ITwitchAccount> _account = account ?? (s => new BroadcasterAccount(s));

    private ITwitchAccount Account => _account(settings.Value);

    public string ClientId => clientId();
    public string AccessToken => Account.AccessToken;
    public string UserId => Account.UserId;
    public string Login => Account.Login;
    public IReadOnlyList<string> Scopes => Account.Scopes;
    public bool HasToken => Account.AccessToken.Length > 0;

    public void Store(TwitchTokens tokens)
    {
        var account = Account;
        account.AccessToken = tokens.AccessToken;
        account.RefreshToken = tokens.RefreshToken;
        account.Scopes = [.. tokens.Scopes];
        settings.ScheduleSave();
    }

    public void Clear()
    {
        var account = Account;
        account.AccessToken = "";
        account.RefreshToken = "";
        account.UserId = "";
        account.Login = "";
        account.Scopes = [];
        settings.ScheduleSave();
    }

    public async Task<bool> TryRefreshAsync(string rejectedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = Account;
            if (account.AccessToken != rejectedToken) return true;
            if (string.IsNullOrEmpty(account.RefreshToken)) return false;
            Store(await auth.RefreshAsync(ClientId, account.RefreshToken, ct).ConfigureAwait(false));
            log.Info("Twitch", $"{label} access token refreshed");
            return true;
        }
        catch (TwitchApiException ex)
        {
            log.Warn("Twitch", ex.Message);
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Validates the current token (refreshing it once if Twitch rejects it) and records the account it belongs to.
    /// Throws <see cref="TwitchApiException"/> when the user must sign in again.
    /// </summary>
    public async Task<TokenValidation> ValidateAsync(CancellationToken ct)
    {
        var token = AccessToken;
        var validation = await auth.ValidateAsync(token, ct).ConfigureAwait(false);
        if (validation is null && await TryRefreshAsync(token, ct).ConfigureAwait(false))
            validation = await auth.ValidateAsync(AccessToken, ct).ConfigureAwait(false);
        if (validation is null)
            throw new TwitchApiException($"{label} sign-in has expired. Please sign in again.", 401);

        var account = Account;
        account.UserId = validation.UserId;
        account.Login = validation.Login;
        account.Scopes = [.. validation.Scopes];
        settings.ScheduleSave();
        return validation;
    }
}
