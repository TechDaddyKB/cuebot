using System.Net;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Kick.Auth;
using Cuebot.Kick.Tests.Support;

namespace Cuebot.Kick.Tests;

public sealed class AuthTests : IDisposable
{
    private static readonly KickEndpoints Endpoints = new(new Uri("https://id.test/"), new Uri("https://api.test/"),
        new Uri("https://web.test/"), new Uri("wss://pusher.test/app/key"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-kick-auth-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly JsonFile<KickSettings> _settings;

    public AuthTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new JsonFile<KickSettings>(Path.Combine(_dir, "kick.json"), _log);
        _settings.Value.ClientId = "client-1";
        _settings.Value.ClientSecret = "very-secret";
    }

    public void Dispose()
    {
        _settings.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Pkce_challenge_matches_the_rfc7636_example()
    {
        var pkce = PkceRequest.From("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "state");
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", pkce.Challenge);
    }

    [Fact]
    public void Generated_pkce_values_are_url_safe_and_long_enough()
    {
        var a = PkceRequest.Create();
        var b = PkceRequest.Create();
        Assert.InRange(a.Verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", a.Verifier);
        Assert.Matches("^[A-Za-z0-9_-]+$", a.State);
        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.State, b.State);
        Assert.Equal(PkceRequest.ChallengeFor(a.Verifier), a.Challenge);
    }

    [Fact]
    public void Too_short_verifier_is_rejected() => Assert.Throws<ArgumentException>(() => PkceRequest.From("short", "s"));

    [Fact]
    public void Authorize_url_carries_every_required_parameter()
    {
        var auth = new KickAuthClient(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage())), Endpoints, TimeProvider.System);
        var pkce = PkceRequest.From(new string('a', 50), "st@te");
        var url = auth.BuildAuthorizeUrl("client-1", "http://localhost:47582/callback", ["user:read", "chat:write"], pkce);

        Assert.Equal("https://id.test/oauth/authorize", url.GetLeftPart(UriPartial.Path));
        var q = OAuthCallback.ParseQuery(url.Query);
        Assert.Equal("client-1", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("http://localhost:47582/callback", q["redirect_uri"]);
        Assert.Equal("user:read chat:write", q["scope"]);
        Assert.Equal("st@te", q["state"]);
        Assert.Equal(pkce.Challenge, q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
    }

    [Fact]
    public async Task Code_exchange_posts_the_form_kick_expects_and_parses_tokens()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"AT","token_type":"Bearer","refresh_token":"RT","expires_in":7200,"scope":"user:read chat:write"}"""));
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var auth = new KickAuthClient(new HttpClient(handler), Endpoints, new FixedTime(now));

        var tokens = await auth.ExchangeCodeAsync("client-1", "very-secret", "http://localhost:1/callback", "CODE", "VERIFIER", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://id.test/oauth/token", request.Uri.ToString());
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        var form = OAuthCallback.ParseQuery(request.Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("CODE", form["code"]);
        Assert.Equal("client-1", form["client_id"]);
        Assert.Equal("very-secret", form["client_secret"]);
        Assert.Equal("http://localhost:1/callback", form["redirect_uri"]);
        Assert.Equal("VERIFIER", form["code_verifier"]);

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        Assert.Equal(now.AddHours(2), tokens.ExpiresAt);
        Assert.Equal(["user:read", "chat:write"], tokens.Scopes);
    }

    [Fact]
    public async Task Failed_token_request_explains_without_echoing_secrets()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"invalid_client"}""", HttpStatusCode.Unauthorized));
        var auth = new KickAuthClient(new HttpClient(handler), Endpoints, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<KickApiException>(() => auth.RefreshAsync("client-1", "very-secret", "REFRESH", default));
        Assert.Equal(401, ex.Status);
        Assert.Contains("invalid_client", ex.Message);
        Assert.DoesNotContain("very-secret", ex.Message);
        Assert.DoesNotContain("REFRESH", ex.Message);
    }

    [Theory]
    [InlineData("?code=abc&state=s1", "abc", null)]
    [InlineData("?code=abc&state=other", null, "state mismatch")]
    [InlineData("?error=access_denied&state=s1", null, "cancelled")]
    [InlineData("?error=invalid_scope&error_description=bad+scope&state=s1", null, "bad scope")]
    [InlineData("?state=s1", null, "authorization code")]
    public void Callback_query_is_validated(string query, string? code, string? error)
    {
        var result = OAuthCallback.Parse(query, "s1");
        Assert.Equal(code, result.Code);
        if (error is null) Assert.Null(result.Error);
        else Assert.Contains(error, result.Error);
    }

    [Fact]
    public async Task Token_manager_refreshes_an_expiring_token_once_for_concurrent_callers()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpHandler(_ =>
        {
            Thread.Sleep(50);
            return FakeHttpHandler.Json("""{"access_token":"NEW","refresh_token":"RT2","expires_in":3600,"scope":"user:read"}""");
        });
        var manager = new KickTokenManager(_settings, new KickAuthClient(new HttpClient(handler), Endpoints, new FixedTime(now)), new FixedTime(now), _log);
        _settings.Value.Broadcaster.AccessToken = "OLD";
        _settings.Value.Broadcaster.RefreshToken = "RT1";
        _settings.Value.Broadcaster.ExpiresAt = now.AddSeconds(30); // inside the refresh margin

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.GetAccessTokenAsync(KickAccount.Broadcaster, default)));

        Assert.All(tokens, t => Assert.Equal("NEW", t));
        var refresh = Assert.Single(handler.Requests);
        var form = OAuthCallback.ParseQuery(refresh.Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("RT1", form["refresh_token"]);
        Assert.Equal("very-secret", form["client_secret"]);
        Assert.Equal("RT2", _settings.Value.Broadcaster.RefreshToken);
        Assert.Equal(now.AddHours(1), _settings.Value.Broadcaster.ExpiresAt);
    }

    [Fact]
    public async Task A_fresh_token_is_used_without_refreshing()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no request expected"));
        var manager = new KickTokenManager(_settings, new KickAuthClient(new HttpClient(handler), Endpoints, TimeProvider.System), TimeProvider.System, _log);
        _settings.Value.Bot.AccessToken = "BOT";
        _settings.Value.Bot.RefreshToken = "BRT";
        _settings.Value.Bot.ExpiresAt = now.AddHours(1);
        Assert.Equal("BOT", await manager.GetAccessTokenAsync(KickAccount.Bot, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejected_refresh_token_clears_the_sign_in()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        var manager = new KickTokenManager(_settings, new KickAuthClient(new HttpClient(handler), Endpoints, TimeProvider.System), TimeProvider.System, _log);
        _settings.Value.Broadcaster.AccessToken = "OLD";
        _settings.Value.Broadcaster.RefreshToken = "RT1";

        Assert.False(await manager.TryRefreshAsync(KickAccount.Broadcaster, "OLD", default));
        Assert.False(manager.IsSignedIn(KickAccount.Broadcaster));
        await Assert.ThrowsAsync<KickApiException>(() => manager.GetAccessTokenAsync(KickAccount.Broadcaster, default));
    }

    [Fact]
    public async Task Loopback_receiver_accepts_the_redirect_and_answers_the_browser()
    {
        var port = FreePort();
        using var receiver = new LoopbackReceiver(port);
        var wait = receiver.WaitAsync("s1", default);
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"http://localhost:{port}/callback?code=xyz&state=s1");
        var result = await wait.WaitAsync(Wait.Timeout);
        Assert.Equal("xyz", result.Code);
        Assert.Contains("signed in", page);
    }

    [Fact]
    public async Task Loopback_receiver_stops_when_cancelled()
    {
        using var receiver = new LoopbackReceiver(FreePort());
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.WaitAsync("s", cts.Token));
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
