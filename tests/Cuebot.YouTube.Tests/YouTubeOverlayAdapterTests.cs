using Cuebot.Core.Events;

namespace Cuebot.YouTube.Tests;

public sealed class YouTubeOverlayAdapterTests
{
    private readonly YouTubeOverlayAdapter _adapter = new();

    private static BotEvent E(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Roles_become_badges()
    {
        var badges = _adapter.Badges(new Dictionary<string, object?> { ["isBroadcaster"] = true, ["isMember"] = true, ["isModerator"] = false });
        Assert.Equal(["owner", "member"], badges.Select(b => b.Type));
        Assert.Equal("/runtime/badge-subscriber.svg", badges[1].Url);
    }

    [Fact]
    public void Memberships_are_subscriber_alerts_and_subscribers_are_follows()
    {
        Assert.Equal("follower-latest", _adapter.ToAlert(E(YouTubeEventTypes.NewSubscriber, ("user", "Kris")))!.Listener);

        var milestone = _adapter.ToAlert(E(YouTubeEventTypes.MemberMilestone, ("user", "Kris"), ("months", 8), ("levelName", "Gold")))!;
        Assert.Equal(("subscriber-latest", 8, "Gold"), (milestone.Listener, milestone.Event["amount"]!.GetValue<int>(),
            milestone.Event["level"]!.GetValue<string>()));

        var gifts = _adapter.ToAlert(E(YouTubeEventTypes.MembershipGift, ("user", "Gifter"), ("total", 20)))!;
        Assert.Equal((20, true), (gifts.Event["amount"]!.GetValue<int>(), gifts.Event["bulkGifted"]!.GetValue<bool>()));

        var received = _adapter.ToAlert(E(YouTubeEventTypes.GiftMembershipReceived, ("user", "Lucky"), ("gifterName", "Gifter")))!;
        Assert.Equal(("Lucky", "Gifter", true), (received.Event["name"]!.GetValue<string>(), received.Event["sender"]!.GetValue<string>(),
            received.Event["gifted"]!.GetValue<bool>()));
    }

    [Fact]
    public void Super_chats_stickers_and_jewels()
    {
        var chat = _adapter.ToAlert(E(YouTubeEventTypes.SuperChat, ("user", "Kris"), ("amount", 10.0), ("currency", "EUR"),
            ("amountText", "€10.00"), ("message", "hello")))!;
        Assert.Equal(("superchat-latest", 10, "EUR", "€10.00", false), (chat.Listener, chat.Event["amount"]!.GetValue<int>(),
            chat.Event["currency"]!.GetValue<string>(), chat.Event["formattedAmount"]!.GetValue<string>(), chat.Event["sticker"]!.GetValue<bool>()));
        Assert.True(_adapter.ToAlert(E(YouTubeEventTypes.SuperSticker, ("user", "Kris"), ("amount", 2.0)))!.Event["sticker"]!.GetValue<bool>());
        Assert.Equal("jewels-latest", _adapter.ToAlert(E(YouTubeEventTypes.JewelsGifted, ("user", "Kris"), ("amount", 50)))!.Listener);
        Assert.Null(_adapter.ToAlert(E(YouTubeEventTypes.ChatMessage)));
    }
}
