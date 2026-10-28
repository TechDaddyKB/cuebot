using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Fourthwall shop webhooks (Settings → For developers → Webhooks). <c>X-Fourthwall-Hmac-SHA256</c> is the base64
/// HMAC-SHA256 of the raw body keyed with the shop's webhook secret (platform apps use
/// <c>X-Fourthwall-Hmac-Apps-SHA256</c>, also accepted). Every event is an envelope
/// <c>{ testMode, id, webhookId, shopId, type, apiVersion, createdAt, data }</c>; dedupe on <c>id</c>.
/// Arguments use Streamer.bot's <c>fw.*</c> names.
/// </summary>
public sealed class FourthwallWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "fourthwall";

    public override string Service => Route;
    public override string DisplayName => "Fourthwall";
    public WebhookServiceSettings Settings => SettingsFile.Value.Fourthwall;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Fourthwall webhook secret";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    /// <summary>
    /// Fourthwall may send both headers (shop webhook and platform-app signatures); the delivery is accepted when
    /// either one verifies with the stored secret.
    /// </summary>
    private protected override bool Verify(WebhookDelivery delivery)
    {
        var secret = Settings.Secret.Trim();
        return WebhookVerification.CheckHmacSha256Base64(secret, delivery.Body, delivery.Header("X-Fourthwall-Hmac-SHA256"))
            | WebhookVerification.CheckHmacSha256Base64(secret, delivery.Body, delivery.Header("X-Fourthwall-Hmac-Apps-SHA256"));
    }

    /// <summary>
    /// Fourthwall's API may return user text HTML-escaped (<c>&amp;#39;</c>); the display values (<c>user</c>,
    /// <c>message</c>, so the feed and speech) are decoded, the <c>fw.*</c> arguments keep the raw text.
    /// </summary>
    private static string Display(string text) => System.Net.WebUtility.HtmlDecode(text);

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery) =>
        Map(JsonReaders.ParseBody(delivery.BodyText), isTest: false) is { } e ? [e] : [];

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var json) && Map(JsonReaders.ParseBody(json), isTest: true) is { } e ? [e] : null;

    internal static readonly IReadOnlyDictionary<string, string> TypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ORDER_PLACED"] = FourthwallEventTypes.OrderPlaced,
        ["ORDER_UPDATED"] = FourthwallEventTypes.OrderUpdated,
        ["DONATION"] = FourthwallEventTypes.Donation,
        ["GIFT_PURCHASE"] = FourthwallEventTypes.GiftPurchase,
        ["GIFT_DRAW_STARTED"] = FourthwallEventTypes.GiftDrawStarted,
        ["GIFT_DRAW_ENDED"] = FourthwallEventTypes.GiftDrawEnded,
        ["SUBSCRIPTION_PURCHASED"] = FourthwallEventTypes.SubscriptionPurchased,
        ["SUBSCRIPTION_CHANGED"] = FourthwallEventTypes.SubscriptionChanged,
        ["SUBSCRIPTION_EXPIRED"] = FourthwallEventTypes.SubscriptionExpired,
        ["NEWSLETTER_SUBSCRIBED"] = FourthwallEventTypes.NewsletterSubscribed,
        ["THANK_YOU_SENT"] = FourthwallEventTypes.ThankYouSent,
        ["PRODUCT_CREATED"] = FourthwallEventTypes.ProductCreated,
        ["PRODUCT_UPDATED"] = FourthwallEventTypes.ProductUpdated,
    };

    internal static WebhookEvent? Map(JsonElement envelope, bool isTest)
    {
        var kind = envelope.Str("type");
        if (!TypeMap.TryGetValue(kind, out var type)) return null;
        var data = envelope.Child("data") ?? throw new FormatException("No data in the Fourthwall event");
        isTest |= envelope.Bool("testMode");

        var a = NewArgs();
        a["fw.eventId"] = envelope.Str("id");
        a["fw.eventType"] = kind;
        a["fw.shopId"] = envelope.Str("shopId") is { Length: > 0 } shop ? shop : data.Str("shopId");
        a["fw.createdAt"] = JsonReaders.Time(data.Str("createdAt") is { Length: > 0 } created ? created : envelope.Str("createdAt"));
        a["fw.testMode"] = envelope.Bool("testMode");

        string user, currency = "", message = "";
        double amount = 0;
        switch (type)
        {
            case FourthwallEventTypes.OrderPlaced:
            case FourthwallEventTypes.OrderUpdated:
            {
                var order = type == FourthwallEventTypes.OrderUpdated ? data.Child("order") ?? data : data;
                Order(order, a);
                if (type == FourthwallEventTypes.OrderUpdated)
                {
                    a["fw.updateType"] = data.PathStr("update.type");
                    a["fw.update"] = data.PathStr("update.type");   // Streamer.bot's name
                }
                user = order.Str("username");
                amount = order.PathNumber("amounts.total.value");
                currency = order.PathStr("amounts.total.currency");
                message = order.Str("message");
                break;
            }
            case FourthwallEventTypes.Donation:
                a["fw.donationId"] = data.Str("id");
                a["fw.status"] = data.Str("status");
                a["fw.email"] = data.Str("email");
                a["fw.username"] = data.Str("username");
                a["fw.message"] = data.Str("message");
                user = data.Str("username");
                amount = data.PathNumber("amounts.total.value");
                currency = data.PathStr("amounts.total.currency");
                message = data.Str("message");
                a["fw.amount"] = amount;
                a["fw.currency"] = currency;
                break;
            case FourthwallEventTypes.GiftPurchase:
            case FourthwallEventTypes.GiftDrawStarted:
            case FourthwallEventTypes.GiftDrawEnded:
                Gift(data, type, a);
                user = data.Str("username");
                amount = data.PathNumber("amounts.total.value");
                currency = data.PathStr("amounts.total.currency");
                message = data.Str("message");
                break;
            case FourthwallEventTypes.SubscriptionPurchased:
            case FourthwallEventTypes.SubscriptionChanged:
            case FourthwallEventTypes.SubscriptionExpired:
            {
                var variant = data.Path("subscription.variant");
                a["fw.id"] = data.Str("id");
                a["fw.email"] = data.Str("email");
                a["fw.nickname"] = data.Str("nickname");
                a["fw.type"] = data.PathStr("subscription.type");
                a["fw.interval"] = variant.Str("interval");
                a["fw.tierId"] = variant.Str("tierId");
                a["fw.variantId"] = variant.Str("id");
                amount = variant?.PathNumber("amount.value") ?? 0;
                currency = variant?.PathStr("amount.currency") ?? "";
                a["fw.amount"] = amount;
                a["fw.currency"] = currency;
                user = data.Str("nickname");
                break;
            }
            case FourthwallEventTypes.NewsletterSubscribed:
                a["fw.email"] = data.Str("email");
                user = "";
                break;
            case FourthwallEventTypes.ThankYouSent:
            {
                var contribution = data.Child("contribution");
                a["fw.id"] = data.Str("id");
                a["fw.mediaUrl"] = data.Str("mediaUrl");
                a["fw.contributionType"] = contribution.Str("type");
                a["fw.contributionId"] = contribution.Str("id");
                var supporter = contribution.Child("supporter");
                a["fw.username"] = supporter.Str("username");
                a["fw.email"] = supporter.Str("email");
                a["fw.message"] = supporter.Str("message");
                user = supporter.Str("username");
                message = supporter.Str("message");
                break;
            }
            default: // products
                a["fw.id"] = data.Str("id");
                a["fw.name"] = data.Str("name");
                a["fw.slug"] = data.Str("slug");
                a["fw.description"] = data.Str("description");
                a["fw.type"] = data.Str("type");
                a["fw.state"] = data.PathStr("state.type");
                a["fw.imageUrl"] = data.PathStr("thumbnailImage.url");
                a["fw.updatedAt"] = JsonReaders.Time(data.Str("updatedAt"));
                a["fw.productId"] = data.Str("id");
                var images = data.Items("images").ToArray();
                for (var i = 0; i < images.Length; i++)
                    a[$"fw.images[{i}]"] = images[i].ValueKind == JsonValueKind.String ? JsonReaders.Text(images[i]) : images[i].Str("url");
                var variants = data.Items("variants").ToArray();
                for (var i = 0; i < variants.Length; i++)
                {
                    var v = variants[i];
                    var p = $"fw.variants[{i}]";
                    a[$"{p}.id"] = v.Str("id");
                    a[$"{p}.name"] = v.Str("name");
                    a[$"{p}.sku"] = v.Str("sku");
                    a[$"{p}.unitPrice"] = v.PathNumber("unitPrice.value");
                    a[$"{p}.currencyCode"] = v.PathStr("unitPrice.currency");
                    a[$"{p}.image"] = v.Items("images").FirstOrDefault() is { ValueKind: JsonValueKind.Object } image ? image.Str("url") : "";
                    Attributes(v.Child("attributes"), p, a);
                    a[$"{p}.attributes.weight"] = v.Path("weight.value") is { } weight
                        ? $"{JsonReaders.Text(weight)} {v.PathStr("weight.unit")}".Trim()
                        : "";
                    Stock(v, p, a);
                }
                user = "";
                message = data.Str("name");
                break;
        }
        a["fw.isTest"] = isTest;   // Streamer.bot's name (fw.testMode is Fourthwall's own flag)
        Common(a, Display(user), amount, currency, Display(message), isTest);
        return new WebhookEvent(type, [envelope.Str("id")], a);
    }

    private static void Order(JsonElement order, Dictionary<string, object?> a)
    {
        a["fw.orderId"] = order.Str("id");
        a["fw.checkoutId"] = order.Str("checkoutId");
        a["fw.friendly"] = order.Str("friendlyId");
        a["fw.status"] = order.Str("status");
        a["fw.email"] = order.Str("email");
        a["fw.emailMarketingOptIn"] = order.Bool("emailMarketingOptIn");
        a["fw.username"] = order.Str("username");
        a["fw.statmessageus"] = order.Str("message");   // Streamer.bot's documented (misspelled) name
        a["fw.message"] = order.Str("message");
        a["fw.source"] = order.PathStr("source.type");
        a["fw.updatedAt"] = JsonReaders.Time(order.Str("updatedAt"));
        a["fw.currency"] = order.PathStr("amounts.total.currency");
        foreach (var part in new[] { "subtotal", "shipping", "tax", "donation", "discount", "total" })
            a[$"fw.{part}"] = order.PathNumber($"amounts.{part}.value");
        var offers = order.Items("offers").ToArray();
        for (var i = 0; i < offers.Length; i++)
        {
            var offer = offers[i];
            var variant = offer.Child("variant");
            var attributes = variant.Child("attributes");
            var p = $"fw.variants[{i}]";
            a[$"{p}.id"] = variant.Str("id") is { Length: > 0 } vid ? vid : offer.Str("id");
            a[$"{p}.name"] = offer.Str("name");
            a[$"{p}.sku"] = variant.Str("sku");
            a[$"{p}.quantity"] = variant.Number("quantity");
            a[$"{p}.unitPrice"] = variant?.PathNumber("unitPrice.value") ?? 0;
            a[$"{p}.currencyCode"] = variant?.PathStr("unitPrice.currency") ?? "";
            a[$"{p}.image"] = offer.PathStr("primaryImage.url");
            Attributes(attributes, p, a);
            Stock(variant, p, a);
        }
        a["fw.itemCount"] = offers.Sum(o => o.Path("variant.quantity") is { } q ? JsonReaders.Number(q) : 1);
        a["fw.items"] = string.Join(", ", offers.Select(o => o.Str("name")));
    }

    /// <summary><c>fw.variants[#].attributes.*</c>: description, color (name), colorSwatch and size (name).</summary>
    private static void Attributes(JsonElement? attributes, string p, Dictionary<string, object?> a)
    {
        a[$"{p}.attributes.description"] = attributes.Str("description");
        a[$"{p}.attributes.color"] = attributes.Child("color") is { ValueKind: JsonValueKind.Object } c ? c.Str("name") : attributes.Str("color");
        a[$"{p}.attributes.colorSwatch"] = attributes.Child("color") is { ValueKind: JsonValueKind.Object } s ? s.Str("swatch") : "";
        a[$"{p}.attributes.size"] = attributes.Child("size") is { ValueKind: JsonValueKind.Object } z ? z.Str("name") : attributes.Str("size");
    }

    /// <summary>
    /// <c>fw.variants[#].stockType</c> (UNLIMITED / LIMITED) and <c>fw.variants[#].stock</c>: the units in stock when
    /// Fourthwall says, otherwise the stock type.
    /// </summary>
    private static void Stock(JsonElement? variant, string p, Dictionary<string, object?> a)
    {
        var stockType = variant?.PathStr("stock.type") ?? "";
        a[$"{p}.stockType"] = stockType;
        a[$"{p}.stock"] = variant?.Path("stock.inStock") is { ValueKind: JsonValueKind.Number } inStock ? JsonReaders.Number(inStock) : stockType;
    }

    private static void Gift(JsonElement data, string type, Dictionary<string, object?> a)
    {
        a["fw.id"] = data.Str("id");
        a["fw.friendly"] = data.Str("friendlyId");
        a["fw.email"] = data.Str("email");
        a["fw.username"] = data.Str("username");
        a["fw.statmessageus"] = data.Str("message");
        a["fw.message"] = data.Str("message");
        // Gift draws carry no quantity: it is the number of gifts.
        a["fw.quantity"] = data.Has("quantity") ? data.Number("quantity") : data.Items("gifts").Count();
        a["fw.currency"] = data.PathStr("amounts.total.currency");
        foreach (var part in new[] { "subtotal", "tax", "donation", "discount", "total", "profit" })
            a[$"fw.{part}"] = data.PathNumber($"amounts.{part}.value");
        a["fw.shipping"] = data.PathNumber("amounts.prepaidShipping.value");
        a["fw.offer.id"] = data.PathStr("offer.id");
        a["fw.offer.name"] = data.PathStr("offer.name");
        a["fw.offer.slug"] = data.PathStr("offer.slug");
        a["fw.offer.description"] = data.PathStr("offer.description");
        a["fw.offer.imageUrl"] = data.PathStr("offer.primaryImage.url");
        if (type != FourthwallEventTypes.GiftPurchase)
        {
            a["fw.drawId"] = data.Str("id");
            a["fw.drawType"] = data.Str("type");
            a["fw.giveawayId"] = data.Str("giveawayId");
            a["fw.giveawayFriendlyId"] = data.Str("giveawayFriendlyId");
            a["fw.durationSeconds"] = data.Number("durationSeconds");
        }
        var gifts = data.Items("gifts").ToArray();
        a["fw.giftCount"] = gifts.Length;
        for (var i = 0; i < gifts.Length; i++)
        {
            var g = gifts[i];
            var p = $"fw.gifts[{i}]";
            a[$"{p}.id"] = g.Str("id");
            a[$"{p}.status"] = g.Str("status");
            a[$"{p}.winner"] = g.Path("winner.username") is { } w ? JsonReaders.Text(w) : "";
            a[$"{p}.redeemUri"] = g.PathStr("winner.redeemUri");
            foreach (var extra in new[] { "orderId", "orderFriendlyId", "promotionId", "giftCardId" })
                if (g.Str(extra) is { Length: > 0 } value) a[$"{p}.{extra}"] = value;
        }
    }

    private const string Envelope = """{"testMode":false,"id":"weve___ID__","webhookId":"wcon_P-VkRfmJTBaC6_Tst22cew","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","type":"__TYPE__","apiVersion":"V1","createdAt":"2026-10-27T15:05:11.078089+00:00","data":__DATA__}""";

    private static string Event(string type, string id, string data) => Envelope
        .Replace("__TYPE__", type, StringComparison.Ordinal).Replace("__ID__", id, StringComparison.Ordinal).Replace("__DATA__", data, StringComparison.Ordinal);

    private const string Offer = """{"id":"b2c201d3-8104-4b2a-b2c9-1f6b335b650a","name":"New T-shirt","slug":"new-t-shirt","description":"Soft tee","primaryImage":{"id":"img_1","url":"https://cdn.fourthwall.com/sample/tee.jpeg","width":800,"height":800}}""";

    internal static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["orderPlaced"] = Event("ORDER_PLACED", "sampleorder", """
            {"id":"7b83a86f-9221-4416-b739-ef88870f999b","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","friendlyId":"F2LGPYCV",
             "checkoutId":"ch_4bRo4nB-RpWwXYx6iG4HuQ","status":"CONFIRMED","email":"supporter@example.com","emailMarketingOptIn":true,
             "username":"MerchFan","message":"message from supporter",
             "amounts":{"subtotal":{"value":10,"currency":"USD"},"shipping":{"value":5,"currency":"USD"},"tax":{"value":1,"currency":"USD"},
               "donation":{"value":3,"currency":"USD"},"discount":{"value":2,"currency":"USD"},"total":{"value":17,"currency":"USD"}},
             "offers":[{"id":"b2c201d3-8104-4b2a-b2c9-1f6b335b650a","name":"New T-shirt","slug":"new-t-shirt",
               "primaryImage":{"id":"img_1","url":"https://cdn.fourthwall.com/sample/tee.jpeg"},
               "variant":{"id":"7a1f0f2e-0000-4000-8000-000000000001","name":"White, S","sku":"Z3YD-8CTV00S","unitPrice":{"value":5,"currency":"USD"},
                 "attributes":{"description":"White, S","color":{"name":"White","swatch":"#FFFFFF"},"size":{"name":"S"}},"quantity":2,
                 "price":{"value":10,"currency":"USD"}}}],
             "source":{"type":"ORDER"},"createdAt":"2026-10-27T15:05:10.000+00:00","updatedAt":"2026-10-27T15:05:10.000+00:00"}
            """),
        ["donation"] = Event("DONATION", "sampledonation", """
            {"id":"don_123","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","status":"COMPLETED","email":"supporter@example.com",
             "username":"KindSupporter","message":"Keep it up!","amounts":{"total":{"value":5,"currency":"USD"}},
             "createdAt":"2026-10-27T15:05:10.000+00:00","updatedAt":"2026-10-27T15:05:10.000+00:00"}
            """),
        ["subscriptionPurchased"] = Event("SUBSCRIPTION_PURCHASED", "samplesub", """
            {"id":"7ad0c438-beda-4779-a885-0dc325a755c1","email":"member@example.com","nickname":"NewMember","createdAt":"2026-10-27T15:05:10.000+00:00",
             "subscription":{"type":"ACTIVE","variant":{"id":"mtv_1","tierId":"mt_1","interval":"MONTHLY","amount":{"value":5,"currency":"USD"},"offerId":"mof_1"}}}
            """),
        ["giftPurchase"] = Event("GIFT_PURCHASE", "samplegift", "{\"id\":\"giv_voWcDCtfQUygxpuYx1HtNg\",\"friendlyId\":\"F2LGPYCV\",\"shopId\":\"sh_7ad0c438-beda-4779-a885-0dc325a755c1\",\"offer\":" + Offer + """
            ,"amounts":{"subtotal":{"value":10,"currency":"USD"},"tax":{"value":1,"currency":"USD"},"total":{"value":17,"currency":"USD"},
              "profit":{"value":4,"currency":"USD"},"prepaidShipping":{"value":5,"currency":"USD"}},
             "email":"gifter@example.com","username":"GenerousGifter","message":"Shirts for everyone","quantity":2,
             "gifts":[{"id":"gft_EdJvIXu3SEiXe_QkPavHSA","status":"AVAILABLE","winner":null},{"id":"gft_EdJvIXu3SEiXe_QkPavHSB","status":"AVAILABLE","winner":null}],
             "createdAt":"2026-10-27T15:05:10.000+00:00"}
            """),
        ["thankYouSent"] = Event("THANK_YOU_SENT", "samplethanks", """
            {"id":"thx_1","mediaUrl":"https://cdn.fourthwall.com/sample/thanks.mp4",
             "contribution":{"type":"ORDER","id":"7b83a86f-9221-4416-b739-ef88870f999b","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1",
               "supporter":{"email":"supporter@example.com","username":"MerchFan","message":"message from supporter"}}}
            """),
        ["newsletterSubscribed"] = Event("NEWSLETTER_SUBSCRIBED", "samplenews", """{"email":"reader@example.com"}"""),
    };
}
