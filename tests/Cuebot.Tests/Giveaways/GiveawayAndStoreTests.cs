using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Giveaways;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Store;
using Cuebot.Core.Variables;
using Cuebot.Tests.ChatExtras;

namespace Cuebot.Tests.Giveaways;

public sealed class GiveawayAndStoreTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly ManualClock _clock = new();
    private readonly PointsService _points;
    private readonly TempDir _dir = new();
    private readonly List<JsonObject> _sent = [];
    private readonly List<BotEvent> _events = [];

    public GiveawayAndStoreTests()
    {
        _bus = new EventBus(_log);
        _feed = new OverlayFeed(_log);
        _feed.Subscribe(m => _sent.Add(JsonNode.Parse(m.Json)!.AsObject()));
        _bus.Subscribe(e => { if (e.Type.StartsWith("cuebot.", StringComparison.Ordinal)) _events.Add(e); });
        _points = new PointsService(new UserVariables(null, _log, _clock), null, _bus, _log, time: _clock);
    }

    public void Dispose()
    {
        _points.Dispose();
        _dir.Dispose();
    }

    private Core.Giveaways.Giveaways Giveaways(AppPaths? paths = null, int seed = 1) =>
        new(paths, _log, _bus, _feed, _points, time: _clock, random: new Random(seed));

    private void Chat(string platform, string user, string text, bool subscriber = false) =>
        _bus.Publish(new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["user"] = user, ["userId"] = user.ToLowerInvariant() + "-id", ["message"] = text, ["isSubscriber"] = subscriber,
        }));

    private long Balance(string platform, string user) => _points.Get(platform, user.ToLowerInvariant() + "-id");

    private JsonObject Last(string listener) => _sent.Last(m => m["listener"]?.GetValue<string>() == listener)["event"]!.AsObject();

    // ---- Giveaways --------------------------------------------------------------------------------------------------

    [Fact]
    public void Viewers_on_every_platform_buy_tickets_up_to_the_limit()
    {
        _points.Add("Twitch", "ann-id", "Ann", 1000);
        _points.Add("Kick", "bob-id", "Bob", 150);
        using var giveaways = Giveaways();
        giveaways.Open(new GiveawayOptions { Title = "A game", TicketCost = 100, MaxTickets = 3 });
        Assert.Throws<InvalidOperationException>(() => giveaways.Open(new GiveawayOptions { Title = "Another" }));

        Chat("Twitch", "Ann", "!join 2");
        Chat("Twitch", "Ann", "!join 5");     // only one more fits
        Chat("Kick", "Bob", "!join 2");       // cannot afford two
        Chat("Kick", "Bob", "!join");
        Chat("Rumble", "Cy", "!join");        // no points
        Chat("Rumble", "Cy", "!joinme");
        Assert.Equal((700L, 50L), (Balance("Twitch", "Ann"), Balance("Kick", "Bob")));
        Assert.Equal([("Ann", 3), ("Bob", 1)], giveaways.Entrants.Select(e => (e.UserName, e.Tickets)));
        var state = Last(Core.Giveaways.Giveaways.Listener);
        Assert.Equal((2, 4, "Bob"), (state["entrants"]!.GetValue<int>(), state["tickets"]!.GetValue<int>(), state["recent"]![0]!.GetValue<string>()));
        Assert.Equal(3, _events.Count(e => e.Type == GiveawayEvents.Entered));   // refused attempts publish nothing

        giveaways.Cancel();
        Assert.Equal((1000L, 150L), (Balance("Twitch", "Ann"), Balance("Kick", "Bob")));   // refunded
        Assert.Equal(400L, _events.Single(e => e.Type == GiveawayEvents.Canceled).Args["refunded"]);
    }

    [Fact]
    public void Draws_are_weighted_unique_and_can_add_one_more()
    {
        using var giveaways = Giveaways(seed: 7);
        giveaways.Open(new GiveawayOptions { Title = "Merch", MaxTickets = 1, Winners = 2, SubscriberLuck = 3 });
        foreach (var name in new[] { "Ann", "Bob", "Cy", "Dee" }) Chat("YouTube", name, "!join", subscriber: name == "Dee");
        Chat("YouTube", "Ann", "!join");      // already in, max one ticket

        var drawn = giveaways.Draw()!;
        Assert.Equal("drawn", drawn["status"]!.GetValue<string>());
        Assert.Equal(2, giveaways.Winners.Distinct().Count());
        Chat("YouTube", "Eve", "!join");      // too late
        Assert.Equal(4, giveaways.Entrants.Count);
        Assert.Null(giveaways.Draw());        // already drawn: only "one more" draws again
        giveaways.Draw(oneMore: true);
        giveaways.Draw(oneMore: true);
        Assert.Equal(4, giveaways.Winners.Select(w => w.UserName).Distinct().Count());
        Assert.Null(giveaways.Draw(oneMore: true));   // nobody left
        Assert.Equal(4, _events.Count(e => e.Type == GiveawayEvents.Winner));
        Assert.Null(giveaways.Cancel());      // drawn giveaways are final

        // Luck: over many draws a subscriber with luck 3 wins about half the time (3 of 6 weighted tickets).
        var wins = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            using var g = Giveaways(seed: seed);
            g.Open(new GiveawayOptions { Title = "Luck", SubscriberLuck = 3 });
            foreach (var name in new[] { "A", "B", "C" }) Chat("Twitch", name, "!join");
            Chat("Twitch", "Sub", "!join", subscriber: true);
            g.Draw();
            if (g.Winners[0].UserName == "Sub") wins++;
            g.Dispose();
        }
        Assert.InRange(wins, 160, 240);
    }

    [Fact]
    public void Entries_close_on_time_subscribers_only_works_and_a_restart_keeps_entries()
    {
        var paths = new AppPaths(_dir.Path);
        var giveaways = Giveaways(paths);
        giveaways.Open(new GiveawayOptions { Title = "Sub-only", SubscribersOnly = true, Seconds = 60, Command = "!enter now" });
        Chat("Twitch", "Ann", "!enter");
        Chat("Twitch", "Bob", "!enter", subscriber: true);
        Assert.Equal(["Bob"], giveaways.Entrants.Select(e => e.UserName));
        giveaways.Dispose();

        using var again = Giveaways(paths);
        Assert.Equal(["Bob"], again.Entrants.Select(e => e.UserName));
        _clock.Advance(TimeSpan.FromSeconds(61));
        again.Tick(_clock.GetUtcNow());
        Assert.Equal(Core.Giveaways.Giveaways.ClosedStatus, again.Status);
        Chat("Twitch", "Cy", "!enter", subscriber: true);
        Assert.Single(again.Entrants);
        Assert.Throws<ArgumentException>(() => Giveaways().Open(new GiveawayOptions { Title = " " }));
    }

    // ---- Store ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Viewers_buy_items_with_input_and_stock_and_cooldowns_hold()
    {
        _points.Add("Kick", "ann-id", "Ann", 1000);
        _points.Add("Rumble", "bob-id", "Bob", 1000);
        using var store = new PointsStore(null, _log, _bus, _points, _feed, _clock);
        store.Add(new StoreItem { Name = "Song", Cost = 100, Stock = 2, UserCooldownSeconds = 60 });
        store.Add(new StoreItem { Name = "Song request", Cost = 300 });
        Assert.Throws<ArgumentException>(() => store.Add(new StoreItem { Name = "song" }));

        Chat("Kick", "Ann", "!redeem Song request Never Gonna Give You Up");
        var redeemed = _events.Single(e => e.Type == StoreEvents.Redeemed);
        Assert.Equal(("Song request", "Never Gonna Give You Up", 300L), (redeemed.Args["item"], redeemed.Args["input"], redeemed.Args["cost"]));
        var alert = Last("redemption-latest");
        Assert.Equal(("Ann", "Song request", true, "kick"), (alert["name"]!.GetValue<string>(), alert["redemption"]!.GetValue<string>(),
            alert["store"]!.GetValue<bool>(), alert["service"]!.GetValue<string>()));

        Chat("Kick", "Ann", "!redeem song");
        Chat("Kick", "Ann", "!redeem song");          // her cooldown
        Chat("Rumble", "Bob", "!redeem SONG");
        Chat("Rumble", "Bob", "!redeem nothing");
        Assert.Equal(0, store.Find("Song")!.Stock);
        Assert.Equal((PointsStore.Refusal.SoldOut, 900L), (store.Redeem("Song", "Rumble", "bob-id", "Bob").Refusal, Balance("Rumble", "Bob")));
        Assert.Equal(600L, Balance("Kick", "Ann"));
        _clock.Advance(TimeSpan.FromSeconds(61));
        store.Update(store.Find("Song")!.Id, i => i.Stock = -1);
        Assert.Equal(PointsStore.Refusal.None, store.Redeem("Song", "Kick", "ann-id", "Ann").Refusal);
        Assert.Equal(PointsStore.Refusal.NotEnoughPoints, store.Redeem("Song request", "Twitch", "cy-id", "Cy").Refusal);
        Assert.Equal(4, store.History.Count);
    }

    [Fact]
    public void Approval_items_wait_and_rejecting_refunds_and_restocks()
    {
        _points.Add("Twitch", "ann-id", "Ann", 500);
        var paths = new AppPaths(_dir.Path);
        var store = new PointsStore(paths, _log, _bus, _points, _feed, _clock);
        var vip = store.Add(new StoreItem { Name = "VIP for a day", Cost = 400, Stock = 1, NeedsApproval = true, SubscribersOnly = true });
        Chat("Twitch", "Ann", "!redeem VIP for a day");          // not a subscriber
        Assert.Empty(store.Pending);
        Chat("Twitch", "Ann", "!redeem vip for a day please", subscriber: true);
        var pending = Assert.Single(store.Pending);
        Assert.Equal(("please", 100L, 0), (pending.Input, Balance("Twitch", "Ann"), store.Find(vip.Id)!.Stock));
        Assert.Single(_events, e => e.Type == StoreEvents.Pending);
        Assert.DoesNotContain(_events, e => e.Type == StoreEvents.Redeemed);
        store.Dispose();

        using var again = new PointsStore(paths, _log, _bus, _points, _feed, _clock);   // pending purchases survive a restart
        Assert.True(again.Reject(again.Pending[0].Id));
        Assert.Equal((500L, 1), (Balance("Twitch", "Ann"), again.Find(vip.Id)!.Stock));
        Assert.Equal(StoreRedemption.Rejected, again.History[0].Status);
        Assert.False(again.Approve("nope"));
    }
}
