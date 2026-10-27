using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Kick.Api;
using Cuebot.Kick.Events;

namespace Cuebot.Kick.Tests;

public sealed class EventHubTests
{
    private readonly List<BotEvent> _published = [];
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly KickEventHub _hub;

    public EventHubTests()
    {
        var bus = new EventBus(new AppLog());
        bus.Subscribe(_published.Add);
        _hub = new KickEventHub(bus, _time, TimeSpan.FromMinutes(2));
    }

    private static KickActor Fan => new(5, "Fan");

    [Fact]
    public void Same_occurrence_from_two_sources_is_published_once()
    {
        _hub.Handle(KickEventFactory.Follow("realtime", Fan));
        _hub.Handle(KickEventFactory.Follow("webhook", Fan));
        Assert.Single(_published);
        Assert.Equal(KickEventTypes.Follow, _published[0].Type);
    }

    [Fact]
    public void Duplicates_are_allowed_again_after_the_window()
    {
        _hub.Handle(KickEventFactory.Follow("realtime", Fan));
        _time.Now += TimeSpan.FromMinutes(3);
        _hub.Handle(KickEventFactory.Follow("realtime", Fan));
        Assert.Equal(2, _published.Count);
    }

    [Fact]
    public void Stream_state_is_published_only_on_transitions()
    {
        _hub.SetInitialLive(false);
        _hub.Handle(KickEventFactory.StreamOnline("realtime", "t", null));
        _hub.Handle(KickEventFactory.StreamOnline("api", "t", null));
        _hub.Handle(KickEventFactory.StreamOffline("webhook"));
        _hub.Handle(KickEventFactory.StreamOffline("realtime"));
        _hub.Handle(KickEventFactory.StreamOnline("api", "t", null));
        Assert.Equal([KickEventTypes.StreamOnline, KickEventTypes.StreamOffline, KickEventTypes.StreamOnline], _published.Select(e => e.Type));
        Assert.True(_hub.IsLive);
    }

    [Fact]
    public void Initial_live_state_suppresses_a_repeated_online()
    {
        _hub.SetInitialLive(true);
        _hub.Handle(KickEventFactory.StreamOnline("realtime", "t", null));
        Assert.Empty(_published);
    }

    [Fact]
    public void Viewer_counts_are_never_deduplicated()
    {
        _hub.Handle(KickEventFactory.ViewerCount("api", 10));
        _hub.Handle(KickEventFactory.ViewerCount("api", 11));
        _hub.Handle(KickEventFactory.ViewerCount("api", 10));
        Assert.Equal(3, _published.Count);
    }

    [Fact]
    public void Moderation_also_publishes_neutral_chat_events()
    {
        _hub.Handle(KickEventFactory.MessageDeleted("realtime", "m1"));
        _hub.Handle(KickEventFactory.Ban("realtime", new KickActor(6, "Spammer"), null, 0, "", null));
        _hub.Handle(KickEventFactory.Ban("realtime", new KickActor(7, "Rude"), null, 60, "", null));
        Assert.Equal(
            [KickEventTypes.MessageDeleted, "chat.messageDeleted", KickEventTypes.UserBanned, "chat.userBanned", KickEventTypes.UserTimedOut, "chat.userTimedOut"],
            _published.Select(e => e.Type));
        Assert.Equal("Kick", _published[1].Args["platform"]);
        Assert.Equal("m1", _published[1].Args["messageId"]);
        Assert.Equal("6", _published[3].Args["userId"]);
        Assert.Equal(60L, _published[5].Args["duration"]);
    }

    [Fact]
    public void User_ids_seen_in_events_can_be_found_by_name()
    {
        _hub.Handle(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("x", new KickActor(42, "Chatty"), "hi", 100, null)));
        Assert.Equal("42", _hub.FindUserId("@chatty"));
        Assert.Null(_hub.FindUserId("nobody"));
    }

    [Fact]
    public void Channel_poll_diff_reports_changes_after_the_baseline()
    {
        var offline = Channel(false, "Title A", 15, 0);
        Assert.Empty(KickChannelPoll.Diff(null, offline));

        var live = Channel(true, "Title A", 15, 20);
        Assert.Equal([KickEventTypes.StreamOnline, KickEventTypes.ViewerCountUpdate], KickChannelPoll.Diff(offline, live).Select(e => e.Type));

        var renamed = Channel(true, "Title B", 15, 20);
        var update = Assert.Single(KickChannelPoll.Diff(live, renamed));
        Assert.Equal(KickEventTypes.ChannelUpdate, update.Type);
        Assert.Equal("Title B", update.Args["title"]);

        var recategorized = Channel(true, "Title B", 16, 25);
        Assert.Equal([KickEventTypes.ChannelUpdate, KickEventTypes.ViewerCountUpdate], KickChannelPoll.Diff(renamed, recategorized).Select(e => e.Type));

        Assert.Equal([KickEventTypes.StreamOffline], KickChannelPoll.Diff(recategorized, Channel(false, "Title B", 16, 0)).Select(e => e.Type));
    }

    private static KickChannel Channel(bool live, string title, long category, int viewers) =>
        new(100, "cuebotdev", title, new KickCategory(category, "Cat" + category, ""), "", live, viewers, null, "en", false, [], 0);
}

public sealed class ChatAdapterTests
{
    private readonly KickChatAdapter _adapter = new();

    private static BotEvent Bus(KickEvent e) => new(e.Type, e.Args);

    [Fact]
    public void Chat_messages_reach_the_combined_chat_with_roles()
    {
        var actor = new KickActor(7, "Vippy", Color: "#123456", Badges: [new KickBadge("vip", "VIP", 0), new KickBadge("subscriber", "Sub", 2)]);
        var message = _adapter.ToChatMessage(Bus(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("id-1", actor, "yo [emote:1:Pog]", 100, null))));
        Assert.NotNull(message);
        Assert.Equal("Kick", message.Platform);
        Assert.Equal("id-1", message.Id);
        Assert.Equal("Vippy", message.User);
        Assert.Equal("7", message.UserId);
        Assert.Equal("yo Pog", message.Text);
        Assert.Equal("#123456", message.Color);
        Assert.Equal(ChatRoles.Vip | ChatRoles.Subscriber, message.Roles);
        Assert.Null(_adapter.ToFeedItem(Bus(KickEventFactory.Chat("realtime", new KickEventFactory.ChatInput("id-1", actor, "x", 100, null)))));
    }

    [Fact]
    public void Activity_reaches_the_feed()
    {
        var fan = new KickActor(5, "Fan");
        Assert.Equal(FeedKind.Follow, _adapter.ToFeedItem(Bus(KickEventFactory.Follow("realtime", fan)))!.Kind);
        Assert.Equal("Fan resubscribed (4 months)", _adapter.ToFeedItem(Bus(KickEventFactory.Subscription("realtime", fan, 4)))!.Title);
        var kicks = _adapter.ToFeedItem(Bus(KickEventFactory.Kicks("webhook", fan, 500, "Rage Quit", "LEVEL_UP", "MID", "")))!;
        Assert.Equal(FeedKind.Tip, kicks.Kind);
        Assert.Equal("500 Kicks", kicks.Amount);
        Assert.Equal(FeedKind.Raid, _adapter.ToFeedItem(Bus(KickEventFactory.Host("realtime", "Big", 40, "")))!.Kind);
        Assert.Equal(FeedKind.Redemption, _adapter.ToFeedItem(Bus(KickEventFactory.Reward("realtime", fan, "", "Hydrate", 0, "", "pending", "")))!.Kind);
        Assert.Equal(FeedKind.StreamOnline, _adapter.ToFeedItem(Bus(KickEventFactory.StreamOnline("api", "t", null)))!.Kind);

        var gifts = KickEventFactory.Gifts("realtime", fan, [new KickActor(0, "A"), new KickActor(0, "B")]);
        Assert.Equal("Fan gifted 2 subscriptions", _adapter.ToFeedItem(Bus(gifts[0]))!.Title);
        Assert.Null(_adapter.ToFeedItem(Bus(gifts[1]))); // recipients of a mass gift are not listed separately
        var single = KickEventFactory.Gifts("realtime", fan, [new KickActor(0, "A")]);
        Assert.Equal("Fan gifted a subscription to A", _adapter.ToFeedItem(Bus(single[0]))!.Title);
    }
}
