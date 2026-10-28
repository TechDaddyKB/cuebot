using System.Net;
using Cuebot.Core.Auth;
using Cuebot.YouTube.Auth;
using Cuebot.YouTube.Tests.Support;

namespace Cuebot.YouTube.Tests;

public sealed class AuthTests
{
    private static readonly YouTubeEndpoints Endpoints =
        new(new Uri("https://oauth.test/"), new Uri("https://accounts.test/o/oauth2/v2/auth"), new Uri("https://api.test/youtube/v3/"));

    [Fact]
    public async Task Device_flow_requests_a_code_with_the_youtube_scope()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"device_code":"DEV","user_code":"ABCD-EFGH","verification_url":"https://www.google.com/device","expires_in":1800,"interval":5}
            """));
        var time = new FakeTimeProvider();
        var oauth = new GoogleOAuthClient(new HttpClient(http), Endpoints, time);

        var auth = await oauth.StartDeviceFlowAsync(YouTubeAccountKind.Bot, "client-id", YouTubeScopes.Device, default);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://oauth.test/device/code", request.Uri.ToString());
        Assert.Equal("client-id", request.Form("client_id"));
        Assert.Equal("https://www.googleapis.com/auth/youtube", request.Form("scope"));
        Assert.Equal("ABCD-EFGH", auth.UserCode);
        Assert.Equal("https://www.google.com/device", auth.VerificationUrl);
        Assert.Equal(YouTubeAccountKind.Bot, auth.Account);
        Assert.Equal(time.GetUtcNow().AddSeconds(1800), auth.ExpiresAt);
    }

    [Fact]
    public async Task Device_token_polling_waits_while_pending_slows_down_and_returns_tokens()
    {
        var answers = new Queue<HttpResponseMessage>([
            FakeHttpHandler.Json("""{"error":"authorization_pending"}""", (HttpStatusCode)428),
            FakeHttpHandler.Json("""{"error":"slow_down"}""", HttpStatusCode.Forbidden),
            FakeHttpHandler.Json("""{"access_token":"AT","expires_in":3599,"refresh_token":"RT","scope":"https://www.googleapis.com/auth/youtube","token_type":"Bearer"}"""),
        ]);
        var http = new FakeHttpHandler(_ => answers.Dequeue());
        var time = new FakeTimeProvider();
        var oauth = new GoogleOAuthClient(new HttpClient(http), Endpoints, time);
        var auth = new DeviceAuthorization(YouTubeAccountKind.Broadcaster, "CODE", "url", time.GetUtcNow().AddMinutes(30))
        {
            DeviceCode = "DEV",
            PollInterval = TimeSpan.FromSeconds(5),
        };
        var start = time.GetUtcNow();

        var tokens = await oauth.PollDeviceTokenAsync(auth, "cid", "secret", default);

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        Assert.Equal(["https://www.googleapis.com/auth/youtube"], tokens.Scopes);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], time.Delays);
        Assert.Equal(time.GetUtcNow().AddSeconds(3599), tokens.ExpiresAt);
        var request = http.Requests[0];
        Assert.Equal("/token", request.Path);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", request.Form("grant_type"));
        Assert.Equal("DEV", request.Form("device_code"));
        Assert.Equal("cid", request.Form("client_id"));
        Assert.Equal("secret", request.Form("client_secret"));
        Assert.True(start < time.GetUtcNow());
    }

    [Theory]
    [InlineData("access_denied", "denied")]
    [InlineData("expired_token", "expired")]
    [InlineData("invalid_client", "invalid_client")]
    public async Task Device_token_polling_fails_on_terminal_errors(string error, string expectedText)
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json($$"""{"error":"{{error}}"}""", HttpStatusCode.Forbidden));
        var time = new FakeTimeProvider();
        var oauth = new GoogleOAuthClient(new HttpClient(http), Endpoints, time);
        var auth = new DeviceAuthorization(YouTubeAccountKind.Broadcaster, "C", "u", time.GetUtcNow().AddMinutes(5)) { DeviceCode = "D" };

        var ex = await Assert.ThrowsAsync<YouTubeApiException>(() => oauth.PollDeviceTokenAsync(auth, "cid", "s", default));
        Assert.Contains(expectedText, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_code_expiry_stops_polling()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"authorization_pending"}""", (HttpStatusCode)428));
        var time = new FakeTimeProvider();
        var oauth = new GoogleOAuthClient(new HttpClient(http), Endpoints, time);
        var auth = new DeviceAuthorization(YouTubeAccountKind.Broadcaster, "C", "u", time.GetUtcNow().AddSeconds(12))
        {
            DeviceCode = "D",
            PollInterval = TimeSpan.FromSeconds(5),
        };

        var ex = await Assert.ThrowsAsync<YouTubeApiException>(() => oauth.PollDeviceTokenAsync(auth, "cid", "s", default));
        Assert.Equal("expired_token", ex.Reason);
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task Refresh_keeps_the_refresh_token_and_reports_invalid_grant()
    {
        var ok = true;
        var http = new FakeHttpHandler(_ => ok
            ? FakeHttpHandler.Json("""{"access_token":"NEW","expires_in":3600,"scope":"s1 s2","token_type":"Bearer"}""")
            : FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest));
        var oauth = new GoogleOAuthClient(new HttpClient(http), Endpoints, new FakeTimeProvider());

        var tokens = await oauth.RefreshAsync("cid", "sec", "OLD-RT", default);
        Assert.Equal("NEW", tokens.AccessToken);
        Assert.Equal("OLD-RT", tokens.RefreshToken);
        Assert.Equal(["s1", "s2"], tokens.Scopes);
        Assert.Equal("refresh_token", http.Requests[0].Form("grant_type"));
        Assert.Equal("OLD-RT", http.Requests[0].Form("refresh_token"));

        ok = false;
        var ex = await Assert.ThrowsAsync<YouTubeApiException>(() => oauth.RefreshAsync("cid", "sec", "OLD-RT", default));
        Assert.True(ex.NeedsSignIn);
        Assert.Contains("7 days", ex.Message);
        Assert.DoesNotContain("OLD-RT", ex.Message);
    }

    [Fact]
    public async Task Code_exchange_sends_the_pkce_verifier_and_redirect()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"access_token":"AT","refresh_token":"RT","expires_in":3600}"""));
        var oauth = new GoogleOAuthClient(new HttpClient(http), Endpoints, new FakeTimeProvider());

        var tokens = await oauth.ExchangeCodeAsync("cid", "sec", "CODE", "VERIFIER", "http://127.0.0.1:5555/", default);

        Assert.Equal("RT", tokens.RefreshToken);
        var r = Assert.Single(http.Requests);
        Assert.Equal("authorization_code", r.Form("grant_type"));
        Assert.Equal("CODE", r.Form("code"));
        Assert.Equal("VERIFIER", r.Form("code_verifier"));
        Assert.Equal("http://127.0.0.1:5555/", r.Form("redirect_uri"));
    }

    [Fact]
    public void Authorize_uri_requests_offline_access_with_s256_challenge()
    {
        var oauth = new GoogleOAuthClient(new HttpClient(), Endpoints, new FakeTimeProvider());
        var uri = oauth.BuildAuthorizeUri("cid", "http://127.0.0.1:5555/", YouTubeScopes.Loopback, "CHALLENGE", "STATE");
        var r = new RecordedRequest(HttpMethod.Get, uri, null, "");

        Assert.Equal("accounts.test", uri.Host);
        Assert.Equal("cid", r.Query("client_id"));
        Assert.Equal("code", r.Query("response_type"));
        Assert.Equal("CHALLENGE", r.Query("code_challenge"));
        Assert.Equal("S256", r.Query("code_challenge_method"));
        Assert.Equal("STATE", r.Query("state"));
        Assert.Equal("offline", r.Query("access_type"));
        Assert.Equal("http://127.0.0.1:5555/", r.Query("redirect_uri"));
        Assert.Contains("youtube.force-ssl", r.Query("scope"));
    }

    [Fact]
    public void Pkce_challenge_matches_the_rfc_7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var verifier = Pkce.CreateVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('=', verifier);
        Assert.NotEqual(verifier, Pkce.CreateVerifier());
    }

    [Fact]
    public async Task Loopback_receiver_returns_the_redirect_query_and_ignores_other_paths()
    {
        using var receiver = new LoopbackReceiver("YouTube");
        Assert.StartsWith("http://127.0.0.1:", receiver.RedirectUri);
        var wait = receiver.WaitForRedirectAsync(CancellationToken.None);

        using var client = new HttpClient();
        var favicon = await client.GetAsync(receiver.RedirectUri + "favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        var page = await client.GetStringAsync(receiver.RedirectUri + "?state=S%20T&code=4%2F0Abc");

        var query = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("4/0Abc", query["code"]);
        Assert.Equal("S T", query["state"]);
        Assert.Contains("signed in", page);
    }
}
