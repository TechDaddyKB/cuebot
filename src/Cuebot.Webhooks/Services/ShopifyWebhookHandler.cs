using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>What a Shopify delivery's headers say (https://shopify.dev/docs/apps/build/webhooks/delivery-structure).</summary>
internal sealed record ShopifyHeaders(string ApiVersion = "", string ShopDomain = "", string TriggeredAt = "", string WebhookId = "", string EventId = "")
{
    public static ShopifyHeaders From(WebhookDelivery d) => new(
        d.Header("X-Shopify-API-Version").Trim(), d.Header("X-Shopify-Shop-Domain").Trim(), d.Header("X-Shopify-Triggered-At").Trim(),
        d.Header("X-Shopify-Webhook-Id").Trim(), d.Header("X-Shopify-Event-Id").Trim());
}

/// <summary>
/// Shopify webhooks: <c>orders/create</c>, <c>orders/paid</c>, <c>orders/cancelled</c>, <c>refunds/create</c>,
/// <c>inventory_levels/update</c> (Low Stock / Sold Out / Restocked) and <c>products/update</c> (refreshes the showcase).
/// <c>X-Shopify-Hmac-Sha256</c> is the base64 HMAC-SHA256 of the raw body keyed with the store's webhook signing key
/// (admin-created webhooks; one key per store) or, for webhooks an app subscribed, the app's client secret.
/// <c>X-Shopify-Topic</c> names the event. Dedupe on <c>X-Shopify-Webhook-Id</c> (one per delivery, kept on retries) and
/// on topic + <c>X-Shopify-Event-Id</c> (shared by every delivery of one merchant action, so two webhooks for the same
/// topic run once), plus the order id for paid / cancelled orders and the refund id (so catch-up never counts twice).
/// Test deliveries: <c>"test": true</c> in the body (a documented sample-payload field); an <c>X-Shopify-Test</c> header
/// is honoured too where it appears (seen in captured deliveries, not documented). Only JSON-format webhooks are read.
/// <c>X-Shopify-API-Version</c> is checked against Shopify's 12-month support window (see <see cref="ShopifyApiVersions"/>).
/// The order is flattened to <c>shopify.*</c> arguments like Streamer.bot's, after contact details are removed (unless
/// switched off); the buyer is named by first name and the order note is not the stream message unless asked for.
/// </summary>
public sealed class ShopifyWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "shopify";

    private readonly HashSet<string> _versionsWarned = new(StringComparer.Ordinal);

    public override string Service => Route;
    public override string DisplayName => "Shopify";
    public WebhookServiceSettings Settings => SettingsFile.Value.Shopify;
    public ShopifyOptions Options => SettingsFile.Value.ShopifyOptions;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Shopify webhook signing key";

    public override IReadOnlyList<string> TestEventKinds =>
        ["orderPaid", "orderCreated", "orderCancelled", "refundCreated", "lowStock", "soldOut", "restocked"];

    /// <summary>Sales counter, goal, stock and the optional Admin API (the app sets the shared one; tests may replace it).</summary>
    public ShopifyTracker Tracker { get; internal set; } = new(settings, log);

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.CheckHmacSha256Base64(Settings.Secret.Trim(), delivery.Body, delivery.Header("X-Shopify-Hmac-Sha256"));

    /// <summary>Message for a webhook created with the XML format, which Cuebot does not read.</summary>
    internal const string XmlNotSupported = "Shopify webhook is set to XML; choose JSON in Shopify's webhook settings";

    private protected override string? Unsupported(WebhookDelivery delivery) =>
        delivery.Header("Content-Type").Contains("xml", StringComparison.OrdinalIgnoreCase) || delivery.BodyText.TrimStart().StartsWith('<')
            ? XmlNotSupported
            : null;

    /// <summary>The last-delivery line names the webhook's API version (with advice when it is old) and, for a refused one, the shop.</summary>
    private protected override string DescribeDelivery(WebhookDelivery delivery, WebhookOutcome outcome, string detail)
    {
        var h = ShopifyHeaders.From(delivery);
        if (outcome == WebhookOutcome.Rejected)
            return h.ShopDomain.Length > 0
                ? $"{detail} It came from {h.ShopDomain}: each store has its own signing key, so a second store's webhooks need that store's key."
                : detail;
        if (outcome is WebhookOutcome.NotConfigured) return detail;
        Tracker.NoteDelivery(h.ApiVersion, h.ShopDomain);
        if (h.ApiVersion.Length == 0) return detail;
        var check = ShopifyApiVersions.Check(h.ApiVersion, Tracker.Time.GetUtcNow());
        if (check.Advice.Length > 0)
        {
            bool first;
            lock (_versionsWarned) first = _versionsWarned.Add(check.Version);
            if (first) Log.Warn(DisplayName, check.Advice);
            return $"{detail}; API version {check.Version}. {check.Advice}";
        }
        return $"{detail}; API version {check.Version}";
    }

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var topic = delivery.Header("X-Shopify-Topic").Trim().ToLowerInvariant();
        var headers = ShopifyHeaders.From(delivery);
        var headerTest = string.Equals(delivery.Header("X-Shopify-Test").Trim(), "true", StringComparison.OrdinalIgnoreCase);
        WebhookEvent? e;
        switch (topic)
        {
            case "products/update" or "products/create" or "products/delete":
                Tracker.ProductsChanged();
                return [];
            case "inventory_levels/update":
                e = MapStock(delivery.BodyText, headers, headerTest);
                break;
            default:
                e = Map(topic, delivery.BodyText, headerTest, Options, headers);
                break;
        }
        if (e is null) return [];
        List<string> keys = [headers.WebhookId, headers.EventId.Length > 0 ? $"{topic}:{headers.EventId}" : ""];
        if (e.Args["isTest"] is not true && ContentKey(e) is { Length: > 0 } content) keys.Add(content);
        return [e with { Keys = keys }];
    }

    /// <summary>
    /// The order or refund id key for events that must never count twice, whoever brought them (a delivery, a second
    /// webhook for the same topic, or catch-up): <c>paid:&lt;order&gt;</c>, <c>cancelled:&lt;order&gt;</c>, <c>refund:&lt;refund&gt;</c>.
    /// </summary>
    internal static string ContentKey(WebhookEvent e) => e.Type switch
    {
        ShopifyEventTypes.OrderPaid when WebhookFilters.Text(e.Args, "orderId") is { Length: > 0 } id => $"paid:{id}",
        ShopifyEventTypes.OrderCancelled when WebhookFilters.Text(e.Args, "orderId") is { Length: > 0 } id => $"cancelled:{id}",
        ShopifyEventTypes.RefundCreated when WebhookFilters.Text(e.Args, "refundId") is { Length: > 0 } id => $"refund:{id}",
        _ => "",
    };

    /// <summary>The old webhook-id-or-event-id key (kept for tests and docs): the webhook id, else topic + event id.</summary>
    internal static string DedupeKey(WebhookDelivery delivery, string topic)
    {
        if (delivery.Header("X-Shopify-Webhook-Id").Trim() is { Length: > 0 } webhookId) return webhookId;
        return delivery.Header("X-Shopify-Event-Id").Trim() is { Length: > 0 } eventId ? $"{topic}:{eventId}" : "";
    }

    /// <summary>
    /// The second event for an order (created, then paid) is not shown in the feed again, also after a restart (the
    /// shared, saved id store). Test deliveries are exempt: Shopify's "Send test" always uses the same sample order id.
    /// </summary>
    private void MarkRepeat(WebhookEvent e)
    {
        if (e.Args["isTest"] is true)
        {
            e.Args["isRepeatOrder"] = false;
            return;
        }
        var order = WebhookFilters.Text(e.Args, "orderId");
        e.Args["isRepeatOrder"] = order.Length > 0 && !Seen.AddAll([$"{Service}:fed:{order}"]);
    }

    private protected override IReadOnlyList<BotEvent> BeforePublish(WebhookEvent e)
    {
        if (e.Type is ShopifyEventTypes.OrderCreated or ShopifyEventTypes.OrderPaid) MarkRepeat(e);
        return Tracker.Record(e.Type, e.Args);
    }

    private protected override Task EnrichAsync(WebhookEvent e, CancellationToken ct) =>
        e.Type is ShopifyEventTypes.LowStock or ShopifyEventTypes.SoldOut or ShopifyEventTypes.Restocked
            ? Tracker.NameStockAsync(e.Args, ct)
            : Task.CompletedTask;

    /// <summary>Runs Order Paid for an order catch-up found (REST-shaped JSON), marked isReplay. True when it was new.</summary>
    internal async Task<bool> ReplayAsync(string orderJson, TimeSpan delay, CancellationToken ct)
    {
        if (!Enabled) return false;
        var e = Map("orders/paid", orderJson, false, Options, new ShopifyHeaders(ShopDomain: ShopifyApi.NormaliseShop(Options.ShopDomain)));
        if (e is null) return false;
        e.Args["isReplay"] = true;
        e.Args["delaySeconds"] = (long)Math.Max(0, delay.TotalSeconds);
        return await PublishNewAsync([e with { Keys = [ContentKey(e)] }], null, ct).ConfigureAwait(false) > 0;
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind)
    {
        var headers = new ShopifyHeaders(ShopifyApiVersions.LatestStable(Tracker.Time.GetUtcNow()), "cue-shop.myshopify.com");
        WebhookEvent? e = kind.ToLowerInvariant() switch
        {
            "ordercreated" => Map("orders/create", SampleOrder, true, Options, headers),
            "orderpaid" => Map("orders/paid", SampleOrder, true, Options, headers),
            "ordercancelled" => Map("orders/cancelled", SampleOrder, true, Options, headers),
            "refundcreated" => Map("refunds/create", SampleRefund, true, Options, headers),
            "lowstock" => SampleStock(ShopifyEventTypes.LowStock, Math.Max(1, Options.LowStockThreshold), 12),
            "soldout" => SampleStock(ShopifyEventTypes.SoldOut, 0, 3),
            "restocked" => SampleStock(ShopifyEventTypes.Restocked, 25, 0),
            _ => null,
        };
        return e is null ? null : [e];
    }

    private WebhookEvent SampleStock(string type, int available, int before)
    {
        var a = NewArgs();
        Common(a, "", 0, "", "", isTest: true);
        a["inventoryItemId"] = "808950810";
        a["locationId"] = "655441491";
        a["available"] = available;
        a["locationAvailable"] = available;
        a["previousAvailable"] = before;
        a["threshold"] = Math.Max(0, Options.LowStockThreshold);
        a["productName"] = "Cuebot hoodie";
        a["variantName"] = "M / Black";
        a["sku"] = "HOODIE-M-BLK";
        a["item"] = "Cuebot hoodie (M / Black)";
        return new WebhookEvent(type, [], a);
    }

    private WebhookEvent? MapStock(string body, ShopifyHeaders headers, bool headerTest)
    {
        var level = JsonReaders.ParseBody(body);
        var item = level.Str("inventory_item_id");
        if (item.Length == 0) return null;
        var a = NewArgs();
        var isTest = headerTest || level.Bool("test");
        Common(a, "", 0, "", "", isTest);
        a["inventoryItemId"] = item;
        a["locationId"] = level.Str("location_id");
        a["updatedAt"] = level.Str("updated_at");
        AddHeaders(a, headers);
        var type = Tracker.Stock(item, level.Str("location_id"), (int)level.Number("available"), isTest, a);
        return type is null ? null : new WebhookEvent(type, [], a);
    }

    private static void AddHeaders(Dictionary<string, object?> a, ShopifyHeaders h)
    {
        a["apiVersion"] = h.ApiVersion;
        a["shopDomain"] = h.ShopDomain;
        a["triggeredAt"] = h.TriggeredAt.Length > 0 ? JsonReaders.Time(h.TriggeredAt) : "";
    }

    /// <summary>Order and refund topics; null for anything else.</summary>
    internal static WebhookEvent? Map(string topic, string body, bool isTest, ShopifyOptions options, ShopifyHeaders? headers = null)
    {
        var type = topic.ToLowerInvariant() switch
        {
            "orders/create" => ShopifyEventTypes.OrderCreated,
            "orders/paid" => ShopifyEventTypes.OrderPaid,
            "orders/cancelled" => ShopifyEventTypes.OrderCancelled,
            "refunds/create" => ShopifyEventTypes.RefundCreated,
            _ => null,
        };
        if (type is null) return null;
        headers ??= new ShopifyHeaders();
        var raw = JsonReaders.ParseBody(body);
        return type == ShopifyEventTypes.RefundCreated
            ? MapRefund(raw, body, isTest, options, headers, topic)
            : MapOrder(type, raw, body, isTest, options, headers, topic);
    }

    private static WebhookEvent MapOrder(string type, JsonElement raw, string body, bool isTest, ShopifyOptions options, ShopifyHeaders headers, string topic)
    {
        var (first, full) = BuyerName(raw);
        var json = options.RedactContactDetails ? Redact(raw) : body;
        var order = options.RedactContactDetails ? JsonReaders.ParseBody(json) : raw;

        var a = NewArgs();
        a["_json"] = json;
        JsonReaders.Flatten(order, "shopify", a, maxDepth: 5);
        a["shopify.event"] = topic;
        var currency = raw.Str("currency");
        Common(a, Shown(first, full, options), raw.Number("total_price"), currency, StreamMessage(raw, options), isTest || raw.Bool("test"));
        a["firstName"] = first;
        a["orderNote"] = raw.Str("note");
        a["cartMessage"] = Attribute(raw, options.MessageAttribute);
        a["orderId"] = raw.Str("id");
        a["orderName"] = raw.First("name", "order_number");
        a["financialStatus"] = raw.Str("financial_status");
        a["fulfillmentStatus"] = raw.Str("fulfillment_status");
        a["sourceName"] = raw.Str("source_name");
        a["discountCodes"] = string.Join(", ", raw.Items("discount_codes").Select(d => d.ValueKind == JsonValueKind.Object ? d.Str("code") : JsonReaders.Text(d))
            .Where(c => c.Length > 0));
        AddHeaders(a, headers);
        a["orderUrl"] = OrderUrl(headers.ShopDomain, raw.Str("id"));
        Items(a, raw.Items("line_items").ToArray(), i => i);
        a["isRepeatOrder"] = false;
        // What the buyer paid in their own currency (the order's currency fields are the shop's).
        var presentment = raw.Path("total_price_set.presentment_money");
        a["presentmentAmount"] = presentment.Child("amount") is { } p ? JsonReaders.Number(p) : raw.Number("total_price");
        a["presentmentCurrency"] = presentment.Str("currency_code") is { Length: > 0 } pc ? pc
            : raw.Str("presentment_currency") is { Length: > 0 } oc ? oc : currency;
        if (type == ShopifyEventTypes.OrderCancelled)
        {
            a["cancelReason"] = raw.Str("cancel_reason");
            a["cancelledAt"] = raw.Str("cancelled_at") is { Length: > 0 } at ? JsonReaders.Time(at) : "";
        }
        return new WebhookEvent(type, [], a);
    }

    /// <summary>
    /// refunds/create: <c>{id, order_id, note, restock, refund_line_items[{quantity, line_item{...}, subtotal}],
    /// transactions[{kind, status, amount, currency}]}</c>. The amount is the successful refund transactions (else the
    /// refunded items' subtotal); the buyer and order number come from the order Cuebot saw earlier, when it did.
    /// </summary>
    private static WebhookEvent MapRefund(JsonElement raw, string body, bool isTest, ShopifyOptions options, ShopifyHeaders headers, string topic)
    {
        var json = options.RedactContactDetails ? Redact(raw) : body;
        var refund = options.RedactContactDetails ? JsonReaders.ParseBody(json) : raw;
        var a = NewArgs();
        a["_json"] = json;
        JsonReaders.Flatten(refund, "shopify", a, maxDepth: 5);
        a["shopify.event"] = topic;
        var transactions = raw.Items("transactions").Where(t => t.Str("kind") is "" or "refund" && t.Str("status") is "" or "success").ToArray();
        var lines = raw.Items("refund_line_items").ToArray();
        var amount = transactions.Length > 0 ? transactions.Sum(t => t.Number("amount")) : lines.Sum(l => l.Number("subtotal"));
        var currency = transactions.Select(t => t.Str("currency")).FirstOrDefault(c => c.Length > 0)
            ?? lines.Select(l => l.PathStr("subtotal_set.shop_money.currency_code")).FirstOrDefault(c => c.Length > 0) ?? "";
        Common(a, "Someone", Math.Round(amount, 2), currency, "", isTest || raw.Bool("test"));
        a["refundId"] = raw.Str("id");
        a["orderId"] = raw.Str("order_id");
        a["orderName"] = "";
        a["refundNote"] = raw.Str("note");
        a["restock"] = raw.Bool("restock") || lines.Any(l => l.Str("restock_type") is "return" or "cancel");
        AddHeaders(a, headers);
        a["orderUrl"] = OrderUrl(headers.ShopDomain, raw.Str("order_id"));
        Items(a, lines, l => l.Child("line_item") is { } li ? li : l, quantityFrom: l => l.Number("quantity"));
        return new WebhookEvent(ShopifyEventTypes.RefundCreated, [], a);
    }

    /// <summary>items / itemCount and item{i}.name, .quantity, .variant, .sku, .productId, .price.</summary>
    private static void Items(Dictionary<string, object?> a, JsonElement[] rows, Func<JsonElement, JsonElement> line, Func<JsonElement, double>? quantityFrom = null)
    {
        var names = new List<string>();
        var count = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var item = line(rows[i]);
            var quantity = (int)(quantityFrom?.Invoke(rows[i]) ?? item.Number("quantity"));
            if (quantity <= 0) quantity = 1;
            var title = item.First("title", "name");
            var variant = item.Str("variant_title");
            a[$"item{i}.name"] = title;
            a[$"item{i}.quantity"] = quantity;
            a[$"item{i}.variant"] = variant;
            a[$"item{i}.sku"] = item.Str("sku");
            a[$"item{i}.productId"] = item.Str("product_id");
            a[$"item{i}.price"] = (decimal)item.Number("price");
            names.Add(item.First("name", "title"));
            count += quantity;
        }
        a["itemCount"] = (double)count;   // a double, as it always was
        a["items"] = string.Join(", ", names.Where(n => n.Length > 0));
    }

    /// <summary>The buyer's first name and full name: the customer (REST or newer camelCase shapes), else the billing address.</summary>
    internal static (string First, string Full) BuyerName(JsonElement order)
    {
        var customer = order.Child("customer");
        var billing = order.Child("billing_address");
        var first = customer.Str("first_name") is { Length: > 0 } f ? f : customer.Str("firstName") is { Length: > 0 } f2 ? f2 : billing.Str("first_name");
        var last = customer.Str("last_name") is { Length: > 0 } l ? l : customer.Str("lastName") is { Length: > 0 } l2 ? l2 : billing.Str("last_name");
        var full = $"{first} {last}".Trim();
        if (full.Length == 0) full = billing.Str("name").Trim();
        if (first.Length == 0 && full.Length > 0) first = full.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return (first.Trim(), full);
    }

    private static string Shown(string first, string full, ShopifyOptions options)
    {
        var name = options.NameOnStream switch
        {
            ShopifyOptions.Someone => "",
            ShopifyOptions.FullName => full,
            _ => first,
        };
        return name.Length > 0 ? name : "Someone";
    }

    private static string StreamMessage(JsonElement order, ShopifyOptions options) => options.MessageSource switch
    {
        ShopifyOptions.OrderNote => order.Str("note"),
        ShopifyOptions.CartAttribute => Attribute(order, options.MessageAttribute),
        _ => "",
    };

    /// <summary>The value of the cart attribute (<c>note_attributes</c>) with this name, ignoring case.</summary>
    private static string Attribute(JsonElement order, string name) =>
        name.Trim().Length == 0 ? "" : order.Items("note_attributes")
            .FirstOrDefault(n => string.Equals(n.Str("name").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } attr
            ? attr.Str("value")
            : "";

    /// <summary>The order in the Shopify admin (https://admin.shopify.com/store/&lt;handle&gt;/orders/&lt;id&gt;), for a mod-only message.</summary>
    internal static string OrderUrl(string shopDomain, string orderId)
    {
        var shop = ShopifyApi.NormaliseShop(shopDomain);
        if (shop.Length == 0 || orderId.Length == 0) return "";
        var handle = shop.EndsWith(".myshopify.com", StringComparison.Ordinal) ? shop[..^".myshopify.com".Length] : shop;
        return $"https://admin.shopify.com/store/{Uri.EscapeDataString(handle)}/orders/{Uri.EscapeDataString(orderId)}";
    }

    /// <summary>
    /// Fields removed by "Redact contact details", wherever they appear: contact data, addresses, the buyer's IP and
    /// browser, the order note (checkout instructions often hold phone numbers or door codes; %orderNote% keeps it for
    /// actions) and the tokens / order-status link that open the order's status page.
    /// </summary>
    internal static readonly HashSet<string> RedactedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "email", "contact_email", "phone", "sms_marketing_phone", "browser_ip", "client_details", "billing_address", "shipping_address",
        "default_address", "addresses", "destination", "origin_address", "note", "order_status_url", "token", "cart_token", "checkout_token",
    };

    internal static string Redact(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        Strip(node);
        return node?.ToJsonString() ?? "{}";
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).Where(RedactedFields.Contains).ToArray()) o.Remove(key);
                foreach (var (_, child) in o) Strip(child);
                break;
            case JsonArray arr:
                foreach (var child in arr) Strip(child);
                break;
        }
    }

    /// <summary>An order in the shape of Shopify's sample webhook payloads (trimmed).</summary>
    internal const string SampleOrder = """
        {"id":820982911946154508,"admin_graphql_api_id":"gid://shopify/Order/820982911946154508","buyer_accepts_marketing":true,
         "closed_at":null,"confirmed":false,"created_at":"2026-10-27T12:00:00-05:00","currency":"USD","current_total_price":"254.98",
         "email":"jon@example.com","financial_status":"paid","landing_site":null,"name":"#9999","note":"Gate code 1234, leave at the back door","number":234,
         "order_number":1234,"referring_site":null,"source_name":"web","subtotal_price":"244.98","test":false,"total_discounts":"5.00",
         "total_line_items_price":"249.98","total_price":"254.98","cancel_reason":"customer","cancelled_at":null,
         "discount_codes":[{"code":"STREAM10","amount":"5.00","type":"fixed_amount"}],
         "note_attributes":[{"name":"Stream message","value":"Love the stream!"}],
         "total_price_set":{"shop_money":{"amount":"254.98","currency_code":"USD"},"presentment_money":{"amount":"254.98","currency_code":"USD"}},"total_tax":"0.00","updated_at":"2026-10-27T12:00:00-05:00",
         "billing_address":{"first_name":"Steve","last_name":"Shipper","name":"Steve Shipper","city":"Shippington","country":"United States"},
         "customer":{"id":115310627314723954,"email":"john@example.com","first_name":"John","last_name":"Smith"},
         "line_items":[
          {"id":866550311766439020,"admin_graphql_api_id":"gid://shopify/LineItem/866550311766439020","fulfillable_quantity":1,
           "fulfillment_service":"manual","fulfillment_status":null,"gift_card":false,"grams":100,"name":"Cuebot hoodie - M / Black","price":"89.99",
           "price_set":{"shop_money":{"amount":"89.99","currency_code":"USD"},"presentment_money":{"amount":"89.99","currency_code":"USD"}},
           "product_exists":true,"product_id":788032119674292922,"quantity":1,"requires_shipping":true,"sku":"HOODIE-M-BLK","taxable":true,
           "title":"Cuebot hoodie","total_discount":"0.00","variant_id":null,"variant_inventory_management":null,"variant_title":"M / Black","vendor":null},
          {"id":141249953214522974,"admin_graphql_api_id":"gid://shopify/LineItem/141249953214522974","fulfillable_quantity":1,
           "fulfillment_service":"manual","fulfillment_status":null,"gift_card":false,"grams":1000,"name":"Sticker pack","price":"159.99",
           "price_set":{"shop_money":{"amount":"159.99","currency_code":"USD"},"presentment_money":{"amount":"159.99","currency_code":"USD"}},
           "product_exists":true,"product_id":788032119674292923,"quantity":1,"requires_shipping":true,"sku":"STICKERS","taxable":true,
           "title":"Sticker pack","total_discount":"5.00","variant_id":null,"variant_inventory_management":null,"variant_title":null,"vendor":null}]}
        """;

    /// <summary>A refund in the shape of Shopify's refunds/create sample payload (trimmed).</summary>
    internal const string SampleRefund = """
        {"id":509562969,"order_id":820982911946154508,"created_at":"2026-10-27T12:30:00-05:00","note":"Wrong size","restock":true,
         "refund_line_items":[{"id":104689539,"quantity":1,"line_item_id":866550311766439020,"restock_type":"return","subtotal":89.99,
           "line_item":{"id":866550311766439020,"name":"Cuebot hoodie - M / Black","title":"Cuebot hoodie","variant_title":"M / Black","sku":"HOODIE-M-BLK","quantity":1,"price":"89.99","product_id":788032119674292922}}],
         "transactions":[{"id":245135,"order_id":820982911946154508,"kind":"refund","status":"success","amount":"89.99","currency":"USD","gateway":"bogus","test":false}]}
        """;
}
