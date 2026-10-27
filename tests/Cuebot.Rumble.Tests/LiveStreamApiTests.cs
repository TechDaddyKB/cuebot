using System.Net;
using System.Text.Json;
using Cuebot.Rumble.LiveApi;
using Cuebot.Rumble.Tests.Support;

namespace Cuebot.Rumble.Tests;

public sealed class LiveStreamApiParserTests
{
    [Fact]
    public void Parses_channel_followers_subscribers_gifts_and_live_stream()
    {
        var s = LiveStreamApiParser.Parse(TestData.Read("live-stream-api-live.json"));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767272400), s.ServerTime);
        Assert.Equal("CuebotDev", s.UserName);
        Assert.Equal("Cuebot Dev Channel", s.DisplayName);
        Assert.Equal(165, s.FollowerCount);
        Assert.Equal(["Carol", "Bob", "Alice"], s.RecentFollowers.Select(f => f.UserName));
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 20, 43, 56, TimeSpan.FromHours(-4)), s.RecentFollowers[2].FollowedOn);
        Assert.Equal(new ApiSubscriber("Dave", 500, new DateTimeOffset(2026, 1, 1, 11, 3, 5, TimeSpan.Zero)), s.RecentSubscribers[0]);
        Assert.Equal(new ApiGiftedSub("Frank", 5, 2, "rumble", 284390214), Assert.Single(s.RecentGiftedSubs));

        var live = Assert.IsType<ApiLivestream>(s.LiveStream);
        Assert.Equal("4ponnu", live.Id);
        Assert.Equal("dev testing: Cuebot", live.Title);
        Assert.Equal(19, live.WatchingNow);
        Assert.Equal(["Bob", "CuebotDev"], live.RecentMessages.Select(m => m.UserName));
        Assert.Equal(["admin", "verified"], live.RecentMessages[1].Badges);
        var rant = Assert.Single(live.RecentRants);
        Assert.Equal(("Gina", "Great stream", 500L), (rant.UserName, rant.Text, rant.AmountCents));
    }

    [Fact]
    public void The_stream_key_in_the_response_is_not_kept()
    {
        var s = LiveStreamApiParser.Parse(TestData.Read("live-stream-api-live.json"));

        Assert.DoesNotContain("SECRET-STREAM-KEY", s.ToString());
        Assert.DoesNotContain("SECRET-STREAM-KEY", s.LiveStream!.ToString());
    }

    [Fact]
    public void Placeholder_entries_and_null_channel_are_ignored()
    {
        var s = LiveStreamApiParser.Parse(TestData.Read("live-stream-api-offline.json"));

        Assert.Equal("CuebotDev", s.DisplayName);
        Assert.Null(s.ChannelName);
        Assert.Empty(s.RecentFollowers);
        Assert.Empty(s.RecentSubscribers);
        Assert.Empty(s.RecentGiftedSubs);
        Assert.Null(s.LiveStream);
    }

    [Fact]
    public void Badges_may_also_arrive_as_an_object()
    {
        const string json = """
            {"followers":{},"livestreams":[{"id":"a1","is_live":true,"chat":{"recent_messages":[
              {"username":"Bob","badges":{"0":"moderator","1":"premium"},"text":"hi","created_on":"2026-01-01T12:00:00+0000"}]}}]}
            """;

        var message = Assert.Single(LiveStreamApiParser.Parse(json).LiveStream!.RecentMessages);

        Assert.Equal(["moderator", "premium"], message.Badges);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), message.CreatedOn);
    }

    [Theory]
    [InlineData("""{"error":"nope"}""")]
    [InlineData("[]")]
    public void Rejects_json_that_is_not_live_stream_api_data(string json) =>
        Assert.Throws<JsonException>(() => LiveStreamApiParser.Parse(json));
}

public sealed class LiveStreamApiUrlTests
{
    [Theory]
    [InlineData("https://rumble.com/-livestream-api/get-data?key=abc123SECRET", "https://rumble.com/-livestream-api/get-data?key=***")]
    [InlineData("https://rumble.com/x?a=1&KEY=abc123SECRET&b=2", "https://rumble.com/x?a=1&KEY=***&b=2")]
    [InlineData("https://rumble.com/x?token=abc123SECRET#frag", "https://rumble.com/x?token=***#frag")]
    [InlineData("https://rumble.com/x?monkey=1", "https://rumble.com/x?monkey=1")]
    public void Redact_hides_key_values(string url, string expected) => Assert.Equal(expected, LiveStreamApiUrl.Redact(url));

    [Theory]
    [InlineData("")]
    [InlineData("rumble.com/-livestream-api/get-data?key=x")]
    [InlineData("ftp://rumble.com/x")]
    [InlineData("http://rumble.com/-livestream-api/get-data?key=x")]
    public void Parse_rejects_unusable_urls(string url) =>
        Assert.Throws<InvalidOperationException>(() => LiveStreamApiUrl.Parse(url));

    [Fact]
    public void Parse_allows_http_only_for_local_relays() =>
        Assert.Equal("localhost", LiveStreamApiUrl.Parse(" http://localhost:9843/api/ls ").Host);
}

public sealed class LiveStreamApiClientTests
{
    private static readonly Uri Url = new("https://rumble.com/-livestream-api/get-data?key=SECRET");

    [Fact]
    public async Task Fetches_and_parses()
    {
        var client = new LiveStreamApiClient(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.Json(TestData.Read("live-stream-api-offline.json")))));

        Assert.Equal("CuebotDev", (await client.FetchAsync(Url, default)).UserName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.NotFound, "rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate-limiting")]
    [InlineData(HttpStatusCode.BadGateway, "HTTP 502")]
    public async Task Errors_are_user_facing_and_never_contain_the_key(HttpStatusCode status, string expected)
    {
        var client = new LiveStreamApiClient(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(status))));

        var error = await Assert.ThrowsAsync<RumbleApiException>(() => client.FetchAsync(Url, default));

        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.Equal((int)status, error.StatusCode);
    }

    [Fact]
    public async Task A_page_that_is_not_api_data_is_reported_clearly()
    {
        var client = new LiveStreamApiClient(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.Json("<html>login</html>"))));

        var error = await Assert.ThrowsAsync<RumbleApiException>(() => client.FetchAsync(Url, default));

        Assert.Contains("Check the URL", error.Message);
    }
}
