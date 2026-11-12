using System.Text.Json.Nodes;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.UiTests;

/// <summary>With the app's real registrations, every platform's events reach overlay pages as StreamElements alerts.</summary>
public sealed class OverlayEventWiringTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-overlay-events-").FullName;
    private readonly AppHost _host;

    public OverlayEventWiringTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("twitch.follow", "follower-latest", "twitch")]
    [InlineData("kick.kicksGifted", "kicks-latest", "kick")]
    [InlineData("rumble.rant", "tip-latest", "rumble")]
    [InlineData("youtube.superChat", "superchat-latest", "youtube")]
    [InlineData("streamelements.tip", "tip-latest", "donations")]
    public void Platform_events_reach_the_overlay_feed(string type, string listener, string provider)
    {
        _host.Get<OverlayEventBridge>();
        var sent = new List<JsonObject>();
        using var subscription = _host.Get<OverlayFeed>().Subscribe(m => sent.Add(JsonNode.Parse(m.Json)!.AsObject()));

        _host.Get<EventBus>().Publish(new BotEvent(type, new Dictionary<string, object?> { ["user"] = "Kris", ["amount"] = 5 }));

        Assert.Equal(2, sent.Count);
        Assert.Equal(listener, sent[0]["listener"]!.GetValue<string>());
        Assert.Equal("Kris", sent[0]["event"]!["name"]!.GetValue<string>());
        Assert.Equal(provider, sent[1]["event"]!["provider"]!.GetValue<string>());
    }

    [Fact]
    public void Every_emulate_button_converts_through_the_real_adapters()
    {
        var bridge = _host.Get<OverlayEventBridge>();
        var expected = new Dictionary<string, string>
        {
            ["chat"] = "message", ["follow"] = "follower-latest", ["sub"] = "subscriber-latest", ["resub"] = "subscriber-latest",
            ["gift"] = "subscriber-latest", ["cheer"] = "cheer-latest", ["raid"] = "raid-latest", ["kicks"] = "kicks-latest",
            ["superchat"] = "superchat-latest", ["tip"] = "tip-latest", ["merch"] = "merch-latest", ["charity"] = "charityCampaignDonation-latest",
            ["membership"] = "subscriber-latest", ["upgrade"] = "subscriber-latest", ["shop"] = "merch-latest", ["commission"] = "merch-latest",
            ["order"] = "merch-latest", ["gifts"] = "merch-latest",
        };
        // The alert's service names where it came from (alert box variations and the {platform} token use it).
        var services = new Dictionary<string, string>
        {
            ["donations"] = "donations", ["kofi"] = "kofi", ["patreon"] = "patreon", ["fourthwall"] = "fourthwall", ["shopify"] = "shopify",
        };
        foreach (var (platform, kinds) in OverlayEmulator.Kinds)
        {
            foreach (var kind in kinds)
            {
                var e = OverlayEmulator.Create(platform, kind, new Random(1));
                Assert.NotNull(e);
                var messages = bridge.Convert(e);
                Assert.True(messages.Count > 0 && messages[0].Listener == expected[kind], $"{platform} {kind}: {string.Join(", ", messages.Select(m => m.Listener))}");
                if (kind == "chat") continue;
                var alert = messages[0].Event!.AsObject();
                Assert.Equal(services.GetValueOrDefault(platform, platform), alert["service"]!.GetValue<string>());
                Assert.False(alert["isCatchUp"]!.GetValue<bool>(), $"{platform} {kind} must always play");
                Assert.Equal(e.Args["user"], alert["name"]!.GetValue<string>());
            }
        }
        Assert.Null(OverlayEmulator.Create("rumble", "kicks"));
        Assert.Null(OverlayEmulator.Create("shopify", "tip"));
    }

    [Theory]
    [InlineData("kofi", "membership", "Bronze", 1)]
    [InlineData("kofi", "resub", "Bronze", 3)]
    [InlineData("patreon", "membership", "Supporter", 1)]
    [InlineData("patreon", "upgrade", "Super Supporter", 1)]
    public void Emulated_memberships_carry_the_tier_and_monthly_price(string platform, string kind, string tierName, int months)
    {
        var bridge = _host.Get<OverlayEventBridge>();
        var alert = bridge.Convert(OverlayEmulator.Create(platform, kind, new Random(1))!)[0].Event!.AsObject();
        Assert.Equal((tierName, months, "USD"), (alert["tierName"]!.GetValue<string>(), alert["amount"]!.GetValue<int>(), alert["currency"]!.GetValue<string>()));
        Assert.True(OverlayAlerts.NumberOf(alert["price"]) > 0);
        if (kind == "upgrade") Assert.Equal("Supporter", alert["previousTier"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("donations", "merch", "Cuebot hoodie")]
    [InlineData("kofi", "shop", "Sticker pack, Poster")]
    [InlineData("kofi", "commission", "Commission")]
    [InlineData("fourthwall", "order", "Cuebot T-shirt, Sticker pack")]
    [InlineData("fourthwall", "gifts", "3 x Cuebot T-shirt (gift)")]
    [InlineData("shopify", "order", "Cuebot mug, Sticker pack")]
    public void Emulated_orders_name_what_was_bought(string platform, string kind, string items)
    {
        var bridge = _host.Get<OverlayEventBridge>();
        var alert = bridge.Convert(OverlayEmulator.Create(platform, kind, new Random(1))!)[0].Event!.AsObject();
        Assert.Equal(items, alert["items"]!.GetValue<string>());
        Assert.True(OverlayAlerts.NumberOf(alert["amount"]) > 0);
    }

    [Theory]
    [InlineData("streamlabs.donation", "isBackfill", "tip-latest")]
    [InlineData("streamelements.tip", "isBackfill", "tip-latest")]
    [InlineData("streamelements.merch", "isBackfill", "merch-latest")]
    [InlineData("kofi.donation", "isReplay", "tip-latest")]
    [InlineData("fourthwall.donation", "isReplay", "tip-latest")]
    [InlineData("fourthwall.orderPlaced", "isReplay", "merch-latest")]
    [InlineData("shopify.orderPaid", "isReplay", "merch-latest")]
    [InlineData("patreon.pledgeCreated", "isStale", "subscriber-latest")]
    public void Catch_up_events_are_marked_for_the_alert_box_but_still_reach_lists_and_totals(string type, string flag, string listener)
    {
        _host.Get<OverlayEventBridge>();
        var session = _host.Get<Cuebot.Core.Overlays.Session.SessionDataStore>();
        var sent = new List<JsonObject>();
        using var subscription = _host.Get<OverlayFeed>().Subscribe(m => sent.Add(JsonNode.Parse(m.Json)!.AsObject()));
        var bus = _host.Get<EventBus>();
        Dictionary<string, object?> Args(bool late) => new()
        {
            ["user"] = "Kris", ["amount"] = 5.0, ["currency"] = "USD", ["items"] = "Mug", [flag] = late,
            ["isPurchase"] = true, ["isCompleted"] = true,
        };

        bus.Publish(new BotEvent(type, Args(late: false)));
        bus.Publish(new BotEvent(type, Args(late: true)));

        var alerts = sent.Where(m => m["listener"]?.GetValue<string>() == listener).Select(m => m["event"]!).ToList();
        Assert.Equal([false, true], alerts.Select(a => a["isCatchUp"]!.GetValue<bool>()));
        // Event lists read the raw "event" listener; it carries the flag too.
        Assert.Equal(2, sent.Count(m => m["listener"]?.GetValue<string>() == "event"));
        var type0 = listener.Replace("-latest", "", StringComparison.Ordinal);
        Assert.Equal(2, session.Snapshot()[$"{type0}-session"]!["count"]!.GetValue<long>());   // the money still counts
    }

    [Fact]
    public void A_Pally_replay_alert_is_not_a_catch_up()
    {
        _host.Get<OverlayEventBridge>();
        var sent = new List<JsonObject>();
        using var subscription = _host.Get<OverlayFeed>().Subscribe(m => sent.Add(JsonNode.Parse(m.Json)!.AsObject()));
        // The streamer pressed "Replay Alert" in Pally.gg's activity feed: it should play again.
        _host.Get<EventBus>().Publish(new BotEvent("pally.campaignTip", new Dictionary<string, object?>
        {
            ["user"] = "Kris", ["amount"] = 5.0, ["currency"] = "USD", ["isReplay"] = true,
        }));
        var alert = sent.Single(m => m["listener"]?.GetValue<string>() == "tip-latest")["event"]!;
        Assert.False(alert["isCatchUp"]!.GetValue<bool>());
    }

    [Fact]
    public void Emulated_Ko_fi_and_Patreon_events_sent_to_everything_never_reach_top_spenders()
    {
        _host.Get<OverlayEventBridge>();
        _host.Get<Cuebot.Core.Chat.ChatHub>();
        var ledger = _host.Get<Cuebot.Core.Spending.SpendingLedger>();
        var bus = _host.Get<EventBus>();
        foreach (var (platform, kind) in new[] { ("kofi", "tip"), ("patreon", "membership"), ("fourthwall", "order"), ("shopify", "order") })
        {
            var e = OverlayEmulator.Create(platform, kind, new Random(1))!;
            // As emulate_event's "everything" target sends them.
            bus.Publish(new BotEvent(e.Type, new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase) { ["isTest"] = true, ["isEmulated"] = true }));
        }
        var top = ledger.Top(Cuebot.Core.Spending.SpendingPeriod.AllTime);
        Assert.Equal((0, 0), (top.Spenders.Count, top.Pending));

        // The same sample as a real event does count, so the check above means something.
        bus.Publish(OverlayEmulator.Create("kofi", "tip", new Random(1))!);
        top = ledger.Top(Cuebot.Core.Spending.SpendingPeriod.AllTime);
        Assert.True(top.Spenders.Count + top.Pending > 0);
    }

    [Fact]
    public void An_emulated_Twitch_gift_bomb_also_sends_the_gift_total_and_the_recipients_like_Twitch()
    {
        _host.Get<OverlayEventBridge>();
        var session = _host.Get<Cuebot.Core.Overlays.Session.SessionDataStore>();
        var hub = _host.Get<Cuebot.Core.Chat.ChatHub>();
        var bomb = OverlayEmulator.Create("twitch", "gift", new Random(1))!;
        var bus = _host.Get<EventBus>();

        bus.Publish(bomb);
        Assert.Empty(hub.RecentFeed());   // the chat notice is for overlays; the feed counts the gift total
        foreach (var companion in OverlayEmulator.Companions(bomb)) bus.Publish(companion);

        var item = Assert.Single(hub.RecentFeed());
        Assert.Equal(Cuebot.Core.Chat.SpendUnit.GiftSubscription, item.Spend!.Unit);
        Assert.Equal(5, item.Spend.Quantity);
        Assert.Equal(bomb.Args["user"], item.User);   // one feed item: the recipients' sub gifts are not repeated there
        var data = session.Snapshot();
        Assert.Equal(5, data["subscriber-session"]!["count"]!.GetValue<long>());
        Assert.Equal(5, data["subscriber-gifted-session"]!["count"]!.GetValue<long>());
        Assert.Empty(OverlayEmulator.Companions(OverlayEmulator.Create("twitch", "cheer")!));
    }

    [Fact]
    public void Alerts_update_the_stream_totals()
    {
        _host.Get<OverlayEventBridge>();
        var session = _host.Get<Cuebot.Core.Overlays.Session.SessionDataStore>();
        var bus = _host.Get<EventBus>();
        bus.Publish(new BotEvent("rumble.rant", new Dictionary<string, object?> { ["user"] = "Kris", ["amount"] = 5.0 }));
        bus.Publish(new BotEvent("streamelements.tip", new Dictionary<string, object?> { ["user"] = "Lee", ["amount"] = 12.5 }));
        bus.Publish(new BotEvent("kick.follow", new Dictionary<string, object?> { ["user"] = "Ash" }));

        var data = session.Snapshot();
        Assert.Equal(17.5, OverlayAlerts.NumberOf(data["tip-session"]!["amount"]));
        Assert.Equal(2, data["tip-session"]!["count"]!.GetValue<long>());
        Assert.Equal("Lee", data["tip-session-top-donation"]!["name"]!.GetValue<string>());
        Assert.Equal(1, data["follower-session"]!["count"]!.GetValue<long>());
    }
}
