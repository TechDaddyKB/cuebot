using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

/// <summary>Behaviour added to match the current dev.twitch.tv documentation (rate limits, EventSub versions, new events).</summary>
public sealed class TwitchDevDocsAlignmentTests
{
    private static readonly Uri HelixBase = new("https://api.test/helix/");

    // ---------------------------------------------------------------- Helix rate limits

    private static HttpResponseMessage RateLimited(DateTimeOffset reset, int remaining)
    {
        var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"status":429,"message":"Too Many Requests"}""");
        response.Headers.Add("Ratelimit-Limit", "800");
        response.Headers.Add("Ratelimit-Remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("Ratelimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    [Fact]
    public async Task An_empty_rate_limit_bucket_waits_for_the_reset_and_retries_once()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var http = new FakeHttpHandler(_ => ++calls == 1
            ? RateLimited(time.GetUtcNow().AddSeconds(5), 0)
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[{"id":"1"}]}"""));
        var helix = new HelixClient(new HttpClient(http), HelixBase, new StaticTokens(), time);

        var json = await helix.RequestAsync(HttpMethod.Get, "users", null, null, "Getting user", default);

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("1", json.Items("data").Single().Str("id"));
        Assert.Contains(time.Delays, d => d >= TimeSpan.FromSeconds(4) && d <= TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(10, 5)]     // an endpoint limit (EventSub's, a shoutout's): the bucket still has room
    [InlineData(0, 300)]    // the bucket refills too late to wait for
    public async Task Other_429s_are_reported_without_waiting(int remaining, int resetInSeconds)
    {
        var time = new FakeTimeProvider();
        var http = new FakeHttpHandler(_ => RateLimited(time.GetUtcNow().AddSeconds(resetInSeconds), remaining));
        var helix = new HelixClient(new HttpClient(http), HelixBase, new StaticTokens(), time);

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => helix.RequestAsync(HttpMethod.Get, "users", null, null, "Getting user", default));

        Assert.Equal(429, error.StatusCode);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_503_is_retried_once()
    {
        var calls = 0;
        var http = new FakeHttpHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[]}"""));
        var helix = new HelixClient(new HttpClient(http), HelixBase, new StaticTokens(), new FakeTimeProvider());

        await helix.RequestAsync(HttpMethod.Get, "users", null, null, "Getting user", default);

        Assert.Equal(2, http.Requests.Count);
    }

    // ---------------------------------------------------------------- revoke

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]   // "Invalid token": it no longer works anyway
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task Revoke_reports_a_token_that_may_still_be_valid(HttpStatusCode status, bool throws)
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json(status, """{"status":500,"message":"oops"}"""));
        var auth = new TwitchAuthClient(new HttpClient(http), new Uri("https://id.test/oauth2/"), TimeProvider.System);

        var error = await Record.ExceptionAsync(() => auth.RevokeAsync("client", "token", default));

        Assert.Equal(throws, error is TwitchApiException);
    }

    // ---------------------------------------------------------------- message length

    [Fact]
    public async Task Chat_messages_over_500_characters_fail_before_calling_Twitch()
    {
        using var h = new ApiHarness();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => h.Api.SendChatMessageAsync(new string('a', 501)));

        Assert.Contains("500", error.Message);
        Assert.Empty(h.Http.Requests);
        await h.Api.SendChatMessageAsync(new string('a', 500));
        Assert.Single(h.Http.Requests);
    }

    // ---------------------------------------------------------------- EventSub versions and new events

    [Fact]
    public void AutoMod_holds_use_version_2_and_the_new_types_are_subscribed()
    {
        var specs = EventSubSubscriptions.All.ToDictionary(s => s.Type + (s.Type == "channel.raid" ? s.BuildCondition("1").Keys.First() : ""), s => s);
        Assert.Equal("2", specs["automod.message.hold"].Version);
        Assert.Equal("2", specs["automod.message.update"].Version);
        Assert.Equal(TwitchScopes.ManageAutomodSettings, specs["automod.settings.update"].Scopes.Single());
        Assert.Equal(TwitchScopes.ReadSubscriptions, specs["channel.subscription.end"].Scopes.Single());
        Assert.Equal(new Dictionary<string, string> { ["broadcaster_user_id"] = "1337", ["user_id"] = "1337" },
            specs["channel.chat.user_message_hold"].BuildCondition("1337"));
        // Get VIPs' scope, not the moderator one channel.moderate uses.
        Assert.Contains(TwitchScopes.ChannelReadVips, specs["channel.vip.add"].Scopes);
    }

    [Fact]
    public void A_watched_channel_costs_two_subscriptions_or_three_with_updates()
    {
        var plain = EventSubSubscriptions.ForWatchedChannel("999", "friend", updates: false);
        var withUpdates = EventSubSubscriptions.ForWatchedChannel("999", "friend", updates: true);

        Assert.Equal(["stream.online", "stream.offline"], plain.Select(s => s.Type));
        Assert.Equal("channel.update", withUpdates[^1].Type);
        Assert.All(withUpdates, s => Assert.Equal("999", s.BuildCondition("1337")["broadcaster_user_id"]));
        Assert.All(withUpdates, s => Assert.Contains("(friend)", s.Name));
        Assert.All(withUpdates, s => Assert.Empty(s.Scopes));
    }

    private static BotEvent Map(string type, string json, string broadcasterId = "1337") =>
        EventSubMapper.Map(type, JsonSerializer.Deserialize<JsonElement>(json), broadcasterId) ?? throw new Xunit.Sdk.XunitException("not mapped");

    [Fact]
    public void A_watched_channel_going_live_is_not_the_broadcasters_stream_online()
    {
        const string online = """{"id":"9","broadcaster_user_id":"999","broadcaster_user_login":"friend","broadcaster_user_name":"Friend","type":"live","started_at":"2026-11-11T10:00:00Z"}""";

        var watched = Map("stream.online", online);
        var own = Map("stream.online", online.Replace("\"999\"", "\"1337\"", StringComparison.Ordinal));

        Assert.Equal(TwitchEventTypes.WatchedChannelOnline, watched.Type);
        Assert.Equal("friend", watched.Args["channelUserName"]);
        Assert.Equal(TwitchEventTypes.StreamOnline, own.Type);
        var update = Map("channel.update", """{"broadcaster_user_id":"999","broadcaster_user_login":"friend","broadcaster_user_name":"Friend","title":"New","category_name":"Art","category_id":"1"}""");
        Assert.Equal(TwitchEventTypes.WatchedChannelUpdate, update.Type);
        Assert.Equal("Art", update.Args["categoryName"]);
    }

    [Fact]
    public void Stream_offline_carries_the_stream_id()
    {
        var e = Map("stream.offline", """{"broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer","id":"stream-7"}""");

        Assert.Equal(TwitchEventTypes.StreamOffline, e.Type);
        Assert.Equal("stream-7", e.Args["streamId"]);
    }

    [Fact]
    public void AutoMod_version_2_reports_the_reason_and_category()
    {
        var automod = Map("automod.message.hold", """
            {"broadcaster_user_id":"1337","user_id":"5","user_login":"rude","user_name":"Rude","message_id":"m1",
             "message":{"text":"bad words","fragments":[]},"held_at":"2026-11-11T10:00:00Z","reason":"automod",
             "automod":{"category":"swearing","level":4,"boundaries":[]},"blocked_term":null}
            """);
        var term = Map("automod.message.hold", """
            {"broadcaster_user_id":"1337","user_id":"5","user_login":"rude","user_name":"Rude","message_id":"m2",
             "message":{"text":"spoiler","fragments":[]},"reason":"blocked_term","automod":null,
             "blocked_term":{"terms_found":[{"term_id":"t1","boundary":{"start_pos":0,"end_pos":6}}]}}
            """);

        Assert.Equal("automod", automod.Args["reason"]);
        Assert.Equal("swearing", automod.Args["category"]);
        Assert.Equal(4, automod.Args["level"]);
        Assert.Equal("blocked_term", term.Args["reason"]);
        Assert.Equal("t1", term.Args["blockedTermIds"]);
        Assert.Equal(true, term.Args["isBlockedTerm"]);
    }

    [Fact]
    public void New_event_types_map_to_their_triggers()
    {
        var ended = Map("channel.subscription.end", """{"user_id":"5","user_login":"fan","user_name":"Fan","broadcaster_user_id":"1337","tier":"2000","is_gift":true}""");
        Assert.Equal(TwitchEventTypes.SubEnded, ended.Type);
        Assert.Equal(true, ended.Args["isGift"]);

        var held = Map("channel.chat.user_message_hold", """{"broadcaster_user_id":"1337","user_id":"777","user_login":"mybot","user_name":"MyBot","message_id":"m","message":{"text":"hey world","fragments":[]}}""");
        Assert.Equal(TwitchEventTypes.SentMessageHeld, held.Type);
        Assert.Equal("hey world", held.Args["message"]);

        var reviewed = Map("channel.chat.user_message_update", """{"broadcaster_user_id":"1337","user_id":"777","user_login":"mybot","user_name":"MyBot","status":"approved","message_id":"m","message":{"text":"hey","fragments":[]}}""");
        Assert.Equal(TwitchEventTypes.SentMessageUpdated, reviewed.Type);
        Assert.Equal("approved", reviewed.Args["status"]);

        var settings = Map("automod.settings.update", """{"broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer","moderator_user_id":"1337","overall_level":null,"swearing":3,"bullying":1}""");
        Assert.Equal(TwitchEventTypes.AutomodSettingsUpdated, settings.Type);
        Assert.Equal(-1, settings.Args["overallLevel"]);
        Assert.Equal(3, settings.Args["swearing"]);
    }

    [Theory]
    [InlineData("gifted_drops_summary", "twitch.giftedDrops")]
    [InlineData("shared_chat_gifted_drops_summary", "twitch.sharedChatGiftedDrops")]
    public void Gifted_drops_summaries_are_reported(string notice, string type)
    {
        var e = Map("channel.chat.notification", $$"""
            {"broadcaster_user_id":"1337","chatter_user_id":"5","chatter_user_login":"fan","chatter_user_name":"Fan","chatter_is_anonymous":false,
             "message_id":"n1","message":{"text":"","fragments":[]},"system_message":"Fan gifted drops","notice_type":"{{notice}}",
             "{{notice}}":{"recipient_count":25},"source_broadcaster_user_id":"42"}
            """);

        Assert.Equal(type, e.Type);
        Assert.Equal(25, e.Args["recipientCount"]);
    }

    [Fact]
    public void Unknown_notice_types_are_ignored()
    {
        Assert.Null(EventSubMapper.Map("channel.chat.notification", JsonSerializer.Deserialize<JsonElement>(
            """{"broadcaster_user_id":"1337","chatter_user_id":"5","notice_type":"unknown","message":{"text":"","fragments":[]}}""")));
    }

    [Fact]
    public void Gif_fragments_are_listed_with_their_urls()
    {
        var e = Map("channel.chat.message", """
            {"broadcaster_user_id":"1337","chatter_user_id":"5","chatter_user_login":"fan","chatter_user_name":"Fan","message_id":"m",
             "message":{"text":"look ","fragments":[{"type":"text","text":"look "},{"type":"gif","text":"","gif":{"id":"g1","url":"https://media.example/g1.gif"}}]},
             "badges":[],"message_type":"text"}
            """);

        Assert.Equal(true, e.Args["hasGif"]);
        Assert.Equal("g1", e.Args["gifIds"]);
        Assert.Equal("https://media.example/g1.gif", e.Args["gifUrls"]);
    }

    // ---------------------------------------------------------------- revocations and watched channels in the listener

    private static TwitchEventSubListener Listener(FakeHttpHandler http, AppLog log, List<(ConnectionState State, string Message)> states,
        Func<CancellationToken, Task<IReadOnlyList<EventSubSubscriptionSpec>>>? more = null) =>
        new(new HelixClient(new HttpClient(http), HelixBase, new StaticTokens()),
            new EventSubAccount("Twitch", () => "1337", () => "streamer", () => [TwitchScopes.ReadChat],
                [.. EventSubSubscriptions.All.Where(s => s.Type is "channel.chat.message" or "stream.online")], MoreSubscriptions: more),
            new TwitchEventRouter(TimeProvider.System, () => "1337", () => "", new SentMessageTracker()), new EventBus(log), log,
            (state, message) => { lock (states) states.Add((state, message)); });

    [Fact]
    public async Task A_revoked_subscription_becomes_unavailable_and_a_withdrawn_sign_in_is_an_error()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, "{}"));
        var log = new AppLog();
        var states = new List<(ConnectionState State, string Message)>();
        var listener = Listener(http, log, states);
        await listener.OnSessionStartedAsync("s1", default);

        listener.OnRevocation("stream.online", "version_removed");
        Assert.Contains("stream.online", listener.Unavailable);
        Assert.Equal(ConnectionState.Connected, states[^1].State);
        Assert.Contains(log.Recent(), e => e.Message.Contains("update Cuebot"));

        var before = states.Count;
        listener.OnRevocation("channel.chat.message", "authorization_revoked");
        listener.OnRevocation("stream.online", "authorization_revoked");
        Assert.Equal(before + 2, states.Count);   // each one updates the status (and with it the unavailable list)
        Assert.Contains("channel.chat.message", listener.Unavailable);
        Assert.Equal(ConnectionState.Error, states[^1].State);
        Assert.Contains("sign in again", states[^1].Message);
        Assert.Single(log.Recent(), e => e.Message.Contains("access was withdrawn"));
    }

    [Fact]
    public async Task Watched_channel_subscriptions_are_created_with_the_session()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, "{}"));
        var states = new List<(ConnectionState State, string Message)>();
        var listener = Listener(http, new AppLog(), states,
            _ => Task.FromResult(EventSubSubscriptions.ForWatchedChannel("999", "friend", updates: false)));

        await listener.OnSessionStartedAsync("s1", default);

        var conditions = http.Requests.Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body))
            .Where(b => b.GetProperty("type").GetString() is "stream.online" or "stream.offline")
            .Select(b => b.GetProperty("condition").GetProperty("broadcaster_user_id").GetString() ?? "").ToArray();
        Assert.Equal(["1337", "999", "999"], conditions);
        Assert.Equal(ConnectionState.Connected, states[^1].State);
    }

    private sealed class StaticTokens : ITwitchTokenSource
    {
        public string ClientId => "client";
        public string AccessToken => "token";
        public Task<bool> TryRefreshAsync(string rejectedToken, CancellationToken ct) => Task.FromResult(false);
    }
}
