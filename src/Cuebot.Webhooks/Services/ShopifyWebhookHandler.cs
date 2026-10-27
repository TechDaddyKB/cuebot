using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Shopify order webhooks (<c>orders/create</c>, <c>orders/paid</c>). <c>X-Shopify-Hmac-Sha256</c> is the base64
/// HMAC-SHA256 of the raw body keyed with the store's webhook signing key (admin-created webhooks) or the app's
/// client secret; <c>X-Shopify-Topic</c> names the event; dedupe on <c>X-Shopify-Webhook-Id</c> (else topic +
/// <c>X-Shopify-Event-Id</c>). Test deliveries carry <c>X-Shopify-Test: true</c> and/or <c>"test": true</c>. Only
/// JSON-format webhooks are read. The order is flattened to <c>shopify.*</c> arguments like Streamer.bot's.
/// </summary>
public sealed class ShopifyWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "shopify";

    // Orders already shown in the activity feed, so "created" and "paid" for the same order appear once.
    private readonly RecentIds _fedOrders = new();

    public override string Service => Route;
    public override string DisplayName => "Shopify";
    public WebhookServiceSettings Settings => SettingsFile.Value.Shopify;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Shopify webhook signing key";
    public override IReadOnlyList<string> TestEventKinds => ["orderCreated", "orderPaid"];

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.CheckHmacSha256Base64(Settings.Secret.Trim(), delivery.Body, delivery.Header("X-Shopify-Hmac-Sha256"));

    /// <summary>Message for a webhook created with the XML format, which Cuebot does not read.</summary>
    internal const string XmlNotSupported = "Shopify webhook is set to XML; choose JSON in Shopify's webhook settings";

    private protected override string? Unsupported(WebhookDelivery delivery) =>
        delivery.Header("Content-Type").Contains("xml", StringComparison.OrdinalIgnoreCase) || delivery.BodyText.TrimStart().StartsWith('<')
            ? XmlNotSupported
            : null;

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var topic = delivery.Header("X-Shopify-Topic").Trim();
        var headerTest = string.Equals(delivery.Header("X-Shopify-Test").Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var e = Map(topic, delivery.BodyText, isTest: headerTest);
        if (e is null) return [];
        MarkRepeat(e);
        return [e with { Keys = [DedupeKey(delivery, topic)] }];
    }

    /// <summary>
    /// <c>X-Shopify-Webhook-Id</c> (one per delivery, kept on retries: Shopify's documented dedupe key), else
    /// <c>topic:X-Shopify-Event-Id</c>; "" when neither is sent (then only the relay delivery id dedupes).
    /// </summary>
    internal static string DedupeKey(WebhookDelivery delivery, string topic)
    {
        if (delivery.Header("X-Shopify-Webhook-Id").Trim() is { Length: > 0 } webhookId) return webhookId;
        return delivery.Header("X-Shopify-Event-Id").Trim() is { Length: > 0 } eventId ? $"{topic}:{eventId}" : "";
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind)
    {
        var topic = kind.ToLowerInvariant() switch { "ordercreated" => "orders/create", "orderpaid" => "orders/paid", _ => null };
        return topic is null ? null : Map(topic, SampleOrder, isTest: true) is { } e ? [e] : null;
    }

    /// <summary>
    /// The second event for an order (created, then paid) is not shown in the feed again. Test deliveries are exempt:
    /// Shopify's "Send test" always uses the same sample order id, so every test must still show.
    /// </summary>
    private void MarkRepeat(WebhookEvent e)
    {
        if (e.Args["isTest"] is true)
        {
            e.Args["isRepeatOrder"] = false;
            return;
        }
        var order = Convert.ToString(e.Args.GetValueOrDefault("shopify.id"), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        e.Args["isRepeatOrder"] = order.Length > 0 && !_fedOrders.AddAll([order]);
    }

    internal static WebhookEvent? Map(string topic, string body, bool isTest)
    {
        var type = topic.ToLowerInvariant() switch
        {
            "orders/create" => ShopifyEventTypes.OrderCreated,
            "orders/paid" => ShopifyEventTypes.OrderPaid,
            _ => null,
        };
        if (type is null) return null;
        var order = JsonReaders.ParseBody(body);

        var a = NewArgs();
        a["_json"] = body;
        JsonReaders.Flatten(order, "shopify", a, maxDepth: 5);
        a["shopify.event"] = topic;
        var customer = order.Child("customer");
        var name = $"{customer.Str("first_name")} {customer.Str("last_name")}".Trim();
        if (name.Length == 0) name = order.Path("billing_address.name") is { } billing ? JsonReaders.Text(billing) : "";
        var currency = order.Str("currency");
        Common(a, name.Length > 0 ? name : "Someone", order.Number("total_price"), currency, order.Str("note"), isTest || order.Bool("test"));
        a["orderName"] = order.First("name", "order_number");
        var items = order.Items("line_items").ToArray();
        a["itemCount"] = items.Sum(i => i.Number("quantity") is > 0 and var q ? q : 1);
        a["items"] = string.Join(", ", items.Select(i => i.First("name", "title")));
        a["isRepeatOrder"] = false;
        // What the buyer paid in their own currency (the order's currency fields are the shop's).
        var presentment = order.Path("total_price_set.presentment_money");
        a["presentmentAmount"] = presentment.Child("amount") is { } p ? JsonReaders.Number(p) : order.Number("total_price");
        a["presentmentCurrency"] = presentment.Str("currency_code") is { Length: > 0 } pc ? pc
            : order.Str("presentment_currency") is { Length: > 0 } oc ? oc : currency;
        return new WebhookEvent(type, [], a);
    }

    /// <summary>An order in the shape of Shopify's sample webhook payloads (trimmed).</summary>
    internal const string SampleOrder = """
        {"id":820982911946154508,"admin_graphql_api_id":"gid://shopify/Order/820982911946154508","buyer_accepts_marketing":true,
         "closed_at":null,"confirmed":false,"created_at":"2026-10-27T12:00:00-05:00","currency":"USD","current_total_price":"254.98",
         "email":"jon@example.com","financial_status":"paid","landing_site":null,"name":"#9999","note":"Love the stream!","number":234,
         "order_number":1234,"referring_site":null,"subtotal_price":"244.98","test":false,"total_discounts":"5.00",
         "total_line_items_price":"249.98","total_price":"254.98",
         "total_price_set":{"shop_money":{"amount":"254.98","currency_code":"USD"},"presentment_money":{"amount":"254.98","currency_code":"USD"}},"total_tax":"0.00","updated_at":"2026-10-27T12:00:00-05:00",
         "billing_address":{"first_name":"Steve","last_name":"Shipper","name":"Steve Shipper","city":"Shippington","country":"United States"},
         "customer":{"id":115310627314723954,"email":"john@example.com","first_name":"John","last_name":"Smith"},
         "line_items":[
          {"id":866550311766439020,"admin_graphql_api_id":"gid://shopify/LineItem/866550311766439020","fulfillable_quantity":1,
           "fulfillment_service":"manual","fulfillment_status":null,"gift_card":false,"grams":100,"name":"Aviator sunglasses","price":"89.99",
           "price_set":{"shop_money":{"amount":"89.99","currency_code":"USD"},"presentment_money":{"amount":"89.99","currency_code":"USD"}},
           "product_exists":true,"product_id":788032119674292922,"quantity":1,"requires_shipping":true,"sku":"SKU2006-001","taxable":true,
           "title":"Aviator sunglasses","total_discount":"0.00",
           "total_discount_set":{"shop_money":{"amount":"0.00","currency_code":"USD"},"presentment_money":{"amount":"0.00","currency_code":"USD"}},
           "variant_id":null,"variant_inventory_management":null,"variant_title":null,"vendor":null},
          {"id":141249953214522974,"admin_graphql_api_id":"gid://shopify/LineItem/141249953214522974","fulfillable_quantity":1,
           "fulfillment_service":"manual","fulfillment_status":null,"gift_card":false,"grams":1000,"name":"Mid-century lounger","price":"159.99",
           "price_set":{"shop_money":{"amount":"159.99","currency_code":"USD"},"presentment_money":{"amount":"159.99","currency_code":"USD"}},
           "product_exists":true,"product_id":788032119674292922,"quantity":1,"requires_shipping":true,"sku":"SKU2006-020","taxable":true,
           "title":"Mid-century lounger","total_discount":"5.00",
           "total_discount_set":{"shop_money":{"amount":"5.00","currency_code":"USD"},"presentment_money":{"amount":"5.00","currency_code":"USD"}},
           "variant_id":null,"variant_inventory_management":null,"variant_title":null,"vendor":null}]}
        """;
}
