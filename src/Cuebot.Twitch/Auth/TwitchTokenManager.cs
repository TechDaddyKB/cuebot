using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Twitch.Auth;

/// <summary>Owns the tokens stored in <see cref="TwitchSettings"/>: storing, refreshing (single-flight) and validating.</summary>
internal sealed class TwitchTokenManager(JsonFile<TwitchSettings> settings, TwitchAuthClient auth, AppLog log) : ITwitchTokenSource
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private TwitchSettings Settings => settings.Value;

    public string ClientId => Settings.ClientId.Trim();
    public string AccessToken => Settings.AccessToken;

    public void Store(TwitchTokens tokens)
    {
        Settings.AccessToken = tokens.AccessToken;
        Settings.RefreshToken = tokens.RefreshToken;
        Settings.Scopes = [.. tokens.Scopes];
        settings.ScheduleSave();
    }

    public void Clear()
    {
        Settings.AccessToken = "";
        Settings.RefreshToken = "";
        Settings.UserId = "";
        Settings.Login = "";
        Settings.Scopes = [];
        settings.ScheduleSave();
    }

    public async Task<bool> TryRefreshAsync(string rejectedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Settings.AccessToken != rejectedToken) return true;
            if (string.IsNullOrEmpty(Settings.RefreshToken)) return false;
            Store(await auth.RefreshAsync(ClientId, Settings.RefreshToken, ct).ConfigureAwait(false));
            log.Info("Twitch", "Access token refreshed");
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
            throw new TwitchApiException("Twitch sign-in has expired. Please sign in again.", 401);

        Settings.UserId = validation.UserId;
        Settings.Login = validation.Login;
        Settings.Scopes = [.. validation.Scopes];
        settings.ScheduleSave();
        return validation;
    }
}
