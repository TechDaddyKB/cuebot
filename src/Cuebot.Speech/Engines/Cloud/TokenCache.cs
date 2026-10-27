using System.Security.Cryptography;
using System.Text;

namespace Cuebot.Speech;

/// <summary>
/// Caches a login token per credential set. Only a hash of the credentials is kept for comparison.
/// </summary>
internal sealed class TokenCache(TimeSpan lifetime, Func<DateTimeOffset>? clock = null)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _credentialHash;
    private string? _token;
    private DateTimeOffset _expires;

    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task<string> GetAsync(string credentials, Func<CancellationToken, Task<string>> login, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials)));
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _credentialHash == hash && _expires > Now) return _token;
            _token = await login(ct).ConfigureAwait(false);
            _credentialHash = hash;
            _expires = Now + lifetime;
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _token = null;
        _credentialHash = null;
    }

    /// <summary>Runs <paramref name="call"/> with a token, logging in again once if the token was rejected.</summary>
    public async Task<T> WithTokenAsync<T>(string credentials, Func<CancellationToken, Task<string>> login,
        Func<string, Task<T>> call, CancellationToken ct)
    {
        var token = await GetAsync(credentials, login, ct).ConfigureAwait(false);
        try
        {
            return await call(token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (CloudHttp.IsUnauthorized(ex))
        {
            Invalidate();
            token = await GetAsync(credentials, login, ct).ConfigureAwait(false);
            return await call(token).ConfigureAwait(false);
        }
    }
}
