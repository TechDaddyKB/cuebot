using System.Net;
using System.Text;
using Cuebot.Core.Events;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;

namespace Cuebot.Webhooks.Tests;

/// <summary>Shop orders and donations from webhooks become overlay alerts (merch and tips).</summary>
public sealed class OverlayAdapterTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private static WebhookOverlayAdapter Adapter(string service) => WebhookOverlayAdapter.All().Single(a => a.Service == service);

    [Fact]
    public void Shop_orders_are_merch_and_donations_are_tips()
    {
        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "f" } };
        foreach (var file in new[] { "fourthwall-order-placed.json", "fourthwall-donation.json" })
        {
            var body = Harness.Data(file);
            fw.Handle(Harness.Delivery("fourthwall", body,
                new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("f", Encoding.UTF8.GetBytes(body)) }));
        }
        var order = Adapter("fourthwall").ToAlert(_h.Events(FourthwallEventTypes.OrderPlaced).Single())!;
        Assert.Equal(("merch-latest", "MerchFan", 17.0), (order.Listener, order.Event["name"]!.GetValue<string>(), Cuebot.Core.Overlays.Events.OverlayAlerts.NumberOf(order.Event["amount"])));
        Assert.False(string.IsNullOrEmpty(order.Event["items"]!.GetValue<string>()));
        Assert.Equal("tip-latest", Adapter("fourthwall").ToAlert(_h.Events(FourthwallEventTypes.Donation).Single())!.Listener);

        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log) { Settings = { Secret = "8e7f3a2b-1c4d-4e5f-9a8b-7c6d5e4f3a2b" } };
        kofi.Handle(Harness.Delivery("kofi", "data=" + WebUtility.UrlEncode(Harness.Data("kofi-shop-order.json")), id: "k1"));
        var shop = Adapter("kofi").ToAlert(_h.Events(KofiEventTypes.ShopOrder).Single())!;
        Assert.Equal(("merch-latest", "Merch Buyer", 18.0), (shop.Listener, shop.Event["name"]!.GetValue<string>(), Cuebot.Core.Overlays.Events.OverlayAlerts.NumberOf(shop.Event["amount"])));
        Assert.Contains("Sticker pack", shop.Event["items"]!.GetValue<string>());

        // Shopify counts an order once it is paid, so an order is not an alert twice.
        Assert.Null(Adapter("shopify").ToAlert(new BotEvent(ShopifyEventTypes.OrderCreated, new Dictionary<string, object?>())));
        Assert.Equal("merch-latest", Adapter("shopify").ToAlert(new BotEvent(ShopifyEventTypes.OrderPaid,
            new Dictionary<string, object?> { ["user"] = "Steve", ["amount"] = 254.98m, ["items"] = "Hoodie" }))!.Listener);
        Assert.Null(Adapter("kofi").ToAlert(_h.Events(FourthwallEventTypes.OrderPlaced).Single()));   // each adapter is its own shop's
    }
}
