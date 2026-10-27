using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Twitch.Tests;

public sealed class TwitchServiceTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-twitch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEventSubServer _server = new();
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private JsonFile<TwitchSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private TwitchService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<TwitchSettings>(Path.Combine(_dataDir, "twitch.json"), _log);
        _settings.Value.ClientId = "client";
        _settings.Value.AccessToken = "expired-token";
        _settings.Value.RefreshToken = "refresh-1";

        _http = new FakeHttpHandler(Respond);
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        var endpoints = new TwitchEndpoints(new Uri("https://id.test/oauth2/"), new Uri("https://api.test/helix/"), _server.Url());
        _service = new TwitchService(_settings, new HttpClient(_http), bus, _log, endpoints, TimeProvider.System,
            EventSubTimeouts.Default with { InitialRetryDelay = TimeSpan.FromMilliseconds(50) });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        await _server.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    /// <summary>Twitch stand-in: the stored token is expired, refreshing yields one with only the chat scopes.</summary>
    private static HttpResponseMessage Respond(RecordedRequest r) => (r.Path, r.Authorization) switch
    {
        ("/oauth2/validate", "OAuth fresh-token") => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"client_id":"client","login":"streamer","scopes":["user:read:chat","user:write:chat"],"user_id":"1337","expires_in":14000}"""),
        ("/oauth2/validate", _) => FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"status":401,"message":"invalid access token"}"""),
        ("/oauth2/token", _) => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"access_token":"fresh-token","refresh_token":"refresh-2","scope":["user:read:chat","user:write:chat"],"token_type":"bearer"}"""),
        ("/helix/eventsub/subscriptions", _) => FakeHttpHandler.Json(HttpStatusCode.Accepted, """{"data":[]}"""),
        ("/helix/chat/messages", _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[{"message_id":"m","is_sent":true}]}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + FakeEventSubServer.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Connect_refreshes_validates_subscribes_and_publishes_events()
    {
        await _service.ConnectAsync();

        Assert.Equal("streamer", _service.ConnectedLogin);
        Assert.Equal("fresh-token", _service.Settings.AccessToken);
        Assert.Equal("refresh-2", _service.Settings.RefreshToken);
        Assert.Equal("1337", _service.Settings.UserId);
        Assert.Equal(ConnectionState.Connecting, _service.State);

        var connection = await _server.NextConnectionAsync();
        await connection.SendAsync(EventSubMessages.Welcome("session-1"));
        await WaitUntilAsync(() => _service.State == ConnectionState.Connected);

        // Only subscriptions whose scope was granted (or that need none) are created.
        var subscribed = _http.Requests.Where(r => r.Path == "/helix/eventsub/subscriptions")
            .Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body).GetProperty("type").GetString())
            .Order(StringComparer.Ordinal);
        string[] expected =
        [
            "channel.chat.clear", "channel.chat.clear_user_messages", "channel.chat.message", "channel.chat.message_delete",
            "channel.chat.notification", "channel.chat_settings.update", "channel.raid", "channel.raid",
            "channel.shared_chat.begin", "channel.shared_chat.end", "channel.shared_chat.update", "channel.update",
            "stream.offline", "stream.online",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), subscribed);
        Assert.Contains("streamer", _service.StatusMessage);
        Assert.Contains("channel.follow", _service.UnavailableEvents);
        Assert.Contains(_log.Recent(), e => e.Message.Contains("moderator:read:followers"));
        Assert.Contains(_published, e => e.Type == TwitchEventTypes.BroadcasterConnected);

        await connection.SendAsync(EventSubMessages.Notification("n1", "channel.follow", EventSubMessages.FollowEvent));
        await WaitUntilAsync(() => _published.Any(e => e.Type == TwitchEventTypes.Follow));
        Assert.Single(_published, e => e.Type == TwitchEventTypes.Follow);

        await _service.SendChatMessageAsync("hello");
        Assert.Contains(_http.Requests, r => r.Path == "/helix/chat/messages" && r.Authorization == "Bearer fresh-token");

        await _service.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, _service.State);
        Assert.Null(_service.ConnectedLogin);
        Assert.Contains(_published, e => e.Type == TwitchEventTypes.BroadcasterDisconnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hello"));
    }

    [Fact]
    public async Task Bot_account_connects_its_own_session_for_whispers_and_sends_chat()
    {
        _settings.Value.Bot.AccessToken = "bot-token";
        _settings.Value.Bot.RefreshToken = "bot-refresh";
        var http = new FakeHttpHandler(r => (r.Path, r.Authorization) switch
        {
            ("/oauth2/validate", "OAuth bot-token") => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"client_id":"client","login":"mybot","scopes":["user:read:chat","user:write:chat","user:manage:whispers"],"user_id":"777","expires_in":14000}"""),
            _ => Respond(r),
        });
        var bus = new EventBus(_log);
        var published = new ConcurrentQueue<BotEvent>();
        bus.Subscribe(published.Enqueue);
        await using var service = new TwitchService(_settings, new HttpClient(http), bus, _log,
            new TwitchEndpoints(new Uri("https://id.test/oauth2/"), new Uri("https://api.test/helix/"), _server.Url()),
            TimeProvider.System, EventSubTimeouts.Default with { InitialRetryDelay = TimeSpan.FromMilliseconds(50) });

        await service.ConnectAsync();
        var first = await _server.NextConnectionAsync();
        var second = await _server.NextConnectionAsync();
        await first.SendAsync(EventSubMessages.Welcome("session-1"));
        await second.SendAsync(EventSubMessages.Welcome("session-2"));
        await WaitUntilAsync(() => service.State == ConnectionState.Connected && service.BotState == ConnectionState.Connected);

        var botSubscriptions = http.Requests.Where(r => r.Path == "/helix/eventsub/subscriptions" && r.Authorization == "Bearer bot-token").ToArray();
        var whisper = JsonSerializer.Deserialize<JsonElement>(Assert.Single(botSubscriptions).Body);
        Assert.Equal("user.whisper.message", whisper.GetProperty("type").GetString());
        Assert.Equal("777", whisper.GetProperty("condition").GetProperty("user_id").GetString());
        Assert.Contains(published, e => e.Type == TwitchEventTypes.BotConnected);
        var bot = whisper.GetProperty("transport").GetProperty("session_id").GetString() == "session-1" ? first : second;

        await bot.SendAsync(EventSubMessages.Notification("w1", "user.whisper.message",
            """
            {"from_user_id":"423374343","from_user_login":"glowingfish","from_user_name":"glowingfish",
             "to_user_id":"777","to_user_login":"mybot","to_user_name":"MyBot","whisper_id":"some-whisper-id","whisper":{"text":"a secret"}}
            """));
        await WaitUntilAsync(() => published.Any(e => e.Type == TwitchEventTypes.BotWhisper));
        var botWhisper = published.First(e => e.Type == TwitchEventTypes.BotWhisper);
        Assert.Equal("a secret", botWhisper.Args["message"]);
        Assert.Equal("Whisper", botWhisper.Args["messageKind"]);

        await service.SendChatMessageAsync("from the bot");
        var send = http.Requests.Last(r => r.Path == "/helix/chat/messages");
        Assert.Equal("Bearer bot-token", send.Authorization);
        var body = JsonSerializer.Deserialize<JsonElement>(send.Body);
        Assert.Equal("777", body.GetProperty("sender_id").GetString());
        Assert.Equal("1337", body.GetProperty("broadcaster_id").GetString());

        _settings.Value.SendChatAsBot = false;
        await service.SendChatMessageAsync("from the broadcaster");
        Assert.Equal("Bearer fresh-token", http.Requests.Last(r => r.Path == "/helix/chat/messages").Authorization);
    }

    [Fact]
    public async Task Connect_fails_with_error_status_when_sign_in_cannot_be_renewed()
    {
        _settings.Value.RefreshToken = "";

        await Assert.ThrowsAsync<TwitchApiException>(() => _service.ConnectAsync());

        Assert.Equal(ConnectionState.Error, _service.State);
        Assert.Contains("sign in again", _service.StatusMessage);
    }

    [Fact]
    public async Task Sending_while_disconnected_throws_a_clear_error()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hi"));

        Assert.Contains("not connected", error.Message);
    }

    [Fact]
    public async Task Authorization_requires_a_client_id()
    {
        _settings.Value.ClientId = " ";

        Assert.False(_service.IsAuthorized);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BeginAuthorizationAsync());
    }

    [Fact]
    public async Task StartAsync_does_nothing_when_auto_connect_is_off()
    {
        _settings.Value.AutoConnect = false;

        await _service.StartAsync();

        Assert.Empty(_http.Requests);
        Assert.Equal(ConnectionState.Disconnected, _service.State);
    }

    [Fact]
    public void SignOut_forgets_tokens_and_account()
    {
        _settings.Value.UserId = "1337";
        _settings.Value.Login = "streamer";

        _service.SignOut();

        Assert.False(_service.IsAuthorized);
        Assert.Equal("", _service.Settings.RefreshToken);
        Assert.Equal("", _service.Settings.Login);
        Assert.Equal("client", _service.Settings.ClientId);
    }

    [Fact]
    public async Task Disposing_right_after_sign_out_still_revokes_the_token()
    {
        // Found in a live test: DI disposed the shared HttpClient (after this service) before the background
        // revoke finished. Reproduce that order with a slow revoke endpoint.
        var http = new HttpClient(new SlowRevokeHandler(_http));
        var service = new TwitchService(_settings, http, new EventBus(_log), _log,
            new TwitchEndpoints(new Uri("https://id.test/oauth2/"), new Uri("https://api.test/helix/"), _server.Url()),
            TimeProvider.System, EventSubTimeouts.Default);

        service.SignOut();
        await service.DisposeAsync();
        http.Dispose();

        Assert.Contains(_http.Requests, r => r.Path == "/oauth2/revoke" && r.Body.Contains("token=expired-token"));
        Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains("Could not revoke"));
    }

    private sealed class SlowRevokeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal)) await Task.Delay(300, ct);
            return await base.SendAsync(request, ct);
        }
    }
}

public sealed class TwitchRegistrationTests
{
    [Fact]
    public async Task AddTwitch_registers_service_triggers_sub_action_and_script_function()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cuebot-twitch-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new ServiceCollection().AddCuebotCore(new AppPaths(dataDir)).AddTwitch().BuildServiceProvider())
            {
                var twitch = provider.GetRequiredService<TwitchService>();
                Assert.Contains(provider.GetServices<IConnectionStatus>(), s => ReferenceEquals(s, twitch));
                Assert.Equal("Twitch", twitch.Name);

                var registry = provider.GetRequiredService<Registry>();
                Assert.NotNull(registry.GetTrigger("twitch.command"));
                Assert.NotNull(registry.GetTrigger("twitch.follow"));
                var send = registry.GetSubAction("twitch.sendMessage");
                Assert.NotNull(send);
                Assert.Equal("Send chat message", send.DisplayName);
                Assert.Equal(ParameterKind.Multiline, send.Parameters[0].Kind);
                Assert.Equal("sendAs", send.Parameters[1].Key);
                Assert.NotNull(registry.GetSubAction("twitch.ban"));
                Assert.NotNull(registry.GetTrigger("twitch.pollCreated"));

                Assert.Contains(provider.GetRequiredService<ScriptApi>().Functions, f => f.Name == "twitch.sendMessage");
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
