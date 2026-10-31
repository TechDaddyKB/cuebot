using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.Twitch.Tests;

public sealed class TwitchOverlayAdapterTests
{
    private readonly Dictionary<string, (string Url, string Title)> _badges = new()
    {
        ["subscriber/12"] = ("https://cdn.test/sub12.png", "1-Year Subscriber"),
    };

    private TwitchOverlayAdapter Adapter() => new(() => _badges);

    private static BotEvent E(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    private static JsonObject Alert(TwitchOverlayAdapter adapter, BotEvent e, string listener)
    {
        var alert = adapter.ToAlert(e);
        Assert.NotNull(alert);
        Assert.Equal(listener, alert.Listener);
        return alert.Event;
    }

    [Fact]
    public void Badges_use_twitch_images_and_fall_back_to_built_in_icons()
    {
        var badges = Adapter().Badges(new Dictionary<string, object?> { ["badges"] = "moderator/1,subscriber/12,glitchcon2020/1" });

        Assert.Equal(2, badges.Count);
        Assert.Equal(new OverlayBadge("moderator", "1", "/runtime/badge-moderator.svg", "moderator"), badges[0]);
        Assert.Equal(new OverlayBadge("subscriber", "12", "https://cdn.test/sub12.png", "1-Year Subscriber"), badges[1]);
    }

    [Fact]
    public void Subs_resubs_and_upgrades_are_subscriber_alerts()
    {
        var adapter = Adapter();
        var sub = Alert(adapter, E(TwitchEventTypes.Sub, ("user", "Kris"), ("userId", "1"), ("tier", "2")), "subscriber-latest");
        Assert.Equal(("Kris", 1, "2000", false), (sub["name"]!.GetValue<string>(), sub["amount"]!.GetValue<int>(),
            sub["tier"]!.GetValue<string>(), sub["gifted"]!.GetValue<bool>()));

        var resub = Alert(adapter, E(TwitchEventTypes.Resub, ("user", "Kris"), ("tier", "1"), ("cumulativeMonths", 14), ("message", "hi")),
            "subscriber-latest");
        Assert.Equal((14, "hi"), (resub["amount"]!.GetValue<int>(), resub["message"]!.GetValue<string>()));

        Assert.NotNull(adapter.ToAlert(E(TwitchEventTypes.PrimePaidUpgrade, ("user", "Kris"), ("tier", "1"))));
        // The recipient of a gift arrives as subGift (with the gifter); channel.subscribe's copy is skipped.
        Assert.Null(adapter.ToAlert(E(TwitchEventTypes.Sub, ("user", "Kris"), ("isGift", true))));
        Assert.Null(adapter.ToAlert(E(TwitchEventTypes.GiftSub, ("user", "Kris"), ("total", 5))));
    }

    [Fact]
    public void Gifts_carry_sender_recipient_and_community_flags()
    {
        var adapter = Adapter();
        var bomb = Alert(adapter, E(TwitchEventTypes.GiftBomb, ("user", "Gifter"), ("total", 5), ("tier", "1")), "subscriber-latest");
        Assert.Equal(("Gifter", 5, true, "Gifter"), (bomb["name"]!.GetValue<string>(), bomb["amount"]!.GetValue<int>(),
            bomb["bulkGifted"]!.GetValue<bool>(), bomb["sender"]!.GetValue<string>()));

        var gift = Alert(adapter, E(TwitchEventTypes.SubGift, ("user", "Gifter"), ("recipientUser", "Lucky"), ("recipientUserId", "7"),
            ("tier", "1"), ("subBombCount", 5)), "subscriber-latest");
        Assert.Equal(("Lucky", "Gifter", true, true, "7"), (gift["name"]!.GetValue<string>(), gift["sender"]!.GetValue<string>(),
            gift["gifted"]!.GetValue<bool>(), gift["isCommunityGift"]!.GetValue<bool>(), gift["providerId"]!.GetValue<string>()));

        var single = Alert(adapter, E(TwitchEventTypes.SubGift, ("user", "Gifter"), ("recipientUser", "Lucky"), ("subBombCount", 0)),
            "subscriber-latest");
        Assert.False(single["isCommunityGift"]!.GetValue<bool>());
    }

    [Fact]
    public void Follows_cheers_raids_and_redemptions()
    {
        var adapter = Adapter();
        Assert.Equal("follower", Alert(adapter, E(TwitchEventTypes.Follow, ("user", "Kris")), "follower-latest")["type"]!.GetValue<string>());
        Assert.Equal(500, Alert(adapter, E(TwitchEventTypes.Cheer, ("user", "Kris"), ("bits", 500)), "cheer-latest")["amount"]!.GetValue<int>());
        Assert.Equal(42, Alert(adapter, E(TwitchEventTypes.Raid, ("user", "Kris"), ("viewers", 42)), "raid-latest")["amount"]!.GetValue<int>());
        var reward = Alert(adapter, E(TwitchEventTypes.RewardRedemption, ("user", "Kris"), ("rewardTitle", "Hydrate"), ("rewardCost", 100),
            ("userInput", "now")), "redemption-latest");
        Assert.Equal(("Hydrate", 100, "now"), (reward["redemption"]!.GetValue<string>(), reward["amount"]!.GetValue<int>(),
            reward["message"]!.GetValue<string>()));
        Assert.Null(adapter.ToAlert(E(TwitchEventTypes.ChatMessage, ("user", "Kris"))));
    }

    [Fact]
    public void Charity_donations_are_alerts_and_the_campaign_goes_to_charity_widgets()
    {
        var donation = Alert(Adapter(), E(TwitchEventTypes.CharityDonation, ("user", "Kris"), ("amount", 25.0), ("currency", "USD"),
            ("charityName", "Hearts")), "charityCampaignDonation-latest");
        Assert.Equal(("Kris", 25.0, "Hearts"), (donation["name"]!.GetValue<string>(), OverlayAlerts.NumberOf(donation["amount"]), donation["charity"]!.GetValue<string>()));

        var progress = Alert(Adapter(), E(TwitchEventTypes.CharityProgress, ("charityName", "Hearts"), ("currentAmount", 125.5), ("targetAmount", 500.0),
            ("currency", "USD"), ("charityLogo", "https://example.com/logo.png")), "cuebot-charity");
        Assert.Equal(("Hearts", 125.5, 500, "active"), (progress["charity"]!.GetValue<string>(), progress["raised"]!.GetValue<double>(),
            progress["target"]!.GetValue<int>(), progress["status"]!.GetValue<string>()));
        Assert.Equal("ended", Alert(Adapter(), E(TwitchEventTypes.CharityCompleted, ("charityName", "Hearts")), "cuebot-charity")["status"]!.GetValue<string>());
    }
}
