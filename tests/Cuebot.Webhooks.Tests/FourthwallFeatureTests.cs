using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Webhooks.Tests;

/// <summary>
/// Fourthwall: donation status, order sources, anonymous and hidden names, the PRODUCT_UPDATED page shape, product access,
/// gift winners and claim links, membership changes, promotions and collections, and the optional Open API connection
/// (shop, tiers, members, products, webhook setup, catch-up, streaming status, gift draws from chat, sub-actions) against
/// a fake Fourthwall API. Payload fixtures only; no real Fourthwall account.
/// </summary>
public sealed class FourthwallFeatureTests : IDisposable
{
    private const string Secret = "fw-secret";
    private readonly Harness _h = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFourthwallApi _api = new();
    private readonly FourthwallWebhookHandler _fw;

    public FourthwallFeatureTests()
    {
        _fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = Secret } };
        _fw.Tracker = new FourthwallTracker(_h.Settings, _h.Log, new HttpClient(_api), time: _clock, apiBaseUrl: "https://fourthwall.test");
        _fw.Tracker.Api.Delay = (_, _) => Task.CompletedTask;
        _fw.Tracker.Replay = _fw.ReplayAsync;
    }

    public void Dispose() => _h.Dispose();

    private WebhookOutcome Send(string body, string? id = null) =>
        _fw.Handle(Harness.Delivery("fourthwall", body, new Dictionary<string, string>
        {
            ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64(Secret, Encoding.UTF8.GetBytes(body)),
        }, id: id));

    private BotEvent Fixture(string file)
    {
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, Send(Harness.Data(file)));
        return _h.Published.Skip(before).Last();
    }

    private static ITriggerType Trigger(string id) => new FourthwallTriggers().GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(string trigger, BotEvent e, Dictionary<string, string>? settings = null)
    {
        var t = Trigger(trigger);
        return t.Matches(e, new TriggerDefinition(), ParameterValues.From(t.Parameters, settings ?? []), new Dictionary<string, object?>());
    }

    private static WebhookOverlayAdapter Overlay => WebhookOverlayAdapter.All().Single(a => a.Service == "fourthwall");

    private void UseApi()
    {
        _h.Settings.Value.FourthwallOptions.ApiUser = "fw-api-user";
        _h.Settings.Value.FourthwallOptions.ApiPassword = "fw-api-password";
    }

    // ---- corrections ----

    [Fact]
    public void Only_completed_donations_are_tips()
    {
        var abandoned = Fixture("fourthwall-donation-abandoned.json");
        Assert.Equal(("ABANDONED", false), (abandoned.Args["fw.status"], abandoned.Args["isCompleted"]));
        Assert.Equal(0m, abandoned.Args["amount"]);
        Assert.Equal(50.0, abandoned.Args["fw.amount"]);   // what Fourthwall named, for actions that want it
        Assert.Null(WebhookChatAdapter.Describe(abandoned));
        Assert.Null(Overlay.ToAlert(abandoned));
        Assert.False(Matches(FourthwallEventTypes.Donation, abandoned));
        Assert.True(Matches(FourthwallEventTypes.Donation, abandoned, new() { ["status"] = FourthwallTriggers.NotCompleted }));
        Assert.True(Matches(FourthwallEventTypes.Donation, abandoned, new() { ["status"] = FourthwallTriggers.Any }));

        foreach (var status in (string[])["OPEN", "CANCELLED", "FAILED"])
        {
            var e = FourthwallWebhookHandler.Map(JsonReaders.ParseBody(Harness.Data("fourthwall-donation-abandoned.json")
                .Replace("ABANDONED", status, StringComparison.Ordinal)), isTest: false)!;
            Assert.Equal(0m, e.Args["amount"]);
            Assert.Null(WebhookChatAdapter.Describe(new BotEvent(e.Type, e.Args)));
        }

        var completed = Fixture("fourthwall-donation.json");
        Assert.Equal(true, completed.Args["isCompleted"]);
        Assert.Equal(5.5m, completed.Args["amount"]);
        Assert.True(Matches(FourthwallEventTypes.Donation, completed));
        Assert.False(Matches(FourthwallEventTypes.Donation, completed, new() { ["status"] = FourthwallTriggers.NotCompleted }));
        Assert.Equal(FeedKind.Tip, WebhookChatAdapter.Describe(completed)!.Kind);
        Assert.Equal("tip-latest", Overlay.ToAlert(completed)!.Listener);

        // A donation without a status (older payloads) still counts.
        var noStatus = FourthwallWebhookHandler.Map(JsonReaders.ParseBody(Harness.Data("fourthwall-donation.json")
            .Replace("\"status\": \"COMPLETED\",", "", StringComparison.Ordinal)), isTest: false)!;
        Assert.Equal(5.5m, noStatus.Args["amount"]);
    }

    [Fact]
    public void Samples_and_redeemed_gifts_are_not_purchases()
    {
        var samples = Fixture("fourthwall-order-samples.json");
        Assert.Equal(("SAMPLES_ORDER", false), (samples.Args["fw.source"], samples.Args["isPurchase"]));
        Assert.Equal(0m, samples.Args["amount"]);
        Assert.Null(WebhookChatAdapter.Describe(samples));
        Assert.Null(Overlay.ToAlert(samples));
        Assert.False(Matches(FourthwallEventTypes.OrderPlaced, samples));
        Assert.True(Matches(FourthwallEventTypes.OrderPlaced, samples, new() { ["source"] = FourthwallTriggers.Samples }));

        var redeemed = Fixture("fourthwall-order-gift-redemption.json");
        Assert.Equal(("giv_voWcDCtfQUygxpuYx1HtNg", "gft_EdJvIXu3SEiXe_QkPavHSA"), (redeemed.Args["fw.giftPurchaseId"], redeemed.Args["fw.giftId"]));
        Assert.Equal(0m, redeemed.Args["amount"]);
        var feed = WebhookChatAdapter.Describe(redeemed)!;
        Assert.Equal("Anonymous redeemed a gift: New T-shirt", feed.Title);
        Assert.Null(feed.Spend);
        Assert.Null(feed.Amount);
        Assert.Null(Overlay.ToAlert(redeemed));
        Assert.False(Matches(FourthwallEventTypes.OrderPlaced, redeemed));
        Assert.True(Matches(FourthwallEventTypes.OrderPlaced, redeemed, new() { ["source"] = FourthwallTriggers.GiftRedemptions }));

        var order = Fixture("fourthwall-order-placed.json");
        Assert.Equal(true, order.Args["isPurchase"]);
        Assert.True(Matches(FourthwallEventTypes.OrderPlaced, order));
        Assert.NotNull(WebhookChatAdapter.Describe(order)!.Spend);
    }

    [Fact]
    public void Blank_names_are_anonymous_and_names_can_be_hidden()
    {
        var anonymous = FourthwallWebhookHandler.Map(JsonReaders.ParseBody(FourthwallWebhookHandler.Samples["anonymousDonation"]), isTest: true)!;
        Assert.Equal((FourthwallWebhookHandler.AnonymousName, true), (anonymous.Args["user"], anonymous.Args["isAnonymous"]));
        Assert.StartsWith("[Test] Anonymous donated", WebhookChatAdapter.Describe(new BotEvent(anonymous.Type, anonymous.Args))!.Title, StringComparison.Ordinal);

        _h.Settings.Value.FourthwallOptions.HideSupporterNames = true;
        var hidden = Fixture("fourthwall-order-placed.json");
        Assert.Equal(FourthwallWebhookHandler.HiddenName, hidden.Args["user"]);
        Assert.Equal("MerchFan", hidden.Args["fw.username"]);
        Assert.Equal(false, hidden.Args["isAnonymous"]);
        Assert.StartsWith("A supporter bought", WebhookChatAdapter.Describe(hidden)!.Title, StringComparison.Ordinal);
        Assert.Equal("A supporter", Overlay.ToAlert(hidden)!.Event["name"]!.GetValue<string>());
    }

    [Fact]
    public void Product_updated_page_publishes_one_event_per_product_and_hidden_ones_are_skipped()
    {
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, Send(Harness.Data("fourthwall-product-updated.json")));
        var events = _h.Published.Skip(before).ToArray();
        Assert.Equal(["Stream Hoodie", "Unreleased Mug"], events.Select(e => e.Args["fw.name"]));
        var (hoodie, mug) = (events[0], events[1]);
        Assert.Equal(("PUBLIC", true, true), (hoodie.Args["fw.access"], hoodie.Args["fw.isPublic"], hoodie.Args["fw.isSoldOut"]));
        Assert.Equal(("https://cdn.fourthwall.com/hoodie.jpeg", "$45.00"), (hoodie.Args["fw.imageUrl"], hoodie.Args["fw.formattedPrice"]));
        Assert.Equal(("HIDDEN", false), (mug.Args["fw.access"], mug.Args["fw.isPublic"]));
        Assert.True(Matches(FourthwallEventTypes.ProductUpdated, hoodie));
        Assert.False(Matches(FourthwallEventTypes.ProductUpdated, mug));
        Assert.True(Matches(FourthwallEventTypes.ProductUpdated, mug, new() { ["publicOnly"] = "false" }));

        // A re-sent delivery of the same event is a duplicate for both products.
        Assert.Equal(WebhookOutcome.Ignored, Send(Harness.Data("fourthwall-product-updated.json")));

        // The bare product (PRODUCT_CREATED, and PRODUCT_UPDATED if Fourthwall sends it that way) still maps.
        var created = FourthwallWebhookHandler.Map(JsonReaders.ParseBody(FourthwallWebhookHandler.Samples["productCreated"]), isTest: true)!;
        Assert.Equal(("Stream Hoodie", true), (created.Args["fw.name"], created.Args["fw.isPublic"]));
        var hiddenNew = JsonReaders.ParseBody(FourthwallWebhookHandler.Samples["productCreated"].Replace("\"PUBLIC\"", "\"PRIVATE\"", StringComparison.Ordinal));
        Assert.False(Matches(FourthwallEventTypes.ProductCreated, new BotEvent(FourthwallEventTypes.ProductCreated, FourthwallWebhookHandler.Map(hiddenNew, false)!.Args)));
    }

    [Fact]
    public void Gift_winners_selection_time_claim_links_and_a_gift_alert()
    {
        _h.Settings.Value.FourthwallOptions.ShopAddress = "https://shop.example.com/";
        Assert.True(_fw.SendTestEvent("giftDrawEnded"));
        var ended = _h.Events(FourthwallEventTypes.GiftDrawEnded).Single();
        Assert.Equal(("LuckyViewer, Anonymous", 2), (ended.Args["fw.winners"], ended.Args["fw.winnerCount"]));
        Assert.IsType<DateTime>(ended.Args["fw.gifts[0].selectedAt"]);
        Assert.Equal("https://shop.example.com/gifts/gft_1 https://shop.example.com/gifts/gft_2", ended.Args["fw.giftUrls"]);
        Assert.Equal("https://shop.example.com/gifts/gft_1", ended.Args["fw.gifts[0].url"]);

        var gift = Fixture("fourthwall-gift-purchase.json");
        var alert = Overlay.ToAlert(gift)!;
        Assert.Equal("merch-latest", alert.Listener);
        Assert.True(alert.Event["gift"]!.GetValue<bool>());
        Assert.Contains("(gift)", alert.Event["items"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0.0, gift.Args["fw.donation"]);   // documented as always 0 for gifts
    }

    [Fact]
    public async Task Subscription_changes_know_cancellations_tiers_and_upgrades()
    {
        UseApi();
        Assert.StartsWith("Connected to Cue Shop", await _fw.Tracker.ConnectAsync(), StringComparison.Ordinal);

        // mem_1 is Silver (5/month) in the member list; a change to Gold yearly (120 = 10/month) is an upgrade.
        var changed = Fixture("fourthwall-subscription-changed.json");
        Assert.Equal(("Gold", "Silver", "upgrade"), (changed.Args["tier"], changed.Args["fw.previousTier"], changed.Args["direction"]));
        Assert.Equal(10.0, changed.Args["fw.monthlyAmount"]);
        Assert.True(Matches(FourthwallEventTypes.SubscriptionChanged, changed, new() { ["change"] = FourthwallTriggers.Upgrades }));
        Assert.False(Matches(FourthwallEventTypes.SubscriptionChanged, changed, new() { ["change"] = FourthwallTriggers.Cancellations }));
        Assert.True(Matches(FourthwallEventTypes.SubscriptionChanged, changed, new() { ["tier"] = "gold" }));
        Assert.Contains("(Gold)", WebhookChatAdapter.Describe(new BotEvent(FourthwallEventTypes.SubscriptionPurchased, changed.Args))!.Title, StringComparison.Ordinal);

        var cancel = Harness.Data("fourthwall-subscription-changed.json").Replace("\"ACTIVE\"", "\"CANCELLED\"", StringComparison.Ordinal)
            .Replace("weve_sub_changed", "weve_sub_cancel", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, Send(cancel));
        var cancelled = _h.Events(FourthwallEventTypes.SubscriptionChanged).Last();
        Assert.Equal((true, "CANCELLED"), (cancelled.Args["fw.isCancelled"], cancelled.Args["fw.state"]));
        Assert.True(Matches(FourthwallEventTypes.SubscriptionChanged, cancelled, new() { ["change"] = FourthwallTriggers.Cancellations }));
        Assert.False(Matches(FourthwallEventTypes.SubscriptionChanged, cancelled, new() { ["change"] = FourthwallTriggers.Upgrades }));

        Assert.Equal(2, _fw.Tracker.MemberCounts().Count);   // mem_1 (cancelled, paid until the period ends) and mem_2
        var expired = cancel.Replace("SUBSCRIPTION_CHANGED", "SUBSCRIPTION_EXPIRED", StringComparison.Ordinal)
            .Replace("weve_sub_cancel", "weve_sub_expired", StringComparison.Ordinal).Replace("\"CANCELLED\"", "\"NONE\"", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, Send(expired));
        Assert.Equal(1, _fw.Tracker.MemberCounts().Count);
    }

    [Fact]
    public void Promotions_and_collections_become_triggers()
    {
        var promo = Fixture("fourthwall-promotion-status-changed.json");
        Assert.Equal(FourthwallEventTypes.PromotionStatusChanged, promo.Type);
        Assert.Equal(("RAID1", "RAID1, RAID2", "AllUsed", true, "$5.00 off"),
            (promo.Args["fw.code"], promo.Args["fw.codes"], promo.Args["fw.status"], promo.Args["fw.isAllUsed"], promo.Args["fw.discount"]));
        Assert.True(Matches(FourthwallEventTypes.PromotionStatusChanged, promo, new() { ["promotionStatus"] = "AllUsed" }));
        Assert.False(Matches(FourthwallEventTypes.PromotionStatusChanged, promo, new() { ["promotionStatus"] = "Live" }));
        Assert.Null(WebhookChatAdapter.Describe(promo));

        Assert.True(_fw.SendTestEvent("promotionCreated"));
        Assert.Equal(("STREAM20", "20% off", 10.0), (_h.Events(FourthwallEventTypes.PromotionCreated).Single().Args["fw.code"],
            _h.Events(FourthwallEventTypes.PromotionCreated).Single().Args["fw.discount"], _h.Events(FourthwallEventTypes.PromotionCreated).Single().Args["fw.maxUses"]));
        Assert.True(_fw.SendTestEvent("collectionUpdated"));
        var collection = _h.Events(FourthwallEventTypes.CollectionUpdated).Single();
        Assert.Equal(("Stream drop", true, 2), (collection.Args["fw.name"], collection.Args["fw.available"], collection.Args["fw.productCount"]));
    }

    [Fact]
    public void Every_trigger_has_a_local_test_event()
    {
        foreach (var kind in _fw.TestEventKinds) Assert.True(_fw.SendTestEvent(kind), kind);
        var types = _h.Published.Select(e => e.Type).ToHashSet();
        foreach (var trigger in new FourthwallTriggers().GetTriggers()) Assert.Contains(trigger.TypeId, types);
    }

    [Fact]
    public void A_delivery_handled_before_a_restart_is_not_published_again()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-fw-seen-").FullName;
        try
        {
            var path = Path.Combine(dir, "webhooks-seen.json");
            _fw.Seen = new RecentIds(path: path);
            Assert.Equal(WebhookOutcome.Handled, Send(Harness.Data("fourthwall-donation.json"), id: "relay-1"));
            var again = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Seen = new RecentIds(path: path) };
            var body = Harness.Data("fourthwall-donation.json");
            // A Fourthwall retry reaching the relay later is a new relay delivery with the same envelope id.
            Assert.Equal(WebhookOutcome.Ignored, again.Handle(Harness.Delivery("fourthwall", body, new Dictionary<string, string>
            {
                ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64(Secret, Encoding.UTF8.GetBytes(body)),
            }, id: "relay-2")));
            Assert.Single(_h.Events(FourthwallEventTypes.Donation));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the API connection ----

    [Fact]
    public async Task Connect_reads_the_shop_tiers_members_and_products_with_basic_auth()
    {
        UseApi();
        _api.WebhookTypes = ["ORDER_PLACED", "DONATION"];
        var result = await _fw.Tracker.ConnectAsync("https://relay.example.com/fourthwall/abc");
        Assert.StartsWith("Connected to Cue Shop: 2 tier(s), 2 member(s), 2 product(s)", result, StringComparison.Ordinal);
        Assert.All(_api.Requests, r => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fw-api-user:fw-api-password")), r.Authorization));
        Assert.True(_fw.Tracker.Connected);
        Assert.Equal("shop.example.com", _fw.Tracker.ShopAddress);
        Assert.Equal("Gold", _fw.Tracker.TierName("mt_gold"));
        Assert.Equal("OPEN_API", _fw.Tracker.GiftingPlatform);
        // The hidden product is left out of the showcase; sold out ones stay, marked.
        Assert.Equal(["Stream Hoodie", "Sticker pack"], _fw.Tracker.Products().Select(p => p.Name));
        Assert.True(_fw.Tracker.Products()[0].SoldOut);
        Assert.Equal("https://shop.example.com/products/stream-hoodie", _fw.Tracker.Products()[0].Url);
        Assert.NotNull(_fw.Tracker.FindMember("SILVER@example.com"));
        Assert.Contains("PROMOTION_CREATED", _fw.Tracker.MissingWebhookTypes());

        var data = new JsonObject();
        _fw.Tracker.AddTo(data);
        Assert.Equal(2, data["fourthwall"]!["memberCount"]!.GetValue<int>());
        Assert.True(data["fourthwall"]!["products"]![0]!["soldOut"]!.GetValue<bool>());
        Assert.DoesNotContain("@", data.ToJsonString(), StringComparison.Ordinal);   // member emails never reach overlays
    }

    [Fact]
    public async Task Refused_credentials_stop_calls_and_rate_limits_are_retried()
    {
        UseApi();
        _api.TooManyRequests = 2;
        Assert.Equal("Cue Shop", (await _fw.Tracker.Api.ShopAsync(default)).Name);
        Assert.Equal(3, _api.Requests.Count(r => r.Path == "/open-api/v1.0/shops/current"));

        _h.Settings.Value.FourthwallOptions.ApiPassword = "wrong";
        var result = await _fw.Tracker.ConnectAsync();
        Assert.Contains("401", result, StringComparison.Ordinal);
        Assert.True(_fw.Tracker.NeedsReconnect);
        Assert.False(_fw.Tracker.Connected);
    }

    [Fact]
    public async Task Set_up_webhook_creates_it_then_adds_missing_types()
    {
        UseApi();
        const string url = "https://relay.example.com/fourthwall/abc";
        Assert.StartsWith("Created the Fourthwall webhook", await _fw.Tracker.SetUpWebhookAsync(url), StringComparison.Ordinal);
        var created = _api.Requests.Single(r => r.Method == "POST" && r.Path == "/open-api/v1.0/webhooks");
        using (var body = JsonDocument.Parse(created.Body))
        {
            Assert.Equal(url, body.RootElement.GetProperty("url").GetString());
            Assert.Equal(FourthwallApi.WebhookTypes.Order(), body.RootElement.GetProperty("allowedTypes").EnumerateArray().Select(t => t.GetString()!).Order());
        }
        Assert.Empty(_fw.Tracker.MissingWebhookTypes());

        _api.WebhookTypes = ["ORDER_PLACED", "DONATION"];
        Assert.StartsWith("Added the missing events", await _fw.Tracker.SetUpWebhookAsync(url), StringComparison.Ordinal);
        Assert.Single(_api.Requests, r => r.Method == "PUT" && r.Path == "/open-api/v1.0/webhooks/wcon_cuebot");
        Assert.StartsWith("There is no Cuebot URL", await _fw.Tracker.SetUpWebhookAsync(""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catch_up_replays_only_new_events_no_delivery_brought()
    {
        UseApi();
        await _fw.Tracker.ConnectAsync();   // catch-up starts from now
        Assert.Equal(WebhookOutcome.Handled, Send(Harness.Data("fourthwall-order-placed.json")));   // this one arrived
        var delivered = JsonNode.Parse(Harness.Data("fourthwall-order-placed.json"))!;
        Assert.NotNull(delivered["id"]);
        var missed = JsonNode.Parse(Harness.Data("fourthwall-donation.json").Replace("\"testMode\": true", "\"testMode\": false", StringComparison.Ordinal)
            .Replace("weve_donation_1", "weve_missed", StringComparison.Ordinal))!;
        _api.Events =
        [
            // The body as the envelope, as data only, and an event older than the connection (never replayed).
            new("weve_missed", "DONATION", _clock.GetUtcNow().AddMinutes(5), missed.ToJsonString()),
            // Delivered already; listed under an id of its own (the list's ids are not documented to equal the envelope's).
            new("wev_list_2", "ORDER_PLACED", _clock.GetUtcNow().AddMinutes(6), delivered["data"]!.ToJsonString()),
            new("weve_old", "DONATION", _clock.GetUtcNow().AddDays(-2), missed.ToJsonString().Replace("weve_missed", "weve_old", StringComparison.Ordinal)),
        ];
        _clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal("Replayed 1 Fourthwall event(s) that never arrived.", await _fw.Tracker.CatchUpAsync());
        var replayed = _h.Events(FourthwallEventTypes.Donation).Single();
        Assert.Equal((true, 5.5m), (replayed.Args["isReplay"], replayed.Args["amount"]));
        Assert.Equal(300L, replayed.Args["delaySeconds"]);
        Assert.Single(_h.Events(FourthwallEventTypes.OrderPlaced));
        Assert.False(Matches(FourthwallEventTypes.Donation, replayed, new() { ["skipReplays"] = "true" }));
        Assert.True(Matches(FourthwallEventTypes.Donation, replayed));

        Assert.Equal("No missed Fourthwall events.", await _fw.Tracker.CatchUpAsync());
    }

    [Fact]
    public async Task Gift_draws_collect_twitch_chat_entries_and_finish_with_them()
    {
        UseApi();
        _h.Settings.Value.FourthwallOptions.RunGiftDraws = true;
        await _fw.Tracker.ConnectAsync();
        Assert.True(_fw.SendTestEvent("giftDrawStarted"));   // a test draw is never run
        Assert.False(_fw.Tracker.DrawOpen);

        Assert.Equal(WebhookOutcome.Handled, Send(FourthwallWebhookHandler.Samples["giftDrawStarted"]));
        var started = _h.Events(FourthwallEventTypes.GiftDrawStarted).Last();
        Assert.Equal((true, "!enter"), (started.Args["fw.cuebotRunsDraw"], started.Args["fw.entryCommand"]));
        Assert.True(_fw.Tracker.DrawOpen);

        void Chat(string platform, string id, string login, string text) => _fw.Tracker.OnEvent(new BotEvent(ChatHub.MessageEvent,
            new Dictionary<string, object?> { ["platform"] = platform, ["userId"] = id, ["userName"] = login, ["user"] = login, ["message"] = text }));
        Chat("Twitch", "111", "Alice", "!enter");
        Chat("Twitch", "111", "Alice", "!ENTER please");   // once per viewer
        Chat("Twitch", "222", "bob", "!enter");
        Chat("Twitch", "333", "carol", "hello !enter");    // not a command
        Chat("YouTube", "UC1", "dave", "!enter");          // Twitch only
        Assert.Equal(2, _fw.Tracker.OpenDrawEntrants());

        Assert.Equal(0, await _fw.Tracker.FinishDueDrawsAsync());   // still running
        _clock.Advance(TimeSpan.FromSeconds(61));
        Chat("Twitch", "444", "late", "!enter");
        Assert.Equal(1, await _fw.Tracker.FinishDueDrawsAsync());
        var finish = _api.Requests.Single(r => r.Path == "/open-api/v1.0/gifting/draw/gdr_1/finish");
        Assert.Equal("PUT", finish.Method);
        using var body = JsonDocument.Parse(finish.Body);
        Assert.Equal(["111:alice", "222:bob"], body.RootElement.GetProperty("participants").EnumerateArray()
            .Select(p => $"{p.GetProperty("userId").GetString()}:{p.GetProperty("userName").GetString()}").Order());
        Assert.All(body.RootElement.GetProperty("participants").EnumerateArray(), p => Assert.Equal("TWITCH", p.GetProperty("service").GetString()));
        Assert.False(_fw.Tracker.DrawOpen);
    }

    [Fact]
    public async Task The_shop_is_marked_live_with_the_twitch_stream()
    {
        UseApi();
        await _fw.Tracker.ConnectAsync();
        _fw.Tracker.OnEvent(new BotEvent("twitch.streamOnline", new Dictionary<string, object?> { ["userId"] = "999", ["userName"] = "streamer" }));
        Assert.DoesNotContain(_api.Requests, r => r.Path.StartsWith("/open-api/v1.0/streaming/", StringComparison.Ordinal));   // off by default

        _h.Settings.Value.FourthwallOptions.MarkStreamingLive = true;
        _fw.Tracker.OnEvent(new BotEvent("twitch.streamOnline", new Dictionary<string, object?> { ["userId"] = "999", ["userName"] = "streamer" }));
        var start = await _api.WaitFor("/open-api/v1.0/streaming/start");
        using var body = JsonDocument.Parse(start.Body);
        var service = body.RootElement.GetProperty("services")[0];
        Assert.Equal(("TWITCH", "999", "streamer"), (service.GetProperty("type").GetString(), service.GetProperty("broadcasterId").GetString(),
            service.GetProperty("broadcasterLogin").GetString()));
        _fw.Tracker.OnEvent(new BotEvent("twitch.streamOffline", new Dictionary<string, object?> { ["userId"] = "999", ["userName"] = "streamer" }));
        await _api.WaitFor("/open-api/v1.0/streaming/end");
    }

    // ---- sub-actions (through the app's own registrations) ----

    [Fact]
    public async Task Sub_actions_create_and_end_codes_and_read_totals()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-fourthwall-").FullName;
        var capture = new Capture();
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(dir)).AddWebhooks();
        collection.AddSingleton(new HttpClient(_api));
        collection.AddSingleton<ISubActionHandler>(capture);
        await using (var services = collection.BuildServiceProvider())
        {
            var settings = services.GetRequiredService<Cuebot.Core.Storage.JsonFile<WebhooksSettings>>();
            settings.Value.FourthwallOptions.ApiUser = "fw-api-user";
            settings.Value.FourthwallOptions.ApiPassword = "fw-api-password";
            var tracker = services.GetRequiredService<FourthwallTracker>();
            tracker.Api.Delay = (_, _) => Task.CompletedTask;
            var engine = services.GetRequiredService<ActionEngine>();
            var registry = services.GetRequiredService<Registry>();
            foreach (var id in FourthwallSubActions.TypeIds) Assert.Equal("Fourthwall", registry.GetSubAction(id)!.Category);
            Assert.Same(tracker, services.GetRequiredService<FourthwallWebhookHandler>().Tracker);

            async Task<Dictionary<string, object?>> Run(string type, Dictionary<string, string> parameters)
            {
                Assert.True(await engine.RunAsync(new ActionDefinition
                {
                    Name = type,
                    SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = Capture.Id }],
                }));
                return capture.Args!;
            }

            var created = await Run("fourthwall.createPromo", new() { ["kind"] = FourthwallSubActions.Percent, ["value"] = "20", ["maxUses"] = "10" });
            Assert.Equal(true, created["fourthwallOk"]);
            Assert.Matches("^STREAM[A-Z2-9]{6}$", (string)created["fw.code"]!);
            var post = _api.Requests.Single(r => r.Method == "POST" && r.Path == "/open-api/v1.0/promotions");
            using (var body = JsonDocument.Parse(post.Body))
            {
                Assert.Equal("SHOP_SINGLE", body.RootElement.GetProperty("type").GetString());
                Assert.Equal(20, body.RootElement.GetProperty("discount").GetProperty("percentage").GetDouble());
                Assert.Equal(10, body.RootElement.GetProperty("limits").GetProperty("maximumUse").GetInt32());
            }

            var ended = await Run("fourthwall.endPromo", new() { ["code"] = "STREAM20" });
            Assert.Equal((true, "Ended"), (ended["fourthwallOk"], ended["fw.promotionStatus"]));
            Assert.Contains(_api.Requests, r => r.Method == "PUT" && r.Path == "/open-api/v1.0/promotions/prm_1" && r.Body.Contains("ENDED", StringComparison.Ordinal));

            // Purchases only (not samples or cancelled orders) and completed donations only.
            var totals = await Run("fourthwall.getTotals", new() { ["period"] = "Last 24 hours" });
            Assert.Equal((20m, 1, 5m, 1), (totals["fourthwallRevenue"], totals["fourthwallOrders"], totals["fourthwallDonations"], totals["fourthwallDonationCount"]));
            Assert.Equal("$25.00", totals["fourthwallFormattedTotal"]);

            var hidden = await Run("fourthwall.setAvailability", new() { ["target"] = "Collection", ["id"] = "stream-drop", ["available"] = "false" });
            Assert.Equal(true, hidden["fourthwallOk"]);
            Assert.Contains(_api.Requests, r => r.Path == "/open-api/v1.0/collections/col_1/availability" && r.Body.Contains("false", StringComparison.Ordinal));

            var links = await Run("fourthwall.createGiveawayLinks", new() { ["productId"] = "prod_public", ["count"] = "2" });
            Assert.Equal("https://shop.example.com/giveaway/a", links["fw.giveawayLink0"]);

            var postResult = await Run("fourthwall.createPost", new() { ["title"] = "I'm live!", ["text"] = "Come say hi", ["access"] = "MembersOnly" });
            Assert.Equal("42", postResult["fw.postId"]);

            var waiting = await Run("fourthwall.getThankYous", new() { ["max"] = "5" });
            Assert.Equal((1, "Anonymous"), (waiting["fourthwallThankYouCount"], waiting["thankYou0.name"]));

            // A refused call reports and lets the action go on.
            settings.Value.FourthwallOptions.ApiPassword = "wrong";
            var refused = await Run("fourthwall.createPromo", new() { ["code"] = "NOPE" });
            Assert.Equal(false, refused["fourthwallOk"]);
            Assert.Contains("401", (string)refused["fourthwallResult"]!, StringComparison.Ordinal);
        }
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Capture : ISubActionHandler
    {
        public const string Id = "test.fwcapture";
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// A fake Fourthwall Open API: one shop, two tiers, two members, three products (one hidden), webhooks, an event list,
    /// promotions, orders, donations, gift draws and the streaming status. Basic auth must be fw-api-user / fw-api-password.
    /// </summary>
    private sealed class FakeFourthwallApi : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Query, string Body, string Authorization);

        public sealed record Event(string Id, string Type, DateTimeOffset CreatedAt, string Body);

        private readonly List<Request> _requests = [];

        public IReadOnlyList<Request> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        public int TooManyRequests { get; set; }
        public string[]? WebhookTypes { get; set; }
        public Event[] Events { get; set; } = [];

        public async Task<Request> WaitFor(string path)
        {
            for (var i = 0; i < 200; i++)
            {
                if (Requests.FirstOrDefault(r => r.Path == path) is { } found) return found;
                await Task.Delay(10);
            }
            throw new TimeoutException(path);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            lock (_requests) _requests.Add(new(request.Method.Method, path, Uri.UnescapeDataString(request.RequestUri.Query), body, request.Headers.Authorization?.ToString() ?? ""));
            if (TooManyRequests > 0)
            {
                TooManyRequests--;
                return Json((HttpStatusCode)429, """{"code":"OPEN_API_TOO_MANY_REQUESTS"}""");
            }
            if (request.Headers.Authorization?.ToString() != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fw-api-user:fw-api-password")))
                return Json(HttpStatusCode.Unauthorized, "{}");
            return (request.Method.Method, path) switch
            {
                ("GET", "/open-api/v1.0/shops/current") => Json(HttpStatusCode.OK,
                    """{"id":"sh_1","name":"Cue Shop","domain":"cue-shop.fourthwall.com","publicDomain":"shop.example.com","status":"LIVE"}"""),
                ("GET", "/open-api/v1.0/memberships/tiers") => Json(HttpStatusCode.OK, """
                    [{"id":"mt_silver","name":"Silver","variants":[{"id":"v1","tierId":"mt_silver","interval":"MONTHLY","amount":{"value":5,"currency":"USD"}}]},
                     {"id":"mt_gold","name":"Gold","variants":[{"id":"v2","tierId":"mt_gold","interval":"ANNUAL","amount":{"value":120,"currency":"USD"}}]}]
                    """),
                ("GET", "/open-api/v1.0/memberships/members") => Json(HttpStatusCode.OK, """
                    {"results":[
                      {"id":"mem_1","email":"silver@example.com","nickname":"NewMember","createdAt":"2026-01-01T00:00:00Z",
                       "subscription":{"type":"ACTIVE","variant":{"id":"v1","tierId":"mt_silver","interval":"MONTHLY","amount":{"value":5,"currency":"USD"}}}},
                      {"id":"mem_2","email":"gold@example.com","nickname":"Goldie","createdAt":"2026-01-01T00:00:00Z",
                       "subscription":{"type":"ACTIVE","variant":{"id":"v2","tierId":"mt_gold","interval":"ANNUAL","amount":{"value":120,"currency":"USD"}}}},
                      {"id":"mem_3","email":"gone@example.com","nickname":"Gone","createdAt":"2026-01-01T00:00:00Z","subscription":{"type":"NONE"}}],
                     "total":3,"page":0,"size":100,"totalPages":1}
                    """),
                ("GET", "/open-api/v1.0/products") => Json(HttpStatusCode.OK, "{\"results\":["
                    + JsonNode.Parse(Harness.Data("fourthwall-product-updated.json"))!["data"]!["product"]!["results"]![0]!.ToJsonString() + ","
                    + JsonNode.Parse(Harness.Data("fourthwall-product-updated.json"))!["data"]!["product"]!["results"]![1]!.ToJsonString() + ","
                    + """{"id":"prod_sticker","name":"Sticker pack","slug":"stickers","state":{"type":"AVAILABLE"},"access":{"type":"PUBLIC"},"images":[{"url":"https://cdn/s.png"}],"variants":[{"unitPrice":{"value":5,"currency":"USD"}}]}"""
                    + "],\"totalPages\":1}"),
                ("GET", "/open-api/v1.0/gifting/config") => Json(HttpStatusCode.OK,
                    """{"enabled":true,"entryTimeLimitSeconds":60,"shipping":{"type":"ALL_CREATOR"},"products":{"type":"ALL"},"platform":"OPEN_API"}"""),
                ("GET", "/open-api/v1.0/webhooks") => Json(HttpStatusCode.OK, WebhookTypes is null
                    ? """{"results":[{"id":"wcon_other","url":"https://elsewhere.example.com/hook","allowedTypes":["ORDER_PLACED"]}]}"""
                    : """{"results":[{"id":"wcon_other","url":"https://elsewhere.example.com/hook","allowedTypes":["ORDER_PLACED"]},""" + Hook(WebhookTypes) + "]}"),
                ("POST", "/open-api/v1.0/webhooks") => Created(body),
                ("PUT", "/open-api/v1.0/webhooks/wcon_cuebot") => Created(body),
                ("GET", "/open-api/v1.0/webhook-events") => Json(HttpStatusCode.OK, new JsonObject
                {
                    ["results"] = new JsonArray([.. Events.Select(e => (JsonNode)new JsonObject
                    {
                        ["id"] = e.Id, ["type"] = e.Type, ["apiVersion"] = "V1", ["createdAt"] = e.CreatedAt.ToString("o"), ["updatedAt"] = e.CreatedAt.ToString("o"),
                        ["body"] = e.Body, ["testMode"] = false, ["state"] = "FAILED", ["url"] = "https://relay.example.com/fourthwall/abc",
                    })]),
                }.ToJsonString()),
                ("PUT", "/open-api/v1.0/gifting/draw/gdr_1/finish") => Json(HttpStatusCode.OK, "{}"),
                ("PUT", "/open-api/v1.0/streaming/start") or ("PUT", "/open-api/v1.0/streaming/end") => Json(HttpStatusCode.OK, """{"shopId":"sh_1","services":[]}"""),
                ("POST", "/open-api/v1.0/promotions") => Json(HttpStatusCode.OK,
                    "{\"type\":\"SHOP_SINGLE\",\"id\":\"prm_new\",\"code\":" + JsonNode.Parse(body)!["code"]!.ToJsonString() + ",\"status\":\"Live\",\"usageCount\":0,\"limits\":{}}"),
                ("GET", "/open-api/v1.0/promotions") => Json(HttpStatusCode.OK,
                    """{"results":[{"type":"SHOP_SINGLE","id":"prm_1","code":"STREAM20","status":"Live","usageCount":3,"limits":{"maximumUsesNumber":10}}]}"""),
                ("PUT", "/open-api/v1.0/promotions/prm_1") => Json(HttpStatusCode.OK,
                    """{"type":"SHOP_SINGLE","id":"prm_1","code":"STREAM20","status":"Ended","usageCount":3,"limits":{"maximumUsesNumber":10}}"""),
                ("GET", "/open-api/v1.0/order") => Json(HttpStatusCode.OK, """
                    {"results":[
                      {"id":"o1","status":"CONFIRMED","createdAt":"2026-10-28T11:00:00Z","amounts":{"total":{"value":20,"currency":"USD"}},"source":{"type":"ORDER"}},
                      {"id":"o2","status":"CONFIRMED","createdAt":"2026-10-28T11:00:00Z","amounts":{"total":{"value":30,"currency":"USD"}},"source":{"type":"SAMPLES_ORDER"}},
                      {"id":"o3","status":"CANCELLED","createdAt":"2026-10-28T11:00:00Z","amounts":{"total":{"value":40,"currency":"USD"}},"source":{"type":"ORDER"}}],
                     "totalPages":1}
                    """),
                ("GET", "/open-api/v1.0/donations") => Json(HttpStatusCode.OK, """
                    {"results":[
                      {"id":"d1","status":"COMPLETED","createdAt":"__RECENT__","amounts":{"total":{"value":5,"currency":"USD"}}},
                      {"id":"d2","status":"ABANDONED","createdAt":"__RECENT__","amounts":{"total":{"value":50,"currency":"USD"}}},
                      {"id":"d3","status":"COMPLETED","createdAt":"__OLD__","amounts":{"total":{"value":7,"currency":"USD"}}}],
                     "totalPages":1}
                    """.Replace("__RECENT__", DateTimeOffset.UtcNow.AddHours(-1).ToString("o"), StringComparison.Ordinal)
                    .Replace("__OLD__", DateTimeOffset.UtcNow.AddDays(-3).ToString("o"), StringComparison.Ordinal)),
                ("GET", "/open-api/v1.0/collections/stream-drop") => Json(HttpStatusCode.OK, """{"id":"col_1","name":"Stream drop","slug":"stream-drop"}"""),
                ("PUT", "/open-api/v1.0/collections/col_1/availability") => Json(HttpStatusCode.OK, """{"id":"col_1","available":false}"""),
                ("POST", "/open-api/v1.0/giveaway-links") => Json(HttpStatusCode.OK, """
                    {"packageId":"pkg_1","giveawayLinks":[{"status":"AVAILABLE","id":"g1","link":"https://shop.example.com/giveaway/a"},{"status":"AVAILABLE","id":"g2","link":"https://shop.example.com/giveaway/b"}]}
                    """),
                ("POST", "/open-api/v1.1/memberships/posts/texts") => Json(HttpStatusCode.OK, """{"type":"Text","action":"Create","postId":42}"""),
                ("GET", "/open-api/v1.0/thank-you-contributions") => Json(HttpStatusCode.OK, """
                    {"results":[{"id":"kud_1","orderId":"o1","supporter":{"email":"x@example.com"},"products":[{"title":"Tee"}],"total":{"amount":20,"currency":"USD"},"state":"WAITING","createdAt":"2026-10-28T11:00:00Z"}],"totalPages":1}
                    """),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
        }

        private static string Hook(IEnumerable<string> types) =>
            new JsonObject
            {
                ["id"] = "wcon_cuebot", ["url"] = "https://relay.example.com/fourthwall/abc",
                ["allowedTypes"] = new JsonArray([.. types.Select(t => (JsonNode)t)]), ["apiVersion"] = "V1",
            }.ToJsonString();

        private HttpResponseMessage Created(string body)
        {
            var types = JsonNode.Parse(body)!["allowedTypes"]!.AsArray().Select(t => (string)t!).ToArray();
            WebhookTypes = types;
            return Json(HttpStatusCode.OK, Hook(types));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
