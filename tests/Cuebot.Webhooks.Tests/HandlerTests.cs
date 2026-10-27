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

        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "t" } };
        var gift = Harness.Data("throne-gift-purchased.json");
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(Harness.Delivery("throne", gift, path: "t")));
        Assert.Equal(WebhookOutcome.Ignored, throne.Handle(Harness.Delivery("throne", gift, path: "t")));
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
        IWebhookHandler handler = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "t" } };
        Assert.Equal("throne", handler.Service);
        Assert.Equal("Throne", handler.DisplayName);
        var outcome = await handler.HandleAsync(Harness.Delivery("throne", Harness.Data("throne-gift-purchased.json"), path: "t"), default);
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
