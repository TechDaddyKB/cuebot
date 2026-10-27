using Cuebot.Core.Chat;
using Cuebot.Rumble.Chat;
using Cuebot.Rumble.LiveApi;
using Cuebot.Rumble.Tests.Support;

namespace Cuebot.Rumble.Tests;

public sealed class EventMappingTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly RumbleChatAdapter _adapter = new();

    private static ChatStreamMessage Message(IReadOnlyList<string> badges, long rantCents = 0, string user = "Bob") =>
        new("m1", "u1", user, "hello", T, badges, "85c742", rantCents);

    [Fact]
    public void Stream_chat_message_carries_twitch_style_arguments()
    {
        var e = RumbleEvents.ChatMessage(Message(["moderator", "premium", "verified"]), "CuebotDev");

        Assert.Equal(RumbleEventTypes.ChatMessage, e.Type);
        Assert.Equal("Bob", e.Args["user"]);
        Assert.Equal("Bob", e.Args["userName"]);
        Assert.Equal("u1", e.Args["userId"]);
        Assert.Equal("hello", e.Args["message"]);
        Assert.Equal("hello", e.Args["rawInput"]);
        Assert.Equal("m1", e.Args["messageId"]);
        Assert.Equal(true, e.Args["isModerator"]);
        Assert.Equal(true, e.Args["isPremium"]);
        Assert.Equal(true, e.Args["isVerified"]);
        Assert.Equal(false, e.Args["isBroadcaster"]);
        Assert.Equal(false, e.Args["isSubscriber"]);
        Assert.Equal("moderator,premium,verified", e.Args["badges"]);
        Assert.Equal("#85c742", e.Args["color"]);
        Assert.Equal(false, e.Args["isRant"]);
    }

    [Theory]
    [InlineData("admin", "Bob")]
    [InlineData("", "cuebotdev")]
    public void The_channel_owner_is_the_broadcaster_by_badge_or_name(string badge, string user)
    {
        var e = RumbleEvents.ChatMessage(Message(badge.Length > 0 ? [badge] : [], user: user), "CuebotDev");

        Assert.Equal(true, e.Args["isBroadcaster"]);
    }

    [Theory]
    [InlineData("recurring_subscription")]
    [InlineData("locals")]
    [InlineData("locals_supporter")]
    public void Channel_supporter_badges_mean_subscriber(string badge) =>
        Assert.Equal(true, RumbleEvents.ChatMessage(Message([badge]), "x").Args["isSubscriber"]);

    [Fact]
    public void Chat_message_maps_to_combined_chat_with_roles_color_and_rant_amount()
    {
        var e = RumbleEvents.ChatMessage(Message(["admin", "moderator", "recurring_subscription", "verified"], rantCents: 500), "CuebotDev");

        var chat = _adapter.ToChatMessage(e);

        Assert.NotNull(chat);
        Assert.Equal(("Rumble", "m1", "Bob", "u1", "hello"), (chat.Platform, chat.Id, chat.User, chat.UserId, chat.Text));
        Assert.Equal(ChatRoles.Broadcaster | ChatRoles.Moderator | ChatRoles.Subscriber | ChatRoles.Verified, chat.Roles);
        Assert.Equal("#85c742", chat.Color);
        Assert.Equal("$5.00", chat.Amount);
        Assert.Null(_adapter.ToFeedItem(e));
    }

    [Fact]
    public void Api_chat_messages_get_a_generated_id_and_no_color()
    {
        var e = RumbleEvents.ChatMessage(new ApiChatMessage("Bob", "hi", T, []), 0, "CuebotDev");

        var chat = _adapter.ToChatMessage(e)!;

        Assert.Equal("", e.Args["userId"]);
        Assert.NotEmpty(chat.Id);
        Assert.Null(chat.Color);
        Assert.Null(chat.Amount);
    }

    [Fact]
    public void Rant_maps_to_a_tip_in_the_feed()
    {
        var e = RumbleEvents.Rant("Bob", "u1", "Great stream", 1050, "m1");

        Assert.Equal(10.5, e.Args["amount"]);
        Assert.Equal("$10.50", e.Args["amountText"]);
        Assert.Equal("USD", e.Args["currency"]);
        var item = _adapter.ToFeedItem(e)!;
        Assert.Equal((FeedKind.Tip, "Bob", "Bob sent a $10.50 rant", "Great stream", "$10.50"),
            (item.Kind, item.User, item.Title, item.Detail, item.Amount));
        Assert.Null(_adapter.ToChatMessage(e));
    }

    [Fact]
    public void Activity_maps_to_feed_items()
    {
        var follow = _adapter.ToFeedItem(RumbleEvents.Follow(new ApiFollower("Alice", T)))!;
        var sub = _adapter.ToFeedItem(RumbleEvents.Subscribe(new ApiSubscriber("Dave", 500, T)))!;
        var gift = _adapter.ToFeedItem(RumbleEvents.GiftSub(new ApiGiftedSub("Frank", 5, 5, "rumble", 1)))!;
        var oneGift = _adapter.ToFeedItem(RumbleEvents.GiftSub(new ApiGiftedSub("Frank", 1, 1, "rumble", 1)))!;
        var stream = Snapshots.Stream();
        var online = _adapter.ToFeedItem(RumbleEvents.StreamOnline(stream, "Cuebot Dev"))!;
        var offline = _adapter.ToFeedItem(RumbleEvents.StreamOffline(stream, "Cuebot Dev"))!;

        Assert.Equal((FeedKind.Follow, "Alice followed"), (follow.Kind, follow.Title));
        Assert.Equal((FeedKind.Subscription, "Dave subscribed", "$5.00"), (sub.Kind, sub.Title, sub.Amount));
        Assert.Equal((FeedKind.GiftSubscription, "Frank gifted 5 subscriptions"), (gift.Kind, gift.Title));
        Assert.Equal("Frank gifted a subscription", oneGift.Title);
        Assert.Equal((FeedKind.StreamOnline, "Cuebot Dev", "Stream went live", "dev testing"), (online.Kind, online.User, online.Title, online.Detail));
        Assert.Equal((FeedKind.StreamOffline, "Stream ended"), (offline.Kind, offline.Title));
        Assert.All([follow, sub, gift, online, offline], i => Assert.Equal("Rumble", i.Platform));
    }

    [Fact]
    public void Stream_events_carry_title_and_stream_id()
    {
        var e = RumbleEvents.StreamOnline(Snapshots.Stream("abc"), "Cuebot Dev");

        Assert.Equal(("dev testing", "abc", "Cuebot Dev"), (e.Args["title"], e.Args["streamId"], e.Args["channel"]));
    }

    [Fact]
    public void Gift_events_name_the_gifter_and_total()
    {
        var e = RumbleEvents.GiftSub(new ApiGiftedSub("Frank", 5, 2, "rumble", 42));

        Assert.Equal(("Frank", 5, 2, "rumble"), (e.Args["user"], e.Args["total"], e.Args["remaining"], e.Args["giftType"]));
    }
}
