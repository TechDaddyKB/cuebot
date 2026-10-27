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
            .Order();
        Assert.Equal(["channel.chat.message", "channel.raid", "stream.offline", "stream.online"], subscribed);
        Assert.Contains("streamer", _service.StatusMessage);

        await connection.SendAsync(EventSubMessages.Notification("n1", "channel.follow", EventSubMessages.FollowEvent));
        await WaitUntilAsync(() => !_published.IsEmpty);
        Assert.Equal(TwitchEventTypes.Follow, Assert.Single(_published).Type);

        await _service.SendChatMessageAsync("hello");
        Assert.Contains(_http.Requests, r => r.Path == "/helix/chat/messages" && r.Authorization == "Bearer fresh-token");

        await _service.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, _service.State);
        Assert.Null(_service.ConnectedLogin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hello"));
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
                Assert.Equal(ParameterKind.Multiline, Assert.Single(send.Parameters).Kind);

                Assert.Contains(provider.GetRequiredService<ScriptApi>().Functions, f => f.Name == "twitch.sendMessage");
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
