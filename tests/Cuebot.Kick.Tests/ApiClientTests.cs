using System.Net;
using System.Text.Json;
using Cuebot.Kick.Api;
using Cuebot.Kick.Auth;
using Cuebot.Kick.Tests.Support;

namespace Cuebot.Kick.Tests;

/// <summary>Request formats against docs.kick.com, with an in-memory HTTP handler.</summary>
public sealed class ApiClientTests
{
    private static readonly KickEndpoints Endpoints = new(new Uri("https://id.test/"), new Uri("https://api.test/"),
        new Uri("https://web.test/"), new Uri("wss://pusher.test/app/key"));

    private readonly FakeTokens _tokens = new();

    private (KickApiClient Api, FakeHttpHandler Http) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var http = new FakeHttpHandler(respond);
        return (new KickApiClient(new HttpClient(http), Endpoints, _tokens), http);
    }

    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    [Fact]
    public async Task Send_chat_as_user_posts_content_type_and_broadcaster()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""{"data":{"is_sent":true,"message_id":"m-1"},"message":"OK"}"""));
        var result = await api.SendChatAsync(KickAccount.Broadcaster, "hi chat", 100, false, null, default);

        var r = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.test/public/v1/chat", r.Uri.ToString());
        Assert.Equal("Bearer broadcaster-token", r.Authorization);
        Assert.Equal("application/json", r.ContentType);
        var body = Body(r);
        Assert.Equal("hi chat", body.GetProperty("content").GetString());
        Assert.Equal("user", body.GetProperty("type").GetString());
        Assert.Equal(100, body.GetProperty("broadcaster_user_id").GetInt64());
        Assert.False(body.TryGetProperty("reply_to_message_id", out _));
        Assert.Equal(new KickChatSendResult("m-1", true), result);
    }

    [Fact]
    public async Task Reply_from_the_bot_account_uses_the_bot_token_and_reply_id()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""{"data":{"is_sent":true,"message_id":"m-2"}}"""));
        await api.SendChatAsync(KickAccount.Bot, "pong", 100, false, "parent-id", default);
        var r = Assert.Single(http.Requests);
        Assert.Equal("Bearer bot-token", r.Authorization);
        Assert.Equal("parent-id", Body(r).GetProperty("reply_to_message_id").GetString());
    }

    [Fact]
    public async Task Bot_type_message_omits_the_broadcaster()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""{"data":{"is_sent":true,"message_id":"m-3"}}"""));
        await api.SendChatAsync(KickAccount.Broadcaster, "x", 100, true, null, default);
        var body = Body(Assert.Single(http.Requests));
        Assert.Equal("bot", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("broadcaster_user_id", out _));
    }

    [Fact]
    public async Task Delete_message_uses_the_message_path()
    {
        var (api, http) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await api.DeleteChatMessageAsync(KickAccount.Broadcaster, "abc-123", default);
        var r = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Delete, r.Method);
        Assert.Equal("/public/v1/chat/abc-123", r.Path);
    }

    [Fact]
    public async Task Update_channel_patches_only_the_given_fields()
    {
        var (api, http) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await api.UpdateChannelAsync("New title", null, default);
        await api.UpdateChannelAsync(null, 15, default);
        Assert.All(http.Requests, r => Assert.Equal(HttpMethod.Patch, r.Method));
        Assert.Equal("""{"stream_title":"New title"}""", http.Requests[0].Body);
        Assert.Equal("""{"category_id":15}""", http.Requests[1].Body);
        await Assert.ThrowsAsync<ArgumentException>(() => api.UpdateChannelAsync(" ", null, default));
    }

    [Fact]
    public async Task Timeout_posts_a_duration_and_ban_does_not()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""{"data":{},"message":"OK"}"""));
        await api.BanAsync(KickAccount.Broadcaster, 100, 666, 10, "spam", default);
        await api.BanAsync(KickAccount.Broadcaster, 100, 666, null, null, default);
        await api.BanAsync(KickAccount.Broadcaster, 100, 666, 99999, null, default);

        Assert.All(http.Requests, r => Assert.Equal("/public/v1/moderation/bans", r.Path));
        var timeout = Body(http.Requests[0]);
        Assert.Equal(100, timeout.GetProperty("broadcaster_user_id").GetInt64());
        Assert.Equal(666, timeout.GetProperty("user_id").GetInt64());
        Assert.Equal(10, timeout.GetProperty("duration").GetInt32());
        Assert.Equal("spam", timeout.GetProperty("reason").GetString());
        Assert.False(Body(http.Requests[1]).TryGetProperty("duration", out _));
        Assert.Equal(10080, Body(http.Requests[2]).GetProperty("duration").GetInt32());
    }

    [Fact]
    public async Task Unban_is_a_delete_with_a_json_body()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""{"data":{},"message":"OK"}"""));
        await api.UnbanAsync(KickAccount.Broadcaster, 100, 666, default);
        var r = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Delete, r.Method);
        Assert.Equal(666, Body(r).GetProperty("user_id").GetInt64());
    }

    [Fact]
    public async Task Channel_response_is_parsed()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""
            {"data":[{"active_subscribers_count":12,"banner_picture":"","broadcaster_user_id":100,"canceled_subscribers_count":0,
              "category":{"id":15,"name":"Just Chatting","thumbnail":"t"},"channel_description":"desc","slug":"cuebotdev",
              "stream":{"custom_tags":["english","chill"],"is_live":true,"is_mature":false,"key":"","language":"en",
                        "start_time":"2025-01-14T16:00:00Z","thumbnail":"","url":"","viewer_count":42},
              "stream_title":"Testing Cuebot"}],"message":"OK"}
            """));
        var channel = await api.GetChannelAsync(null, default);

        Assert.Equal("/public/v1/channels", Assert.Single(http.Requests).Path);
        Assert.NotNull(channel);
        Assert.Equal(100, channel.BroadcasterUserId);
        Assert.Equal("cuebotdev", channel.Slug);
        Assert.Equal("Testing Cuebot", channel.Title);
        Assert.Equal(new KickCategory(15, "Just Chatting", "t"), channel.Category);
        Assert.True(channel.IsLive);
        Assert.Equal(42, channel.ViewerCount);
        Assert.Equal(["english", "chill"], channel.Tags);
        Assert.Equal(new DateTimeOffset(2025, 1, 14, 16, 0, 0, TimeSpan.Zero), channel.StartTime);
    }

    [Fact]
    public async Task Offline_channel_with_empty_start_time_has_no_start()
    {
        var (api, _) = Create(_ => FakeHttpHandler.Json("""
            {"data":[{"broadcaster_user_id":100,"slug":"x","stream_title":"","category":{"id":0,"name":"","thumbnail":""},
              "stream":{"is_live":false,"viewer_count":0,"start_time":"0001-01-01T00:00:00Z"}}]}
            """));
        var channel = await api.GetChannelAsync(null, default);
        Assert.False(channel!.IsLive);
        Assert.Null(channel.StartTime);
        Assert.Null(channel.Category);
    }

    [Fact]
    public async Task Users_and_categories_build_array_queries()
    {
        var (api, http) = Create(r => r.Path.Contains("users")
            ? FakeHttpHandler.Json("""{"data":[{"user_id":1,"name":"A","email":"","profile_picture":"p1"},{"user_id":2,"name":"B","profile_picture":""}]}""")
            : FakeHttpHandler.Json("""{"data":[{"id":15,"name":"Just Chatting","thumbnail":""}]}"""));
        var users = await api.GetUsersAsync([1, 2], default);
        var categories = await api.SearchCategoriesAsync("just chat", default);

        Assert.Equal("?id=1&id=2", http.Requests[0].Query);
        Assert.Equal(new KickUser(1, "A", "p1"), users[0]);
        Assert.Equal("/public/v2/categories", http.Requests[1].Path);
        Assert.Equal("?limit=1000", http.Requests[1].Query);
        Assert.Equal("Just Chatting", Assert.Single(categories).Name);
    }

    [Fact]
    public async Task Unauthorized_response_refreshes_once_and_retries()
    {
        var calls = 0;
        var (api, http) = Create(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : FakeHttpHandler.Json("""{"data":[{"user_id":100,"name":"CuebotDev"}]}"""));
        _tokens.OnRefresh = () => _tokens.Broadcaster = "refreshed-token";

        var user = await api.GetCurrentUserAsync(KickAccount.Broadcaster, default);

        Assert.Equal("CuebotDev", user.Name);
        Assert.Equal(["Bearer broadcaster-token", "Bearer refreshed-token"], http.Requests.Select(r => r.Authorization));
    }

    [Fact]
    public async Task Forbidden_and_rate_limited_errors_are_explained()
    {
        var (api, _) = Create(r => r.Method == HttpMethod.Post
            ? FakeHttpHandler.Json("""{"message":"missing scope"}""", HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var forbidden = await Assert.ThrowsAsync<KickApiException>(() => api.SendChatAsync(KickAccount.Broadcaster, "x", 1, false, null, default));
        Assert.Equal(403, forbidden.Status);
        Assert.Contains("missing scope", forbidden.Message);
        var limited = await Assert.ThrowsAsync<KickApiException>(() => api.GetChannelAsync(null, default));
        Assert.Contains("rate limit", limited.Message);
    }

    [Fact]
    public async Task Webhook_key_and_subscriptions()
    {
        var (api, http) = Create(r => r.Path switch
        {
            "/public/v1/public-key" => FakeHttpHandler.Json("""{"data":{"public_key":"-----BEGIN PUBLIC KEY-----\nAAA\n-----END PUBLIC KEY-----"},"message":"OK"}"""),
            _ => FakeHttpHandler.Json("""{"data":[{"name":"chat.message.sent","version":1,"subscription_id":"s1","error":""},{"name":"kicks.gifted","version":1,"error":"not allowed"}]}"""),
        });
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", await api.GetWebhookPublicKeyAsync(default));
        Assert.Null(http.Requests[0].Authorization);

        var errors = await api.SubscribeWebhookEventsAsync([("chat.message.sent", 1), ("kicks.gifted", 1)], default);
        Assert.Equal(["kicks.gifted: not allowed"], errors);
        var body = Body(http.Requests[1]);
        Assert.Equal("webhook", body.GetProperty("method").GetString());
        Assert.Equal("chat.message.sent", body.GetProperty("events")[0].GetProperty("name").GetString());
        Assert.Equal(1, body.GetProperty("events")[0].GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Website_lookup_reads_channel_and_chatroom_ids()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"id":1234,"user_id":100,"slug":"cuebotdev","chatroom":{"id":5678,"chatable_id":1234},"user":{"id":100,"username":"CuebotDev"}}"""));
        var web = new KickWebClient(new HttpClient(http), Endpoints);
        var ids = await web.GetChannelIdsAsync("@CuebotDev", default);
        Assert.Equal(new KickChannelIds(1234, 5678, 100, "cuebotdev"), ids);
        var r = Assert.Single(http.Requests);
        Assert.Equal("/api/v2/channels/cuebotdev", r.Path);
        Assert.Contains("Mozilla", r.Headers["User-Agent"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "refused")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task Website_lookup_failures_are_explained(HttpStatusCode status, string expected)
    {
        var web = new KickWebClient(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(status))), Endpoints);
        var ex = await Assert.ThrowsAsync<KickApiException>(() => web.GetChannelIdsAsync("x", default));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Website_lookup_answered_with_a_challenge_page_is_explained()
    {
        var web = new KickWebClient(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Just a moment...</html>"),
        })), Endpoints);
        var ex = await Assert.ThrowsAsync<KickApiException>(() => web.GetChannelIdsAsync("x", default));
        Assert.Contains("bot protection", ex.Message);
    }

    [Theory]
    [InlineData("CuebotDev", "cuebotdev")]
    [InlineData("https://kick.com/CuebotDev/", "cuebotdev")]
    [InlineData(" @someone ", "someone")]
    public void Slugs_are_normalized(string input, string expected) => Assert.Equal(expected, KickWebClient.NormalizeSlug(input));

    // ---- endpoints added from the KickDevDocs review (2026-11) ----

    [Fact]
    public async Task Category_search_reads_every_v2_page_once_and_ranks_names()
    {
        var (api, http) = Create(r => r.Query.Contains("cursor=")
            ? FakeHttpHandler.Json("""{"data":[{"id":3,"name":"Rust","tags":[],"thumbnail":""},{"id":4,"name":"Just Chatting","tags":["IRL"],"thumbnail":"t"}],"pagination":{"next_cursor":""}}""")
            : FakeHttpHandler.Json("""{"data":[{"id":1,"name":"Fortnite","tags":[],"thumbnail":""},{"id":2,"name":"Chatting Games","tags":[],"thumbnail":""}],"pagination":{"next_cursor":"c2"}}"""));

        var first = await api.SearchCategoriesAsync("chatting", default);
        var again = await api.SearchCategoriesAsync("rust", default);

        Assert.Equal(["?limit=1000", "?limit=1000&cursor=c2"], http.Requests.Select(r => r.Query));   // read once, then cached
        Assert.Equal(["Chatting Games", "Just Chatting"], first.Select(c => c.Name));   // starts-with before contains
        Assert.Equal(new KickCategory(3, "Rust", ""), Assert.Single(again));
        Assert.Equal(["Just Chatting"], KickApiClient.RankCategories(first, "JUST CHATTING").Select(c => c.Name));
    }

    [Fact]
    public async Task Category_search_falls_back_to_the_v1_keyword_search()
    {
        var (api, http) = Create(r => r.Path.StartsWith("/public/v2/", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""{"message":"Not found"}""", HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("""{"data":[{"id":15,"name":"Just Chatting","thumbnail":""}]}"""));
        var categories = await api.SearchCategoriesAsync("just chat", default);
        Assert.Equal("?q=just%20chat", http.Requests[1].Query);
        Assert.Equal("Just Chatting", Assert.Single(categories).Name);
    }

    [Fact]
    public async Task Tags_are_patched_as_custom_tags()
    {
        var (api, http) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await api.SetTagsAsync([" chill ", "English", "english", ""], default);
        var r = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Patch, r.Method);
        Assert.Equal("""{"custom_tags":["chill","English"]}""", r.Body);
    }

    [Fact]
    public async Task Channel_has_subscriber_counts_and_banner()
    {
        var (api, _) = Create(_ => FakeHttpHandler.Json("""
            {"data":[{"active_subscribers_count":12,"active_gifted_subscribers_count":5,"canceled_subscribers_count":2,
              "banner_picture":"https://kick.test/banner.png","broadcaster_user_id":100,"slug":"cuebotdev","stream_title":"",
              "stream":{"is_live":false,"key":"sk_live_secret","url":"rtmps://ingest.kick.test/app"}}]}
            """));
        var channel = await api.GetChannelAsync(null, default);
        Assert.Equal(12, channel!.ActiveSubscribers);
        Assert.Equal(5, channel.ActiveGiftedSubscribers);
        Assert.Equal(2, channel.CanceledSubscribers);
        Assert.Equal("https://kick.test/banner.png", channel.BannerPicture);
        Assert.DoesNotContain("sk_live_secret", channel.ToString(), StringComparison.Ordinal);   // the key never rides on the channel
    }

    [Fact]
    public async Task Stream_credentials_come_from_the_own_channel_and_hide_the_key()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""
            {"data":[{"broadcaster_user_id":100,"slug":"x","stream":{"key":"sk_live_secret","url":"rtmps://ingest.kick.test/app"}}]}
            """));
        var credentials = await api.GetStreamCredentialsAsync(default);
        Assert.Equal("/public/v1/channels", Assert.Single(http.Requests).Path);
        Assert.Equal("rtmps://ingest.kick.test/app", credentials!.Url);
        Assert.Equal("sk_live_secret", credentials.Key);
        Assert.DoesNotContain("sk_live_secret", credentials.ToString(), StringComparison.Ordinal);

        var (empty, _) = Create(_ => FakeHttpHandler.Json("""{"data":[{"stream":{"key":"","url":""}}]}"""));
        Assert.Null(await empty.GetStreamCredentialsAsync(default));
    }

    [Fact]
    public async Task Livestreams_by_user_and_by_category_are_parsed()
    {
        const string stream = """
            {"broadcaster_user":{"id":7,"username":"Pal","profile_picture":"p"},"category":{"id":15,"name":"Just Chatting","thumbnail":""},
             "channel":{"slug":"pal"},"has_mature_content":true,"id":99,"language_code":"en","started_at":"2026-11-10T20:00:00Z",
             "tags":["chill"],"thumbnail":"th","title":"Hi","viewer_count":321}
            """;
        var (api, http) = Create(_ => FakeHttpHandler.Json("{\"data\":[" + stream + "],\"pagination\":{\"next_cursor\":\"n\"}}"));

        var byUser = await api.GetLivestreamsForUsersAsync([7, 8, 7], default);
        var (byCategory, next) = await api.GetLivestreamsAsync(15, " EN ", 500, "c", default);

        Assert.Equal("/public/v1/users/livestreams", http.Requests[0].Path);
        Assert.Equal("?user_id=7&user_id=8", http.Requests[0].Query);
        Assert.Equal("?limit=100&category_id=15&language_code=en&cursor=c", http.Requests[1].Query);
        Assert.Equal("n", next);
        var l = Assert.Single(byUser);
        Assert.Equal((7L, "Pal", "pal", "Hi", "Just Chatting", 321, "en", true), (l.BroadcasterUserId, l.UserName, l.Slug, l.Title, l.Category!.Name, l.ViewerCount, l.Language, l.IsMature));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 20, 0, 0, TimeSpan.Zero), l.StartedAt);
        Assert.Single(byCategory);
    }

    [Fact]
    public async Task Kicks_leaderboard_has_three_periods_in_rank_order()
    {
        var (api, http) = Create(_ => FakeHttpHandler.Json("""
            {"data":{"week":[{"rank":2,"user_id":2,"username":"b","gifted_amount":50},{"rank":1,"user_id":1,"username":"a","gifted_amount":100}],
                     "month":[{"rank":1,"user_id":3,"username":"c","gifted_amount":900}],"lifetime":[]}}
            """));
        var board = await api.GetKicksLeaderboardAsync(500, default);
        Assert.Equal("?top=100", Assert.Single(http.Requests).Query);
        Assert.Equal(["a", "b"], board.Week.Select(e => e.UserName));
        Assert.Equal(900, Assert.Single(board.Month).GiftedAmount);
        Assert.Empty(board.Lifetime);
    }

    [Fact]
    public async Task Ad_break_sends_a_clamped_length_and_a_request_uuid()
    {
        var id = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");
        var (api, http) = Create(r => r.Path.EndsWith("ad-break-status", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""
                {"data":{"opted_in":true,"ads_blocked":false,"auto_ads_active":true,"remaining_ad_breaks":2,"limits":{"max":3,"period_seconds":3600},
                 "ad_breaks":[{"id":"x","ad_break_id":"a1","break_duration_seconds":30,"status":"inserted","source":"public_api","created_at":"2026-11-10T20:00:00Z"}]}}
                """)
            : FakeHttpHandler.Json("""{"data":{"id":"123e4567-e89b-12d3-a456-426614174000","ad_break_id":"Xq3","remaining_ad_breaks":2}}"""));

        var ad = await api.CreateAdBreakAsync(200, id, default);
        var status = await api.GetAdStatusAsync(default);
        await api.EnrollInAdsAsync(default);

        var body = Body(http.Requests[0]);
        Assert.Equal(90, body.GetProperty("break_duration_seconds").GetInt32());
        Assert.Equal(id.ToString("D"), body.GetProperty("id").GetString());
        Assert.Equal(new KickAdBreak(id.ToString("D"), "Xq3", 2), ad);
        Assert.Equal((true, false, true, 2, 3, 3600), (status.OptedIn, status.AdsBlocked, status.AutoAdsActive, status.RemainingAdBreaks, status.MaxAdBreaks, status.LimitPeriodSeconds));
        Assert.Equal("inserted", Assert.Single(status.AdBreaks).Status);
        Assert.Equal((HttpMethod.Post, "/public/v1/ads/enroll"), (http.Requests[2].Method, http.Requests[2].Path));
    }

    [Fact]
    public async Task Event_subscriptions_are_deleted_by_id()
    {
        var (api, http) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await api.DeleteEventSubscriptionsAsync(["01A", "01B", "01A", ""], default);
        var r = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Delete, r.Method);
        Assert.Equal("?id=01A&id=01B", r.Query);
    }

    [Fact]
    public async Task Public_reads_use_an_app_token_when_nobody_is_signed_in()
    {
        _tokens.SignedIn = false;
        var calls = 0;
        var (api, http) = Create(_ => ++calls == 1
            ? FakeHttpHandler.Json("""{"message":"Unauthorized"}""", HttpStatusCode.Unauthorized)
            : FakeHttpHandler.Json("""{"data":[]}"""));
        await api.GetLivestreamsForUsersAsync([7], default);
        Assert.Equal(["Bearer app-token-1", "Bearer app-token-2"], http.Requests.Select(r => r.Authorization));
    }

    private sealed class FakeTokens : IKickTokenSource
    {
        public string Broadcaster { get; set; } = "broadcaster-token";
        public Action? OnRefresh { get; set; }
        public bool SignedIn { get; set; } = true;
        private int _appTokens;
        public bool IsSignedIn(KickAccount account) => SignedIn;

        public Task<string> GetAppTokenAsync(bool renew, CancellationToken ct)
        {
            if (renew || _appTokens == 0) _appTokens++;
            return Task.FromResult($"app-token-{_appTokens}");
        }
        public Task<string> GetAccessTokenAsync(KickAccount account, CancellationToken ct) =>
            Task.FromResult(account == KickAccount.Bot ? "bot-token" : Broadcaster);
        public Task<bool> TryRefreshAsync(KickAccount account, string rejectedToken, CancellationToken ct)
        {
            OnRefresh?.Invoke();
            return Task.FromResult(OnRefresh is not null);
        }
    }
}
