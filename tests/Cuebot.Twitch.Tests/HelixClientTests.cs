using System.Net;
using System.Text.Json;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

public sealed class HelixClientTests
{
    private static readonly Uri HelixBase = new("https://api.test/helix/");
    private readonly FakeTokens _tokens = new();

    private const string Sent = """{"data":[{"message_id":"abc-123","is_sent":true}]}""";

    private HelixClient Client(FakeHttpHandler handler) => new(new HttpClient(handler), HelixBase, _tokens);

    [Fact]
    public async Task SendChatMessage_posts_ids_and_text_with_credentials()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Sent));

        await Client(handler).SendChatMessageAsync("1337", "1337", "Hello, chat!", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/helix/chat/messages", request.Path);
        Assert.Equal("Bearer token-1", request.Authorization);
        Assert.Equal("client", request.ClientId);
        var body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal("1337", body.GetProperty("broadcaster_id").GetString());
        Assert.Equal("1337", body.GetProperty("sender_id").GetString());
        Assert.Equal("Hello, chat!", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendChatMessage_reports_drop_reason()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"data":[{"message_id":"","is_sent":false,"drop_reason":{"code":"msg_duplicate","message":"Your message is identical to the one you sent within the last 30 seconds."}}]}"""));

        var error = await Assert.ThrowsAsync<TwitchApiException>(() =>
            Client(handler).SendChatMessageAsync("1", "1", "again", default));

        Assert.Contains("identical", error.Message);
    }

    [Fact]
    public async Task Unauthorized_refreshes_once_and_retries_with_the_new_token()
    {
        var handler = new FakeHttpHandler(r => r.Authorization == "Bearer token-1"
            ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"error":"Unauthorized","status":401,"message":"Invalid OAuth token"}""")
            : FakeHttpHandler.Json(HttpStatusCode.OK, Sent));

        await Client(handler).SendChatMessageAsync("1", "1", "hi", default);

        Assert.Equal(["Bearer token-1", "Bearer token-2"], handler.Requests.Select(r => r.Authorization));
        Assert.Equal(1, _tokens.Refreshes);
    }

    [Fact]
    public async Task Unauthorized_after_failed_refresh_asks_to_sign_in_again()
    {
        _tokens.CanRefresh = false;
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}"));

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => Client(handler).SendChatMessageAsync("1", "1", "hi", default));

        Assert.Equal(401, error.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateSubscription_sends_websocket_transport_and_condition()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, """{"data":[{"id":"x","status":"enabled"}]}"""));
        var follow = EventSubSubscriptions.All.Single(s => s.Type == "channel.follow");

        await Client(handler).CreateEventSubSubscriptionAsync(follow, "1337", "session-9", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/helix/eventsub/subscriptions", request.Path);
        var body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal("channel.follow", body.GetProperty("type").GetString());
        Assert.Equal("2", body.GetProperty("version").GetString());
        Assert.Equal("1337", body.GetProperty("condition").GetProperty("broadcaster_user_id").GetString());
        Assert.Equal("1337", body.GetProperty("condition").GetProperty("moderator_user_id").GetString());
        Assert.Equal("websocket", body.GetProperty("transport").GetProperty("method").GetString());
        Assert.Equal("session-9", body.GetProperty("transport").GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task CreateSubscription_treats_conflict_as_success_and_reports_other_errors()
    {
        var spec = EventSubSubscriptions.All[0];
        var conflict = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"message":"subscription already exists"}"""));
        var forbidden = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Forbidden, """{"message":"subscription missing proper authorization"}"""));

        await Client(conflict).CreateEventSubSubscriptionAsync(spec, "1", "s", default);
        var error = await Assert.ThrowsAsync<TwitchApiException>(() => Client(forbidden).CreateEventSubSubscriptionAsync(spec, "1", "s", default));

        Assert.Contains("missing proper authorization", error.Message);
        Assert.Contains(spec.Type, error.Message);
    }

    private sealed class FakeTokens : ITwitchTokenSource
    {
        public string ClientId => "client";
        public string AccessToken { get; private set; } = "token-1";
        public bool CanRefresh { get; set; } = true;
        public int Refreshes { get; private set; }

        public Task<bool> TryRefreshAsync(string rejectedToken, CancellationToken ct)
        {
            if (!CanRefresh) return Task.FromResult(false);
            Refreshes++;
            AccessToken = "token-2";
            return Task.FromResult(true);
        }
    }
}
