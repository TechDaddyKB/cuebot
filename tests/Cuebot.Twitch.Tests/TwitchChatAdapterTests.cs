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

    [Fact]
    public void Gifted_sub_recipients_are_not_listed_twice()
    {
        Assert.Null(_adapter.ToFeedItem(Event(TwitchEventTypes.Sub, ("user", "Recipient"), ("isGift", true))));
        Assert.NotNull(_adapter.ToFeedItem(Event(TwitchEventTypes.Sub, ("user", "Payer"), ("isGift", false), ("tier", "1"))));
    }
}
