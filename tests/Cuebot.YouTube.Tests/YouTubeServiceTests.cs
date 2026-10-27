using System.Collections.Concurrent;
using System.Net;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Auth;
using Cuebot.YouTube.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.YouTube.Tests.Support.ChatJson;

namespace Cuebot.YouTube.Tests;

/// <summary>End to end against a fake Google: sign-in, broadcast monitoring, chat, sending, moderation, updates, quota.</summary>
public sealed class YouTubeServiceTests : IAsyncLifetime
{
    private const string LiveBroadcast = """
        {"id":"b1","snippet":{"title":"Dev testing","description":"d","liveChatId":"chat1","channelId":"UCownerownerownerowner01",
          "actualStartTime":"2026-03-10T17:00:00Z"},"status":{"lifeCycleStatus":"live","privacyStatus":"public"}}
        """;

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-youtube-svc-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly ConcurrentQueue<string> _chatPages = new();
    private volatile string _active = "";
    private volatile string _videoTitle = "Dev testing";
    private volatile bool _quotaExhausted;
    private JsonFile<YouTubeSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private YouTubeService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<YouTubeSettings>(Path.Combine(_dataDir, "youtube.json"), _log);
        var s = _settings.Value;
        s.ClientId = "cid";
        s.ClientSecret = "secret";
        s.Broadcaster.AccessToken = "OWNER-AT";
        s.Broadcaster.RefreshToken = "OWNER-RT";
        s.Broadcaster.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        s.MinimumChatPollSeconds = 0;
        s.BroadcastPollSeconds = 0;
        s.LivePollSeconds = 0;
        s.SubscriberPollSeconds = 0;
        _http = new FakeHttpHandler(Respond);
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _service = new YouTubeService(_settings, new HttpClient(_http), bus, _log, TestApi.Endpoints, TimeProvider.System, TestApi.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r)
    {
        if (_quotaExhausted && r.Path.StartsWith("/youtube/", StringComparison.Ordinal))
            return FakeHttpHandler.Error(HttpStatusCode.Forbidden, "quotaExceeded");
        return (r.Method.Method, r.Path) switch
        {
            ("POST", "/device/code") => FakeHttpHandler.Json("""{"device_code":"DEV","user_code":"WXYZ-1234","verification_url":"https://www.google.com/device","expires_in":600,"interval":0}"""),
            ("POST", "/token") when r.Form("grant_type") == "refresh_token" => FakeHttpHandler.Json("""{"access_token":"REFRESHED","expires_in":3600}"""),
            ("POST", "/token") => FakeHttpHandler.Json("""{"access_token":"DEVICE-AT","refresh_token":"DEVICE-RT","expires_in":3600,"scope":"https://www.googleapis.com/auth/youtube"}"""),
            ("POST", "/revoke") => new HttpResponseMessage(HttpStatusCode.OK),
            ("GET", "/youtube/v3/channels") when r.Query("mine") == "true" => r.Bearer == "BOT-AT"
                ? FakeHttpHandler.Json($$$"""{"items":[{"id":"{{{Bob}}}","snippet":{"title":"Bot"}}]}""")
                : FakeHttpHandler.Json($$$"""{"items":[{"id":"{{{Owner}}}","snippet":{"title":"Owner","customUrl":"@owner"}}]}"""),
            ("GET", "/youtube/v3/channels") => FakeHttpHandler.Json($$$"""{"items":[{"id":"{{{Alice}}}","snippet":{"title":"Alice"},"statistics":{"subscriberCount":"3"}}]}"""),
            ("GET", "/youtube/v3/liveBroadcasts") => FakeHttpHandler.Json($$$"""{"items":[{{{_active}}}]}"""),
            ("GET", "/youtube/v3/videos") => FakeHttpHandler.Json($$$"""
                {"items":[{"id":"b1","snippet":{"title":"{{{_videoTitle}}}","description":"d","categoryId":"20","tags":["t1"]},"status":{"privacyStatus":"public"},
                  "statistics":{"viewCount":"10"},"liveStreamingDetails":{"concurrentViewers":"7","activeLiveChatId":"chat1"}}]}
                """),
            ("PUT", "/youtube/v3/videos") => FakeHttpHandler.Json(r.Body),
            ("GET", "/youtube/v3/liveChat/messages") => FakeHttpHandler.Json(_chatPages.TryDequeue(out var page) ? page : Page("same", 0)),
            ("POST", "/youtube/v3/liveChat/messages") => FakeHttpHandler.Json("""{"id":"SENT"}"""),
            ("POST", "/youtube/v3/liveChat/bans") => FakeHttpHandler.Json("""{"id":"BAN-ID"}"""),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("GET", "/youtube/v3/subscriptions") => FakeHttpHandler.Json("""{"items":[]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private Task<BotEvent> WaitForEvent(string type, Func<BotEvent, bool>? match = null) =>
        Wait.ForAsync(() => _published.FirstOrDefault(e => e.Type == type && (match?.Invoke(e) ?? true)), type);

    private async Task ConnectLiveAsync()
    {
        _active = LiveBroadcast;
        _chatPages.Enqueue(Page("p1", 0, Text("old", "history")));
        await _service.ConnectAsync();
        await WaitForEvent(YouTubeEventTypes.BroadcastMonitoringStarted);
        await Wait.UntilAsync(() => _http.To("/liveChat/messages").Count >= 2, "chat to be seeded");
    }

    [Fact]
    public async Task Connect_monitors_the_active_broadcast_and_publishes_new_chat_without_history()
    {
        await ConnectLiveAsync();
        _chatPages.Enqueue(Page("p2", 0, Text("m1", "hello from chat")));

        var message = await WaitForEvent(YouTubeEventTypes.ChatMessage);

        Assert.Equal("hello from chat", message.Args["message"]);
        Assert.DoesNotContain(_published, e => e.Type == YouTubeEventTypes.ChatMessage && (string)e.Args["message"]! == "history");
        Assert.DoesNotContain(_published, e => e.Type == YouTubeEventTypes.BroadcastStarted);
        Assert.Equal(ConnectionState.Connected, _service.State);
        await Wait.UntilAsync(() => _service.StatusMessage.StartsWith("Live: Dev testing", StringComparison.Ordinal), "live status");
        Assert.Equal("b1", _service.LatestBroadcast!.Id);
        Assert.True(_service.IsLive);
        Assert.Equal(Owner, _settings.Value.Broadcaster.ChannelId);
        Assert.Equal("chat1", _http.To("/liveChat/messages")[0].Query("liveChatId"));
    }

    [Fact]
    public async Task Going_live_and_ending_fire_started_and_ended_and_statistics_update()
    {
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _http.To("/liveBroadcasts").Count >= 1, "first broadcast check");
        await Wait.UntilAsync(() => _service.StatusMessage.Contains("waiting", StringComparison.Ordinal), "waiting status");

        _active = LiveBroadcast;
        var started = await WaitForEvent(YouTubeEventTypes.BroadcastStarted);
        Assert.Equal("Dev testing", started.Args["broadcastTitle"]);
        Assert.Equal("Owner", started.Args["user"]);
        var stats = await WaitForEvent(YouTubeEventTypes.BroadcastStatisticsUpdated);
        Assert.Equal(7L, stats.Args["concurrentViewers"]);

        _videoTitle = "Renamed";
        var updated = await WaitForEvent(YouTubeEventTypes.BroadcastUpdated);
        Assert.Equal("Renamed", updated.Args["broadcastTitle"]);
        Assert.Equal("Dev testing", updated.Args["previousTitle"]);

        _chatPages.Enqueue("""{"nextPageToken":"x","pollingIntervalMillis":0,"offlineAt":"2026-03-10T19:00:00Z","items":[]}""");
        await WaitForEvent(YouTubeEventTypes.BroadcastEnded);
        await WaitForEvent(YouTubeEventTypes.BroadcastMonitoringEnded);
        Assert.False(_service.IsLive);
    }

    [Fact]
    public async Task Sending_uses_the_broadcaster_or_the_bot_with_fallback()
    {
        await ConnectLiveAsync();

        await _service.SendChatMessageAsync("as owner");
        await _service.SendChatMessageAsync("bot missing, fallback", useBot: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("no fallback", useBot: true, fallback: false));

        _settings.Value.Bot.AccessToken = "BOT-AT";
        _settings.Value.Bot.RefreshToken = "BOT-RT";
        _settings.Value.Bot.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await _service.SendChatMessageAsync("as bot", useBot: true);
        await _service.SendChatMessageAsync(string.Join(' ', Enumerable.Repeat("long", 60)));

        var posts = _http.To("/liveChat/messages").Where(r => r.Method == HttpMethod.Post).ToArray();
        string Text(RecordedRequest r) => r.Json.GetProperty("snippet").GetProperty("textMessageDetails").GetProperty("messageText").GetString()!;
        Assert.Equal(("as owner", "OWNER-AT"), (Text(posts[0]), posts[0].Bearer));
        Assert.Equal(("bot missing, fallback", "OWNER-AT"), (Text(posts[1]), posts[1].Bearer));
        Assert.Equal(("as bot", "BOT-AT"), (Text(posts[2]), posts[2].Bearer));
        Assert.Equal(5, posts.Length);   // 299 characters split in two
        Assert.All(posts, p => Assert.Equal("chat1", p.Json.GetProperty("snippet").GetProperty("liveChatId").GetString()));
    }

    [Fact]
    public async Task Sending_without_a_live_broadcast_fails_clearly()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hi"));
        Assert.Contains("not connected", ex.Message);
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _http.To("/liveBroadcasts").Count >= 1, "first broadcast check");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("hi"));
        Assert.Contains("no live", ex.Message);
    }

    [Fact]
    public async Task Moderation_resolves_recent_chatters_and_lifts_its_own_bans()
    {
        await ConnectLiveAsync();
        _chatPages.Enqueue(Page("p2", 0, Text("m1", "spam", Author(Alice, "Alice"))));
        await WaitForEvent(YouTubeEventTypes.ChatMessage);

        await _service.TimeoutUserAsync("@Alice", 60);
        await _service.BanUserAsync(Alice);
        await _service.UnbanUserAsync("Alice");
        await _service.DeleteMessageAsync("m1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UnbanUserAsync(Bob));

        var bans = _http.To("/liveChat/bans");
        var timeout = bans[0].Json.GetProperty("snippet");
        Assert.Equal(("temporary", 60, Alice), (timeout.GetProperty("type").GetString(), timeout.GetProperty("banDurationSeconds").GetInt32(),
            timeout.GetProperty("bannedUserDetails").GetProperty("channelId").GetString()));
        Assert.Equal("permanent", bans[1].Json.GetProperty("snippet").GetProperty("type").GetString());
        Assert.Equal((HttpMethod.Delete, "BAN-ID"), (bans[2].Method, bans[2].Query("id")));
        Assert.Contains(_http.To("/liveChat/messages"), r => r.Method == HttpMethod.Delete && r.Query("id") == "m1");
        Assert.DoesNotContain(_http.Requests, r => r.Query("forHandle").Length > 0);
    }

    [Fact]
    public async Task Unknown_names_are_looked_up_by_handle()
    {
        await ConnectLiveAsync();
        var (id, name) = await _service.ResolveUserAsync("@stranger");
        Assert.Equal((Alice, "Alice"), (id, name));
        Assert.Equal("@stranger", _http.Requests.Last(r => r.Path.EndsWith("/channels", StringComparison.Ordinal)).Query("forHandle"));
    }

    [Fact]
    public async Task Set_title_updates_the_live_broadcast_video()
    {
        await ConnectLiveAsync();

        await _service.UpdateBroadcastAsync(new VideoChange(Title: "New title"));

        var put = _http.To("/videos").Single(r => r.Method == HttpMethod.Put);
        var snippet = put.Json.GetProperty("snippet");
        Assert.Equal("New title", snippet.GetProperty("title").GetString());
        Assert.Equal("20", snippet.GetProperty("categoryId").GetString());
        Assert.Equal("b1", put.Json.GetProperty("id").GetString());
        Assert.Equal("New title", _service.LatestBroadcast!.Title);
    }

    [Fact]
    public async Task Quota_exhaustion_publishes_an_event_and_shows_in_the_status()
    {
        _quotaExhausted = true;
        await Assert.ThrowsAsync<YouTubeApiException>(() => _service.ConnectAsync());
        var e = await WaitForEvent(YouTubeEventTypes.QuotaExceeded);
        Assert.Equal(_service.QuotaResetsAt.ToString("O"), e.Args["resumesAt"]);
        Assert.NotNull(_service.QuotaExhaustedUntil);
        Assert.Equal(ConnectionState.Error, _service.State);
    }

    [Fact]
    public async Task Device_sign_in_stores_tokens_and_channel_then_connects()
    {
        _settings.Value.Broadcaster = new YouTubeAccount();
        var auth = await _service.BeginDeviceSignInAsync(YouTubeAccountKind.Broadcaster);
        Assert.Equal("WXYZ-1234", auth.UserCode);

        await _service.CompleteDeviceSignInAsync(auth);

        var account = _settings.Value.Broadcaster;
        Assert.Equal(("DEVICE-AT", "DEVICE-RT", Owner, "Owner", "@owner"),
            (account.AccessToken, account.RefreshToken, account.ChannelId, account.ChannelTitle, account.CustomUrl));
        Assert.True(_service.IsConnected);
        Assert.Equal("https://www.googleapis.com/auth/youtube", _http.To("/device/code").Single().Form("scope"));
    }

    [Fact]
    public async Task Signing_out_the_bot_revokes_its_refresh_token_and_keeps_the_broadcaster()
    {
        _settings.Value.Bot.AccessToken = "BOT-AT";
        _settings.Value.Bot.RefreshToken = "BOT-RT";

        await _service.SignOutAsync(YouTubeAccountKind.Bot);

        Assert.False(_service.IsBotSignedIn);
        Assert.True(_service.IsBroadcasterSignedIn);
        Assert.Equal("BOT-RT", _http.To("/revoke").Single().Form("token"));
    }

    [Fact]
    public async Task Expired_tokens_refresh_before_connecting()
    {
        _settings.Value.Broadcaster.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _service.ConnectAsync();
        Assert.Equal("REFRESHED", _settings.Value.Broadcaster.AccessToken);
        Assert.Equal("OWNER-RT", _settings.Value.Broadcaster.RefreshToken);
        Assert.Equal("REFRESHED", _http.To("/channels").Last().Bearer);
    }

    [Fact]
    public async Task Auto_connect_requires_client_and_sign_in()
    {
        _settings.Value.ClientSecret = "";
        await _service.StartAsync();
        Assert.False(_service.IsConnected);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task AddYouTube_registers_service_adapter_sender_triggers_sub_actions_and_script_functions()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cuebot-youtube-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            await using (var provider = new ServiceCollection().AddCuebotCore(new AppPaths(dataDir)).AddYouTube().BuildServiceProvider())
            {
                var service = provider.GetRequiredService<YouTubeService>();
                Assert.Contains(provider.GetServices<IConnectionStatus>(), s => ReferenceEquals(s, service));
                Assert.Contains(provider.GetServices<IChatEventAdapter>(), a => a.Platform == "YouTube");
                Assert.Contains(provider.GetServices<IChatSender>(), s => s.Platform == "YouTube" && !s.CanSend);
                var registry = provider.GetRequiredService<Registry>();
                Assert.Contains(registry.Triggers, t => t.TypeId == YouTubeEventTypes.SuperChat);
                foreach (var id in (string[])["youtube.sendMessage", "youtube.banUser", "youtube.timeoutUser", "youtube.setTitle",
                             "youtube.setDescription", "youtube.setTitleAndDescription", "youtube.deleteMessage", "youtube.createPoll"])
                    Assert.NotNull(registry.GetSubAction(id));
                Assert.Equal(YouTubeSubActionsCount, registry.SubActions.Count(s => s.TypeId.StartsWith("youtube.", StringComparison.Ordinal)));
                var functions = provider.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
                Assert.Contains("youtube.sendMessage", functions);
                Assert.Contains("youtube.getLatestMonitoredBroadcast", functions);
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static int YouTubeSubActionsCount => Cuebot.YouTube.SubActions.YouTubeSubActions.Count;
}
