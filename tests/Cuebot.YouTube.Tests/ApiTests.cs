using System.Net;
using System.Text.Json;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Tests.Support;

namespace Cuebot.YouTube.Tests;

/// <summary>Request formats of the YouTube Data API calls, error mapping and quota accounting.</summary>
public sealed class ApiTests
{
    private string _response = "{}";

    private TestApi Create() => new(_ => FakeHttpHandler.Json(_response));

    [Fact]
    public async Task Insert_chat_message_posts_a_text_message_event()
    {
        _response = """{"id":"NEWID"}""";
        using var t = Create();
        t.SignIn(t.Settings.Value.Bot, "BOT-TOKEN", ChatJson.Bob, "Bot");

        var id = await t.Api.InsertChatMessageAsync(t.Bot, "chat1", "hello", default);

        Assert.Equal("NEWID", id);
        var r = Assert.Single(t.Http.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("/youtube/v3/liveChat/messages", r.Path);
        Assert.Equal("snippet", r.Query("part"));
        var s = r.Json.GetProperty("snippet");
        Assert.Equal("chat1", s.GetProperty("liveChatId").GetString());
        Assert.Equal("textMessageEvent", s.GetProperty("type").GetString());
        Assert.Equal("hello", s.GetProperty("textMessageDetails").GetProperty("messageText").GetString());
        Assert.Equal("BOT-TOKEN", r.Bearer);
        Assert.Equal(QuotaCosts.LiveChatMessagesInsert, t.Quota.UsedToday);
    }

    [Fact]
    public async Task Delete_message_and_moderation_requests()
    {
        _response = """{"id":"BAN1"}""";
        using var t = Create();

        await t.Api.DeleteChatMessageAsync(t.Broadcaster, "m1", default);
        var permanent = await t.Api.BanAsync(t.Broadcaster, "chat1", "UCx", null, default);
        await t.Api.BanAsync(t.Broadcaster, "chat1", "UCy", 300, default);
        await t.Api.UnbanAsync(t.Broadcaster, "BAN1", default);
        await t.Api.AddModeratorAsync(t.Broadcaster, "chat1", "UCz", default);
        await t.Api.RemoveModeratorAsync(t.Broadcaster, "MOD1", default);

        var r = t.Http.Requests;
        Assert.Equal((HttpMethod.Delete, "/youtube/v3/liveChat/messages", "m1"), (r[0].Method, r[0].Path, r[0].Query("id")));

        Assert.Equal("BAN1", permanent);
        Assert.Equal("/youtube/v3/liveChat/bans", r[1].Path);
        var ban = r[1].Json.GetProperty("snippet");
        Assert.Equal("permanent", ban.GetProperty("type").GetString());
        Assert.Equal("UCx", ban.GetProperty("bannedUserDetails").GetProperty("channelId").GetString());
        Assert.False(ban.TryGetProperty("banDurationSeconds", out _));

        var timeout = r[2].Json.GetProperty("snippet");
        Assert.Equal("temporary", timeout.GetProperty("type").GetString());
        Assert.Equal(300, timeout.GetProperty("banDurationSeconds").GetInt32());

        Assert.Equal((HttpMethod.Delete, "/youtube/v3/liveChat/bans", "BAN1"), (r[3].Method, r[3].Path, r[3].Query("id")));

        Assert.Equal("/youtube/v3/liveChat/moderators", r[4].Path);
        var mod = r[4].Json.GetProperty("snippet");
        Assert.Equal("chat1", mod.GetProperty("liveChatId").GetString());
        Assert.Equal("UCz", mod.GetProperty("moderatorDetails").GetProperty("channelId").GetString());
        Assert.Equal((HttpMethod.Delete, "MOD1"), (r[5].Method, r[5].Query("id")));
        Assert.All(r, x => Assert.Equal("BROADCASTER-TOKEN", x.Bearer));
    }

    [Fact]
    public async Task List_moderators_follows_pages()
    {
        var pages = new Queue<string>([
            """{"nextPageToken":"n2","items":[{"id":"MOD-A","snippet":{"moderatorDetails":{"channelId":"UCa"}}}]}""",
            """{"items":[{"id":"MOD-B","snippet":{"moderatorDetails":{"channelId":"UCb"}}}]}""",
        ]);
        using var t = new TestApi(_ => FakeHttpHandler.Json(pages.Dequeue()));

        var mods = await t.Api.ListModeratorsAsync(t.Broadcaster, "chat1", default);

        Assert.Equal("MOD-A", mods["UCa"]);
        Assert.Equal("MOD-B", mods["UCb"]);
        Assert.Equal("n2", t.Http.Requests[1].Query("pageToken"));
    }

    [Fact]
    public async Task Update_video_reads_then_writes_the_whole_snippet_and_status()
    {
        var responses = new Queue<string>([
            """
            {"items":[{"id":"v1","snippet":{"title":"Old","description":"Old desc","categoryId":"20","tags":["a","b"],"defaultLanguage":"en"},
                       "status":{"privacyStatus":"public","embeddable":true,"license":"youtube","publicStatsViewable":false,"selfDeclaredMadeForKids":false}}]}
            """,
            """{"id":"v1","snippet":{"title":"New","description":"Old desc","categoryId":"20"},"status":{"privacyStatus":"unlisted"}}""",
        ]);
        using var t = new TestApi(_ => FakeHttpHandler.Json(responses.Dequeue()));

        var video = await t.Api.UpdateVideoAsync(t.Broadcaster, "v1", new VideoChange(Title: "New", PrivacyStatus: "unlisted"), default);

        Assert.Equal("New", video.Title);
        var (get, put) = (t.Http.Requests[0], t.Http.Requests[1]);
        Assert.Equal((HttpMethod.Get, "snippet,status", "v1"), (get.Method, get.Query("part"), get.Query("id")));
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("/youtube/v3/videos", put.Path);
        Assert.Equal("snippet,status", put.Query("part"));
        var body = put.Json;
        Assert.Equal("v1", body.GetProperty("id").GetString());
        var snippet = body.GetProperty("snippet");
        Assert.Equal("New", snippet.GetProperty("title").GetString());
        Assert.Equal("Old desc", snippet.GetProperty("description").GetString());
        Assert.Equal("20", snippet.GetProperty("categoryId").GetString());
        Assert.Equal(["a", "b"], snippet.GetProperty("tags").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal("en", snippet.GetProperty("defaultLanguage").GetString());
        var status = body.GetProperty("status");
        Assert.Equal("unlisted", status.GetProperty("privacyStatus").GetString());
        Assert.True(status.GetProperty("embeddable").GetBoolean());
        Assert.False(status.GetProperty("publicStatsViewable").GetBoolean());
        Assert.Equal("youtube", status.GetProperty("license").GetString());
        Assert.Equal(QuotaCosts.VideosList + QuotaCosts.VideosUpdate, t.Quota.UsedToday);
    }

    [Fact]
    public async Task Update_without_privacy_writes_only_the_snippet()
    {
        var responses = new Queue<string>([
            """{"items":[{"id":"v1","snippet":{"title":"T","categoryId":"22"},"status":{"privacyStatus":"public"}}]}""",
            """{"id":"v1","snippet":{"title":"T","description":"D"}}""",
        ]);
        using var t = new TestApi(_ => FakeHttpHandler.Json(responses.Dequeue()));

        await t.Api.UpdateVideoAsync(t.Broadcaster, "v1", new VideoChange(Description: "D"), default);

        var put = t.Http.Requests[1];
        Assert.Equal("snippet", put.Query("part"));
        Assert.False(put.Json.TryGetProperty("status", out _));
        Assert.Equal("T", put.Json.GetProperty("snippet").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Polls_create_and_transition()
    {
        _response = """{"id":"POLLMSG"}""";
        using var t = Create();

        var id = await t.Api.CreatePollAsync(t.Broadcaster, "chat1", "Pick", ["A", "B"], default);
        await t.Api.EndPollAsync(t.Broadcaster, id, default);

        var create = t.Http.Requests[0].Json.GetProperty("snippet");
        Assert.Equal("pollEvent", create.GetProperty("type").GetString());
        var meta = create.GetProperty("pollDetails").GetProperty("metadata");
        Assert.Equal("Pick", meta.GetProperty("questionText").GetString());
        Assert.Equal(["A", "B"], meta.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("optionText").GetString()!));
        var end = t.Http.Requests[1];
        Assert.Equal("/youtube/v3/liveChat/messages/transition", end.Path);
        Assert.Equal("POLLMSG", end.Query("id"));
        Assert.Equal("closed", end.Query("status"));
    }

    [Fact]
    public async Task Broadcast_video_channel_and_subscriber_reads_parse()
    {
        using var t = new TestApi(r => FakeHttpHandler.Json(r.Path switch
        {
            "/youtube/v3/liveBroadcasts" => """
                {"items":[{"id":"b1","snippet":{"title":"Live!","description":"d","liveChatId":"chat1","channelId":"UCo",
                  "actualStartTime":"2026-03-10T17:00:00Z","thumbnails":{"high":{"url":"https://i.test/h.jpg"}}},
                  "status":{"lifeCycleStatus":"live","privacyStatus":"public"}}]}
                """,
            "/youtube/v3/videos" => """
                {"items":[{"id":"b1","snippet":{"title":"Live!","channelTitle":"Owner","tags":["x"],"liveBroadcastContent":"live"},
                  "status":{"privacyStatus":"public"},"statistics":{"viewCount":"100","likeCount":"7","commentCount":"0"},
                  "liveStreamingDetails":{"concurrentViewers":"42","activeLiveChatId":"chat1","actualStartTime":"2026-03-10T17:00:00Z"}}]}
                """,
            "/youtube/v3/channels" => """
                {"items":[{"id":"UCo","snippet":{"title":"Owner","customUrl":"@owner","thumbnails":{"default":{"url":"https://i.test/d.jpg"}}},
                  "statistics":{"subscriberCount":"1234","videoCount":"5","viewCount":"999","hiddenSubscriberCount":false}}]}
                """,
            _ => """
                {"items":[{"id":"sub1","snippet":{"publishedAt":"2026-03-10T17:30:00Z"},
                  "subscriberSnippet":{"title":"Fan","channelId":"UCfan","thumbnails":{"default":{"url":"https://i.test/f.jpg"}}}}]}
                """,
        }));

        var broadcast = Assert.Single(await t.Api.ListBroadcastsAsync(t.Broadcaster, "active", default));
        Assert.Equal(("b1", "Live!", "chat1", "live", "public"), (broadcast.Id, broadcast.Title, broadcast.LiveChatId, broadcast.LifeCycleStatus, broadcast.PrivacyStatus));
        Assert.Equal("https://i.test/h.jpg", broadcast.ThumbnailUrl);
        Assert.Equal("active", t.Http.Requests[0].Query("broadcastStatus"));
        Assert.Equal("id,snippet,status", t.Http.Requests[0].Query("part"));

        var video = Assert.Single(await t.Api.GetVideosAsync(t.Broadcaster, ["b1"], default));
        Assert.Equal(42, video.Statistics.ConcurrentViewers);
        Assert.Equal(100, video.Statistics.ViewCount);
        Assert.Equal(["x"], video.Tags);

        var channel = (await t.Api.GetChannelByHandleAsync(t.Broadcaster, "owner", default))!;
        Assert.Equal("@owner", t.Http.Requests[2].Query("forHandle"));
        Assert.Equal((1234L, "https://www.youtube.com/@owner"), (channel.SubscriberCount, channel.Url));

        var sub = Assert.Single(await t.Api.ListRecentSubscribersAsync(t.Broadcaster, default));
        Assert.Equal(("UCfan", "Fan"), (sub.ChannelId, sub.Title));
        Assert.Equal("true", t.Http.Requests[3].Query("myRecentSubscribers"));

        Assert.Equal(QuotaCosts.LiveBroadcastsList + QuotaCosts.VideosList + QuotaCosts.ChannelsList + QuotaCosts.SubscriptionsList,
            t.Quota.UsedToday);
    }

    [Fact]
    public async Task Quota_exceeded_marks_the_tracker_exhausted_until_the_reset()
    {
        using var t = new TestApi(_ => FakeHttpHandler.Error(HttpStatusCode.Forbidden, "quotaExceeded"));
        DateTimeOffset? reported = null;
        t.Quota.Exhausted += until => reported = until;

        var ex = await Assert.ThrowsAsync<YouTubeApiException>(() => t.Api.ListBroadcastsAsync(t.Broadcaster, "active", default));

        Assert.True(ex.IsQuotaExceeded);
        Assert.Contains("quota", ex.Message);
        Assert.Equal(t.Quota.NextReset, t.Quota.ExhaustedUntil);
        Assert.Equal(t.Quota.NextReset, reported);

        // A second rejection does not report again.
        reported = null;
        await Assert.ThrowsAsync<YouTubeApiException>(() => t.Api.ListBroadcastsAsync(t.Broadcaster, "active", default));
        Assert.Null(reported);
    }

    [Fact]
    public void Error_reasons_are_parsed()
    {
        var e = YouTubeApiClient.ParseError(JsonSerializer.Deserialize<JsonElement>(
            """{"error":{"code":403,"message":"The live chat is no longer live.","errors":[{"domain":"youtube.liveChat","reason":"liveChatEnded"}]}}"""), 403);
        Assert.Equal("liveChatEnded", e.Reason);
        Assert.True(e.IsChatGone);
        Assert.Equal(403, e.StatusCode);

        var unknown = YouTubeApiClient.ParseError(default, 500);
        Assert.Contains("HTTP 500", unknown.Message);
    }
}
