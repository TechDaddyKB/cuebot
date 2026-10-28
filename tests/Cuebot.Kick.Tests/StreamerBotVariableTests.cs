using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Kick.Api;
using Cuebot.Kick.Compat;
using Cuebot.Kick.Events;
using Cuebot.Kick.Webhooks;

namespace Cuebot.Kick.Tests;

/// <summary>
/// Streamer.bot's documented trigger variable names (docs: 3.api/2.triggers/kick/**) are present on Cuebot's Kick
/// events, next to Cuebot's own names.
/// </summary>
public sealed class StreamerBotVariableTests
{
    private readonly List<BotEvent> _published = [];
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly KickEventHub _hub;

    public StreamerBotVariableTests()
    {
        var bus = new EventBus(new AppLog());
        bus.Subscribe(_published.Add);
        _hub = new KickEventHub(bus, _time, TimeSpan.FromMinutes(2)) { Broadcaster = () => new KickBroadcasterArgs("CuebotDev", "100") };
    }

    private BotEvent Publish(KickEvent e)
    {
        _published.Clear();
        _hub.Handle(e);
        return _published.First(p => p.Type == e.Type);
    }

    private BotEvent Webhook(string type, string body) => Publish(KickWebhookParser.Parse(type, body)[0]);

    private static KickActor Fan => new(5, "Fan", "fan", "#FF8000", [new KickBadge("subscriber", "Subscriber", 3), new KickBadge("vip", "VIP", 0)],
        ProfilePicture: "https://files.test/fan.png");

    [Fact]
    public void Chat_message_has_KickChat_KickUser_and_broadcaster_names()
    {
        var e = Publish(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("m1", Fan, "hi [emote:37226:KEKW] there", 100, null)));

        Assert.Equal("CuebotDev", e.Args["broadcastUser"]);
        Assert.Equal("CuebotDev", e.Args["broadcastUsername"]);
        Assert.Equal("100", e.Args["broadcastUserId"]);
        Assert.Equal("kick", e.Args["userType"]);
        Assert.Equal(true, e.Args["isSubscribed"]);
        Assert.Equal("hi there", e.Args["messageStripped"]);
        Assert.Equal("KEKW", e.Args["emotes.0.text"]);
        Assert.Equal("37226", e.Args["emotes.0.id"]);
        Assert.Equal("https://files.kick.com/emotes/37226/fullsize", e.Args["emotes.0.url"]);
        Assert.Equal((byte)255, e.Args["colorR"]);
        Assert.Equal((byte)128, e.Args["colorG"]);
        Assert.Equal(2, e.Args["badgeCount"]);
        Assert.Equal("subscriber", e.Args["badge.0.id"]);
        Assert.Equal(3, e.Args["badge.0.info"]);
        Assert.Equal("hi KEKW there", e.Args["rawInput"]);
        Assert.Equal("KEKW", e.Args["input1"]);
        Assert.Equal("hi%20KEKW%20there", e.Args["rawInputUrlEncoded"]);
        Assert.Equal(3, e.Args["input.count"]);
        Assert.Equal(false, e.Args["isInternal"]);
        Assert.Equal("hi KEKW there", e.Args["message"]);
    }

    [Fact]
    public void Messages_cuebot_sent_are_internal()
    {
        _hub.MarkSent("mine");
        var e = Publish(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("mine", Fan, "hello", 100, null)));
        Assert.Equal(true, e.Args["isInternal"]);
    }

    [Fact]
    public void Reward_redemption_has_reward_names_and_known_settings()
    {
        _hub.FindReward = id => id == "01R" ? new KickReward("01R", "Hydrate", "Drink", 100, "#00e701", true, false, true, false) : null;

        var e = Publish(KickEventFactory.Reward("webhook", Fan, "01R", "Hydrate", 100, "a b $", "pending", "red1", "Drink"));

        Assert.Equal("01R", e.Args["reward.id"]);
        Assert.Equal("Hydrate", e.Args["reward.title"]);
        Assert.Equal(100L, e.Args["reward.cost"]);
        Assert.Equal("Drink", e.Args["reward.description"]);
        Assert.Equal("#00e701", e.Args["reward.backgroundColor"]);
        Assert.Equal(false, e.Args["reward.skipsQueue"]);
        Assert.Equal(true, e.Args["reward.requiresUserInput"]);
        Assert.Equal(@"a b \$", e.Args["rawInputEscaped"]);
        Assert.Equal("a%20b%20%24", e.Args["rawInputUrlEncoded"]);
    }

    [Fact]
    public void Kicks_gifted_has_kicks_names_and_input_words()
    {
        var e = Webhook("kicks.gifted", """
            {"broadcaster":{"user_id":100,"username":"CuebotDev"},"sender":{"user_id":5,"username":"Fan"},
             "gift":{"amount":100,"name":"Full Send","type":"LEVEL_UP","tier":"MID","message":"go go","pinned_time_seconds":600},
             "created_at":"2026-01-01T12:00:00Z"}
            """);

        Assert.Equal(100m, e.Args["kicks.amount"]);
        Assert.Equal("Full Send", e.Args["kicks.name"]);
        Assert.Equal("LEVEL_UP", e.Args["kicks.type"]);
        Assert.Equal("MID", e.Args["kicks.tier"]);
        Assert.Equal(600L, e.Args["kicks.pinnedTimeSeconds"]);
        Assert.Equal("2026-01-01T12:00:00Z", e.Args["kicks.createdAt"]);
        Assert.Equal(2, e.Args["input.count"]);
        Assert.Equal("go", e.Args["inputEscaped1"]);
    }

    [Fact]
    public void Subscriptions_have_months_duration_and_times()
    {
        var e = Webhook("channel.subscription.renewal", """
            {"broadcaster":{"user_id":100},"subscriber":{"user_id":5,"username":"Fan"},"duration":4,
             "created_at":"2026-01-01T12:00:00Z","expires_at":"2026-02-01T12:00:00Z"}
            """);

        Assert.Equal(KickEventTypes.Resubscription, e.Type);
        Assert.Equal(4, e.Args["monthsSubscribed"]);
        Assert.Equal(4, e.Args["duration"]);
        Assert.IsType<DateTime>(e.Args["expiresAt"]);
        Assert.IsType<DateTime>(e.Args["subscribedAt"]);
    }

    [Fact]
    public void Gifts_have_recipient_names()
    {
        var events = KickWebhookParser.Parse("channel.subscription.gifts", """
            {"broadcaster":{"user_id":100},"gifter":{"user_id":5,"username":"Fan"},
             "giftees":[{"user_id":7,"username":"Lucky","channel_slug":"lucky","profile_picture":"l.png"},{"user_id":8,"username":"Other"}],
             "created_at":"2026-01-01T12:00:00Z","expires_at":"2026-02-01T12:00:00Z"}
            """);
        var mass = Publish(events[0]);
        var gift = Publish(events[1]);

        Assert.Equal(KickEventTypes.MassGiftSubscription, mass.Type);
        Assert.Equal(2, mass.Args["count"]);
        Assert.Equal("Lucky", mass.Args["recipient.0.userName"]);
        Assert.Equal("lucky", mass.Args["recipient.0.userLogin"]);
        Assert.Equal("7", mass.Args["recipient.0.userId"]);
        Assert.Equal("l.png", mass.Args["recipient.0.profilePicture"]);
        Assert.Equal("kick", mass.Args["recipient.1.platform"]);
        Assert.IsType<DateTime>(mass.Args["subscribedAt"]);

        Assert.Equal("Lucky", gift.Args["recipient.userName"]);
        Assert.Equal("7", gift.Args["recipient.userId"]);
        Assert.Equal("l.png", gift.Args["recipient.profilePicture"]);
        Assert.Equal("kick", gift.Args["recipient.platform"]);
        Assert.IsType<DateTime>(gift.Args["expiresAt"]);
    }

    [Fact]
    public void Bans_have_createdBy_names()
    {
        var e = Webhook("moderation.banned", """
            {"broadcaster":{"user_id":100},"moderator":{"user_id":9,"username":"Mod"},"banned_user":{"user_id":5,"username":"Fan"},
             "metadata":{"reason":"spam","created_at":"2026-01-01T12:00:00Z","expires_at":"2026-01-01T12:10:00Z"}}
            """);

        Assert.Equal(KickEventTypes.UserTimedOut, e.Type);
        Assert.Equal("9", e.Args["createdById"]);
        Assert.Equal("Mod", e.Args["createdByUsername"]);
        Assert.Equal("Mod", e.Args["createdByDisplayName"]);
        Assert.IsType<DateTime>(e.Args["createdAt"]);
        Assert.Equal(600L, e.Args["duration"]);
    }

    [Fact]
    public void Channel_update_reports_old_values_and_the_stream_gets_the_category()
    {
        _hub.SeedChannel(new KickChannel(100, "cuebotdev", "Old", new KickCategory(1, "Art", "art.png"), "", false, 0, null, "en", false, [], 0));

        var update = Publish(KickEventFactory.ChannelUpdate("webhook", "New", "Games", 2, "en", false, "games.png"));
        Assert.Equal(true, update.Args["statusUpdated"]);
        Assert.Equal(true, update.Args["categoryUpdated"]);
        Assert.Equal("Old", update.Args["oldStatus"]);
        Assert.Equal("1", update.Args["oldCategoryId"]);
        Assert.Equal("Art", update.Args["oldCategoryName"]);
        Assert.Equal("art.png", update.Args["oldCategoryThumbnail"]);
        Assert.Equal("Games", update.Args["categoryName"]);
        Assert.Equal("games.png", update.Args["categoryThumbnail"]);
        Assert.Equal("New", update.Args["status"]);

        var online = Publish(KickEventFactory.StreamOnline("webhook", "New", new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Games", online.Args["category.name"]);
        Assert.Equal(2, online.Args["category.id"]);
        Assert.Equal("games.png", online.Args["category.thumbnail"]);

        var offline = Publish(KickEventFactory.StreamOffline("webhook"));
        Assert.Equal("New", offline.Args["title"]);
        Assert.True(offline.Args.ContainsKey("startedAt"));
        Assert.IsType<DateTime>(offline.Args["endedAt"]);
    }

    [Fact]
    public void Neutral_events_keep_only_cuebot_names()
    {
        _hub.Handle(KickEventFactory.MessageDeleted("realtime", "m1"));
        var neutral = Assert.Single(_published, e => e.Type == KickEventTypes.NeutralMessageDeleted);
        Assert.False(neutral.Args.ContainsKey("broadcastUser"));
    }

    [Fact]
    public void Variables_kick_cannot_supply_are_present_with_neutral_values()
    {
        var sub = Webhook("channel.subscription.renewal", """
            {"broadcaster":{"user_id":100},"subscriber":{"user_id":5,"username":"Fan"},"duration":4,
             "created_at":"2026-01-01T12:00:00Z","expires_at":"2026-02-01T12:00:00Z"}
            """);
        Assert.Equal(0, sub.Args["badgeCount"]);
        Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(sub.Args["badge"]));
        Assert.Equal("", sub.Args["userGroups"]);
        Assert.Equal(default(DateTime), sub.Args["userPreviousActive"]);
        Assert.Equal("", sub.Args["broadcastUserProfileImage"]);   // the test broadcaster has no picture

        var chat = Publish(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("m9", Fan, "hi", 100, null)));
        Assert.Equal(2, chat.Args["badgeCount"]);   // real values win
    }

    // ---------------------------------------------------------------- signed in: Streamer.bot 1.0.7 runs with an account connected

    [Fact]
    public void Every_event_has_the_broadcaster_channel_block()
    {
        var bus = new EventBus(new AppLog());
        var hub = new KickEventHub(bus, _time, TimeSpan.FromMinutes(2))
        {
            Broadcaster = () => new KickBroadcasterArgs("CuebotDev", "100", "https://files.test/me.png"),
        };
        var published = new List<BotEvent>();
        bus.Subscribe(published.Add);

        hub.Handle(KickEventFactory.ViewerCount("api", 12));
        var before = Assert.Single(published);
        Assert.Equal((0, "", "", ""), (before.Args["broadcasterChannel.categoryId"], before.Args["broadcasterChannel.categoryName"],
            before.Args["broadcasterChannel.categoryThumbnail"], before.Args["broadcasterChannel.title"]));

        hub.SeedChannel(new KickChannel(100, "cuebotdev", "Hello", new KickCategory(15, "Just Chatting", "jc.png"), "", false, 0, null, "en", false, [], 0));
        published.Clear();
        hub.Handle(KickEventFactory.ViewerCount("api", 13));
        var e = Assert.Single(published);
        Assert.Equal(15, e.Args["broadcasterChannel.categoryId"]);
        Assert.Equal("Just Chatting", e.Args["broadcasterChannel.categoryName"]);
        Assert.Equal("jc.png", e.Args["broadcasterChannel.categoryThumbnail"]);
        Assert.Equal("Hello", e.Args["broadcasterChannel.title"]);
        Assert.Equal("https://files.test/me.png", e.Args["broadcastUserProfileImage"]);
        Assert.Equal("CuebotDev", e.Args["broadcastUserName"]);
        Assert.Equal(13, e.Args["viewerCount"]);   // Streamer.bot: Int32

        // A channel update changes what the next events report.
        published.Clear();
        hub.Handle(KickEventFactory.ChannelUpdate("webhook", "New", "Games", 2, "en", false, "games.png"));
        var update = published.First(p => p.Type == KickEventTypes.ChannelUpdate);
        Assert.Equal("True", update.Args["statusUpdate"]);
        Assert.Equal((2, "Games", "New"), (update.Args["broadcasterChannel.categoryId"], update.Args["broadcasterChannel.categoryName"],
            update.Args["broadcasterChannel.title"]));
    }

    [Fact]
    public void Events_without_badges_get_the_roles_last_seen_in_chat()
    {
        Publish(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("m1", Fan, "hi", 100, null)));
        var kicks = Publish(KickEventFactory.Kicks("webhook", new KickActor(5, "Fan", "fan"), 100, "Rage Quit", "LEVEL_UP", "BASIC", "gg"));
        Assert.Equal((false, true), (kicks.Args["isModerator"], kicks.Args["isSubscribed"]));
        Assert.Equal(100m, kicks.Args["kicks.amount"]);

        var stranger = Publish(KickEventFactory.Follow("webhook", new KickActor(6, "New", "new")));
        Assert.Equal((false, false), (stranger.Args["isModerator"], stranger.Args["isSubscribed"]));
    }

    [Fact]
    public void Stream_times_are_datetimes()
    {
        var online = Publish(KickEventFactory.StreamOnline("webhook", "Live", new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero).LocalDateTime, Assert.IsType<DateTime>(online.Args["startedAt"]));
        var offline = Publish(KickEventFactory.StreamOffline("webhook"));
        Assert.IsType<DateTime>(offline.Args["startedAt"]);
    }
}
