using System.Net;
using System.Text.Json;
using Cuebot.Rumble.Auth;
using Cuebot.Rumble.Chat;
using Cuebot.Rumble.Tests.Support;

namespace Cuebot.Rumble.Tests;

public sealed class PasswordHasherTests
{
    // Reference values computed independently with md5sum in a shell loop.
    [Fact]
    public void Stretches_with_md5_as_rumble_expects()
    {
        Assert.Equal("704c51ed82ccf276ef58013bfb8e29ce", PasswordHasher.Stretch("hunter2", "saltA"));
        Assert.Equal("94d010fe51c58205edc98f73191a5155,e16471a242ceca962ed520fd58efad05,saltB",
            PasswordHasher.Hash("hunter2", ["saltA", "saltB", "saltC"]));
    }

    [Fact]
    public void Needs_three_salts() => Assert.Throws<RumbleApiException>(() => PasswordHasher.Hash("pw", ["a", "b"]));
}

public sealed class RumbleLoginClientTests
{
    private const string Password = "correct horse battery staple";
    private string? _totpId;

    private HttpResponseMessage Respond(RecordedRequest r) => r.Uri.Query switch
    {
        "?name=user.get_salts" => FakeHttpHandler.Json("""{"data":{"salts":["saltA","saltB","saltC"]}}"""),
        "?name=user.2fa.first_step" => FakeHttpHandler.Json(JsonSerializer.Serialize(new { data = new { totp_id = _totpId, user_key = "k" } })),
        "?name=user.login" => WithCookie(FakeHttpHandler.Json("""{"data":{"session":true}}""")),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static HttpResponseMessage WithCookie(HttpResponseMessage response)
    {
        response.Headers.Add("Set-Cookie", "a_s=other; path=/");
        response.Headers.Add("Set-Cookie", "u_s=session-token-123; expires=Fri, 01 Jan 2027 00:00:00 GMT; path=/; domain=.rumble.com; secure; HttpOnly");
        return response;
    }

    [Fact]
    public async Task Logs_in_with_salted_hashes_and_returns_the_session_cookie()
    {
        var http = new FakeHttpHandler(Respond);

        var session = await new RumbleLoginClient(new HttpClient(http), RumbleEndpoints.Production).SignInAsync(" Streamer ", Password, default);

        Assert.Equal(new RumbleSession("session-token-123", "Streamer"), session);
        Assert.Equal(["user.get_salts", "user.2fa.first_step", "user.login"], http.Requests.Select(r => r.Uri.Query[6..]));
        Assert.All(http.Requests, r => Assert.Equal(("rumble.com", "/service.php"), (r.Uri.Host, r.Path)));
        Assert.Contains("username=Streamer", http.Requests[0].Body);
        var hashes = Uri.EscapeDataString(PasswordHasher.Hash(Password, ["saltA", "saltB", "saltC"]));
        Assert.Contains("password_hashes=" + hashes, http.Requests[2].Body);
        Assert.All(http.Requests, r => Assert.DoesNotContain("horse", r.Body));
    }

    [Fact]
    public async Task Two_factor_accounts_get_a_clear_error()
    {
        _totpId = "totp-1";
        var http = new FakeHttpHandler(Respond);

        var error = await Assert.ThrowsAsync<RumbleApiException>(() =>
            new RumbleLoginClient(new HttpClient(http), RumbleEndpoints.Production).SignInAsync("Streamer", Password, default));

        Assert.Contains("two-factor", error.Message);
        Assert.DoesNotContain(http.Requests, r => r.Uri.Query.Contains("user.login"));
    }

    [Theory]
    [InlineData("""{"data":{"success":false}}""")]
    [InlineData("""{"data":{"session":false}}""")]
    public async Task Rejected_credentials_are_reported(string loginAnswer)
    {
        var http = new FakeHttpHandler(r => r.Uri.Query == "?name=user.login" ? FakeHttpHandler.Json(loginAnswer) : Respond(r));

        var error = await Assert.ThrowsAsync<RumbleApiException>(() =>
            new RumbleLoginClient(new HttpClient(http), RumbleEndpoints.Production).SignInAsync("Streamer", Password, default));

        Assert.Contains("rejected the username or password", error.Message);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_any_request()
    {
        var http = new FakeHttpHandler(Respond);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RumbleLoginClient(new HttpClient(http), RumbleEndpoints.Production).SignInAsync("", "pw", default));
        Assert.Empty(http.Requests);
    }
}

public sealed class ChatPostClientTests
{
    [Fact]
    public async Task Posts_the_web_chat_body_with_the_session_cookie()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"data":{"id":"1","user":{}}}"""));

        await new ChatPostClient(new HttpClient(http), RumbleEndpoints.Production).SendAsync(285005514, "hi chat", "tok", default);

        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://web7.rumble.com/chat/api/chat/285005514/message", request.Uri.ToString());
        Assert.Equal("u_s=tok", request.Cookie);
        var data = JsonDocument.Parse(request.Body).RootElement.GetProperty("data");
        Assert.Equal("hi chat", data.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(43, data.GetProperty("request_id").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("rant").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("channel_id").ValueKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in to Rumble again")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    public async Task Rejections_are_user_facing(HttpStatusCode status, string expected)
    {
        var client = new ChatPostClient(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(status))), RumbleEndpoints.Production);

        var error = await Assert.ThrowsAsync<RumbleApiException>(() => client.SendAsync(1, "hi", "tok", default));

        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("tok", error.Message);
    }

    [Fact]
    public async Task Messages_over_rumbles_limit_are_refused_locally()
    {
        var http = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAsync<RumbleApiException>(() =>
            new ChatPostClient(new HttpClient(http), RumbleEndpoints.Production).SendAsync(1, new string('x', 201), "tok", default));
        Assert.Empty(http.Requests);
    }
}
