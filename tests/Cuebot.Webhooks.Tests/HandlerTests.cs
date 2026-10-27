using System.Net;
using System.Text;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;

namespace Cuebot.Webhooks.Tests;

public sealed class HandlerTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Kofi_redeliveries_are_dropped_by_message_id()
    {
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "8e7f3a2b-1c4d-4e5f-9a8b-7c6d5e4f3a2b" } };
        var body = "data=" + WebUtility.UrlEncode(Harness.Data("kofi-donation.json"));
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", body, id: "d1")));
        // Ko-fi retried: new relay delivery id, same message_id.
        Assert.Equal(WebhookOutcome.Ignored, kofi.Handle(Harness.Delivery("kofi", body, id: "d2")));
        Assert.Single(_h.Published);
        Assert.Equal("Duplicate delivery (already handled).", kofi.LastDelivery!.Detail);
    }

    [Fact]
    public void Shopify_and_fourthwall_dedupe_on_their_event_ids_and_the_relay_delivery_id()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var body = Harness.Data("shopify-order.json");
        Dictionary<string, string> H(string id) => new()
        {
            ["X-Shopify-Topic"] = "orders/create", ["X-Shopify-Event-Id"] = id,
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(body)),
        };
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H("evt-1"), id: "r1")));
        Assert.Equal(WebhookOutcome.Ignored, shopify.Handle(Harness.Delivery("shopify", body, H("evt-1"), id: "r2")));
        Assert.Equal(WebhookOutcome.Ignored, shopify.Handle(Harness.Delivery("shopify", body, H("evt-2"), id: "r1")));   // relay replay
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H("evt-3"), id: "r3")));
        Assert.Equal(2, _h.Published.Count);

        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "f" } };
        var fwBody = Harness.Data("fourthwall-donation.json");
        var sig = new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(fwBody)) };
        Assert.Equal(WebhookOutcome.Handled, fw.Handle(Harness.Delivery("fourthwall", fwBody, sig)));
        Assert.Equal(WebhookOutcome.Ignored, fw.Handle(Harness.Delivery("fourthwall", fwBody, sig)));
    }

    [Fact]
    public void Patreon_and_throne_dedupe_identical_bodies()
    {
        var patreon = new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "s" } };
        var body = Harness.Data("patreon-pledge-create.json");
        Dictionary<string, string> H(string topic) => new()
        {
            ["X-Patreon-Event"] = topic, ["X-Patreon-Signature"] = WebhookVerification.HmacMd5Hex("s", Encoding.UTF8.GetBytes(body)),
        };
        Assert.Equal(WebhookOutcome.Handled, patreon.Handle(Harness.Delivery("patreon", body, H("members:pledge:create"))));
        Assert.Equal(WebhookOutcome.Ignored, patreon.Handle(Harness.Delivery("patreon", body, H("members:pledge:create"))));
        Assert.Equal(WebhookOutcome.Handled, patreon.Handle(Harness.Delivery("patreon", body, H("members:update"))));   // same body, other event

        var signer = new ThroneSigner();
        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log, signer.PublicKey);
        var gift = Harness.Data("throne-gift-purchased.json");
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(signer.Delivery(gift)));
        Assert.Equal(WebhookOutcome.Ignored, throne.Handle(signer.Delivery(gift)));
    }

    [Fact]
    public void Throne_dedupes_on_event_id_even_when_the_body_differs()
    {
        var signer = new ThroneSigner();
        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log, signer.PublicKey);
        var gift = Harness.Data("throne-gift-purchased.json");
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(signer.Delivery(gift)));
        var resent = gift.Replace("Happy Birthday!", "Happy Birthday!!", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Ignored, throne.Handle(signer.Delivery(resent)));
        var other = gift.Replace("4b6f2d1e", "4b6f2d1f", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(signer.Delivery(other)));
        Assert.Equal(2, _h.Published.Count);
    }

    [Fact]
    public void Blank_service_ids_never_dedupe_unrelated_deliveries()
    {
        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "f" } };
        WebhookDelivery D(string body) => Harness.Delivery("fourthwall", body,
            new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(body)) });
        var first = Harness.Data("fourthwall-donation.json").Replace("\"id\": \"weve_", "\"id\": \"\", \"old\": \"weve_", StringComparison.Ordinal);
        Assert.Contains("\"id\": \"\"", first, StringComparison.Ordinal);
        var second = first.Replace("KindSupporter", "OtherSupporter", StringComparison.Ordinal);
        Assert.Equal(WebhookOutcome.Handled, fw.Handle(D(first)));
        Assert.Equal(WebhookOutcome.Handled, fw.Handle(D(second)));
        Assert.Equal(2, _h.Published.Count);

        // Shopify without X-Shopify-Webhook-Id or X-Shopify-Event-Id: only the relay delivery id dedupes.
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var order = Harness.Data("shopify-order.json");
        var h = new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = "orders/create", ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(order)),
        };
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", order, h, id: "s1")));
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", order, h, id: "s2")));
        Assert.Equal(WebhookOutcome.Ignored, shopify.Handle(Harness.Delivery("shopify", order, h, id: "s1")));
    }

    [Fact]
    public void Shopify_dedupes_on_the_webhook_id_then_topic_and_event_id()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var body = Harness.Data("shopify-order.json");
        Dictionary<string, string> H(string topic, string? webhookId, string eventId)
        {
            var h = new Dictionary<string, string>
            {
                ["X-Shopify-Topic"] = topic, ["X-Shopify-Event-Id"] = eventId,
                ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(body)),
            };
            if (webhookId is not null) h["X-Shopify-Webhook-Id"] = webhookId;
            return h;
        }
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H("orders/create", "wh-1", "evt-1"))));
        Assert.Equal(WebhookOutcome.Ignored, shopify.Handle(Harness.Delivery("shopify", body, H("orders/create", "wh-1", "evt-9"))));   // retry
        // The same event id for another topic's subscription is a different delivery.
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H("orders/paid", "wh-2", "evt-1"))));
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H("orders/create", null, "evt-1"))));
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H("orders/paid", null, "evt-1"))));
        Assert.Equal(WebhookOutcome.Ignored, shopify.Handle(Harness.Delivery("shopify", body, H("orders/paid", null, "evt-1"))));
        Assert.Equal(4, _h.Published.Count);
    }

    [Fact]
    public void Shopify_xml_webhooks_fail_with_a_clear_message()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        const string xml = """<?xml version="1.0" encoding="UTF-8"?><order><id type="integer">820982911946154508</id></order>""";
        var sig = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(xml));
        Assert.Equal(WebhookOutcome.Failed, shopify.Handle(Harness.Delivery("shopify", xml, new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = "orders/create", ["X-Shopify-Hmac-Sha256"] = sig, ["Content-Type"] = "application/xml",
        })));
        Assert.Equal("Shopify webhook is set to XML; choose JSON in Shopify's webhook settings", shopify.LastDelivery!.Detail);
        Assert.Contains(_h.Log.Recent(), e => e.Message.Contains("choose JSON", StringComparison.Ordinal));
        // Detected from the body too, when no content type is forwarded.
        Assert.Equal(WebhookOutcome.Failed, shopify.Handle(Harness.Delivery("shopify", xml, new Dictionary<string, string>
        {
            ["X-Shopify-Topic"] = "orders/create", ["X-Shopify-Hmac-Sha256"] = sig,
        })));
        Assert.Empty(_h.Published);
    }

    [Fact]
    public void Shopify_test_sends_reuse_one_order_id_but_all_show_in_the_feed()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "k" } };
        var body = Harness.Data("shopify-test-order.json");
        Dictionary<string, string> H(string topic) => new()
        {
            ["X-Shopify-Topic"] = topic, ["X-Shopify-Test"] = "true", ["X-Shopify-Webhook-Id"] = Guid.NewGuid().ToString(),
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(body)),
        };
        foreach (var topic in new[] { "orders/create", "orders/paid", "orders/create" })
            Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, H(topic))));
        Assert.Equal(3, _h.Published.Count);
        Assert.All(_h.Published, e => Assert.Equal(false, e.Args["isRepeatOrder"]));
        Assert.All(_h.Published, e => Assert.NotNull(WebhookChatAdapter.Describe(e)));

        // Real orders: the paid event after created is a repeat (not fed again).
        var real = Harness.Data("shopify-order.json");
        Dictionary<string, string> R(string topic) => new()
        {
            ["X-Shopify-Topic"] = topic, ["X-Shopify-Webhook-Id"] = Guid.NewGuid().ToString(),
            ["X-Shopify-Hmac-Sha256"] = WebhookVerification.HmacSha256Base64("k", Encoding.UTF8.GetBytes(real)),
        };
        shopify.Handle(Harness.Delivery("shopify", real, R("orders/create")));
        shopify.Handle(Harness.Delivery("shopify", real, R("orders/paid")));
        Assert.Equal([false, true], _h.Published.Skip(3).Select(e => (bool)e.Args["isRepeatOrder"]!));
    }

    [Fact]
    public void Kofi_test_buttons_reuse_one_transaction_id_and_are_all_published_as_tests()
    {
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "8e7f3a2b-1c4d-4e5f-9a8b-7c6d5e4f3a2b" } };
        var real = Harness.Data("kofi-donation.json");
        var test = real.Replace("5f8d0c2e-7b4a-4c1e-9e3d-2a6b8c4d1f07", KofiWebhookHandler.TestTransactionId, StringComparison.Ordinal);
        foreach (var messageId in new[] { "aaaaaaaa-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000002" })
        {
            var body = test.Replace("3a1fac0c-f960-4506-a60e-824979a74e74", messageId, StringComparison.Ordinal);
            Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(body))));
        }
        Assert.Equal(2, _h.Published.Count);
        Assert.All(_h.Published, e => Assert.Equal(true, e.Args["isTest"]));

        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(real))));
        Assert.Equal(false, _h.Published.Last().Args["isTest"]);
    }

    [Fact]
    public void Custom_hooks_dedupe_only_on_the_relay_delivery_id()
    {
        var custom = new CustomWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        custom.Hooks.Add(new CustomWebhookSettings { Name = "door", Token = "tok" });
        Assert.Equal(WebhookOutcome.Handled, custom.Handle(Harness.Delivery("custom", "{}", path: "door", query: "token=tok", id: "a")));
        Assert.Equal(WebhookOutcome.Handled, custom.Handle(Harness.Delivery("custom", "{}", path: "door", query: "token=tok", id: "b")));
        Assert.Equal(WebhookOutcome.Ignored, custom.Handle(Harness.Delivery("custom", "{}", path: "door", query: "token=tok", id: "a")));
        Assert.Equal(2, _h.Published.Count);
    }

    [Fact]
    public void Switched_off_services_verify_but_publish_nothing_and_bad_bodies_fail()
    {
        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "f", Enabled = false } };
        var body = Harness.Data("fourthwall-donation.json");
        var sig = new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(body)) };
        Assert.Equal(WebhookOutcome.Ignored, fw.Handle(Harness.Delivery("fourthwall", body, sig)));
        Assert.Equal(WebhookOutcome.Rejected, fw.Handle(Harness.Delivery("fourthwall", body)));
        Assert.Empty(_h.Published);

        fw.Settings.Enabled = true;
        const string bad = "not json";
        Assert.Equal(WebhookOutcome.Failed, fw.Handle(Harness.Delivery("fourthwall", bad,
            new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(bad)) })));
        Assert.Equal(WebhookOutcome.Failed, fw.LastDelivery!.Outcome);
    }

    [Fact]
    public async Task HandleAsync_reports_through_the_contract_and_records_recent_events()
    {
        var signer = new ThroneSigner();
        IWebhookHandler handler = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log, signer.PublicKey);
        Assert.Equal("throne", handler.Service);
        Assert.Equal("Throne", handler.DisplayName);
        var outcome = await handler.HandleAsync(signer.Delivery(Harness.Data("throne-gift-purchased.json")), default);
        Assert.Equal(WebhookOutcome.Handled, outcome);
        var record = Assert.Single(((WebhookServiceBase)handler).RecentEvents());
        Assert.Equal(("Throne", ThroneEventTypes.GiftPurchased, false), (record.Service, record.Type, record.IsTest));
        Assert.Equal("Lyfesaver74 bought AirPods Max from the Throne wishlist", record.Summary);
    }

    [Fact]
    public void Test_events_bypass_verification_and_dedupe_and_are_marked()
    {
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        Assert.False(kofi.IsConfigured);
        Assert.Equal(["donation", "subscription", "resubscription", "commission", "shopOrder"], kofi.TestEventKinds);
        Assert.True(kofi.SendTestEvent("donation"));
        Assert.True(kofi.SendTestEvent("donation"));
        Assert.False(kofi.SendTestEvent("nope"));
        Assert.Equal(2, _h.Events(KofiEventTypes.Donation).Length);
        Assert.All(_h.Published, e => Assert.Equal(true, e.Args["isTest"]));

        foreach (var service in new WebhookServiceBase[]
                 {
                     new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log), new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log),
                     new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log), new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log),
                     new CustomWebhookHandler(_h.Settings, _h.Bus, _h.Log),
                 })
        {
            Assert.NotEmpty(service.TestEventKinds);
            foreach (var kind in service.TestEventKinds) Assert.True(service.SendTestEvent(kind), $"{service.Service}/{kind}");
        }
    }
}
