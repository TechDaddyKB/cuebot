using Cuebot.Core.Events;

namespace Cuebot.Rumble.Tests;

public sealed class RumbleOverlayAdapterTests
{
    private readonly RumbleOverlayAdapter _adapter = new();

    private static BotEvent E(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Badge_slugs_become_icons_once_each()
    {
        var badges = _adapter.Badges(new Dictionary<string, object?> { ["badges"] = "admin,locals,recurring_subscription,unknown" });
        Assert.Equal(["broadcaster", "subscriber"], badges.Select(b => b.Type));
        Assert.Equal("/runtime/badge-broadcaster.svg", badges[0].Url);
    }

    [Fact]
    public void Rants_are_tips_in_dollars()
    {
        var rant = _adapter.ToAlert(E(RumbleEventTypes.Rant, ("user", "Kris"), ("amount", 5.0), ("currency", "USD"), ("message", "go")))!;
        Assert.Equal(("tip-latest", 5, "USD", "go"), (rant.Listener, rant.Event["amount"]!.GetValue<int>(),
            rant.Event["currency"]!.GetValue<string>(), rant.Event["message"]!.GetValue<string>()));
        var odd = _adapter.ToAlert(E(RumbleEventTypes.Rant, ("user", "Kris"), ("amount", 2.5)))!;
        Assert.Equal(2.5, odd.Event["amount"]!.GetValue<double>());
    }

    [Fact]
    public void Follows_subs_and_gift_subs()
    {
        Assert.Equal("follower-latest", _adapter.ToAlert(E(RumbleEventTypes.Follow, ("user", "Kris")))!.Listener);
        Assert.Equal("subscriber-latest", _adapter.ToAlert(E(RumbleEventTypes.Subscribe, ("user", "Kris")))!.Listener);
        var gifts = _adapter.ToAlert(E(RumbleEventTypes.GiftSub, ("user", "Gifter"), ("total", 3)))!;
        Assert.Equal((3, true), (gifts.Event["amount"]!.GetValue<int>(), gifts.Event["bulkGifted"]!.GetValue<bool>()));
        Assert.Null(_adapter.ToAlert(E(RumbleEventTypes.ChatMessage)));
    }
}
