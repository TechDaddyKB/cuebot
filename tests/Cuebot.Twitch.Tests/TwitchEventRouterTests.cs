using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

public sealed class TwitchEventRouterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly SentMessageTracker _sent = new();
    private string _botId = "";
    private readonly TwitchEventRouter _router;

    public TwitchEventRouterTests() => _router = new TwitchEventRouter(_time, () => "1337", () => _botId, _sent);

    private IReadOnlyList<BotEvent> Route(string type, string json, bool bot = false) =>
        _router.Route(type, JsonSerializer.Deserialize<JsonElement>(json), bot);

    private const string Target = """ "broadcaster_user_id":"1337","user_id":"66","user_login":"troll","user_name":"Troll","moderator_user_id":"1337" """;

    private static string Ban(bool permanent, int seconds = 0) =>
        "{" + Target + $",\"reason\":\"spam\",\"banned_at\":\"2026-01-01T12:00:00Z\",\"ends_at\":{(permanent ? "null" : $"\"2026-01-01T12:{seconds / 60:00}:{seconds % 60:00}Z\"")},\"is_permanent\":{(permanent ? "true" : "false")}" + "}";

    [Fact]
    public void Ban_publishes_the_neutral_ban_event()
    {
        var events = Route("channel.ban", Ban(permanent: true));

        Assert.Equal([TwitchEventTypes.UserBanned, NeutralChatEvents.UserBanned], events.Select(e => e.Type));
        var neutral = events[1];
        Assert.Equal("Twitch", neutral.Args["platform"]);
        Assert.Equal("66", neutral.Args["userId"]);
        Assert.Equal("Troll", neutral.Args["user"]);
        Assert.Equal("troll", neutral.Args["userName"]);
        Assert.Equal(0, neutral.Args["duration"]);
    }

    [Fact]
    public void Timeout_publishes_the_neutral_timeout_with_duration_and_unban_becomes_untimeout()
    {
        var events = Route("channel.ban", Ban(permanent: false, seconds: 300));
        Assert.Equal([TwitchEventTypes.UserTimedOut, NeutralChatEvents.UserTimedOut], events.Select(e => e.Type));
        Assert.Equal(300, events[1].Args["duration"]);

        Assert.Equal(TwitchEventTypes.UserUntimedOut, Assert.Single(Route("channel.unban", "{" + Target + "}")).Type);
        Assert.Equal(TwitchEventTypes.UserUnbanned, Assert.Single(Route("channel.unban", "{" + Target + "}")).Type);
    }

    [Fact]
    public void Unban_after_the_timeout_expired_is_an_unban()
    {
        Route("channel.ban", Ban(permanent: false, seconds: 60));
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(TwitchEventTypes.UserUnbanned, Assert.Single(Route("channel.unban", "{" + Target + "}")).Type);
    }

    [Fact]
    public void Message_delete_publishes_the_neutral_delete()
    {
        var events = Route("channel.chat.message_delete",
            """{"broadcaster_user_id":"1337","target_user_id":"66","target_user_name":"Troll","target_user_login":"troll","message_id":"m-5"}""");

        Assert.Equal([TwitchEventTypes.ChatMessageDeleted, NeutralChatEvents.MessageDeleted], events.Select(e => e.Type));
        Assert.Equal("m-5", events[1].Args["messageId"]);
        Assert.Equal("Twitch", events[1].Args["platform"]);
        Assert.Equal("66", events[1].Args["userId"]);
    }

    private static string Settings(bool emote, int? follower, int? slow, bool subs, bool unique) =>
        $$"""{"broadcaster_user_id":"1337","emote_mode":{{(emote ? "true" : "false")}},"follower_mode_duration_minutes":{{(follower?.ToString() ?? "null")}},"slow_mode_wait_time_seconds":{{(slow?.ToString() ?? "null")}},"subscriber_mode":{{(subs ? "true" : "false")}},"unique_chat_mode":{{(unique ? "true" : "false")}}}""";

    [Fact]
    public void Chat_mode_changes_are_reported_against_the_previous_state()
    {
        Assert.Equal([TwitchEventTypes.ChatSettingsUpdated], Route("channel.chat_settings.update", Settings(false, null, null, false, false)).Select(e => e.Type));

        var on = Route("channel.chat_settings.update", Settings(true, 10, 30, true, true)).Select(e => e.Type).ToArray();
        Assert.Equal([TwitchEventTypes.ChatSettingsUpdated, TwitchEventTypes.EmoteModeOn, TwitchEventTypes.FollowerModeOn,
            TwitchEventTypes.SlowModeOn, TwitchEventTypes.SubscriberModeOn, TwitchEventTypes.UniqueModeOn], on);

        var changed = Route("channel.chat_settings.update", Settings(true, 60, 5, true, true)).Select(e => e.Type).ToArray();
        Assert.Equal([TwitchEventTypes.ChatSettingsUpdated, TwitchEventTypes.FollowerModeChanged, TwitchEventTypes.SlowModeChanged], changed);

        var off = Route("channel.chat_settings.update", Settings(false, null, null, false, false)).Select(e => e.Type).ToArray();
        Assert.Equal([TwitchEventTypes.ChatSettingsUpdated, TwitchEventTypes.EmoteModeOff, TwitchEventTypes.FollowerModeOff,
            TwitchEventTypes.SlowModeOff, TwitchEventTypes.SubscriberModeOff, TwitchEventTypes.UniqueModeOff], off);
    }

    [Fact]
    public void Seeded_chat_settings_make_the_first_change_visible()
    {
        _router.SeedChatSettings(new TwitchChatSettings(false, false, 0, false, 0, false, false));

        Assert.Contains(TwitchEventTypes.SlowModeOn, Route("channel.chat_settings.update", Settings(false, null, 30, false, false)).Select(e => e.Type));
    }

    private static string Train(int level) =>
        $$"""{"broadcaster_user_id":"1337","id":"t","level":{{level}},"total":100,"progress":10,"goal":100,"top_contributions":[]}""";

    [Fact]
    public void Hype_train_level_up_is_detected()
    {
        Route("channel.hype_train.begin", Train(1));
        Assert.Single(Route("channel.hype_train.progress", Train(1)));

        var up = Route("channel.hype_train.progress", Train(2));
        Assert.Equal([TwitchEventTypes.HypeTrainUpdate, TwitchEventTypes.HypeTrainLevelUp], up.Select(e => e.Type));
        Assert.Equal(2, up[1].Args["level"]);

        Route("channel.hype_train.end", Train(2));
        Assert.Single(Route("channel.hype_train.progress", Train(3))); // no train running
    }

    private static string Chat(string userId, string messageId, string text = "hello") =>
        $$"""{"broadcaster_user_id":"1337","chatter_user_id":"{{userId}}","chatter_user_login":"u{{userId}}","chatter_user_name":"U{{userId}}","message_id":"{{messageId}}","message":{"text":"{{text}}","fragments":[]},"badges":[]}""";

    [Fact]
    public void Chat_messages_flag_the_bot_account_and_cuebots_own_messages()
    {
        _botId = "777";
        _sent.Add("mine-1");

        var fromBot = Assert.Single(Route("channel.chat.message", Chat("777", "mine-1")));
        var fromViewer = Assert.Single(Route("channel.chat.message", Chat("5", "other")));

        Assert.Equal(true, fromBot.Args["isBot"]);
        Assert.Equal(true, fromBot.Args["isBotAccount"]);
        Assert.Equal(true, fromBot.Args["isInternal"]);
        Assert.Equal(false, fromViewer.Args["isBot"]);
        Assert.Equal(false, fromViewer.Args["isInternal"]);
    }

    [Fact]
    public void Whispers_on_the_bot_session_are_bot_whispers()
    {
        const string whisper = """{"from_user_id":"1","from_user_login":"a","from_user_name":"A","to_user_id":"777","whisper_id":"w","whisper":{"text":"hi"}}""";

        Assert.Equal(TwitchEventTypes.BotWhisper, Assert.Single(Route("user.whisper.message", whisper, bot: true)).Type);
        Assert.Equal(TwitchEventTypes.Whisper, Assert.Single(Route("user.whisper.message", whisper)).Type);
    }

    [Fact]
    public void Pyramids_complete_and_break()
    {
        string[] build = ["Kappa", "Kappa Kappa", "Kappa Kappa Kappa", "Kappa Kappa", "Kappa"];
        var events = build.SelectMany((text, i) => Route("channel.chat.message", Chat("5", "p" + i, text))).ToArray();

        var pyramid = Assert.Single(events, e => e.Type == TwitchEventTypes.Pyramid);
        Assert.Equal(3, pyramid.Args["width"]);
        Assert.Equal("Kappa", pyramid.Args["emote"]);
        Assert.Equal("U5", pyramid.Args["user"]);

        Route("channel.chat.message", Chat("5", "q1", "LUL"));
        Route("channel.chat.message", Chat("5", "q2", "LUL LUL"));
        var broken = Route("channel.chat.message", Chat("9", "q3", "stop"));
        var e = Assert.Single(broken, x => x.Type == TwitchEventTypes.PyramidBroken);
        Assert.Equal("U9", e.Args["brokenBy"]);
    }

    [Fact]
    public void Small_pyramids_do_not_count()
    {
        string[] build = ["a", "a a", "a"];
        Assert.DoesNotContain(build.SelectMany((t, i) => Route("channel.chat.message", Chat("5", "s" + i, t))),
            e => e.Type == TwitchEventTypes.Pyramid);
    }

    [Fact]
    public void Unknown_types_publish_nothing() =>
        Assert.Empty(Route("channel.not_real", "{}"));
}

public sealed class TwitchEventSubListenerTests
{
    [Fact]
    public async Task Subscribes_only_what_the_scopes_allow_and_warns_about_the_rest()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Json(System.Net.HttpStatusCode.Accepted, "{}"));
        var log = new AppLog();
        var helix = new HelixClient(new HttpClient(http), new Uri("https://api.test/helix/"), new StaticTokens());
        var states = new List<ConnectionState>();
        var account = new EventSubAccount("Twitch", () => "1337", () => "streamer",
            () => [TwitchScopes.ReadChat, TwitchScopes.ManagePolls], EventSubSubscriptions.All);
        var listener = new TwitchEventSubListener(helix, account,
            new TwitchEventRouter(TimeProvider.System, () => "1337", () => "", new SentMessageTracker()), new EventBus(log), log,
            (state, _) => states.Add(state));

        await listener.OnSessionStartedAsync("s1", default);

        var types = http.Requests.Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body).GetProperty("type").GetString()).ToHashSet();
        Assert.Contains("channel.poll.begin", types);
        Assert.Contains("channel.chat.notification", types);
        Assert.DoesNotContain("channel.ban", types);
        Assert.DoesNotContain("channel.prediction.begin", types);
        Assert.Contains("channel.ban", listener.Unavailable);
        Assert.Contains(log.Recent(), e => e.Level == LogLevel.Warning && e.Message.Contains("channel:moderate") && e.Message.Contains("Settings tab"));
        Assert.Equal([ConnectionState.Connected], states);
    }

    [Fact]
    public async Task Channel_update_waits_for_the_box_art_lookup_and_keeps_the_order()
    {
        var log = new AppLog();
        var bus = new EventBus(log);
        var published = new System.Collections.Concurrent.ConcurrentQueue<BotEvent>();
        bus.Subscribe(published.Enqueue);
        var router = new TwitchEventRouter(TimeProvider.System, () => "1337", () => "", new SentMessageTracker());
        router.SeedChannel(new TwitchChannelInfo("1337", "streamer", "Streamer", "en", "1", "Art", "Title", [], [], false));
        router.SeedBoxArt("1", "art-1");
        var lookup = new TaskCompletionSource();
        var lookups = new List<string>();
        var listener = new TwitchEventSubListener(new HelixClient(new HttpClient(), new Uri("https://api.test/helix/"), new StaticTokens()),
            new EventSubAccount("Twitch", () => "1337", () => "streamer", () => [], []), router, bus, log, (_, _) => { },
            lookUpBoxArt: async id =>
            {
                lookups.Add(id);
                await lookup.Task;
                router.SeedBoxArt(id, "art-" + id);
            });

        listener.OnNotification(Update("n1", "2", "Chess"));
        listener.OnNotification(Update("n2", "1", "Art")); // known, but must wait for the first
        Assert.Empty(published);

        lookup.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (published.Count(e => e.Type == TwitchEventTypes.StreamUpdate) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);

        var updates = published.Where(e => e.Type == TwitchEventTypes.StreamUpdate).ToList();
        Assert.Equal(["2", "1"], updates.Select(e => (string)e.Args["gameId"]!));
        Assert.Equal("art-2", updates[0].Args["gameBoxArt"]);
        Assert.Equal("art-1", updates[0].Args["oldGameBoxArt"]);
        Assert.Equal("art-1", updates[1].Args["gameBoxArt"]);
        Assert.Equal(["2"], lookups);

        static EventSubNotification Update(string id, string categoryId, string name) => new(id, "channel.update", JsonSerializer.Deserialize<JsonElement>(
            $$"""{"broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer","title":"Title","language":"en","category_id":"{{categoryId}}","category_name":"{{name}}","content_classification_labels":[]}"""));
    }

    [Fact]
    public async Task Channel_update_is_published_without_box_art_when_the_lookup_fails()
    {
        var log = new AppLog();
        var bus = new EventBus(log);
        var published = new System.Collections.Concurrent.ConcurrentQueue<BotEvent>();
        bus.Subscribe(published.Enqueue);
        var router = new TwitchEventRouter(TimeProvider.System, () => "1337", () => "", new SentMessageTracker());
        var listener = new TwitchEventSubListener(new HelixClient(new HttpClient(), new Uri("https://api.test/helix/"), new StaticTokens()),
            new EventSubAccount("Twitch", () => "1337", () => "streamer", () => [], []), router, bus, log, (_, _) => { },
            lookUpBoxArt: _ => throw new InvalidOperationException("offline"));

        listener.OnNotification(new("n1", "channel.update", JsonSerializer.Deserialize<JsonElement>(
            """{"broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer","title":"T","language":"en","category_id":"9","category_name":"X","content_classification_labels":[]}""")));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!published.Any(e => e.Type == TwitchEventTypes.StreamUpdate) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal("", Assert.Single(published, e => e.Type == TwitchEventTypes.StreamUpdate).Args["gameBoxArt"]);
    }

    [Fact]
    public void Every_scope_a_subscription_needs_is_requested_by_the_full_profile()
    {
        foreach (var spec in EventSubSubscriptions.All.Where(s => s.Scopes.Count > 0))
            Assert.True(spec.Scopes.Any(TwitchScopes.All.Contains), spec.Type);
        Assert.True(EventSubSubscriptions.Bot.All(s => s.Scopes.Any(TwitchScopes.Bot.Contains)));
    }

    [Fact]
    public void Scope_profiles()
    {
        Assert.Equal(TwitchScopes.All, TwitchScopes.ForBroadcaster([]));
        var minimal = TwitchScopes.ForBroadcaster([TwitchScopes.ReadBits, "unknown:scope"]);
        Assert.Equal([TwitchScopes.ReadChat, TwitchScopes.WriteChat, TwitchScopes.ReadBits], minimal);
        Assert.All(TwitchScopes.Minimal, s => Assert.Contains(s, TwitchScopes.All));
        Assert.Equal(TwitchScopes.All.Count, TwitchScopes.All.Distinct().Count());
    }

    private sealed class StaticTokens : Cuebot.Twitch.Auth.ITwitchTokenSource
    {
        public string ClientId => "client";
        public string AccessToken => "token";
        public Task<bool> TryRefreshAsync(string rejectedToken, CancellationToken ct) => Task.FromResult(false);
    }
}

public sealed class TwitchFeedTests
{
    private readonly TwitchChatAdapter _adapter = new();

    private static BotEvent Event(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value));

    [Theory]
    [InlineData(TwitchEventTypes.RaidSend, FeedKind.Raid, "Raided Friend with 12 viewers")]
    [InlineData(TwitchEventTypes.HypeTrainLevelUp, FeedKind.Other, "Hype train reached level 3")]
    [InlineData(TwitchEventTypes.PollCreated, FeedKind.Other, "Poll started: Best?")]
    [InlineData(TwitchEventTypes.PredictionLocked, FeedKind.Other, "Prediction locked: Best?")]
    [InlineData(TwitchEventTypes.ShoutoutReceived, FeedKind.Other, "Friend gave a shoutout")]
    [InlineData(TwitchEventTypes.AdRun, FeedKind.Other, "90s ad break started")]
    [InlineData(TwitchEventTypes.PowerUp, FeedKind.Tip, "Friend used a power-up (celebration)")]
    [InlineData(TwitchEventTypes.CustomPowerUpRedemption, FeedKind.Tip, "Friend used Confetti")]
    [InlineData(TwitchEventTypes.UserTimedOut, FeedKind.Other, "Friend was timed out for 90s")]
    public void New_activity_reaches_the_feed(string type, FeedKind kind, string title)
    {
        var item = _adapter.ToFeedItem(Event(type, ("user", "Friend"), ("viewers", 12), ("level", 3), ("title", "Best?"),
            ("duration", 90), ("powerUpType", "celebration"), ("bits", 50), ("rewardTitle", "Confetti")))!;

        Assert.Equal(kind, item.Kind);
        Assert.Equal(title, item.Title);
    }

    [Fact]
    public void Announcements_show_in_chat_but_whispers_do_not()
    {
        Assert.NotNull(_adapter.ToChatMessage(Event(TwitchEventTypes.Announcement, ("user", "Mod"), ("message", "News"))));
        Assert.Null(_adapter.ToChatMessage(Event(TwitchEventTypes.Whisper, ("user", "A"), ("message", "secret"))));
    }

    [Fact]
    public void Gift_bombs_are_not_listed_twice() =>
        Assert.Null(_adapter.ToFeedItem(Event(TwitchEventTypes.GiftBomb, ("user", "Giver"), ("total", 5))));
}
