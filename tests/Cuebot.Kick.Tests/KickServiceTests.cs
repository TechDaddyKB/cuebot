using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Auth;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Kick.Auth;
using Cuebot.Kick.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Kick.Tests;

/// <summary>End to end against fake Kick servers: sign-in, connect, real-time events, sending and moderation.</summary>
public sealed class KickServiceTests : IAsyncLifetime
{
    private static readonly KickEndpoints Endpoints = new(new Uri("https://id.test/"), new Uri("https://api.test/"),
        new Uri("https://web.test/"), new Uri("https://rt.test/api/"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-kick-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeRealtimeServer _pusher = new();
    private readonly FakeReceiver _receiver = new();
    private volatile bool _live;
    private volatile HttpStatusCode _webStatus = HttpStatusCode.OK;
    private JsonFile<KickSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private KickService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _settings = new JsonFile<KickSettings>(Path.Combine(_dir, "kick.json"), _log);
        _settings.Value.ClientId = "cid";
        _settings.Value.ClientSecret = "csecret";
        _settings.Value.PollSeconds = 0;
        _http = new FakeHttpHandler(Respond);
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _service = new KickService(_settings, new HttpClient(_http), bus, _log, Endpoints, TestTimings.Fast, TimeProvider.System,
            _pusher.Create, _ => _receiver);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r) => (r.Uri.Host, r.Path) switch
    {
        ("id.test", "/oauth/token") => FakeHttpHandler.Json(
            """{"access_token":"AT-1","token_type":"Bearer","refresh_token":"RT-1","expires_in":7200,"scope":"user:read chat:write"}"""),
        ("id.test", "/oauth/revoke") => new HttpResponseMessage(HttpStatusCode.OK),
        ("api.test", "/public/v1/users") when r.Query.Length == 0 => FakeHttpHandler.Json(
            r.Authorization == "Bearer BOT-AT"
                ? """{"data":[{"user_id":200,"name":"CueBot","email":"","profile_picture":""}]}"""
                : """{"data":[{"user_id":100,"name":"CuebotDev","email":"","profile_picture":""}]}"""),
        ("api.test", "/public/v1/users") => FakeHttpHandler.Json("""{"data":[{"user_id":666,"name":"Spammer","profile_picture":"pic"}]}"""),
        ("api.test", "/public/v1/channels") => FakeHttpHandler.Json(ChannelJson()),
        ("api.test", "/public/v1/chat") => FakeHttpHandler.Json("""{"data":{"is_sent":true,"message_id":"sent-1"},"message":"OK"}"""),
        ("api.test", "/public/v1/moderation/bans") => FakeHttpHandler.Json("""{"data":{},"message":"OK"}"""),
        ("api.test", "/public/v1/categories") => FakeHttpHandler.Json(
            """{"data":[{"id":1,"name":"Just Chatting IRL","thumbnail":""},{"id":15,"name":"Just Chatting","thumbnail":""}]}"""),
        ("web.test", "/api/v2/channels/cuebotdev") => _webStatus == HttpStatusCode.OK
            ? FakeHttpHandler.Json("""{"id":1234,"user_id":100,"slug":"cuebotdev","chatroom":{"id":5678}}""")
            : new HttpResponseMessage(_webStatus),
        ("web.test", "/api/v2/channels/stranger") => FakeHttpHandler.Json("""{"id":9,"user_id":909,"slug":"stranger","chatroom":{"id":99}}"""),
        ("rt.test", var path) when path.EndsWith("/chat/connection", StringComparison.Ordinal) => FakeHttpHandler.Json(
            """{"data":{"connections":[{"credentials":{"url":"wss://realtime.test/connection/websocket"},"provider":"centrifugo"}],"mode":"websocket"},"message":"success"}"""),
        ("rt.test", "/api/v1/realtime/auth/connection") => FakeHttpHandler.Json("""{"data":{"token":"rt-token"}}"""),
        _ when r.Method == HttpMethod.Patch => new HttpResponseMessage(HttpStatusCode.NoContent),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private string ChannelJson() => $$$"""
        {"data":[{"broadcaster_user_id":100,"slug":"cuebotdev","stream_title":"Testing","category":{"id":15,"name":"Just Chatting","thumbnail":""},
          "channel_description":"","stream":{"is_live":{{{(_live ? "true" : "false")}}},"viewer_count":{{{(_live ? 12 : 0)}}},"start_time":"","language":"en","is_mature":false,"custom_tags":[]}}]}
        """;

    private void SignedIn()
    {
        _settings.Value.Broadcaster.AccessToken = "AT-1";
        _settings.Value.Broadcaster.RefreshToken = "RT-1";
        _settings.Value.Broadcaster.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        _settings.Value.Broadcaster.UserId = 100;
    }

    private async Task<FakeRealtimeTransport> ConnectAsync()
    {
        SignedIn();
        await _service.ConnectAsync();
        var conn = await _pusher.NextConnectionAsync();
        await conn.EstablishAsync();
        for (var i = 0; i < 4; i++) await conn.NextSentAsync(); // four subscriptions
        await Wait.UntilAsync(() => _service.IsRealtimeConnected, "real-time connected");
        return conn;
    }

    private BotEvent Published(string type) => _published.Last(e => e.Type == type);

    [Fact]
    public async Task Sign_in_uses_pkce_end_to_end_and_stores_the_account()
    {
        Uri? opened = null;
        _receiver.Respond = state => new OAuthCallbackResult("the-code", null);
        await _service.SignInAsync(KickAccount.Broadcaster, url => opened = url);

        Assert.NotNull(opened);
        var query = OAuthCallback.ParseQuery(opened.Query);
        Assert.Equal("cid", query["client_id"]);
        Assert.Equal("http://localhost:47582/callback", query["redirect_uri"]);
        Assert.Contains("moderation:ban", query["scope"]);
        Assert.Equal(_receiver.LastState, query["state"]);

        var token = _http.Requests.Single(r => r.Path == "/oauth/token");
        var form = OAuthCallback.ParseQuery(token.Body);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal(query["code_challenge"], PkceRequest.ChallengeFor(form["code_verifier"]));

        var account = _settings.Value.Broadcaster;
        Assert.Equal("AT-1", account.AccessToken);
        Assert.Equal("RT-1", account.RefreshToken);
        Assert.Equal(100, account.UserId);
        Assert.Equal("CuebotDev", account.UserName);
        Assert.Equal("cuebotdev", _settings.Value.ChannelSlug);
        Assert.Equal("CuebotDev", Published(KickEventTypes.BroadcasterAuthenticated).Args["user"]);
    }

    [Fact]
    public async Task Sign_in_failure_from_the_browser_is_reported()
    {
        _receiver.Respond = _ => new OAuthCallbackResult(null, "Sign-in was cancelled on Kick.");
        var ex = await Assert.ThrowsAsync<KickApiException>(() => _service.SignInAsync(KickAccount.Broadcaster, _ => { }));
        Assert.Contains("cancelled", ex.Message);
        Assert.False(_service.IsBroadcasterSignedIn);
    }

    [Fact]
    public async Task Without_an_own_app_the_built_in_app_signs_in_on_its_registered_port()
    {
        _settings.Value.ClientId = "";
        _settings.Value.ClientSecret = "";
        _settings.Value.RedirectPort = 50001;   // only the user's own app may use another port
        await using var service = new KickService(_settings, new HttpClient(_http), new EventBus(_log), _log, Endpoints, TestTimings.Fast,
            TimeProvider.System, _pusher.Create, port => { Assert.Equal(KickEndpoints.DefaultRedirectPort, port); return _receiver; },
            builtInApp: new AppCredentials("builtin-cid", "builtin-secret", true));
        Uri? opened = null;
        _receiver.Respond = _ => new OAuthCallbackResult("the-code", null);
        await service.SignInAsync(KickAccount.Broadcaster, url => opened = url);

        var query = OAuthCallback.ParseQuery(opened!.Query);
        Assert.Equal("builtin-cid", query["client_id"]);
        Assert.Equal("http://localhost:47582/callback", query["redirect_uri"]);
        var form = OAuthCallback.ParseQuery(_http.Requests.Single(r => r.Path == "/oauth/token").Body);
        Assert.Equal("builtin-secret", form["client_secret"]);

        // The user's own app, once entered, replaces it (with the port they chose).
        _settings.Value.ClientId = "own-cid";
        _settings.Value.ClientSecret = "own-secret";
        Assert.False(service.App.IsBuiltIn);
        Assert.Equal("http://localhost:50001/callback", service.RedirectUri);
    }

    [Fact]
    public async Task Sign_in_needs_app_credentials()
    {
        _settings.Value.ClientSecret = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SignInAsync(KickAccount.Broadcaster, _ => { }));
    }

    [Fact]
    public async Task Connect_looks_up_ids_subscribes_and_publishes_realtime_events()
    {
        SignedIn();
        await _service.ConnectAsync();
        var conn = await _pusher.NextConnectionAsync();
        Assert.Equal("rt-token", (await conn.EstablishAsync()).GetProperty("token").GetString());
        Assert.Equal(new Uri("wss://realtime.test/connection/websocket"), conn.Url); // from the discovery call
        var channels = new List<string>();
        for (var i = 0; i < 4; i++) channels.Add(await conn.NextSubscribedChannelAsync());
        Assert.Equal(["chatrooms.5678.v2", "chatroom_5678", "channel.1234", "channel_1234"], channels);
        Assert.Equal(1234, _settings.Value.ChannelId);
        Assert.Equal(5678, _settings.Value.ChatroomId);
        await Wait.UntilAsync(() => _service.IsRealtimeConnected, "real-time connected");
        Assert.Equal(ConnectionState.Connected, _service.State);
        Assert.Contains("real-time chat connected", _service.StatusMessage);
        Assert.Contains(_published, e => e.Type == KickEventTypes.ChatConnected);

        conn.EventRaw("App\\Events\\ChatMessageEvent", "chatrooms.5678.v2", TestData.Read("pusher-chat.json"));
        conn.EventRaw("App\\Events\\ChatMessageEvent", "chatroom_5678", TestData.Read("pusher-chat.json")); // same message on a second channel
        await Wait.UntilAsync(() => _published.Any(e => e.Type == KickEventTypes.ChatMessage), "the chat message");
        await Task.Delay(100);
        Assert.Single(_published, e => e.Type == KickEventTypes.ChatMessage);
        Assert.Equal("first! KEKW", Published(KickEventTypes.ChatMessage).Args["message"]);
    }

    [Fact]
    public async Task Realtime_lookup_blocked_still_connects_with_api_features()
    {
        _webStatus = HttpStatusCode.Forbidden;
        SignedIn();
        await _service.ConnectAsync();
        Assert.Equal(ConnectionState.Connected, _service.State);
        Assert.Contains("unavailable", _service.StatusMessage);
        Assert.Equal(0, _pusher.Attempts);
    }

    [Fact]
    public async Task Manually_entered_ids_skip_the_lookup()
    {
        _settings.Value.ChannelSlug = "cuebotdev";
        _settings.Value.ChannelId = 1;
        _settings.Value.ChatroomId = 2;
        SignedIn();
        await _service.ConnectAsync();
        var conn = await _pusher.NextConnectionAsync();
        await conn.EstablishAsync();
        Assert.Equal("chatrooms.2.v2", await conn.NextSubscribedChannelAsync());
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == "web.test");
    }

    [Fact]
    public async Task Present_viewers_get_the_live_state_title_viewers_and_category()
    {
        var before = new Dictionary<string, object?>();
        _service.AddPresentViewersArgs(before);
        Assert.Equal(false, before["isLive"]);
        Assert.Equal("", before["categoryName"]);
        Assert.Equal(0, before["viewerCount"]);

        _live = true;
        await ConnectAsync();
        await Wait.UntilAsync(() => _service.Channel?.IsLive == true, "a poll while live");
        var args = new Dictionary<string, object?>();
        new KickPresentViewersArgs(_service).Add(args);

        Assert.Equal(true, args["isLive"]);
        Assert.Equal("Testing", args["title"]);
        Assert.Equal(12, args["viewerCount"]);
        Assert.Equal("15", args["categoryId"]);
        Assert.Equal("Just Chatting", args["categoryName"]);
        Assert.Equal("", args["categoryThumbnail"]);
        Assert.IsType<DateTime>(args["startedAt"]);
    }

    [Fact]
    public async Task Polling_reports_going_live_once_even_when_realtime_said_so_first()
    {
        var conn = await ConnectAsync();
        conn.Event("App\\Events\\StreamerIsLive", "channel.1234", new { livestream = new { id = 1, session_title = "Testing" } });
        await Wait.UntilAsync(() => _published.Any(e => e.Type == KickEventTypes.StreamOnline), "stream online");
        _live = true;
        await Wait.UntilAsync(() => _published.Any(e => e.Type == KickEventTypes.ViewerCountUpdate), "a poll while live");
        Assert.Single(_published, e => e.Type == KickEventTypes.StreamOnline);
        Assert.Equal(12L, Published(KickEventTypes.ViewerCountUpdate).Args["viewers"]);
        Assert.True(_service.IsLive);
    }

    [Fact]
    public async Task Send_chat_as_broadcaster_bot_and_reply()
    {
        SignedIn();
        Assert.Equal("sent-1", await _service.SendChatMessageAsync("hello"));
        var send = Body(_http.Requests.Last(r => r.Path == "/public/v1/chat"));
        Assert.Equal("user", send.GetProperty("type").GetString());
        Assert.Equal(100, send.GetProperty("broadcaster_user_id").GetInt64());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendChatMessageAsync("x", useBot: true, fallback: false));
        await _service.SendChatMessageAsync("fallback", useBot: true, fallback: true);
        Assert.Equal("Bearer AT-1", _http.Requests.Last(r => r.Path == "/public/v1/chat").Authorization);

        _settings.Value.Bot.AccessToken = "BOT-AT";
        _settings.Value.Bot.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await _service.SendChatMessageAsync("from bot", useBot: true, replyTo: "parent");
        var bot = _http.Requests.Last(r => r.Path == "/public/v1/chat");
        Assert.Equal("Bearer BOT-AT", bot.Authorization);
        Assert.Equal(100, Body(bot).GetProperty("broadcaster_user_id").GetInt64());
        Assert.Equal("parent", Body(bot).GetProperty("reply_to_message_id").GetString());

        Assert.Null(await _service.SendChatMessageAsync("   "));
    }

    [Fact]
    public async Task Chat_sent_with_internal_off_is_not_flagged_internal()
    {
        SignedIn();
        // Streamer.bot's SendMessage internal: false, through the combined chat's sender.
        IChatSender sender = new KickChatSender(_service);
        await sender.SendAsync("hello", new ChatSendOptions(Internal: false, ReplyTo: "parent"));
        Assert.Equal("parent", Body(_http.Requests.Last(r => r.Path == "/public/v1/chat")).GetProperty("reply_to_message_id").GetString());

        _service.Hub.Handle(Events.KickEventFactory.Chat("realtime",
            new Events.KickEventFactory.ChatInput("sent-1", new Events.KickActor(5, "Fan", "fan"), "hello", 100, null)));
        Assert.Equal(false, Published(KickEventTypes.ChatMessage).Args["isInternal"]);
    }

    [Fact]
    public async Task Moderation_resolves_users_from_chat_ids_and_kick_lookups()
    {
        var conn = await ConnectAsync();
        conn.EventRaw("App\\Events\\ChatMessageEvent", "chatrooms.5678.v2", TestData.Read("pusher-chat.json"));
        await Wait.UntilAsync(() => _published.Any(e => e.Type == KickEventTypes.ChatMessage), "chat");

        await _service.TimeoutAsync("ModAlice", 90, "calm down");
        var timeout = Body(_http.Requests.Last(r => r.Path == "/public/v1/moderation/bans"));
        Assert.Equal(333, timeout.GetProperty("user_id").GetInt64());
        Assert.Equal(2, timeout.GetProperty("duration").GetInt32());

        await _service.BanAsync("stranger", null);
        Assert.Equal(909, Body(_http.Requests.Last(r => r.Path == "/public/v1/moderation/bans")).GetProperty("user_id").GetInt64());

        await _service.UnbanAsync("12345");
        var unban = _http.Requests.Last(r => r.Path == "/public/v1/moderation/bans");
        Assert.Equal(HttpMethod.Delete, unban.Method);
        Assert.Equal(12345, Body(unban).GetProperty("user_id").GetInt64());
    }

    [Fact]
    public async Task Category_by_name_prefers_an_exact_match()
    {
        SignedIn();
        var category = await _service.SetCategoryAsync("just chatting");
        Assert.Equal(15, category.Id);
        Assert.Equal("""{"category_id":15}""", _http.Requests.Last(r => r.Method == HttpMethod.Patch).Body);
    }

    [Fact]
    public async Task User_info_and_broadcaster_info()
    {
        SignedIn();
        _settings.Value.Broadcaster.UserName = "CuebotDev";
        var info = await _service.GetUserInfoAsync("666");
        Assert.Equal("Spammer", info["targetUser"]);
        Assert.Equal("pic", info["targetProfilePicture"]);
        Assert.Equal("CuebotDev", _service.BroadcasterInfo()["broadcastUser"]);
    }

    [Fact]
    public async Task Sign_out_revokes_and_forgets_tokens()
    {
        SignedIn();
        await _service.SignOutAsync(KickAccount.Broadcaster);
        Assert.False(_service.IsBroadcasterSignedIn);
        var revoke = _http.Requests.Single(r => r.Path == "/oauth/revoke");
        Assert.Contains("token=RT-1", revoke.Query);
    }

    [Fact]
    public async Task Secrets_are_not_logged()
    {
        _receiver.Respond = _ => new OAuthCallbackResult("the-code", null);
        await _service.SignInAsync(KickAccount.Broadcaster, _ => { });
        await _service.ConnectAsync();
        await _service.SignOutAsync(KickAccount.Broadcaster);
        Assert.NotEmpty(_log.Recent());
        Assert.DoesNotContain(_log.Recent(), entry =>
            entry.Message.Contains("csecret") || entry.Message.Contains("AT-1") || entry.Message.Contains("RT-1") || entry.Message.Contains("the-code"));
    }

    [Fact]
    public async Task Module_registers_everything_and_sub_actions_run()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(_log);
        services.AddSingleton(new EventBus(_log));
        services.AddSingleton(new HttpClient(_http));
        services.AddSingleton(_settings);
        services.AddSingleton(_service);
        services.AddSingleton(new Cuebot.Core.Users.GroupStore(null, _log));
        services.AddSingleton(sp => new Cuebot.Core.Users.UserRegistry(null, sp.GetRequiredService<EventBus>(), _log));
        // AddKick registers its own settings file and service; the test overrides them with the fakes above.
        var registered = new ServiceCollection();
        registered.AddKick();
        Assert.Contains(registered, d => d.ServiceType == typeof(KickService));
        Assert.Contains(registered, d => d.ServiceType == typeof(IConnectionStatus));
        Assert.Contains(registered, d => d.ServiceType == typeof(IChatEventAdapter));
        Assert.Contains(registered, d => d.ServiceType == typeof(IChatSender));
        Assert.Contains(registered, d => d.ServiceType == typeof(IScriptFunctionProvider));
        Assert.Contains(registered, d => d.ServiceType == typeof(Cuebot.Core.Chat.Emotes.IEmoteChannelSource));
        Assert.Contains(registered, d => d.ServiceType == typeof(Cuebot.Core.Webhooks.IWebhookHandler));

        foreach (var d in registered.Where(d => d.ServiceType == typeof(ISubActionHandler) || d.ServiceType == typeof(ITriggerProvider)
                                               || d.ServiceType == typeof(IScriptFunctionProvider) || d.ServiceType == typeof(IChatSender)
                                               || d.ServiceType == typeof(Cuebot.Core.Webhooks.IWebhookHandler)))
            services.Add(d);
        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetServices<ISubActionHandler>().ToArray();
        string[] expected =
        [
            "kick.sendMessage", "kick.replyToMessage", "kick.deleteMessage", "kick.setTitle", "kick.setCategory", "kick.banUser",
            "kick.timeoutUser", "kick.unbanUser", "kick.untimeoutUser", "kick.addBroadcasterInfo", "kick.getUserInfo",
            "kick.addRandomUsers", "kick.addUserToGroup", "kick.removeUserFromGroup", "kick.userInGroup",
            "kick.createReward", "kick.deleteReward", "kick.getRewardInfo", "kick.updateRedemption", "kick.updatePendingRedemptions",
            "kick.setRewardEnabled", "kick.setRewardPaused", "kick.setRewardTitle", "kick.setRewardCost", "kick.setRewardDescription",
            "kick.setRewardColor", "kick.setRewardUserInput", "kick.setRewardSkipQueue",
            "kick.getRewardCounter", "kick.resetRewardCounter", "kick.resetRewardUserCounters", "kick.setRewardGroupEnabled",
            "kick.setRewardGroupPaused", "kick.configureRewards",
        ];
        Assert.Equal(expected.Order(), handlers.Select(h => h.TypeId).Order());
        Assert.All(handlers, h => Assert.Equal("Kick", h.Category));

        var functions = provider.GetServices<IScriptFunctionProvider>().SelectMany(p => p.GetFunctions()).Select(f => f.Name).ToArray();
        Assert.Contains("kick.sendMessage", functions);
        Assert.Contains("kick.timeout", functions);
        Assert.Equal("kick", Assert.Single(provider.GetServices<Cuebot.Core.Webhooks.IWebhookHandler>()).Service);

        SignedIn();
        var sender = provider.GetServices<IChatSender>().Single();
        Assert.Equal("Kick", sender.Platform);
        Assert.True(sender.CanSend);
        await sender.SendAsync("via hub");
        Assert.Equal("via hub", Body(_http.Requests.Last(r => r.Path == "/public/v1/chat")).GetProperty("content").GetString());
    }

    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    private sealed class FakeReceiver : IOAuthReceiver
    {
        public Func<string, OAuthCallbackResult> Respond { get; set; } = _ => new OAuthCallbackResult(null, "no response configured");
        public string? LastState { get; private set; }

        public Task<OAuthCallbackResult> WaitAsync(string expectedState, CancellationToken ct)
        {
            LastState = expectedState;
            return Task.FromResult(Respond(expectedState));
        }

        public void Dispose()
        {
        }
    }
}
