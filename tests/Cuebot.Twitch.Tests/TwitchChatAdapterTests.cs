using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Twitch.Tests;

public class TwitchChatAdapterTests
{
    private readonly TwitchChatAdapter _adapter = new();

    private static BotEvent Event(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public void Chat_messages_keep_roles_colour_and_bits()
    {
        var message = _adapter.ToChatMessage(Event(TwitchEventTypes.ChatMessage,
            ("user", "Ann"), ("userId", "7"), ("message", "Cheer100 hi"), ("messageId", "m1"),
            ("isModerator", true), ("isSubscriber", true), ("isBroadcaster", false), ("color", "#FF0000"), ("bits", 100)))!;

        Assert.Equal("Twitch", message.Platform);
        Assert.Equal(ChatRoles.Moderator | ChatRoles.Subscriber, message.Roles);
        Assert.Equal("#FF0000", message.Color);
        Assert.Equal("100 bits", message.Amount);
        Assert.Null(_adapter.ToFeedItem(Event(TwitchEventTypes.ChatMessage)));
    }

    [Theory]
    [InlineData(TwitchEventTypes.Follow, FeedKind.Follow, "Ann followed")]
    [InlineData(TwitchEventTypes.Raid, FeedKind.Raid, "Ann raided with 42 viewers")]
    [InlineData(TwitchEventTypes.Cheer, FeedKind.Tip, "Ann cheered 500 bits")]
    [InlineData(TwitchEventTypes.GiftSub, FeedKind.GiftSubscription, "Ann gifted 5 tier 1 subs")]
    [InlineData(TwitchEventTypes.StreamOnline, FeedKind.StreamOnline, "Stream went live")]
    public void Activity_events_become_feed_items(string type, FeedKind kind, string title)
    {
        var item = _adapter.ToFeedItem(Event(type, ("user", "Ann"), ("viewers", 42), ("bits", 500), ("total", 5), ("tier", "1")))!;
        Assert.Equal(kind, item.Kind);
        Assert.Equal(title, item.Title);
    }

    [Theory]
    [InlineData(TwitchEventTypes.Cheer, SpendUnit.Bits, 500, "", "Cheer")]
    [InlineData(TwitchEventTypes.PowerUp, SpendUnit.Bits, 500, "", "Power-up")]
    [InlineData(TwitchEventTypes.Sub, SpendUnit.Subscription, 1, "2", "Subscription")]
    [InlineData(TwitchEventTypes.Resub, SpendUnit.Subscription, 1, "2", "Subscription")]
    [InlineData(TwitchEventTypes.GiftSub, SpendUnit.GiftSubscription, 5, "2", "Gift subs")]
    [InlineData(TwitchEventTypes.PayItForward, SpendUnit.GiftSubscription, 1, "2", "Gift subs")]
    public void Paid_events_say_what_was_spent(string type, SpendUnit unit, double quantity, string tier, string label)
    {
        var spend = _adapter.ToFeedItem(Event(type, ("user", "Ann"), ("bits", 500), ("total", 5), ("tier", "2"), ("isGift", false)))!.Spend!;
        Assert.Equal((unit, quantity, label), (spend.Unit, spend.Quantity, spend.Label));
        if (tier.Length > 0) Assert.Equal(tier, spend.Tier);
    }

    [Fact]
    public void Charity_donations_are_marked_and_free_events_spend_nothing()
    {
        var charity = _adapter.ToFeedItem(Event(TwitchEventTypes.CharityDonation, ("user", "Ann"), ("amount", 12.5), ("currency", "eur")))!.Spend!;
        Assert.Equal((SpendUnit.Money, 12.5, "EUR", true), (charity.Unit, charity.Quantity, charity.Currency, charity.IsCharity));
        Assert.Null(_adapter.ToFeedItem(Event(TwitchEventTypes.Follow, ("user", "Ann")))!.Spend);
        Assert.Null(_adapter.ToFeedItem(Event(TwitchEventTypes.Cheer, ("user", "Ann"), ("bits", 0)))!.Spend);
    }

    [Fact]
    public void Gifted_sub_recipients_are_not_listed_twice()
    {
        Assert.Null(_adapter.ToFeedItem(Event(TwitchEventTypes.Sub, ("user", "Recipient"), ("isGift", true))));
        Assert.NotNull(_adapter.ToFeedItem(Event(TwitchEventTypes.Sub, ("user", "Payer"), ("isGift", false), ("tier", "1"))));
    }
}
