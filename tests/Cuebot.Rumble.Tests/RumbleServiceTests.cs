using System.Collections.Concurrent;
using System.Net;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Rumble.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Rumble.Tests;

/// <summary>End to end against a fake Rumble: Live Stream API polling, the chat stream, sending and sign-in.</summary>
public sealed class RumbleServiceTests : IAsyncLifetime
{
    private const string ApiUrl = "https://rumble.test/-livestream-api/get-data?key=SECRETKEY";
    private const long ChatId = 285005514;   // "4ponnu" in base 10
    private static readonly RumbleEndpoints Endpoints = new(new Uri("https://site.test/"), new Uri("https://chat.test/chat/api/chat/"));

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-rumble-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly ConcurrentQueue<FakeEventStream> _chatConnections = new();
    private volatile string _apiJson = TestData.Read("live-stream-api-offline.json");
    private volatile HttpStatusCode _apiStatus = HttpStatusCode.OK;
    private JsonFile<RumbleSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private RumbleService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<RumbleSettings>(Path.Combine(_dataDir, "rumble.json"), _log);
        _settings.Value.LiveStreamApiUrl = ApiUrl;
        _settings.Value.PollSeconds = 0;   // raised to the fast test minimum
        _http = new FakeHttpHandler(Respond);
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _service = new RumbleService(_settings, new HttpClient(_http), ownsHttp: true, bus, _log, Endpoints, TimeProvider.System, TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r) => r.Uri.Host switch
    {
        "rumble.test" => _apiStatus == HttpStatusCode.OK ? FakeHttpHandler.Json(_apiJson) : new HttpResponseMessage(_apiStatus),
        "chat.test" when r.Path.EndsWith("/stream", StringComparison.Ordinal) =>
            _chatConnections.TryDequeue(out var stream) ? stream.Response() : new HttpResponseMessage(HttpStatusCode.NoContent),
        "chat.test" when r.Path.EndsWith("/message", StringComparison.Ordinal) => FakeHttpHandler.Json("""{"data":{"id":"99"}}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private FakeEventStream ChatConnection()
    {
        var stream = new FakeEventStream();
        _chatConnections.Enqueue(stream);
        return stream;
    }

    private IReadOnlyList<BotEvent> Events(string type) => _published.Where(e => e.Type == type).ToArray();

    private IReadOnlyList<string> ChatTexts() => Events(RumbleEventTypes.ChatMessage).Select(e => (string)e.Args["message"]!).ToArray();

    /// <summary>Lets several more polls run so duplicates, if any, would show up.</summary>
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task Goes_live_publishes_new_activity_once_attaches_chat_sends_and_goes_offline()
    {
        await _service.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, _service.State);
        Assert.Equal("CuebotDev", _service.ChannelName);
        Assert.False(_service.IsLive);
        Assert.Contains("offline", _service.StatusMessage);

        var chat = ChatConnection();
        _apiJson = TestData.Read("live-stream-api-live.json");
        await Wait.UntilAsync(() => Events(RumbleEventTypes.StreamOnline).Count == 1, "stream online");
        Assert.True(_service.IsLive);
        Assert.Equal("dev testing: Cuebot", _service.CurrentStreamTitle);
        Assert.Equal("Cuebot Dev Channel", _service.ChannelName);
        Assert.Equal("4ponnu", Events(RumbleEventTypes.StreamOnline)[0].Args["streamId"]);

        // The chat stream replays what the API already delivered; each message is published once.
        chat.SendData(TestData.Read("chat-init.json").ReplaceLineEndings(""));
        await Wait.UntilAsync(() => _service.IsChatAttached, "chat attached");
        chat.SendData("""{"type":"messages","data":{"users":[{"id":"5","username":"Zed","badges":[]}],"messages":[{"id":"4","user_id":"5","text":"!hello","time":"2026-01-01T13:00:00+0000"}]}}""");
        await Wait.UntilAsync(() => ChatTexts().Contains("!hello"), "chat stream message");
        await SettleAsync();

        Assert.Equal(["Alice", "Bob", "Carol"], Events(RumbleEventTypes.Follow).Select(e => e.Args["user"]));
        Assert.Equal(["Erin", "Dave"], Events(RumbleEventTypes.Subscribe).Select(e => e.Args["user"]));
        Assert.Equal("Frank", Assert.Single(Events(RumbleEventTypes.GiftSub)).Args["user"]);
        Assert.Equal(["welcome!", "hello there", "Great stream", "!hello"], ChatTexts());
        Assert.Equal([5.0, 5.0], Events(RumbleEventTypes.Rant).Select(e => e.Args["amount"]));   // API rant at 12:50, stream rant at 12:59
        Assert.Contains("live chat connected", _service.StatusMessage);
        var hello = Events(RumbleEventTypes.ChatMessage).Single(e => (string)e.Args["message"]! == "!hello");
        Assert.Equal(("Zed", "5", "4"), (hello.Args["user"], hello.Args["userId"], hello.Args["messageId"]));

        _service.Settings.SessionCookie = "tok";
        await _service.SendChatMessageAsync("  hi from cuebot  ");
        var sent = Assert.Single(_http.Requests, r => r.Path.EndsWith("/message", StringComparison.Ordinal));
        Assert.Equal($"/chat/api/chat/{ChatId}/message", sent.Path);
        Assert.Equal("u_s=tok", sent.Cookie);
        Assert.Contains("\"hi from cuebot\"", sent.Body);

        _apiJson = TestData.Read("live-stream-api-offline.json");
        await Wait.UntilAsync(() => Events(RumbleEventTypes.StreamOffline).Count == 1, "stream offline");
        Assert.False(_service.IsLive);
        Assert.False(_service.IsChatAttached);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hi"));
        await SettleAsync();
        Assert.Single(Events(RumbleEventTypes.StreamOnline));
        Assert.Single(Events(RumbleEventTypes.StreamOffline));
    }

    [Fact]
    public async Task Connecting_while_live_publishes_no_history_and_falls_back_to_api_chat_when_the_stream_closes()
    {
        _apiJson = TestData.Read("live-stream-api-live.json");
        var chat = ChatConnection();

        await _service.ConnectAsync();
        chat.SendData(TestData.Read("chat-init.json").ReplaceLineEndings(""));
        await Wait.UntilAsync(() => _service.IsChatAttached, "chat attached");
        await SettleAsync();
        Assert.Empty(_published);

        chat.End();   // the next connection answers 204: Rumble closed the chat
        await Wait.UntilAsync(() => !_service.IsChatAttached, "chat detached");
        Assert.Contains("chat via Live Stream API", _service.StatusMessage);

        _apiJson = _apiJson.Replace("\"recent_messages\": [",
            "\"recent_messages\": [ " + """{ "username": "Bob", "badges": ["premium"], "text": "via the api", "created_on": "2026-01-01T13:01:00+00:00" },""",
            StringComparison.Ordinal);
        await Wait.UntilAsync(() => ChatTexts().Count == 1, "API chat message");
        await SettleAsync();

        Assert.Equal(["via the api"], ChatTexts());
        var message = Events(RumbleEventTypes.ChatMessage)[0];
        Assert.Equal(("Bob", "", true), (message.Args["user"], message.Args["userId"], message.Args["isPremium"]));
        Assert.Empty(Events(RumbleEventTypes.StreamOnline));
    }

    [Fact]
    public async Task A_rejected_api_url_fails_the_connection_without_revealing_the_key()
    {
        _apiStatus = HttpStatusCode.Forbidden;

        var error = await Assert.ThrowsAsync<RumbleApiException>(() => _service.ConnectAsync());

        Assert.Equal(ConnectionState.Error, _service.State);
        Assert.Contains("rejected", _service.StatusMessage);
        Assert.DoesNotContain("SECRETKEY", error.Message);
        Assert.DoesNotContain("SECRETKEY", _service.StatusMessage);
    }

    [Fact]
    public async Task Poll_failures_are_retried_and_logged_without_the_key()
    {
        await _service.ConnectAsync();
        _apiStatus = HttpStatusCode.InternalServerError;
        await Wait.UntilAsync(() => _service.StatusMessage.Contains("retrying"), "retry status");

        _apiStatus = HttpStatusCode.OK;
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "recovered");

        Assert.Contains(_log.Recent(), e => e.Message.Contains("HTTP 500"));
        Assert.Contains(_log.Recent(), e => e.Message.Contains("get-data?key=***"));
        Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains("SECRETKEY") || e.Message.Contains("SECRET-STREAM-KEY"));
    }

    [Fact]
    public async Task Connect_requires_an_api_url()
    {
        _settings.Value.LiveStreamApiUrl = " ";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task StartAsync_connects_only_when_configured_and_never_throws()
    {
        _settings.Value.AutoConnect = false;
        await _service.StartAsync();
        Assert.Empty(_http.Requests);

        _settings.Value.AutoConnect = true;
        _apiStatus = HttpStatusCode.Unauthorized;
        await _service.StartAsync();
        Assert.Equal(ConnectionState.Error, _service.State);
    }

    [Fact]
    public async Task Disconnect_stops_polling()
    {
        await _service.ConnectAsync();
        await _service.DisconnectAsync();
        var requests = _http.Requests.Count;
        await SettleAsync();

        Assert.Equal(ConnectionState.Disconnected, _service.State);
        Assert.Null(_service.ChannelName);
        Assert.Equal(requests, _http.Requests.Count);
    }

    [Fact]
    public async Task Sending_needs_a_sign_in_and_a_live_stream()
    {
        var notSignedIn = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hi"));
        Assert.Contains("Sign in", notSignedIn.Message);

        _settings.Value.SessionCookie = "tok";
        await _service.ConnectAsync();
        var notLive = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hi"));
        Assert.Contains("live", notLive.Message);
    }

    [Fact]
    public async Task Sign_in_stores_only_the_session_and_sign_out_clears_it()
    {
        var http = new FakeHttpHandler(r => r.Uri.Query switch
        {
            "?name=user.get_salts" => FakeHttpHandler.Json("""{"data":{"salts":["a","b","c"]}}"""),
            "?name=user.2fa.first_step" => FakeHttpHandler.Json("""{"data":{"totp_id":null}}"""),
            _ => FakeHttpHandler.Json("""{"data":{"session":"session-from-body"}}"""),
        });
        await using var service = new RumbleService(_settings, new HttpClient(http), ownsHttp: true, new EventBus(_log), _log, Endpoints,
            TimeProvider.System, TestTimings.Fast);

        await service.SignInAsync("Streamer", "pa55word");

        Assert.True(service.CanSendChat);
        Assert.Equal("Streamer", service.SignedInUser);
        Assert.Equal("session-from-body", _settings.Value.SessionCookie);
        _settings.Flush();
        var saved = await File.ReadAllTextAsync(Path.Combine(_dataDir, "rumble.json"));
        Assert.DoesNotContain("pa55word", saved);
        Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains("pa55word") || e.Message.Contains("session-from-body"));

        service.SignOut();
        Assert.False(service.CanSendChat);
        Assert.Null(service.SignedInUser);
        Assert.Equal("", _settings.Value.SessionCookie);
    }
}

public sealed class RumbleRegistrationTests
{
    [Fact]
    public async Task AddRumble_registers_service_chat_adapter_sender_triggers_sub_action_and_script_function()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cuebot-rumble-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new ServiceCollection().AddCuebotCore(new AppPaths(dataDir)).AddRumble().BuildServiceProvider())
            {
                var rumble = provider.GetRequiredService<RumbleService>();
                Assert.Equal("Rumble", rumble.Name);
                Assert.Contains(provider.GetServices<IConnectionStatus>(), s => ReferenceEquals(s, rumble));

                var hub = provider.GetRequiredService<ChatHub>();
                Assert.Contains("Rumble", hub.Platforms);
                var sender = Assert.Single(provider.GetServices<IChatSender>(), s => s.Platform == "Rumble");
                Assert.False(sender.CanSend);

                var registry = provider.GetRequiredService<Registry>();
                foreach (var id in new[] { "rumble.command", "rumble.chatMessage", "rumble.rant", "rumble.follow", "rumble.subscribe",
                             "rumble.giftSub", "rumble.streamOnline", "rumble.streamOffline" })
                    Assert.NotNull(registry.GetTrigger(id));
                var send = registry.GetSubAction("rumble.sendMessage");
                Assert.NotNull(send);
                Assert.Equal(("Rumble", "Send chat message"), (send.Category, send.DisplayName));
                Assert.Equal(ParameterKind.Multiline, Assert.Single(send.Parameters).Kind);

                Assert.Contains(provider.GetRequiredService<ScriptApi>().Functions, f => f.Name == "rumble.sendMessage");

                // Rumble events reach the combined chat and feed.
                var bus = provider.GetRequiredService<EventBus>();
                bus.Publish(RumbleEvents.Follow(new LiveApi.ApiFollower("Alice", DateTimeOffset.Now)));
                Assert.Equal("Alice followed", Assert.Single(hub.RecentFeed()).Title);
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
