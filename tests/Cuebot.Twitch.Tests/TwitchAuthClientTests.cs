using System.Net;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

public sealed class TwitchAuthClientTests
{
    private static readonly Uri AuthBase = new("https://id.test/oauth2/");
    private readonly FakeTimeProvider _time = new();

    private const string DeviceResponse =
        """
        {"device_code":"dev-code","expires_in":1800,"interval":5,"user_code":"ABCDEFGH",
         "verification_uri":"https://www.twitch.tv/activate?public=true&device-code=ABCDEFGH"}
        """;

    private const string TokenResponse =
        """
        {"access_token":"access-1","expires_in":14400,"refresh_token":"refresh-1",
         "scope":["user:read:chat","user:write:chat"],"token_type":"bearer"}
        """;

    private static HttpResponseMessage Error(string message) =>
        FakeHttpHandler.Json(HttpStatusCode.BadRequest, $$"""{"status":400,"message":"{{message}}"}""");

    private TwitchAuthClient Client(FakeHttpHandler handler) => new(new HttpClient(handler), AuthBase, _time);

    [Fact]
    public async Task StartDeviceFlow_posts_client_id_and_scopes()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, DeviceResponse));

        var auth = await Client(handler).StartDeviceFlowAsync("my-client", TwitchScopes.All, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/oauth2/device", request.Path);
        Assert.Equal("my-client", request.Form["client_id"]);
        Assert.Equal(string.Join(' ', TwitchScopes.All), request.Form["scopes"]);
        Assert.Equal("ABCDEFGH", auth.UserCode);
        Assert.StartsWith("https://www.twitch.tv/activate", auth.VerificationUri);
        Assert.Equal(_time.GetUtcNow().AddMinutes(30), auth.ExpiresAt);
        Assert.Equal("dev-code", auth.DeviceCode);
        Assert.Equal(TimeSpan.FromSeconds(5), auth.PollInterval);
    }

    [Fact]
    public async Task Poll_waits_through_pending_and_slow_down_then_returns_tokens()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Error("authorization_pending"),
            Error("slow_down"),
            FakeHttpHandler.Json(HttpStatusCode.OK, TokenResponse),
        ]);
        var handler = new FakeHttpHandler(_ => responses.Dequeue());
        var auth = Pending();

        var tokens = await Client(handler).PollDeviceTokenAsync(auth, default);

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal(["user:read:chat", "user:write:chat"], tokens.Scopes);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], _time.Delays);
        var form = handler.Requests[0].Form;
        Assert.Equal("/oauth2/token", handler.Requests[0].Path);
        Assert.Equal("client", form["client_id"]);
        Assert.Equal("dev-code", form["device_code"]);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
        Assert.Equal("user:read:chat", form["scopes"]);
    }

    [Fact]
    public async Task Poll_reports_denial()
    {
        var handler = new FakeHttpHandler(_ => Error("access_denied"));

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => Client(handler).PollDeviceTokenAsync(Pending(), default));

        Assert.Contains("access_denied", error.Message);
    }

    [Fact]
    public async Task Poll_stops_when_the_code_expires()
    {
        var handler = new FakeHttpHandler(_ => Error("authorization_pending"));
        var auth = Pending() with { ExpiresAt = _time.GetUtcNow().AddSeconds(12) };

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => Client(handler).PollDeviceTokenAsync(auth, default));

        Assert.Contains("expired", error.Message);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Refresh_uses_refresh_token_grant_without_secret()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, TokenResponse));

        var tokens = await Client(handler).RefreshAsync("client", "old-refresh", default);

        var form = Assert.Single(handler.Requests).Form;
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("old-refresh", form["refresh_token"]);
        Assert.Equal("client", form["client_id"]);
        Assert.False(form.ContainsKey("client_secret"));
        Assert.Equal("access-1", tokens.AccessToken);
    }

    [Fact]
    public async Task Validate_returns_account_or_null_when_invalid()
    {
        var valid = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"client_id":"client","login":"streamer","scopes":["user:read:chat"],"user_id":"1337","expires_in":5000}"""));
        var invalid = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Unauthorized,
            """{"status":401,"message":"invalid access token"}"""));

        var account = await Client(valid).ValidateAsync("token", default);

        Assert.Equal("OAuth token", Assert.Single(valid.Requests).Authorization);
        Assert.Equal("/oauth2/validate", valid.Requests[0].Path);
        Assert.Equal(("streamer", "1337"), (account!.Login, account.UserId));
        Assert.Equal(["user:read:chat"], account.Scopes);
        Assert.Null(await Client(invalid).ValidateAsync("token", default));
    }

    private DeviceAuthorization Pending() => new("ABCDEFGH", "https://www.twitch.tv/activate", _time.GetUtcNow().AddMinutes(30))
    {
        ClientId = "client",
        DeviceCode = "dev-code",
        Scopes = ["user:read:chat"],
        PollInterval = TimeSpan.FromSeconds(5),
    };
}
