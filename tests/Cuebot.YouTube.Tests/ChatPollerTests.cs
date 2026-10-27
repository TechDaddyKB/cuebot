using System.Collections.Concurrent;
using System.Net;
using Cuebot.Core.Events;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Tests.Support;
using static Cuebot.YouTube.Tests.Support.ChatJson;

namespace Cuebot.YouTube.Tests;

public sealed class ChatPollerTests
{
    private readonly ConcurrentQueue<HttpResponseMessage> _answers = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly CancellationTokenSource _done = new();

    private TestApi Create(out ChatPoller poller, int minimumSeconds = 0)
    {
        var api = new TestApi(_ =>
        {
            if (_answers.TryDequeue(out var answer)) return answer;
            _done.Cancel();   // script finished: stop the poller at its next wait
            return FakeHttpHandler.Json(Page("end", 0));
        });
        var ctx = new ChatContext("b1", "chat1", new ChatterDirectory());
        poller = new ChatPoller(api.Api, api.Broadcaster, ctx, _published.Enqueue, api.Timings, api.Time, () => minimumSeconds, api.Log);
        return api;
    }

    private void Answer(string json) => _answers.Enqueue(FakeHttpHandler.Json(json));

    private string[] Messages() => _published.Where(e => e.Type == YouTubeEventTypes.ChatMessage).Select(e => (string)e.Args["message"]!).ToArray();

    [Fact]
    public async Task First_page_seeds_without_replaying_history_then_new_items_publish()
    {
        Answer(Page("p2", 1000, Text("old1", "history 1"), Text("old2", "history 2")));
        Answer(Page("p3", 1000, Text("new1", "fresh")));
        using var api = Create(out var poller);

        var reason = await poller.RunAsync(_done.Token);

        Assert.Equal(ChatEndReason.Cancelled, reason);
        Assert.Equal(["fresh"], Messages());
    }

    [Fact]
    public async Task Follows_next_page_tokens_and_requests_the_expected_parts()
    {
        Answer(Page("p2", 1000));
        Answer(Page("p3", 1000));
        using var api = Create(out var poller);

        await poller.RunAsync(_done.Token);

        var requests = api.Http.To("/liveChat/messages");
        Assert.Equal("", requests[0].Query("pageToken"));
        Assert.Equal("p2", requests[1].Query("pageToken"));
        Assert.Equal("p3", requests[2].Query("pageToken"));
        Assert.All(requests, r =>
        {
            Assert.Equal("chat1", r.Query("liveChatId"));
            Assert.Equal("id,snippet,authorDetails", r.Query("part"));
            Assert.Equal("2000", r.Query("maxResults"));
            Assert.Equal("BROADCASTER-TOKEN", r.Bearer);
        });
    }

    [Fact]
    public async Task Honors_polling_interval_but_never_polls_faster_than_the_minimum()
    {
        Answer(Page("p2", 12_000));
        Answer(Page("p3", 500));
        using var api = Create(out var poller, minimumSeconds: 5);

        await poller.RunAsync(_done.Token);

        var delays = ((FakeTimeProvider)api.Time).Delays;
        Assert.Equal(TimeSpan.FromSeconds(12), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), delays[1]);
    }

    [Fact]
    public async Task Duplicate_items_across_pages_publish_once()
    {
        Answer(Page("p2", 0));
        Answer(Page("p3", 0, Text("m1", "one"), Text("m2", "two")));
        Answer(Page("p4", 0, Text("m2", "two"), Text("m3", "three")));
        using var api = Create(out var poller);

        await poller.RunAsync(_done.Token);

        Assert.Equal(["one", "two", "three"], Messages());
    }

    [Fact]
    public async Task Offline_at_or_chat_ended_event_ends_the_chat()
    {
        Answer(Page("p2", 0));
        Answer("""{"nextPageToken":"p3","pollingIntervalMillis":0,"offlineAt":"2026-03-10T19:00:00Z","items":[]}""");
        using var api = Create(out var poller);
        Assert.Equal(ChatEndReason.ChatEnded, await poller.RunAsync(_done.Token));

        Answer(Page("q2", 0));
        Answer(Page("q3", 0, Item("e1", "chatEndedEvent", "")));
        using var api2 = Create(out var poller2);
        Assert.Equal(ChatEndReason.ChatEnded, await poller2.RunAsync(_done.Token));
    }

    [Theory]
    [InlineData("liveChatEnded", "ChatEnded")]
    [InlineData("liveChatNotFound", "Unavailable")]
    [InlineData("liveChatDisabled", "Unavailable")]
    public async Task Chat_gone_errors_stop_polling(string reason, string expected)
    {
        _answers.Enqueue(FakeHttpHandler.Error(HttpStatusCode.Forbidden, reason));
        using var api = Create(out var poller);
        Assert.Equal(Enum.Parse<ChatEndReason>(expected), await poller.RunAsync(_done.Token));
        Assert.Single(api.Http.Requests);
    }

    [Fact]
    public async Task Transient_errors_back_off_and_keep_the_page_token()
    {
        Answer(Page("p2", 0));
        _answers.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _answers.Enqueue(FakeHttpHandler.Error(HttpStatusCode.Forbidden, "rateLimitExceeded"));
        Answer(Page("p3", 0, Text("m1", "after errors")));
        using var api = Create(out var poller);

        await poller.RunAsync(_done.Token);

        Assert.Equal(["after errors"], Messages());
        var requests = api.Http.To("/liveChat/messages");
        Assert.Equal("p2", requests[1].Query("pageToken"));
        Assert.Equal("p2", requests[3].Query("pageToken"));
        var delays = ((FakeTimeProvider)api.Time).Delays;
        Assert.Contains(TimeSpan.FromMilliseconds(10), delays);
        Assert.Contains(TimeSpan.FromMilliseconds(20), delays);
    }

    [Fact]
    public async Task Quota_exceeded_pauses_until_the_pacific_midnight_reset()
    {
        Answer(Page("p2", 0));
        _answers.Enqueue(FakeHttpHandler.Error(HttpStatusCode.Forbidden, "quotaExceeded", "The request cannot be completed because you have exceeded your quota."));
        Answer(Page("p3", 0, Text("m1", "next day")));
        using var api = Create(out var poller);
        api.Settings.Value.Broadcaster.AccessTokenExpiresAt = api.Time.GetUtcNow().AddDays(2);   // outlives the wait
        var exhaustedAt = DateTimeOffset.MinValue;
        api.Quota.Exhausted += until => exhaustedAt = until;
        var reset = QuotaTracker.NextResetAfter(api.Time.GetUtcNow());

        await poller.RunAsync(_done.Token);

        Assert.Equal(reset, exhaustedAt);
        Assert.Equal(["next day"], Messages());
        Assert.True(api.Time.GetUtcNow() >= reset);
        Assert.Null(api.Quota.ExhaustedUntil);
        Assert.Contains(((FakeTimeProvider)api.Time).Delays, d => d > TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_once_and_the_poll_retried()
    {
        using var api = Create(out var poller);
        api.Settings.Value.Broadcaster.AccessTokenExpiresAt = api.Time.GetUtcNow().AddSeconds(30);   // inside the refresh margin
        _answers.Enqueue(FakeHttpHandler.Json("""{"access_token":"FRESH","expires_in":3600}"""));
        Answer(Page("p2", 0));

        await poller.RunAsync(_done.Token);

        var token = api.Http.To("/token").Single();
        Assert.Equal("RT-BROADCASTER-TOKEN", token.Form("refresh_token"));
        Assert.All(api.Http.To("/liveChat/messages"), r => Assert.Equal("FRESH", r.Bearer));
        Assert.Equal("RT-BROADCASTER-TOKEN", api.Settings.Value.Broadcaster.RefreshToken);
    }

    [Fact]
    public async Task Unauthorized_response_refreshes_and_retries_the_same_request()
    {
        _answers.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        _answers.Enqueue(FakeHttpHandler.Json("""{"access_token":"FRESH","expires_in":3600}"""));
        Answer(Page("p2", 0));
        using var api = Create(out var poller);

        await poller.RunAsync(_done.Token);

        var requests = api.Http.Requests;
        Assert.Equal("BROADCASTER-TOKEN", requests[0].Bearer);
        Assert.Equal("/token", requests[1].Path);
        Assert.Equal("FRESH", requests[2].Bearer);
        Assert.Equal(requests[0].Uri, requests[2].Uri);
    }

    [Fact]
    public async Task Each_poll_spends_quota()
    {
        Answer(Page("p2", 0));
        Answer(Page("p3", 0));
        using var api = Create(out var poller);

        await poller.RunAsync(_done.Token);

        Assert.Equal(api.Http.To("/liveChat/messages").Count * QuotaCosts.LiveChatMessagesList, api.Quota.UsedToday);
    }
}
