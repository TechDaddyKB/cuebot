using Cuebot.Core.Auth;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.YouTube.Auth;
using Cuebot.YouTube.Tests.Support;

namespace Cuebot.YouTube.Tests;

/// <summary>Cuebot's built-in Google clients versus the user's own, and refreshing through the client that signed in.</summary>
public sealed class BuiltInAppTests : IDisposable
{
    private static readonly AppCredentials Browser = new("browser-cid", "browser-secret", true);
    private static readonly AppCredentials Device = new("device-cid", "device-secret", true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-youtube-apps-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly JsonFile<YouTubeSettings> _settings;

    public BuiltInAppTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new JsonFile<YouTubeSettings>(Path.Combine(_dir, "youtube.json"), _log);
    }

    public void Dispose()
    {
        _settings.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Built_in_clients_are_used_until_the_user_enters_their_own()
    {
        var apps = new YouTubeApps(Browser, Device);
        Assert.Same(Browser, apps.ForBrowser(_settings.Value));
        Assert.Same(Device, apps.ForDevice(_settings.Value));

        _settings.Value.ClientId = "own-cid";
        _settings.Value.ClientSecret = "own-secret";
        Assert.Equal(new AppCredentials("own-cid", "own-secret", false), apps.ForBrowser(_settings.Value));
        Assert.Equal(new AppCredentials("own-cid", "own-secret", false), apps.ForDevice(_settings.Value));
    }

    [Fact]
    public void An_account_remembers_which_client_signed_it_in()
    {
        Assert.Equal(YouTubeApps.BuiltInBrowser, YouTubeApps.Marker(Browser, device: false));
        Assert.Equal(YouTubeApps.BuiltInDevice, YouTubeApps.Marker(Device, device: true));
        Assert.Equal("", YouTubeApps.Marker(new AppCredentials("own", "s", false), device: true));
    }

    [Theory]
    [InlineData(YouTubeApps.BuiltInBrowser, "browser-cid", "browser-secret")]
    [InlineData(YouTubeApps.BuiltInDevice, "device-cid", "device-secret")]
    [InlineData("", "own-cid", "own-secret")]
    public async Task Tokens_are_refreshed_through_the_client_that_issued_them(string signedInWith, string clientId, string secret)
    {
        // The user's own client is entered too: an account signed in with a built-in client must still use that one.
        _settings.Value.ClientId = "own-cid";
        _settings.Value.ClientSecret = "own-secret";
        var account = _settings.Value.Broadcaster;
        account.AccessToken = "OLD";
        account.RefreshToken = "RT";
        account.SignedInWith = signedInWith;
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"access_token":"NEW","expires_in":3600,"scope":"s","token_type":"Bearer"}"""));
        var time = new FakeTimeProvider();
        account.AccessTokenExpiresAt = time.GetUtcNow().AddHours(-1);
        var tokens = new AccountTokens(_settings, YouTubeAccountKind.Broadcaster, new GoogleOAuthClient(new HttpClient(http), TestApi.Endpoints, time),
            time, TestApi.Fast, _log, new YouTubeApps(Browser, Device));

        Assert.Equal("NEW", await tokens.GetAccessTokenAsync(default));
        Assert.Equal(clientId, http.Requests[0].Form("client_id"));
        Assert.Equal(secret, http.Requests[0].Form("client_secret"));
    }

    [Fact]
    public async Task An_account_whose_client_is_gone_asks_to_sign_in_again()
    {
        var account = _settings.Value.Broadcaster;
        account.RefreshToken = "RT";   // signed in with the user's own client, which has since been cleared
        var http = new FakeHttpHandler(_ => throw new InvalidOperationException("no request expected"));
        var time = new FakeTimeProvider();
        var tokens = new AccountTokens(_settings, YouTubeAccountKind.Broadcaster, new GoogleOAuthClient(new HttpClient(http), TestApi.Endpoints, time),
            time, TestApi.Fast, _log, new YouTubeApps(Browser, Device));

        var ex = await Assert.ThrowsAsync<YouTubeApiException>(() => tokens.GetAccessTokenAsync(default));
        Assert.Contains("Sign in again", ex.Message);
        Assert.Empty(http.Requests);
    }
}
