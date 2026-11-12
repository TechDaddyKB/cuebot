using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;

namespace Cuebot.Webhooks.Tests;

/// <summary>Ko-fi privacy, replays, product names, overlays, the goal, members and commissions, from payload fixtures.</summary>
public sealed class KofiFeatureTests : IDisposable
{
    private const string Token = "8e7f3a2b-1c4d-4e5f-9a8b-7c6d5e4f3a2b";
    private readonly Harness _h = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly KofiWebhookHandler _kofi;

    public KofiFeatureTests()
    {
        _kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = Token }, Clock = _clock };
        _kofi.Tracker = new KofiTracker(_h.Settings, _h.Bus, _h.Log, time: _clock);
    }

    public void Dispose() => _h.Dispose();

    private WebhookOutcome Send(string json, string? id = null, DateTimeOffset? receivedAt = null) =>
        _kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(json), id: id) with { ReceivedAt = receivedAt ?? _clock.GetUtcNow() });

    private static string Fixture(string name) => Harness.Data(name);

    private static string With(string json, params (string Key, object? Value)[] changes)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var (key, value) in changes) node[key] = JsonSerializer.SerializeToNode(value);
        return node.ToJsonString();
    }

    private static WebhookOverlayAdapter Overlay => WebhookOverlayAdapter.All().Single(a => a.Service == "kofi");

    private static ITriggerType Trigger(string id) => new KofiTriggers().GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string>? settings = null) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());

    [Fact]
    public void A_private_support_never_reaches_triggers_or_overlays_with_its_name_or_message()
    {
        Assert.Equal(WebhookOutcome.Handled, Send(Fixture("kofi-private-donation.json")));
        var e = _h.Events(KofiEventTypes.Donation).Single();
        Assert.Equal(("Anonymous", "", "", false), (e.Args["user"], e.Args["message"], e.Args["from"], e.Args["isPublic"]));
        // Kept for deliberate private use only.
        Assert.Equal(("Secret Sam", "Please do not read my name out", false), (e.Args["privateName"], e.Args["privateMessage"], e.Args["privateShown"]));
        // Personal data Ko-fi sends is never an argument.
        Assert.DoesNotContain(e.Args, a => a.Value is string s && (s.Contains("sam@example.com", StringComparison.Ordinal)));
        Assert.DoesNotContain(e.Args.Keys, k => k.Contains("email", StringComparison.OrdinalIgnoreCase) || k.Contains("shipping", StringComparison.OrdinalIgnoreCase));

        var alert = Overlay.ToAlert(e)!;
        Assert.Equal(("tip-latest", "Anonymous", ""), (alert.Listener, alert.Event["name"]!.GetValue<string>(), alert.Event["message"]!.GetValue<string>()));
        Assert.DoesNotContain("Secret Sam", alert.Event.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("Someone", WebhookChatAdapter.Describe(e)!.User);

        // A "thank %user%: %message%" action posts nothing private.
        var donation = Trigger(KofiEventTypes.Donation);
        Assert.True(Matches(donation, e));
        Assert.True(Matches(donation, e, new() { ["visibility"] = KofiTriggers.PrivateOnly }));
        Assert.False(Matches(donation, e, new() { ["visibility"] = KofiTriggers.PublicOnly }));
    }

    [Fact]
    public void Showing_private_supporters_is_an_opt_in()
    {
        _kofi.Options.ShowPrivateSupporters = true;
        Send(Fixture("kofi-private-donation.json"));
        var e = _h.Events(KofiEventTypes.Donation).Single();
        Assert.Equal(("Secret Sam", "Please do not read my name out", true), (e.Args["user"], e.Args["message"], e.Args["privateShown"]));
        Assert.Equal("Secret Sam", Overlay.ToAlert(e)!.Event["name"]!.GetValue<string>());

        Send(Fixture("kofi-donation.json"));
        var pub = _h.Events(KofiEventTypes.Donation).Last();
        Assert.Equal(("Ko-fi Team", true), (pub.Args["user"], pub.Args["isPublic"]));
        Assert.False(pub.Args.ContainsKey("privateName"));
        Assert.False(Matches(Trigger(KofiEventTypes.Donation), pub, new() { ["visibility"] = KofiTriggers.PrivateOnly }));
    }

    [Fact]
    public void The_private_test_event_hides_the_name_too()
    {
        Assert.True(_kofi.SendTestEvent("privateDonation"));
        var e = _h.Events(KofiEventTypes.Donation).Single();
        Assert.Equal(("Anonymous", "", true), (e.Args["user"], e.Args["message"], e.Args["isTest"]));
    }

    [Fact]
    public void Handled_ids_survive_a_restart_so_a_relay_replay_is_not_counted_twice()
    {
        var path = Path.Combine(Path.GetTempPath(), "cuebot-seen-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            _kofi.Seen = new RecentIds(path: path);
            Assert.Equal(WebhookOutcome.Handled, Send(Fixture("kofi-donation.json"), id: "relay-1"));

            // Cuebot quit before acknowledging; after the restart the relay replays the same delivery.
            var restarted = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Seen = new RecentIds(path: path) };
            restarted.Tracker = _kofi.Tracker;
            var replay = Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(Fixture("kofi-donation.json")), id: "relay-1");
            Assert.Equal(WebhookOutcome.Ignored, restarted.Handle(replay));
            // Ko-fi's own retry (same message_id, new relay id) is dropped as well.
            Assert.Equal(WebhookOutcome.Ignored, restarted.Handle(replay with { Id = "relay-2" }));
            Assert.Single(_h.Events(KofiEventTypes.Donation));
            Assert.Equal(3.0, _kofi.Tracker.Goal().Raised);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Old_ids_are_forgotten_after_two_days()
    {
        var path = Path.Combine(Path.GetTempPath(), "cuebot-seen-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.True(new RecentIds(path: path, time: _clock).AddAll(["kofi:a"]));
            _clock.Advance(TimeSpan.FromHours(49));
            var reloaded = new RecentIds(path: path, time: _clock);
            Assert.False(reloaded.Contains("kofi:a"));
            Assert.True(new RecentIds(path: path, time: _clock).AddAll(["kofi:b"]));
            Assert.True(new RecentIds(path: path, time: _clock).Contains("kofi:b"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Late_deliveries_are_marked_and_triggers_can_skip_them_while_the_goal_still_counts()
    {
        _kofi.Options.Goal.Target = 100;
        Send(Fixture("kofi-donation.json"), receivedAt: _clock.GetUtcNow().AddMinutes(-2));
        Send(With(Fixture("kofi-donation.json"), ("message_id", "late-1")), receivedAt: _clock.GetUtcNow().AddHours(-3));
        var (onTime, late) = (_h.Events(KofiEventTypes.Donation)[0], _h.Events(KofiEventTypes.Donation)[1]);
        Assert.Equal((false, true), (onTime.Args["isReplay"], late.Args["isReplay"]));
        Assert.Equal(3L * 3600, late.Args["delaySeconds"]);

        var trigger = Trigger(KofiEventTypes.Donation);
        Assert.True(Matches(trigger, late));
        Assert.False(Matches(trigger, late, new() { ["skipLate"] = "true" }));
        Assert.True(Matches(trigger, onTime, new() { ["skipLate"] = "true" }));
        Assert.Equal(6.0, _kofi.Tracker.Goal().Current);
        Assert.True(Overlay.ToAlert(late)!.Event["isReplay"]!.GetValue<bool>());
    }

    [Fact]
    public void Tier_filters_ignore_case_and_spaces_and_take_lists()
    {
        var sub = Trigger(KofiEventTypes.Subscription);
        BotEvent Tier(string t) => new(KofiEventTypes.Subscription, new Dictionary<string, object?> { ["tier"] = t, ["amount"] = 5m, ["currency"] = "USD" });
        Assert.True(Matches(sub, Tier("Gold Tier"), new() { ["tier"] = "goldtier" }));
        Assert.True(Matches(sub, Tier("Silver"), new() { ["tier"] = "Gold, silver" }));
        Assert.False(Matches(sub, Tier("Bronze"), new() { ["tier"] = "Gold, Silver" }));
        Assert.True(Matches(sub, Tier("Bronze")));
    }

    [Fact]
    public void Shop_items_use_product_names_from_settings_and_never_show_a_bare_code()
    {
        var order = JsonNode.Parse(Fixture("kofi-shop-order.json"))!.AsObject();
        order["shop_items"] = new JsonArray(
            new JsonObject { ["direct_link_code"] = "1a2b3c4d5e", ["variation_name"] = "", ["quantity"] = 1 },
            new JsonObject { ["direct_link_code"] = "9z8y7x6w5v", ["variation_name"] = "A2", ["quantity"] = 1 },
            new JsonObject { ["direct_link_code"] = "0000000000", ["variation_name"] = "", ["quantity"] = 3 });
        _kofi.Options.Products = [new() { Code = "1a2b3c4d5e", Name = "Sticker pack", ImageUrl = "https://example.com/s.png" },
            new() { Code = "9Z8Y7X6W5V", Name = "Poster" }];
        Send(order.ToJsonString());
        var e = _h.Events(KofiEventTypes.ShopOrder).Single();
        Assert.Equal(("Sticker pack", "https://example.com/s.png", "1a2b3c4d5e"), (e.Args["item0"], e.Args["item0.image"], e.Args["item0.code"]));
        Assert.Equal(("Poster (A2)", "A2"), (e.Args["item1"], e.Args["item1.variation"]));
        Assert.Equal("item 0000000000", e.Args["item2"]);
        Assert.Contains("Sticker pack", Overlay.ToAlert(e)!.Event["items"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(e.Args, a => a.Value is string s && s.Contains("123 Example St", StringComparison.Ordinal));
    }

    [Fact]
    public void Memberships_and_commissions_become_overlay_alerts()
    {
        var first = With(Fixture("kofi-subscription.json"), ("is_first_subscription_payment", true), ("is_public", true), ("message_id", "m1"));
        Send(first);
        Send(With(Fixture("kofi-subscription.json"), ("is_public", true), ("message_id", "m2")));
        Send(With(Fixture("kofi-donation.json"), ("type", "Commission"), ("message_id", "c1"), ("message", "A cat portrait")));

        var sub = Overlay.ToAlert(_h.Events(KofiEventTypes.Subscription).Single())!;
        Assert.Equal(("subscriber-latest", "Quiet Supporter", 1.0, "Gold"), (sub.Listener, sub.Event["name"]!.GetValue<string>(),
            OverlayAlerts.NumberOf(sub.Event["amount"]), sub.Event["tierName"]!.GetValue<string>()));
        var resub = Overlay.ToAlert(_h.Events(KofiEventTypes.Resubscription).Single())!;
        Assert.Equal(("subscriber-latest", 2.0), (resub.Listener, OverlayAlerts.NumberOf(resub.Event["amount"])));
        Assert.Equal(2, _h.Events(KofiEventTypes.Resubscription).Single().Args["months"]);
        var commission = Overlay.ToAlert(_h.Events(KofiEventTypes.Commission).Single())!;
        Assert.Equal(("merch-latest", "Commission"), (commission.Listener, commission.Event["items"]!.GetValue<string>()));
        // Only the Ko-fi adapter claims it (the Fourthwall one is asked first and used to, making the alert "fourthwall").
        Assert.All(WebhookOverlayAdapter.All().Where(a => a.Service != "kofi"),
            a => Assert.Null(a.ToAlert(_h.Events(KofiEventTypes.Commission).Single())));
    }

    [Fact]
    public void Discord_linked_supporters_can_be_mentioned_or_messaged()
    {
        Send(With(Fixture("kofi-subscription.json"), ("discord_username", "quiet"), ("discord_userid", "012345678901234567")));
        var e = _h.Events(KofiEventTypes.Resubscription).Single();
        Assert.Equal(("012345678901234567", "<@012345678901234567>"), (e.Args["discordUserId"], e.Args["discordMention"]));
    }

    [Fact]
    public void The_goal_counts_real_new_payments_in_its_currency_and_is_reached_once()
    {
        var goal = _kofi.Options.Goal;
        goal.Target = 20;
        goal.StartAmount = 5;
        goal.Currency = "USD";
        goal.CountShopOrders = false;

        Send(Fixture("kofi-donation.json"));                                                   // 3 USD
        Send(Fixture("kofi-donation.json"), id: "again");                                      // Ko-fi retry: not counted
        _kofi.SendTestEvent("donation");                                                        // test: not counted
        Send(Fixture("kofi-subscription.json"));                                                // EUR: not counted
        Send(Fixture("kofi-shop-order.json"));                                                  // shop orders switched off
        Assert.Equal((8.0, 40, false), (_kofi.Tracker.Goal().Current, _kofi.Tracker.Goal().Percent, _kofi.Tracker.Goal().Reached));
        Assert.Equal(8m, _h.Events(KofiEventTypes.Donation)[0].Args["kofiGoalCurrent"]);
        Assert.Empty(_h.Events(KofiEventTypes.GoalReached));

        Send(With(Fixture("kofi-donation.json"), ("message_id", "big"), ("amount", "12.50")));
        var reached = Assert.Single(_h.Events(KofiEventTypes.GoalReached));
        Assert.Equal(("Ko-fi Team", 20.5m, 102), (reached.Args["user"], reached.Args["kofiGoalCurrent"], reached.Args["kofiGoalPercent"]));
        Send(With(Fixture("kofi-donation.json"), ("message_id", "more")));
        Assert.Single(_h.Events(KofiEventTypes.GoalReached));
        Assert.True(Matches(Trigger(KofiEventTypes.GoalReached), reached));

        // A higher target lets it be reached again; a reset starts from the start amount.
        _kofi.Tracker.SetGoal(target: 30);
        Assert.False(_kofi.Tracker.Goal().Reached);
        _kofi.Tracker.AddToGoal(10);
        Assert.Equal(2, _h.Events(KofiEventTypes.GoalReached).Length);
        Assert.Equal(5.0, _kofi.Tracker.ResetGoal().Current);

        var data = new JsonObject();
        _kofi.Tracker.AddTo(data);
        Assert.Equal((5.0, 30.0, "USD"), (data["kofi-goal"]!["current"]!.GetValue<double>(), data["kofi-goal"]!["target"]!.GetValue<double>(),
            data["kofi-goal"]!["currency"]!.GetValue<string>()));
    }

    [Fact]
    public void Members_whose_last_payment_is_too_old_are_reported_once_as_probably_lapsed()
    {
        _kofi.Options.LapseDays = 35;
        Send(With(Fixture("kofi-subscription.json"), ("is_first_subscription_payment", true), ("message_id", "s1")));
        Send(With(Fixture("kofi-donation.json"), ("type", "Subscription"), ("is_first_subscription_payment", true), ("message_id", "s2"),
            ("tier_name", "Bronze"), ("from_name", "Bea")));
        _clock.Advance(TimeSpan.FromDays(20));
        Send(With(Fixture("kofi-donation.json"), ("type", "Subscription"), ("message_id", "s3"), ("tier_name", "Bronze"), ("from_name", "Bea")));
        Assert.Equal(2, _h.Events(KofiEventTypes.Resubscription).Single().Args["months"]);

        _clock.Advance(TimeSpan.FromDays(16));
        Assert.Equal(1, _kofi.Tracker.CheckLapses(_clock.GetUtcNow()));
        var lapsed = Assert.Single(_h.Events(KofiEventTypes.MembershipLapsed));
        // The private member stays anonymous; the real name is only in privateName.
        Assert.Equal(("Anonymous", "Quiet Supporter", "Gold", 36, true), (lapsed.Args["user"], lapsed.Args["privateName"], lapsed.Args["tier"],
            lapsed.Args["daysSincePayment"], lapsed.Args["isEstimate"]));
        Assert.True(Matches(Trigger(KofiEventTypes.MembershipLapsed), lapsed, new() { ["tier"] = "gold" }));
        Assert.False(Matches(Trigger(KofiEventTypes.MembershipLapsed), lapsed, new() { ["tier"] = "bronze" }));
        Assert.Equal(0, _kofi.Tracker.CheckLapses(_clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromDays(20));
        Assert.Equal(1, _kofi.Tracker.CheckLapses(_clock.GetUtcNow()));
        Assert.Equal(["Anonymous", "Bea"], _kofi.Tracker.Members().Select(m => m.Name).Order());
    }

    [Fact]
    public void Commissions_queue_up_and_are_marked_done()
    {
        Send(With(Fixture("kofi-donation.json"), ("type", "Commission"), ("message_id", "c1"), ("from_name", "Ana"), ("message", "A cat")));
        Send(With(Fixture("kofi-donation.json"), ("type", "Commission"), ("message_id", "c2"), ("from_name", "Ben"), ("message", "A logo")));
        _kofi.SendTestEvent("commission");   // tests never join the queue
        Assert.Equal(2, _h.Events(KofiEventTypes.Commission)[1].Args["commissionsOpen"]);
        Assert.Equal(["Ana", "Ben"], _kofi.Tracker.Commissions().Select(c => c.Name));

        var args = new Dictionary<string, object?>();
        KofiSubActions.StoreCommissions(args, _kofi.Tracker.Commissions());
        Assert.Equal("1. Ana: A cat; 2. Ben: A logo", args["commissionsText"]);

        Assert.Equal("Ben", _kofi.Tracker.CompleteCommission("2")!.Name);
        Assert.Equal("Ana", _kofi.Tracker.CompleteCommission("c1")!.Name);
        Assert.Null(_kofi.Tracker.CompleteCommission());
        Assert.Equal("Cat", _kofi.Tracker.AddCommission("Cat", "Sketch").Name);
        var data = new JsonObject();
        _kofi.Tracker.AddTo(data);
        Assert.Equal("Cat", Assert.Single(data["kofi-commissions"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal(2, _kofi.Tracker.ClearCommissions());
    }

    [Fact]
    public async Task Script_functions_and_steps_reach_the_tracker()
    {
        var functions = new KofiScriptFunctions(_kofi.Tracker).GetFunctions().ToDictionary(f => f.Name);
        static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
        await functions["kofi.setGoal"].Invoke(null!, Args(new { target = 50, title = "New mic" }), default);
        await functions["kofi.addToGoal"].Invoke(null!, Args(new { amount = 12.5 }), default);
        var goal = JsonSerializer.SerializeToElement(await functions["kofi.goal"].Invoke(null!, Args(new { }), default));
        Assert.Equal(("New mic", 12.5, 25), (goal.GetProperty("title").GetString(), goal.GetProperty("current").GetDouble(), goal.GetProperty("percent").GetInt32()));
        Assert.Null(await functions["kofi.completeCommission"].Invoke(null!, Args(new { }), default));

        Assert.Equal(KofiSubActionSet.TypeIds.Order(), KofiSubActions.Create(_kofi.Tracker).Select(s => s.TypeId).Order());
    }

    /// <summary>A clock tests move by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
