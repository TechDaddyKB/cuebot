using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.Kick.Tests;

public sealed class KickOverlayAdapterTests
{
    private readonly KickOverlayAdapter _adapter = new();

    private static BotEvent E(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Badges_come_from_the_badge_list_as_built_in_icons()
    {
        var badges = _adapter.Badges(new Dictionary<string, object?>
        {
            ["badge"] = new List<Dictionary<string, object?>>
            {
                new() { ["id"] = "moderator", ["name"] = "Moderator", ["info"] = 0 },
                new() { ["id"] = "subscriber", ["name"] = "Subscriber", ["info"] = 6 },
                new() { ["id"] = "unknown_badge", ["name"] = "?", ["info"] = 0 },
            },
        });

        Assert.Equal(2, badges.Count);
        Assert.Equal(new OverlayBadge("moderator", "1", "/runtime/badge-moderator.svg", "Moderator"), badges[0]);
        Assert.Equal(new OverlayBadge("subscriber", "6", "/runtime/badge-subscriber.svg", "Subscriber"), badges[1]);
        Assert.Empty(_adapter.Badges(new Dictionary<string, object?> { ["badge"] = "not a list" }));
    }

    [Fact]
    public void Subs_gifts_kicks_and_hosts()
    {
        var resub = _adapter.ToAlert(E(KickEventTypes.Resubscription, ("user", "Kris"), ("months", 3)))!;
        Assert.Equal(("subscriber-latest", 3), (resub.Listener, resub.Event["amount"]!.GetValue<int>()));

        var gift = _adapter.ToAlert(E(KickEventTypes.GiftSubscription, ("user", "Gifter"), ("recipient", "Lucky"), ("recipientId", "9"),
            ("fromMassGift", true)))!;
        Assert.Equal(("Lucky", "Gifter", true, "9"), (gift.Event["name"]!.GetValue<string>(), gift.Event["sender"]!.GetValue<string>(),
            gift.Event["isCommunityGift"]!.GetValue<bool>(), gift.Event["providerId"]!.GetValue<string>()));

        var mass = _adapter.ToAlert(E(KickEventTypes.MassGiftSubscription, ("user", "Gifter"), ("gifts", 10)))!;
        Assert.Equal((10, true), (mass.Event["amount"]!.GetValue<int>(), mass.Event["bulkGifted"]!.GetValue<bool>()));

        var kicks = _adapter.ToAlert(E(KickEventTypes.KicksGifted, ("user", "Kris"), ("kicks", 100), ("giftName", "Rage Quit"), ("message", "gg")))!;
        Assert.Equal(("kicks-latest", 100, "Rage Quit", "gg"), (kicks.Listener, kicks.Event["amount"]!.GetValue<int>(),
            kicks.Event["giftName"]!.GetValue<string>(), kicks.Event["message"]!.GetValue<string>()));

        var host = _adapter.ToAlert(E(KickEventTypes.Host, ("user", "Friend"), ("viewers", 25)))!;
        Assert.Equal(("raid-latest", 25), (host.Listener, host.Event["amount"]!.GetValue<int>()));
        Assert.Equal("follower-latest", _adapter.ToAlert(E(KickEventTypes.Follow, ("user", "Kris")))!.Listener);
        Assert.Null(_adapter.ToAlert(E(KickEventTypes.ChatMessage)));
    }
}
