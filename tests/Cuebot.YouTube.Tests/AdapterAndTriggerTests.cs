using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.YouTube.Triggers;

namespace Cuebot.YouTube.Tests;

public sealed class AdapterAndTriggerTests
{
    private readonly YouTubeChatAdapter _adapter = new();

    private static BotEvent E(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public void Chat_message_maps_roles_into_the_combined_chat()
    {
        var m = _adapter.ToChatMessage(E(YouTubeEventTypes.ChatMessage, ("user", "Alice"), ("userId", "UC1"), ("message", "hi"),
            ("messageId", "m1"), ("isModerator", true), ("isSubscriber", true), ("isVerified", true)))!;
        Assert.Equal(("YouTube", "m1", "Alice", "UC1", "hi"), (m.Platform, m.Id, m.User, m.UserId, m.Text));
        Assert.Equal(ChatRoles.Moderator | ChatRoles.Subscriber | ChatRoles.Verified, m.Roles);
        Assert.Null(m.Amount);
        Assert.Null(_adapter.ToFeedItem(E(YouTubeEventTypes.ChatMessage, ("user", "Alice"))));
    }

    [Fact]
    public void Super_chats_and_stickers_show_in_chat_with_amount_and_in_the_feed_as_tips()
    {
        var sc = E(YouTubeEventTypes.SuperChat, ("user", "Alice"), ("message", "gg"), ("amountText", "$5.00"), ("messageId", "s1"));
        Assert.Equal("$5.00", _adapter.ToChatMessage(sc)!.Amount);
        var feed = _adapter.ToFeedItem(sc)!;
        Assert.Equal((FeedKind.Tip, "$5.00", "gg"), (feed.Kind, feed.Amount, feed.Detail));
        Assert.Contains("Super Chat", feed.Title);

        var sticker = E(YouTubeEventTypes.SuperSticker, ("user", "Bob"), ("message", "Cat"), ("stickerAltText", "Cat"), ("amountText", "€2.00"));
        Assert.Equal("€2.00", _adapter.ToChatMessage(sticker)!.Amount);
        Assert.Equal(FeedKind.Tip, _adapter.ToFeedItem(sticker)!.Kind);

        Assert.Equal(FeedKind.Tip, _adapter.ToFeedItem(E(YouTubeEventTypes.JewelsGifted, ("user", "C"), ("jewels", 50L)))!.Kind);
    }

    [Fact]
    public void Memberships_and_gifts_are_subscriptions_in_the_feed()
    {
        var sponsor = _adapter.ToFeedItem(E(YouTubeEventTypes.NewSponsor, ("user", "A"), ("levelName", "Gold"), ("isUpgrade", false)))!;
        Assert.Equal((FeedKind.Subscription, "A became a member", "Gold"), (sponsor.Kind, sponsor.Title, sponsor.Detail));
        var milestone = _adapter.ToFeedItem(E(YouTubeEventTypes.MemberMilestone, ("user", "A"), ("months", 6)))!;
        Assert.Equal((FeedKind.Subscription, "A has been a member for 6 months"), (milestone.Kind, milestone.Title));
        var gift = _adapter.ToFeedItem(E(YouTubeEventTypes.MembershipGift, ("user", "A"), ("total", 5)))!;
        Assert.Equal((FeedKind.GiftSubscription, "A gifted 5 memberships"), (gift.Kind, gift.Title));
        Assert.Equal("A gifted a membership", _adapter.ToFeedItem(E(YouTubeEventTypes.MembershipGift, ("user", "A"), ("total", 1)))!.Title);
        var received = _adapter.ToFeedItem(E(YouTubeEventTypes.GiftMembershipReceived, ("user", "B"), ("gifterName", "A")))!;
        Assert.Equal((FeedKind.Subscription, "B received a membership from A"), (received.Kind, received.Title));
    }

    [Fact]
    public void Subscribers_and_broadcasts_in_the_feed()
    {
        Assert.Equal(FeedKind.Follow, _adapter.ToFeedItem(E(YouTubeEventTypes.NewSubscriber, ("user", "Fan")))!.Kind);
        Assert.Equal(FeedKind.StreamOnline, _adapter.ToFeedItem(E(YouTubeEventTypes.BroadcastStarted, ("broadcastTitle", "T")))!.Kind);
        Assert.Equal(FeedKind.StreamOffline, _adapter.ToFeedItem(E(YouTubeEventTypes.BroadcastEnded))!.Kind);
        Assert.Null(_adapter.ToFeedItem(E(YouTubeEventTypes.BroadcastStatisticsUpdated)));
    }

    private static readonly IReadOnlyDictionary<string, ITriggerType> Triggers =
        new YouTubeTriggers().GetTriggers().ToDictionary(t => t.TypeId);

    private static bool Matches(string type, BotEvent e, params (string Key, string Value)[] settings)
    {
        var trigger = Triggers[type];
        var values = ParameterValues.From(trigger.Parameters, settings.ToDictionary(s => s.Key, s => s.Value));
        return trigger.Matches(e, new TriggerDefinition(), values, new Dictionary<string, object?>());
    }

    [Fact]
    public void Every_youtube_event_type_has_a_trigger_in_the_youtube_category()
    {
        var expected = typeof(YouTubeEventTypes).GetFields()
            .Select(f => (string)f.GetValue(null)!)
            .Where(v => v.StartsWith("youtube.", StringComparison.Ordinal));
        Assert.All(expected, type => Assert.True(Triggers.ContainsKey(type), type));
        Assert.All(Triggers.Values, t =>
        {
            Assert.Equal("YouTube", t.Category);
            Assert.Equal([t.TypeId], t.EventTypes);
        });
    }

    [Fact]
    public void Super_chat_filters_on_amount_currency_and_tier()
    {
        var e = E(YouTubeEventTypes.SuperChat, ("amount", 10.0), ("currency", "USD"), ("tier", 3));
        Assert.True(Matches(YouTubeEventTypes.SuperChat, e));
        Assert.True(Matches(YouTubeEventTypes.SuperChat, e, ("minAmount", "10"), ("currency", "usd"), ("minTier", "3")));
        Assert.False(Matches(YouTubeEventTypes.SuperChat, e, ("minAmount", "10.01")));
        Assert.False(Matches(YouTubeEventTypes.SuperChat, e, ("maxAmount", "5")));
        Assert.False(Matches(YouTubeEventTypes.SuperChat, e, ("currency", "EUR")));
        Assert.False(Matches(YouTubeEventTypes.SuperChat, e, ("minTier", "4")));
    }

    [Fact]
    public void Membership_filters_on_level_months_count_and_upgrades()
    {
        var sponsor = E(YouTubeEventTypes.NewSponsor, ("levelName", "Gold Tier"), ("isUpgrade", true));
        Assert.True(Matches(YouTubeEventTypes.NewSponsor, sponsor, ("level", "gold")));
        Assert.False(Matches(YouTubeEventTypes.NewSponsor, sponsor, ("level", "silver")));
        Assert.False(Matches(YouTubeEventTypes.NewSponsor, sponsor, ("upgrades", "No upgrades")));
        Assert.True(Matches(YouTubeEventTypes.NewSponsor, sponsor, ("upgrades", "Only upgrades")));

        Assert.False(Matches(YouTubeEventTypes.MemberMilestone, E(YouTubeEventTypes.MemberMilestone, ("months", 3)), ("minMonths", "6")));
        Assert.True(Matches(YouTubeEventTypes.MembershipGift, E(YouTubeEventTypes.MembershipGift, ("total", 10)), ("minTotal", "10")));
        Assert.False(Matches(YouTubeEventTypes.MembershipGift, E(YouTubeEventTypes.MembershipGift, ("total", 1)), ("minTotal", "5")));
    }

    [Fact]
    public void Chat_message_trigger_filters_text_and_ignores_the_bot_by_default()
    {
        var bot = E(YouTubeEventTypes.ChatMessage, ("message", "hello"), ("isBotAccount", true));
        Assert.False(Matches(YouTubeEventTypes.ChatMessage, bot));
        Assert.True(Matches(YouTubeEventTypes.ChatMessage, bot, ("ignoreBot", "false")));
        var user = E(YouTubeEventTypes.ChatMessage, ("message", "Hello there"));
        Assert.True(Matches(YouTubeEventTypes.ChatMessage, user, ("contains", "hello")));
        Assert.False(Matches(YouTubeEventTypes.ChatMessage, user, ("contains", "bye")));
    }

    [Fact]
    public void Timeout_and_viewer_thresholds()
    {
        Assert.False(Matches(YouTubeEventTypes.UserTimedOut, E(YouTubeEventTypes.UserTimedOut, ("duration", 60L)), ("minDuration", "300")));
        Assert.True(Matches(YouTubeEventTypes.BroadcastStatisticsUpdated, E(YouTubeEventTypes.BroadcastStatisticsUpdated, ("concurrentViewers", 50L)), ("minViewers", "50")));
    }
}
