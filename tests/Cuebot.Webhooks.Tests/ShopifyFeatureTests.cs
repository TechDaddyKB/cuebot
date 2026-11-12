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
/// Shopify: privacy defaults (order note, name on stream, contact details), the Webhook API version check, the shop in a
/// refused delivery, the extra order arguments, Event-Id dedupe, the saved repeat-order marker, cancellations and refunds,
/// the merch counter and goal, stock alerts, the trigger filters, and the optional Admin API (client credentials token,
/// products, stock names, catch-up, totals, sub-actions) against a fake Shopify. Payload fixtures only; no real store.
/// </summary>
public sealed class ShopifyFeatureTests : IDisposable
{
    private const string Key = "shopify-signing-key";
    private readonly Harness _h = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeShopify _api = new();
    private readonly ShopifyWebhookHandler _shop;

    public ShopifyFeatureTests()
    {
        _shop = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = Key } };
        _shop.Tracker = new ShopifyTracker(_h.Settings, _h.Log, new HttpClient(_api), time: _clock, apiBaseUrl: "https://shopify.test");
        _shop.Tracker.Api.Delay = (_, _) => Task.CompletedTask;
        _shop.Tracker.Replay = _shop.ReplayAsync;
    }

    public void Dispose() => _h.Dispose();

    private ShopifyOptions Options => _h.Settings.Value.ShopifyOptions;

    private static Dictionary<string, string> Headers(string topic, string body, string? webhookId = null, string? eventId = null,
        string version = "2026-10", string key = Key)
    {
        var h = new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = topic,
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64(key, Encoding.UTF8.GetBytes(body)),
            ["X-Shopify-Shop-Domain"] = "cue-shop.myshopify.com",
            ["X-Shopify-API-Version"] = version,
            ["X-Shopify-Webhook-Id"] = webhookId ?? Guid.NewGuid().ToString(),
            ["X-Shopify-Triggered-At"] = "2026-11-11T16:00:00.123Z",
            ["Content-Type"] = "application/json",
        };
        if (eventId is not null) h["X-Shopify-Event-Id"] = eventId;
        return h;
    }

    private WebhookOutcome Send(string topic, string body, string? webhookId = null, string? eventId = null, string version = "2026-10") =>
        _shop.Handle(Harness.Delivery("shopify", body, Headers(topic, body, webhookId, eventId, version)));

    private BotEvent Deliver(string topic, string body, string version = "2026-10")
    {
        var before = _h.Published.Count;
        Assert.Equal(WebhookOutcome.Handled, Send(topic, body, version: version));
        return _h.Published.Skip(before).First();
    }

    private static string Full => Harness.Data("shopify-order-paid-full.json");

    private static ITriggerType Trigger(string id) => new ShopifyTriggers().GetTriggers().Single(t => t.TypeId == id);

    private static bool Matches(string trigger, BotEvent e, Dictionary<string, string>? settings = null)
    {
        var t = Trigger(trigger);
        return t.Matches(e, new TriggerDefinition(), ParameterValues.From(t.Parameters, settings ?? []), new Dictionary<string, object?>());
    }

    private void UseApi()
    {
        Options.ShopDomain = "cue-shop";
        Options.ClientId = "client-id";
        Options.ClientSecret = "client-secret";
    }

    // ---- privacy ----

    [Fact]
    public void The_order_note_is_never_the_stream_message_unless_asked_for()
    {
        var e = Deliver("orders/paid", Full);
        Assert.Equal("", e.Args["message"]);
        Assert.Equal("Gate code 4321, call 555-0100", e.Args["orderNote"]);   // for actions that want it
        Assert.Equal("Hi chat, love the stream!", e.Args["cartMessage"]);
        Assert.Null(WebhookChatAdapter.Describe(e)!.Detail);

        Options.MessageSource = ShopifyOptions.CartAttribute;
        Assert.Equal("Hi chat, love the stream!", ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!.Args["message"]);
        Options.MessageAttribute = "gift WRAP";
        Assert.Equal("yes", ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!.Args["message"]);
        Options.MessageSource = ShopifyOptions.OrderNote;
        Assert.Equal("Gate code 4321, call 555-0100", ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!.Args["message"]);
    }

    [Fact]
    public void Buyers_are_shown_by_first_name_unless_told_otherwise()
    {
        Assert.Equal(("Bob", "Bob"), (ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!.Args["user"], ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!.Args["firstName"]));
        Options.NameOnStream = ShopifyOptions.FullName;
        Assert.Equal("Bob Norman", ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!.Args["user"]);
        Options.NameOnStream = ShopifyOptions.Someone;
        var hidden = ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!;
        Assert.Equal(("Someone", "Bob"), (hidden.Args["user"], hidden.Args["firstName"]));

        // Newer / other shapes: camelCase customer, or only a billing name.
        var camel = """{"id":1,"total_price":"1.00","customer":{"firstName":"Ada","lastName":"Lovelace"}}""";
        Assert.Equal("Ada", ShopifyWebhookHandler.Map("orders/paid", camel, false, new ShopifyOptions())!.Args["user"]);
        var billingOnly = """{"id":2,"total_price":"1.00","billing_address":{"name":"Grace Hopper"}}""";
        Assert.Equal("Grace", ShopifyWebhookHandler.Map("orders/paid", billingOnly, false, new ShopifyOptions())!.Args["user"]);
        Assert.Equal("Someone", ShopifyWebhookHandler.Map("orders/paid", """{"id":3,"total_price":"1.00"}""", false, new ShopifyOptions())!.Args["user"]);
    }

    [Fact]
    public void Contact_details_never_reach_actions_or_websocket_clients_by_default()
    {
        var e = Deliver("orders/paid", Full);
        var json = (string)e.Args["_json"]!;
        foreach (var secret in (string[])["bob.norman@example.com", "555-0100", "+15550100", "Chestnut", "203.0.113.7", "imasecretipod",
                     "b1946ac92492d2347c6235b4d2611184", "68778783ad298f1c80c3bafcddeea02f", "Gate code"])
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(e.Args, a => a.Key.StartsWith("shopify.", StringComparison.Ordinal) && a.Value is string v && v.Contains(secret, StringComparison.Ordinal));
        }
        Assert.False(e.Args.ContainsKey("shopify.email"));
        Assert.False(e.Args.ContainsKey("shopify.customer.email"));
        Assert.False(e.Args.ContainsKey("shopify.shipping_address.address1"));
        Assert.Equal("Bob", e.Args["shopify.customer.first_name"]);   // names and the order itself stay
        Assert.Equal("Cuebot hoodie", e.Args["shopify.line_items[0].title"]);

        Options.RedactContactDetails = false;
        var raw = ShopifyWebhookHandler.Map("orders/paid", Full, false, Options)!;
        Assert.Equal(Full, raw.Args["_json"]);
        Assert.Equal("bob.norman@example.com", raw.Args["shopify.email"]);
    }

    // ---- API version and shop ----

    [Fact]
    public void Api_versions_follow_shopifys_twelve_month_window()
    {
        var now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-10", ShopifyApiVersions.LatestStable(now));
        Assert.Equal("2026-01", ShopifyApiVersions.OldestSupported(now));
        Assert.Equal(ShopifyVersionState.Supported, ShopifyApiVersions.Check("2026-10", now).State);
        Assert.Equal(ShopifyVersionState.Supported, ShopifyApiVersions.Check("2026-04", now).State);
        Assert.Equal(ShopifyVersionState.RetiringSoon, ShopifyApiVersions.Check("2026-01", now).State);
        Assert.Equal(ShopifyVersionState.Retired, ShopifyApiVersions.Check("2025-10", now).State);
        Assert.Equal(ShopifyVersionState.ReleaseCandidate, ShopifyApiVersions.Check("2027-01", now).State);
        Assert.Equal(ShopifyVersionState.Unknown, ShopifyApiVersions.Check("unstable", now).State);
        Assert.Equal(ShopifyVersionState.Unknown, ShopifyApiVersions.Check("", now).State);
        Assert.Contains("choose 2026-10", ShopifyApiVersions.Check("2025-07", now).Advice, StringComparison.Ordinal);
        Assert.Equal("", ShopifyApiVersions.Check("2026-07", now).Advice);
        Assert.Equal("2027-01", ShopifyApiVersions.LatestStable(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_webhook_api_version_is_shown_and_an_old_one_is_warned_about_once()
    {
        var e = Deliver("orders/paid", Full);
        Assert.Equal("2026-10", e.Args["apiVersion"]);
        Assert.EndsWith("; API version 2026-10", _shop.LastDelivery!.Detail, StringComparison.Ordinal);
        Assert.Equal((DateTimeOffset?)_clock.GetUtcNow(), _shop.Tracker.LastDelivery.At);

        Deliver("orders/create", Full, version: "2025-07");
        Assert.Contains("API version 2025-07", _shop.LastDelivery!.Detail, StringComparison.Ordinal);
        Assert.Contains("is retired", _shop.LastDelivery.Detail, StringComparison.Ordinal);
        Assert.Contains("choose 2026-10", _shop.LastDelivery.Detail, StringComparison.Ordinal);
        Assert.Equal("2025-07", _shop.Tracker.LastDelivery.ApiVersion);
        Send("orders/create", Harness.Data("shopify-order.json"), version: "2025-07");
        Assert.Single(_h.Log.Recent(), l => l.Message.Contains("2025-07 is retired", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_delivery_names_the_shop_it_came_from()
    {
        var body = Full;
        var h = Headers("orders/paid", body, key: "another-stores-key");
        h["X-Shopify-Shop-Domain"] = "second-store.myshopify.com";
        Assert.Equal(WebhookOutcome.Rejected, _shop.Handle(Harness.Delivery("shopify", body, h)));
        Assert.Contains("second-store.myshopify.com", _shop.LastDelivery!.Detail, StringComparison.Ordinal);
        Assert.Contains("each store has its own signing key", _shop.LastDelivery.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, _shop.LastDelivery.Detail, StringComparison.Ordinal);
        Assert.Null(_shop.Tracker.LastDelivery.At);   // refused deliveries do not count as "Shopify is still sending"
    }

    [Fact]
    public void A_quiet_week_warns_that_shopify_may_have_deleted_the_webhook()
    {
        Assert.Equal("", _shop.Tracker.QuietWarningText());
        Deliver("orders/paid", Full);
        _clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal("", _shop.Tracker.QuietWarningText());
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Contains("No Shopify delivery for 8 days", _shop.Tracker.QuietWarningText(), StringComparison.Ordinal);
        Assert.Contains("route secret", _shop.Tracker.QuietWarningText(), StringComparison.Ordinal);
    }

    // ---- arguments and dedupe ----

    [Fact]
    public void Orders_carry_the_extra_arguments()
    {
        var e = Deliver("orders/paid", Full);
        Assert.Equal("450789469", e.Args["orderId"]);
        Assert.Equal("paid", e.Args["financialStatus"]);
        Assert.Equal("cue-shop.myshopify.com", e.Args["shopDomain"]);
        Assert.IsType<DateTime>(e.Args["triggeredAt"]);
        Assert.Equal("https://admin.shopify.com/store/cue-shop/orders/450789469", e.Args["orderUrl"]);
        Assert.Equal("TECHDADDY10", e.Args["discountCodes"]);
        Assert.Equal("web", e.Args["sourceName"]);
        Assert.Equal(("Cuebot hoodie", 1, "M / Black", "HOODIE-M-BLK"), (e.Args["item0.name"], e.Args["item0.quantity"], e.Args["item0.variant"], e.Args["item0.sku"]));
        Assert.Equal(("Sticker pack", 2, 10m), (e.Args["item1.name"], e.Args["item1.quantity"], e.Args["item1.price"]));
        Assert.Equal(3.0, e.Args["itemCount"]);
        Assert.Equal("Cuebot hoodie - M / Black, Sticker pack", e.Args["items"]);
        Assert.Equal((60.12, "EUR"), (e.Args["presentmentAmount"], e.Args["presentmentCurrency"]));
        Assert.Equal(65m, e.Args["amount"]);
    }

    [Fact]
    public void Two_webhooks_for_the_same_topic_run_once()
    {
        // Two admin webhooks pointing at Cuebot: different webhook ids, one event id.
        Assert.Equal(WebhookOutcome.Handled, Send("orders/paid", Full, "wh-a", "evt-1"));
        Assert.Equal(WebhookOutcome.Ignored, Send("orders/paid", Full, "wh-b", "evt-1"));
        Assert.Single(_h.Events(ShopifyEventTypes.OrderPaid));
    }

    [Fact]
    public void A_repeat_order_stays_a_repeat_after_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cuebot-shopify-seen-{Guid.NewGuid():N}.json");
        try
        {
            _shop.Seen = new RecentIds(path: path);
            Assert.Equal(false, Deliver("orders/create", Full).Args["isRepeatOrder"]);
            var restarted = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = Key }, Seen = new RecentIds(path: path) };
            var before = _h.Published.Count;
            Assert.Equal(WebhookOutcome.Handled, restarted.Handle(Harness.Delivery("shopify", Full, Headers("orders/paid", Full))));
            var paid = _h.Published.Skip(before).First();
            Assert.Equal(true, paid.Args["isRepeatOrder"]);
            Assert.Null(WebhookChatAdapter.Describe(paid));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- cancellations, refunds and the merch counter ----

    [Fact]
    public void Cancelled_and_refunded_orders_come_off_the_merch_counter()
    {
        Options.Goal.Target = 3;
        Deliver("orders/create", Full);
        Assert.Equal(0, _shop.Tracker.Sales().Orders);   // created is not paid

        var paid = Deliver("orders/paid", Full);
        Assert.Equal(true, paid.Args["wasCounted"]);
        var reached = Assert.Single(_h.Events(ShopifyEventTypes.GoalReached));
        Assert.Equal(true, reached.Args["shopifyGoalReached"]);
        var sales = _shop.Tracker.Sales();
        Assert.Equal((65.0, 1, 3), (sales.Revenue, sales.Orders, sales.Items));
        Assert.Equal(2, sales.Products["Sticker pack"]);

        // A refund of the hoodie: names the buyer and order from the paid order.
        var refundBody = Harness.Data("shopify-refund.json").Replace("820982911946154508", "450789469", StringComparison.Ordinal);
        var refund = Deliver("refunds/create", refundBody);
        Assert.Equal(ShopifyEventTypes.RefundCreated, refund.Type);
        Assert.Equal((89.99m, "USD", "Bob", "#1001"), (refund.Args["amount"], refund.Args["currency"], refund.Args["user"], refund.Args["orderName"]));
        Assert.Equal(("509562969", true, "Aviator sunglasses"), (refund.Args["refundId"], refund.Args["restock"], refund.Args["item0.name"]));
        Assert.Equal("Wrong size", refund.Args["refundNote"]);
        Assert.Equal("Refunded $89.99 on order #1001 (Bob)", WebhookChatAdapter.Describe(refund)!.Title);
        Assert.Null(WebhookChatAdapter.Describe(refund)!.Spend);
        sales = _shop.Tracker.Sales();
        Assert.Equal((0.0, 65.0, 2), (sales.Revenue, sales.Refunded, sales.Items));   // never below what the order brought

        // Then cancelled: the order leaves the counter; the rest of its items too.
        var cancelled = Deliver("orders/cancelled", Full.Replace("\"cancel_reason\": null", "\"cancel_reason\": \"fraud\"", StringComparison.Ordinal)
            .Replace("\"cancelled_at\": null", "\"cancelled_at\": \"2026-11-11T13:00:00-05:00\"", StringComparison.Ordinal));
        Assert.Equal(("fraud", true), (cancelled.Args["cancelReason"], cancelled.Args["wasCounted"]));
        Assert.Equal("Order #1001 from Bob was cancelled ($65.00)", WebhookChatAdapter.Describe(cancelled)!.Title);
        sales = _shop.Tracker.Sales();
        Assert.Equal((0.0, 0, 0), (sales.Revenue, sales.Orders, sales.Items));
        Assert.Empty(sales.Products);

        // A cancelled order paid again (a late or replayed delivery) does not count again.
        Assert.Equal(WebhookOutcome.Ignored, Send("orders/paid", Full));
        Assert.Equal(0, _shop.Tracker.Sales().Orders);
    }

    [Fact]
    public void The_merch_counter_resets_when_a_stream_goes_live_and_tests_never_count()
    {
        Deliver("orders/paid", Full);
        Assert.True(_shop.SendTestEvent("orderPaid"));
        Assert.True(_shop.SendTestEvent("refundCreated"));
        Assert.True(_shop.SendTestEvent("orderCancelled"));
        Assert.Equal(1, _shop.Tracker.Sales().Orders);

        _clock.Advance(TimeSpan.FromHours(2));
        _shop.Tracker.OnEvent(new BotEvent(ChatHub.ActivityEvent, new Dictionary<string, object?> { ["kind"] = nameof(FeedKind.StreamOnline) }));
        Assert.Equal((0, _clock.GetUtcNow()), (_shop.Tracker.Sales().Orders, _shop.Tracker.Sales().Since));

        Options.ResetSalesOnStreamStart = false;
        Deliver("orders/paid", Full.Replace("450789469", "450789470", StringComparison.Ordinal));
        _clock.Advance(TimeSpan.FromHours(2));
        _shop.Tracker.OnEvent(new BotEvent(ChatHub.ActivityEvent, new Dictionary<string, object?> { ["kind"] = nameof(FeedKind.StreamOnline) }));
        Assert.Equal(1, _shop.Tracker.Sales().Orders);
    }

    [Fact]
    public void The_goal_can_count_revenue_and_reaches_once_per_counter()
    {
        _shop.Tracker.SetGoal("Hoodie money", 100, "Revenue");
        Deliver("orders/paid", Full);
        Assert.Empty(_h.Events(ShopifyEventTypes.GoalReached));
        Assert.Equal((65.0, 65, "USD"), (_shop.Tracker.Goal().Current, _shop.Tracker.Goal().Percent, _shop.Tracker.Goal().Currency));
        Deliver("orders/paid", Full.Replace("450789469", "450789470", StringComparison.Ordinal));
        Deliver("orders/paid", Full.Replace("450789469", "450789471", StringComparison.Ordinal));
        Assert.Single(_h.Events(ShopifyEventTypes.GoalReached));

        var data = new JsonObject();
        _shop.Tracker.AddTo(data);
        Assert.Equal(195.0, data["shopify"]!["goal"]!["current"]!.GetValue<double>());
        Assert.Equal("Hoodie money", data["shopify"]!["goal"]!["title"]!.GetValue<string>());
        Assert.Equal(3, data["shopify"]!["sales"]!["orders"]!.GetValue<int>());
    }

    // ---- stock ----

    [Fact]
    public void Stock_levels_raise_low_stock_sold_out_and_restocked_once_each()
    {
        string Level(int available, string location = "24826418") => Harness.Data("shopify-inventory-level.json")
            .Replace("\"available\": 3", $"\"available\": {available}", StringComparison.Ordinal)
            .Replace("\"location_id\": 24826418", $"\"location_id\": {location}", StringComparison.Ordinal);

        Assert.Equal(WebhookOutcome.Ignored, Send("inventory_levels/update", Level(20)));    // first sight, plenty
        var low = Deliver("inventory_levels/update", Level(3));
        Assert.Equal(ShopifyEventTypes.LowStock, low.Type);
        Assert.Equal((3, 20, 5), (low.Args["available"], low.Args["previousAvailable"], low.Args["threshold"]));
        Assert.Equal(WebhookOutcome.Ignored, Send("inventory_levels/update", Level(2)));     // still low: once
        Assert.Equal(ShopifyEventTypes.SoldOut, Deliver("inventory_levels/update", Level(0)).Type);
        Assert.Equal(WebhookOutcome.Ignored, Send("inventory_levels/update", Level(0)));
        // Stock at a second location counts towards the total.
        Assert.Equal(ShopifyEventTypes.Restocked, Deliver("inventory_levels/update", Level(10, "99")).Type);
        Assert.Null(WebhookChatAdapter.Describe(low));

        var sku = Trigger(ShopifyEventTypes.LowStock);
        Assert.True(Matches(ShopifyEventTypes.LowStock, low, new() { ["sku"] = "271878346596884015" }));
        Assert.False(Matches(ShopifyEventTypes.LowStock, low, new() { ["sku"] = "OTHER" }));
        Assert.NotNull(sku);
    }

    [Fact]
    public async Task Stock_alerts_are_named_through_the_api()
    {
        UseApi();
        string Level(int available) => Harness.Data("shopify-inventory-level.json").Replace("\"available\": 3", $"\"available\": {available}", StringComparison.Ordinal);
        Send("inventory_levels/update", Level(20));
        var low = await Task.Run(() => Deliver("inventory_levels/update", Level(2)));
        Assert.Equal(("Cuebot hoodie", "M / Black", "HOODIE-M-BLK", "Cuebot hoodie (M / Black)"),
            (low.Args["productName"], low.Args["variantName"], low.Args["sku"], low.Args["item"]));
        Assert.True(Matches(ShopifyEventTypes.LowStock, low, new() { ["product"] = "hoodie" }));
        var query = _api.Requests.Single(r => r.Body.Contains("inventoryItem(", StringComparison.Ordinal));
        Assert.Contains("gid://shopify/InventoryItem/271878346596884015", query.Body, StringComparison.Ordinal);
        // Cached: the next alert for the item asks nothing.
        Send("inventory_levels/update", Level(0));
        Assert.Single(_api.Requests, r => r.Body.Contains("inventoryItem(", StringComparison.Ordinal));
    }

    // ---- triggers ----

    [Fact]
    public void Order_triggers_filter_on_products_skus_discount_codes_and_payment_status()
    {
        var e = Deliver("orders/paid", Full);
        Assert.True(Matches(ShopifyEventTypes.OrderPaid, e));
        Assert.True(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["product"] = "mug, HOODIE" }));
        Assert.True(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["product"] = "black" }));   // variant
        Assert.False(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["product"] = "mug" }));
        Assert.True(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["sku"] = "stickers" }));
        Assert.False(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["sku"] = "STICK" }));
        Assert.True(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["discountCode"] = "techdaddy10" }));
        Assert.False(Matches(ShopifyEventTypes.OrderPaid, e, new() { ["discountCode"] = "OTHER" }));

        var pending = ShopifyWebhookHandler.Map("orders/create", Full.Replace("\"financial_status\": \"paid\"", "\"financial_status\": \"pending\"",
            StringComparison.Ordinal), false, Options)!;
        var created = new BotEvent(pending.Type, pending.Args);
        Assert.True(Matches(ShopifyEventTypes.OrderCreated, created));
        Assert.False(Matches(ShopifyEventTypes.OrderCreated, created, new() { ["financialStatus"] = "paid" }));
        Assert.True(Matches(ShopifyEventTypes.OrderCreated, created, new() { ["financialStatus"] = "paid, pending" }));

        var replay = new BotEvent(ShopifyEventTypes.OrderPaid, new Dictionary<string, object?>(e.Args) { ["isReplay"] = true });
        Assert.True(Matches(ShopifyEventTypes.OrderPaid, replay));
        Assert.False(Matches(ShopifyEventTypes.OrderPaid, replay, new() { ["skipReplays"] = "true" }));
    }

    [Fact]
    public void Every_test_kind_publishes_a_test_event()
    {
        foreach (var kind in _shop.TestEventKinds) Assert.True(_shop.SendTestEvent(kind), kind);
        Assert.All(_h.Published.Where(e => e.Type.StartsWith("shopify.", StringComparison.Ordinal)), e => Assert.Equal(true, e.Args["isTest"]));
        var sample = _h.Events(ShopifyEventTypes.OrderPaid).Single();
        Assert.Equal(("", "John"), (sample.Args["message"], sample.Args["user"]));   // the sample's note is never read out
        Assert.Equal(new[] { ShopifyEventTypes.LowStock, ShopifyEventTypes.SoldOut, ShopifyEventTypes.Restocked },
            _h.Published.Where(e => e.Type is ShopifyEventTypes.LowStock or ShopifyEventTypes.SoldOut or ShopifyEventTypes.Restocked).Select(e => e.Type));
        Assert.Equal(0, _shop.Tracker.Sales().Orders);
    }

    // ---- Admin API ----

    [Fact]
    public async Task Connecting_uses_the_client_credentials_grant_and_reads_products()
    {
        UseApi();
        Assert.True(_shop.Tracker.HasCredentials);
        Assert.Equal("Connected to Cue Shop: 2 product(s) for the showcase.", await _shop.Tracker.ConnectAsync());
        var token = _api.Requests.Single(r => r.Path == "/admin/oauth/access_token");
        Assert.Contains("grant_type=client_credentials", token.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=client-id", token.Body, StringComparison.Ordinal);
        Assert.All(_api.Requests.Where(r => r.Path.EndsWith("graphql.json", StringComparison.Ordinal)), r =>
        {
            Assert.Equal("/admin/api/2026-10/graphql.json", r.Path);
            Assert.Equal("token-1", r.Token);
        });
        var products = _shop.Tracker.Products();
        Assert.Equal(("Cuebot hoodie", "$50.00", false), (products[0].Name, products[0].FormattedPrice, products[0].SoldOut));
        Assert.True(products[1].SoldOut);
        var data = new JsonObject();
        _shop.Tracker.AddTo(data);
        Assert.Equal("Cue Shop", data["shopify"]!["shop"]!.GetValue<string>());
        Assert.Equal("https://cue-shop.example/products/hoodie", data["shopify"]!["products"]![0]!["url"]!.GetValue<string>());

        // The token is reused while it lasts, renewed when it runs out, and renewed once when refused.
        await _shop.Tracker.RefreshProductsAsync();
        Assert.Single(_api.Requests, r => r.Path == "/admin/oauth/access_token");
        _clock.Advance(TimeSpan.FromHours(24));
        await _shop.Tracker.RefreshProductsAsync();
        Assert.Equal(2, _api.Requests.Count(r => r.Path == "/admin/oauth/access_token"));
        _api.RevokeTokens();
        Assert.StartsWith("Read 2", await _shop.Tracker.RefreshProductsAsync(), StringComparison.Ordinal);
        Assert.Equal(3, _api.Requests.Count(r => r.Path == "/admin/oauth/access_token"));
    }

    [Fact]
    public async Task A_pasted_access_token_is_used_as_is_and_a_refusal_asks_to_reconnect()
    {
        Options.ShopDomain = "https://cue-shop.myshopify.com/admin";
        Options.AccessToken = "shpat_legacy";
        await _shop.Tracker.ConnectAsync();
        Assert.DoesNotContain(_api.Requests, r => r.Path == "/admin/oauth/access_token");
        Assert.All(_api.Requests, r => Assert.Equal("shpat_legacy", r.Token));
        Assert.True(_shop.Tracker.Connected);

        Options.AccessToken = "shpat_wrong";
        var result = await _shop.Tracker.RefreshProductsAsync().ContinueWith(t => t.Exception?.InnerException);
        Assert.IsType<ShopifyAuthException>(result);

        Options.AccessToken = "";
        Options.ClientId = "client-id";
        Options.ClientSecret = "wrong-secret";
        Assert.Contains("refused the app's client id and secret", await _shop.Tracker.ConnectAsync(), StringComparison.Ordinal);
        Assert.True(_shop.Tracker.NeedsReconnect);
        Assert.DoesNotContain(_h.Log.Recent(), l => l.Message.Contains("wrong-secret", StringComparison.Ordinal) || l.Message.Contains("shpat_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Throttled_queries_are_retried()
    {
        UseApi();
        _api.Throttle = 2;
        Assert.StartsWith("Connected", await _shop.Tracker.ConnectAsync(), StringComparison.Ordinal);
        _api.Throttle = 9;
        Assert.Contains("rate limiting", await _shop.Tracker.ConnectAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catch_up_runs_order_paid_for_orders_no_delivery_brought_and_never_twice()
    {
        UseApi();
        await _shop.Tracker.ConnectAsync();   // catch-up starts from now
        _clock.Advance(TimeSpan.FromMinutes(30));
        _api.OrdersCreatedAt = _clock.GetUtcNow() - TimeSpan.FromMinutes(10);
        // One of the two orders did arrive by webhook.
        Deliver("orders/paid", ShopifyApi.RestOrder(JsonDocument.Parse(FakeShopify.Order("1002", "Ada", "LISTED", _api.OrdersCreatedAt)).RootElement));

        Assert.Equal("Ran Order Paid for 1 order(s) that never arrived.", await _shop.Tracker.CatchUpAsync());
        var replayed = _h.Events(ShopifyEventTypes.OrderPaid).Last();
        Assert.Equal((true, 600L, "1001", "Grace"), (replayed.Args["isReplay"], replayed.Args["delaySeconds"], replayed.Args["orderId"], replayed.Args["user"]));
        Assert.Equal(("Cuebot hoodie - M / Black", "M / Black", "TECHDADDY10"), (replayed.Args["items"], replayed.Args["item0.variant"], replayed.Args["discountCodes"]));
        Assert.Equal("Hi chat!", replayed.Args["cartMessage"]);
        Assert.Contains("financial_status:paid", _api.Requests.Last(r => r.Body.Contains("orders(", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Equal(2, _shop.Tracker.Sales().Orders);

        Assert.Equal("No missed Shopify orders.", await _shop.Tracker.CatchUpAsync());
        // The real delivery arriving late is a duplicate, not a second sale.
        Assert.Equal(WebhookOutcome.Ignored,
            Send("orders/paid", ShopifyApi.RestOrder(JsonDocument.Parse(FakeShopify.Order("1001", "Grace", "LISTED", _api.OrdersCreatedAt)).RootElement)));
        Assert.Equal(2, _shop.Tracker.Sales().Orders);
        Assert.Equal((0, (DateTimeOffset?)_clock.GetUtcNow()), (_shop.Tracker.LastCatchUp.Count, _shop.Tracker.LastCatchUp.At));
    }

    [Fact]
    public async Task Sub_actions_read_the_counter_set_the_goal_and_total_sales()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-shopify-").FullName;
        var capture = new Capture();
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(dir)).AddWebhooks();
        collection.AddSingleton(new HttpClient(_api));
        collection.AddSingleton<ISubActionHandler>(capture);
        await using (var services = collection.BuildServiceProvider())
        {
            var settings = services.GetRequiredService<Cuebot.Core.Storage.JsonFile<WebhooksSettings>>();
            var tracker = services.GetRequiredService<ShopifyTracker>();
            tracker.Api.Delay = (_, _) => Task.CompletedTask;
            var handler = services.GetRequiredService<ShopifyWebhookHandler>();
            Assert.Same(tracker, handler.Tracker);
            handler.Settings.Secret = Key;
            var engine = services.GetRequiredService<ActionEngine>();
            var registry = services.GetRequiredService<Registry>();
            foreach (var id in ShopifySubActions.TypeIds) Assert.Equal("Shopify", registry.GetSubAction(id)!.Category);

            async Task<Dictionary<string, object?>> Run(string type, Dictionary<string, string> parameters)
            {
                Assert.True(await engine.RunAsync(new ActionDefinition
                {
                    Name = type,
                    SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = Capture.Id }],
                }));
                return capture.Args!;
            }

            Assert.Equal(WebhookOutcome.Handled, handler.Handle(Harness.Delivery("shopify", Full, Headers("orders/paid", Full))));
            var sales = await Run("shopify.getSales", []);
            Assert.Equal((true, 65m, 1, 3), (sales["shopifyOk"], sales["shopifySalesRevenue"], sales["shopifySalesOrders"], sales["shopifySalesItems"]));
            Assert.Equal(("Sticker pack", "2 x Sticker pack, 1 x Cuebot hoodie"), (sales["shopifyTopProduct"], sales["shopifySalesProductsText"]));

            var goal = await Run("shopify.setGoal", new() { ["title"] = "Hoodies", ["target"] = "10" });
            Assert.Equal(("Hoodies", 10m, 30, ShopifyGoalSettings.Items), (goal["shopifyGoalTitle"], goal["shopifyGoalTarget"], goal["shopifyGoalPercent"], goal["shopifyGoalMeasure"]));
            Assert.Equal(10, settings.Value.ShopifyOptions.Goal.Target);

            // Without the API the totals step reports and lets the action go on.
            var noApi = await Run("shopify.getTotals", []);
            Assert.Equal(false, noApi["shopifyOk"]);
            Assert.Contains("Connect the Shopify API", (string)noApi["shopifyResult"]!, StringComparison.Ordinal);

            settings.Value.ShopifyOptions.ShopDomain = "cue-shop";
            settings.Value.ShopifyOptions.AccessToken = "shpat_legacy";
            _api.OrdersCreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
            var totals = await Run("shopify.getTotals", new() { ["since"] = ShopifySubActions.SinceHours, ["hours"] = "6" });
            Assert.Equal((true, 130m, 2, 2, "$130.00"), (totals["shopifyOk"], totals["shopifyRevenue"], totals["shopifyOrders"], totals["shopifyItems"], totals["shopifyFormattedRevenue"]));

            await Run("shopify.refreshProducts", []);
            var found = await Run("shopify.getProducts", new() { ["search"] = "hood" });
            Assert.Equal((1, "Cuebot hoodie", "$50.00"), (found["shopifyProductCount"], found["product0.name"], found["product0.price"]));

            var reset = await Run("shopify.resetSales", []);
            Assert.Equal(0, reset["shopifySalesOrders"]);
        }
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Capture : ISubActionHandler
    {
        public const string Id = "test.shopifycapture";
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
    /// A fake Shopify: the client credentials token endpoint (client-id / client-secret → token-1, token-2 ...) and the
    /// Admin GraphQL endpoint answering the shop, products, inventoryItem and orders queries in Shopify's documented shapes.
    /// </summary>
    private sealed class FakeShopify : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Body, string Token);

        private readonly List<Request> _requests = [];
        private readonly HashSet<string> _valid = ["shpat_legacy"];
        private int _issued;

        public IReadOnlyList<Request> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        /// <summary>How many GraphQL calls answer THROTTLED first.</summary>
        public int Throttle { get; set; }

        public DateTimeOffset OrdersCreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public void RevokeTokens()
        {
            lock (_valid) _valid.RemoveWhere(t => t.StartsWith("token-", StringComparison.Ordinal));
        }

        public static string Order(string id, string firstName, string listed, DateTimeOffset createdAt) => """
            {"id":"gid://shopify/Order/ID","legacyResourceId":"ID","name":"#ID","createdAt":"AT","processedAt":"AT",
             "updatedAt":"AT","cancelledAt":null,"test":false,"note":"Door code 99","sourceName":"web","currencyCode":"USD",
             "presentmentCurrencyCode":"USD","displayFinancialStatus":"PAID","discountCodes":["TECHDADDY10"],
             "totalPriceSet":{"shopMoney":{"amount":"65.0","currencyCode":"USD"},"presentmentMoney":{"amount":"65.0","currencyCode":"USD"}},
             "customAttributes":[{"key":"Stream message","value":"Hi chat!"}],"customer":{"firstName":"FIRST","lastName":"Smith"},
             "lineItems":{"nodes":[{"name":"Cuebot hoodie - M / Black","title":"Cuebot hoodie","quantity":1,"sku":"HOODIE-M-BLK","variantTitle":"M / Black",
               "product":{"legacyResourceId":"632910392"},"originalUnitPriceSet":{"shopMoney":{"amount":"65.0"}}}]}}
            """.Replace("ID", id, StringComparison.Ordinal).Replace("FIRST", firstName, StringComparison.Ordinal)
            .Replace("\"AT\"", "\"" + createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.TryGetValues("X-Shopify-Access-Token", out var values) ? values.Single() : "";
            lock (_requests) _requests.Add(new(request.Method.Method, path, body, token));
            if (path == "/admin/oauth/access_token")
            {
                if (!body.Contains("client_secret=client-secret", StringComparison.Ordinal))
                    return Json(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");
                var issued = $"token-{Interlocked.Increment(ref _issued)}";
                lock (_valid) _valid.Add(issued);
                return Json(HttpStatusCode.OK, $$"""{"access_token":"{{issued}}","scope":"read_orders,read_products,read_inventory","expires_in":86399}""");
            }
            if (path != "/admin/api/2026-10/graphql.json") return Json(HttpStatusCode.NotFound, "{}");
            bool ok;
            lock (_valid) ok = _valid.Contains(token);
            if (!ok) return Json(HttpStatusCode.Unauthorized, """{"errors":"[API] Invalid API key or access token (unrecognized login or wrong password)"}""");
            if (Throttle > 0)
            {
                Throttle--;
                return Json(HttpStatusCode.OK, """{"errors":[{"message":"Throttled","extensions":{"code":"THROTTLED"}}]}""");
            }
            if (body.Contains("shop {", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"data":{"shop":{"name":"Cue Shop","myshopifyDomain":"cue-shop.myshopify.com","currencyCode":"USD","primaryDomain":{"url":"https://cue-shop.example"}}}}""");
            if (body.Contains("products(", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """
                    {"data":{"products":{"nodes":[
                      {"id":"gid://shopify/Product/1","title":"Cuebot hoodie","handle":"hoodie","onlineStoreUrl":"https://cue-shop.example/products/hoodie",
                       "totalInventory":12,"tracksInventory":true,"featuredMedia":{"preview":{"image":{"url":"https://cdn.shopify.test/hoodie.png"}}},
                       "priceRangeV2":{"minVariantPrice":{"amount":"50.0","currencyCode":"USD"}}},
                      {"id":"gid://shopify/Product/2","title":"Limited tee","handle":"tee","onlineStoreUrl":null,"totalInventory":0,"tracksInventory":true,
                       "featuredMedia":null,"priceRangeV2":{"minVariantPrice":{"amount":"25.0","currencyCode":"USD"}}}]}},
                     "extensions":{"cost":{"requestedQueryCost":5,"actualQueryCost":4,"throttleStatus":{"maximumAvailable":2000,"currentlyAvailable":1996,"restoreRate":100}}}}
                    """);
            if (body.Contains("inventoryItem(", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """
                    {"data":{"inventoryItem":{"id":"gid://shopify/InventoryItem/271878346596884015","sku":"HOODIE-M-BLK",
                      "variant":{"title":"M / Black","displayName":"Cuebot hoodie - M / Black","product":{"title":"Cuebot hoodie"}}}}}
                    """);
            if (body.Contains("orders(", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"data":{"orders":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":["""
                    + Order("1002", "Ada", "LISTED", OrdersCreatedAt) + "," + Order("1001", "Grace", "LISTED", OrdersCreatedAt) + "]}}}");
            return Json(HttpStatusCode.OK, """{"errors":[{"message":"Unknown query"}]}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
