using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Variables;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Fourthwall shop webhooks (Settings → For developers → Webhooks). <c>X-Fourthwall-Hmac-SHA256</c> is the base64
/// HMAC-SHA256 of the raw body keyed with the shop's webhook secret (platform apps use
/// <c>X-Fourthwall-Hmac-Apps-SHA256</c>, also accepted). Every event is an envelope
/// <c>{ testMode, id, webhookId, shopId, type, apiVersion, createdAt, data }</c>; dedupe on <c>id</c>. <c>apiVersion</c>
/// is never checked (examples say V1, the spec V1_BETA). Arguments use Streamer.bot's <c>fw.*</c> names.
/// <para>
/// Money only counts when it arrived: a <c>DONATION</c> that is not <c>COMPLETED</c> has amount 0 (the trigger can still
/// run for it), and an order that is not a purchase (a creator's samples order, a stream gift or giveaway link being
/// redeemed) has amount 0 and stays out of the feed, alerts and totals. Missing supporter names become "Anonymous";
/// "Hide supporter names" shows "A supporter" instead of any name. The <c>email</c> fields are personal data, kept only
/// in <c>fw.email</c> (Streamer.bot has them too) and never shown in the feed.
/// </para>
/// </summary>
public sealed class FourthwallWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "fourthwall";

    /// <summary>The name used when a supporter left theirs blank.</summary>
    public const string AnonymousName = "Anonymous";

    /// <summary>The name shown instead of any supporter's when "Hide supporter names" is on.</summary>
    public const string HiddenName = "A supporter";

    public override string Service => Route;
    public override string DisplayName => "Fourthwall";
    public WebhookServiceSettings Settings => SettingsFile.Value.Fourthwall;
    public FourthwallOptions Options => SettingsFile.Value.FourthwallOptions;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Fourthwall webhook secret";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    /// <summary>Tier names, the last tier of each member, gift links, the API connection (in memory unless the app gives it fourthwall.json).</summary>
    public FourthwallTracker Tracker { get; internal set; } = new(settings, log);

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
        Map(JsonReaders.ParseBody(delivery.BodyText), isTest: false, Options);

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var json) && Map(JsonReaders.ParseBody(json), isTest: true, Options) is { Count: > 0 } e ? e : null;

    private protected override IReadOnlyList<BotEvent> BeforePublish(WebhookEvent e)
    {
        Tracker.Record(e.Type, e.Args);
        return [];
    }

    /// <summary>
    /// Publishes an event Fourthwall's API lists (GET /webhook-events) that no delivery brought, marked
    /// <c>isReplay</c>; nothing happens when it was handled before (same envelope id). Returns true when it was new.
    /// </summary>
    internal async Task<bool> ReplayAsync(JsonElement envelope, TimeSpan delay, CancellationToken ct)
    {
        if (!Enabled) return false;
        var events = Map(envelope, isTest: false, Options);
        foreach (var e in events)
        {
            e.Args["isReplay"] = true;
            e.Args["delaySeconds"] = (long)Math.Max(0, delay.TotalSeconds);
        }
        return events.Count > 0 && await PublishNewAsync(events, null, ct).ConfigureAwait(false) > 0;
    }

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
        ["PROMOTION_CREATED"] = FourthwallEventTypes.PromotionCreated,
        ["PROMOTION_UPDATED"] = FourthwallEventTypes.PromotionUpdated,
        ["PROMOTION_STATUS_CHANGED"] = FourthwallEventTypes.PromotionStatusChanged,
        ["COLLECTION_UPDATED"] = FourthwallEventTypes.CollectionUpdated,
    };

    /// <summary>Order sources that are a real purchase (an order without a source counts as one).</summary>
    internal static bool IsPurchaseSource(string source) => source.Length == 0 || source.Equals("ORDER", StringComparison.OrdinalIgnoreCase);

    /// <summary>A donation that brought money: COMPLETED, or no status at all (older payloads).</summary>
    internal static bool IsCompletedDonation(string status) => status.Length == 0 || status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);

    internal static WebhookEvent? Map(JsonElement envelope, bool isTest) => Map(envelope, isTest, null).FirstOrDefault();

    internal static IReadOnlyList<WebhookEvent> Map(JsonElement envelope, bool isTest, FourthwallOptions? options)
    {
        options ??= new FourthwallOptions();
        var kind = envelope.Str("type");
        if (!TypeMap.TryGetValue(kind, out var type)) return [];
        var data = envelope.Child("data") ?? throw new FormatException("No data in the Fourthwall event");
        isTest |= envelope.Bool("testMode");
        var id = envelope.Str("id");

        if (type == FourthwallEventTypes.ProductUpdated && data.Child("product") is { } page)
        {
            // The spec's PRODUCT_UPDATED is a page of products ({product:{results:[...]}}); one event per product.
            var products = page.Items("results").Where(p => p.ValueKind == JsonValueKind.Object).ToArray();
            return products.Length == 1
                ? [One(envelope, products[0], kind, type, isTest, options, [id, ContentKey(kind, products[0])])]
                : [.. products.Select(p => One(envelope, p, kind, type, isTest, options, [$"{id}:{p.Str("id")}", ContentKey(kind, p)]))];
        }
        return [One(envelope, data, kind, type, isTest, options, [id, ContentKey(kind, data)])];
    }

    /// <summary>
    /// A second duplicate key from the event's content (type + SHA-256 of the data, whitespace normalised). Catch-up reads
    /// events from Fourthwall's event list, whose ids are not documented to equal the delivery envelope's id; the same
    /// event delivered and listed has the same data, so it still counts once.
    /// </summary>
    internal static string ContentKey(string kind, JsonElement data)
    {
        var canonical = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())?.ToJsonString() ?? "";
        return $"{kind}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))}";
    }

    private static WebhookEvent One(JsonElement envelope, JsonElement data, string kind, string type, bool isTest, FourthwallOptions options,
        IReadOnlyList<string> keys)
    {
        var a = NewArgs();
        a["fw.eventId"] = envelope.Str("id");
        a["fw.eventType"] = kind;
        a["fw.shopId"] = envelope.Str("shopId") is { Length: > 0 } shop ? shop : data.Str("shopId");
        a["fw.createdAt"] = JsonReaders.Time(data.Str("createdAt") is { Length: > 0 } created ? created : envelope.Str("createdAt"));
        a["fw.testMode"] = envelope.Bool("testMode");

        // supporter: the event names a person who paid or joined (their name may be hidden on stream).
        string user = "", currency = "", message = "";
        double amount = 0;
        var supporter = true;
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
                currency = order.PathStr("amounts.total.currency");
                message = order.Str("message");
                // Only a purchase is money the supporter spent: samples and gift / giveaway-link redemptions are not.
                var isPurchase = IsPurchaseSource(order.PathStr("source.type"));
                a["isPurchase"] = isPurchase;
                a["isCancelled"] = order.Str("status").Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                amount = isPurchase ? order.PathNumber("amounts.total.value") : 0;
                break;
            }
            case FourthwallEventTypes.Donation:
            {
                var status = data.Str("status");
                a["fw.donationId"] = data.Str("id");
                a["fw.status"] = status;
                a["fw.email"] = data.Str("email");
                a["fw.username"] = data.Str("username");
                a["fw.message"] = data.Str("message");
                a["fw.amount"] = data.PathNumber("amounts.total.value");
                a["fw.currency"] = data.PathStr("amounts.total.currency");
                // Fourthwall's DonationV1 also has OPEN, ABANDONED, CANCELLED and FAILED: no money arrived for those.
                var completed = IsCompletedDonation(status);
                a["isCompleted"] = completed;
                user = data.Str("username");
                amount = completed ? data.PathNumber("amounts.total.value") : 0;
                currency = data.PathStr("amounts.total.currency");
                message = data.Str("message");
                break;
            }
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
                var state = data.PathStr("subscription.type");
                a["fw.id"] = data.Str("id");
                a["fw.email"] = data.Str("email");
                a["fw.nickname"] = data.Str("nickname");
                a["fw.type"] = state;
                a["fw.state"] = state;
                // subscription.type is the membership's state (ACTIVE, CANCELLED, SUSPENDED, NONE), not what happened.
                a["fw.isCancelled"] = state.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                a["fw.isSuspended"] = state.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase);
                a["fw.isActive"] = state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
                a["fw.interval"] = variant.Str("interval");
                a["fw.tierId"] = variant.Str("tierId");
                a["fw.variantId"] = variant.Str("id");
                amount = variant?.PathNumber("amount.value") ?? 0;
                currency = variant?.PathStr("amount.currency") ?? "";
                a["fw.amount"] = amount;
                a["fw.currency"] = currency;
                a["fw.monthlyAmount"] = Monthly(amount, variant.Str("interval"));
                user = data.Str("nickname");
                break;
            }
            case FourthwallEventTypes.NewsletterSubscribed:
                a["fw.email"] = data.Str("email");
                supporter = false;
                break;
            case FourthwallEventTypes.ThankYouSent:
            {
                var contribution = data.Child("contribution");
                a["fw.id"] = data.Str("id");
                a["fw.mediaUrl"] = data.Str("mediaUrl");
                // ORDER, DONATION, GIFT_PURCHASE or MEMBERSHIP_PAYMENT_CHARGE.
                a["fw.contributionType"] = contribution.Str("type");
                a["fw.contributionId"] = contribution.Str("id");
                var who = contribution.Child("supporter");
                a["fw.username"] = who.Str("username");
                a["fw.email"] = who.Str("email");
                a["fw.message"] = who.Str("message");
                user = who.Str("username");
                message = who.Str("message");
                break;
            }
            case FourthwallEventTypes.PromotionCreated:
            case FourthwallEventTypes.PromotionUpdated:
            case FourthwallEventTypes.PromotionStatusChanged:
                Promotion(data, a);
                supporter = false;
                message = WebhookFilters.Text(a, "fw.code");
                break;
            case FourthwallEventTypes.CollectionUpdated:
                a["fw.id"] = data.Str("id");
                a["fw.name"] = data.Str("name");
                a["fw.slug"] = data.Str("slug");
                a["fw.description"] = data.Str("description");
                a["fw.available"] = data.Bool("available") || data.Path("state.available") is { ValueKind: JsonValueKind.True };
                a["fw.access"] = data.PathStr("state.type");
                a["fw.isPublic"] = data.PathStr("state.type") is "" or "PUBLIC";
                a["fw.productCount"] = data.Items("offerIds").Count();
                a["fw.productIds"] = string.Join(",", data.Items("offerIds").Select(JsonReaders.Text));
                supporter = false;
                message = data.Str("name");
                break;
            default: // products
                Product(data, a);
                supporter = false;
                message = data.Str("name");
                break;
        }
        a["fw.isTest"] = isTest;   // Streamer.bot's name (fw.testMode is Fourthwall's own flag)

        var name = Display(user).Trim();
        var isAnonymous = supporter && name.Length == 0;
        if (isAnonymous) name = AnonymousName;
        if (supporter && options.HideSupporterNames) name = HiddenName;
        Common(a, name, amount, currency, Display(message), isTest);
        a["isAnonymous"] = isAnonymous;
        a["isReplay"] = false;
        Unprefixed(type, a);
        return new WebhookEvent(type, keys, a);
    }

    /// <summary>A membership price per month (annual prices / 12).</summary>
    internal static double Monthly(double amount, string interval) =>
        interval.Equals("ANNUAL", StringComparison.OrdinalIgnoreCase) ? Math.Round(amount / 12, 2) : amount;

    /// <summary>
    /// Streamer.bot 1.0.7's runs name the fields without the "fw." its docs show (donationId, email, shopId, status,
    /// username, createdAt...; a subscription's id is subscriptionId), so both spellings are set; Cuebot's own
    /// unprefixed names (amount, currency, message, user) win.
    /// </summary>
    private static void Unprefixed(string type, Dictionary<string, object?> a)
    {
        if (type is FourthwallEventTypes.SubscriptionPurchased or FourthwallEventTypes.SubscriptionChanged or FourthwallEventTypes.SubscriptionExpired)
            StreamerBotArgs.Alias(a, "subscriptionId", "fw.id");
        foreach (var key in a.Keys.Where(k => k.StartsWith("fw.", StringComparison.Ordinal)).ToArray())
            StreamerBotArgs.Alias(a, key[3..], key);
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
        // ORDER (a purchase), SAMPLES_ORDER (the creator's samples), GIVEAWAY_LINKS or TWITCH_GIFT_REDEMPTION (a gift redeemed).
        a["fw.source"] = order.PathStr("source.type");
        a["fw.giftId"] = order.PathStr("source.giftId");
        a["fw.giftPurchaseId"] = order.PathStr("source.giftPurchaseId");
        a["fw.promotionId"] = order.Str("promotionId");
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

    private static void Product(JsonElement data, Dictionary<string, object?> a)
    {
        a["fw.id"] = data.Str("id");
        a["fw.name"] = data.Str("name");
        a["fw.slug"] = data.Str("slug");
        a["fw.description"] = data.Str("description");
        a["fw.type"] = data.Str("type");
        a["fw.state"] = data.PathStr("state.type");
        // ARCHIVED, HIDDEN, PRIVATE or PUBLIC. No access in the payload counts as public (older shape).
        var access = data.PathStr("access.type");
        a["fw.access"] = access;
        a["fw.isPublic"] = access.Length == 0 || access.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase);
        a["fw.isSoldOut"] = data.PathStr("state.type").Equals("SOLD_OUT", StringComparison.OrdinalIgnoreCase);
        a["fw.imageUrl"] = data.PathStr("thumbnailImage.url") is { Length: > 0 } thumb ? thumb
            : data.Items("images").FirstOrDefault() is { ValueKind: JsonValueKind.Object } first ? first.Str("url") : "";
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
        var prices = variants.Select(v => v.PathNumber("unitPrice.value")).Where(x => x > 0).ToArray();
        var priceCurrency = variants.Select(v => v.PathStr("unitPrice.currency")).FirstOrDefault(c => c.Length > 0) ?? "";
        a["fw.price"] = prices.Length > 0 ? prices.Min() : 0;
        a["fw.formattedPrice"] = prices.Length > 0 ? Money.Format(prices.Min(), priceCurrency) : "";
    }

    /// <summary>
    /// A promotion (PromotionV1): <c>SHOP_SINGLE</c> / <c>MEMBERSHIPS_SINGLE</c> carry one <c>code</c>, <c>SHOP_MULTI</c> /
    /// <c>MEMBERSHIPS_MULTI</c> a <c>codes</c> list, <c>SHOP_AUTO_APPLYING</c> a code and a title. Status is Live, Ended,
    /// Archived or AllUsed.
    /// </summary>
    private static void Promotion(JsonElement data, Dictionary<string, object?> a)
    {
        var codes = data.Items("codes").Select(c => c.ValueKind == JsonValueKind.Object ? c.Str("code") : JsonReaders.Text(c)).Where(c => c.Length > 0).ToArray();
        var code = data.Str("code") is { Length: > 0 } single ? single : codes.FirstOrDefault() ?? "";
        var status = data.Str("status");
        a["fw.promotionId"] = data.Str("id");
        a["fw.id"] = data.Str("id");
        a["fw.promotionType"] = data.Str("type");
        a["fw.title"] = data.Str("title");
        a["fw.code"] = code;
        a["fw.codes"] = codes.Length > 0 ? string.Join(", ", codes) : code;
        a["fw.status"] = status;
        a["fw.isLive"] = status.Equals("Live", StringComparison.OrdinalIgnoreCase);
        a["fw.isAllUsed"] = status.Equals("AllUsed", StringComparison.OrdinalIgnoreCase);
        a["fw.usageCount"] = data.Number("usageCount");
        a["fw.maxUses"] = data.PathNumber("limits.maximumUsesNumber");
        var discount = data.Child("discount");
        var discountType = discount.Str("type");
        a["fw.discountType"] = discountType;
        a["fw.percentage"] = discount.Number("percentage");
        a["fw.discount"] = discountType switch
        {
            "PERCENTAGE" or "MEMBERSHIPS_PERCENTAGE" => $"{discount.Number("percentage").ToString("0.##", CultureInfo.InvariantCulture)}% off",
            "FLAT_RATE" => $"{Money.Format(discount.PathNumber("money.value"), discount.PathStr("money.currency"))} off",
            "FREE_SHIPPING" => "free shipping",
            "FREE_PRODUCTS" => "free products",
            _ => "",
        };
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
        // Gift amounts are subtotal, tax, total, profit and prepaidShipping (draws: subtotal and total). donation and
        // discount do not exist for gifts; they stay (always 0) because Streamer.bot's runs have them.
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
        var winners = new List<string>();
        a["fw.giftCount"] = gifts.Length;
        for (var i = 0; i < gifts.Length; i++)
        {
            var g = gifts[i];
            var p = $"fw.gifts[{i}]";
            var winner = g.Child("winner");
            a[$"{p}.id"] = g.Str("id");
            a[$"{p}.status"] = g.Str("status");
            a[$"{p}.winner"] = winner.Path("username") is { } w ? JsonReaders.Text(w) : "";
            a[$"{p}.redeemUri"] = winner.Str("redeemUri");
            a[$"{p}.selectedAt"] = winner.Str("selectedAt") is { Length: > 0 } at ? JsonReaders.Time(at) : "";
            foreach (var extra in new[] { "orderId", "orderFriendlyId", "promotionId", "giftCardId" })
                if (g.Str(extra) is { Length: > 0 } value) a[$"{p}.{extra}"] = value;
            if (winner is not null) winners.Add(Display(winner.Str("username")) is { Length: > 0 } n ? n : AnonymousName);
        }
        a["fw.winnerCount"] = winners.Count;
        a["fw.winners"] = string.Join(", ", winners);
        a["fw.giftIds"] = string.Join(",", gifts.Select(g => g.Str("id")).Where(x => x.Length > 0));
    }

    private const string Envelope = """{"testMode":false,"id":"weve___ID__","webhookId":"wcon_P-VkRfmJTBaC6_Tst22cew","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","type":"__TYPE__","apiVersion":"V1","createdAt":"2026-10-27T15:05:11.078089+00:00","data":__DATA__}""";

    private static string Event(string type, string id, string data) => Envelope
        .Replace("__TYPE__", type, StringComparison.Ordinal).Replace("__ID__", id, StringComparison.Ordinal).Replace("__DATA__", data, StringComparison.Ordinal);

    private const string Offer = """{"id":"b2c201d3-8104-4b2a-b2c9-1f6b335b650a","name":"New T-shirt","slug":"new-t-shirt","description":"Soft tee","primaryImage":{"id":"img_1","url":"https://cdn.fourthwall.com/sample/tee.jpeg","width":800,"height":800}}""";

    private const string OrderData = """
        {"id":"7b83a86f-9221-4416-b739-ef88870f999b","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","friendlyId":"F2LGPYCV",
         "checkoutId":"ch_4bRo4nB-RpWwXYx6iG4HuQ","status":"__STATUS__","email":"supporter@example.com","emailMarketingOptIn":true,
         "username":"MerchFan","message":"message from supporter",
         "amounts":{"subtotal":{"value":10,"currency":"USD"},"shipping":{"value":5,"currency":"USD"},"tax":{"value":1,"currency":"USD"},
           "donation":{"value":3,"currency":"USD"},"discount":{"value":2,"currency":"USD"},"total":{"value":17,"currency":"USD"}},
         "offers":[{"id":"b2c201d3-8104-4b2a-b2c9-1f6b335b650a","name":"New T-shirt","slug":"new-t-shirt",
           "primaryImage":{"id":"img_1","url":"https://cdn.fourthwall.com/sample/tee.jpeg"},
           "variant":{"id":"7a1f0f2e-0000-4000-8000-000000000001","name":"White, S","sku":"Z3YD-8CTV00S","unitPrice":{"value":5,"currency":"USD"},
             "attributes":{"description":"White, S","color":{"name":"White","swatch":"#FFFFFF"},"size":{"name":"S"}},"quantity":2,
             "price":{"value":10,"currency":"USD"}}}],
         "source":{"type":"ORDER"},"createdAt":"2026-10-27T15:05:10.000+00:00","updatedAt":"2026-10-27T15:05:10.000+00:00"}
        """;

    private const string ProductData = """
        {"id":"prod_hoodie","name":"Stream Hoodie","slug":"stream-hoodie","description":"Warm and cosy","type":"STANDARD",
         "state":{"type":"AVAILABLE"},"access":{"type":"PUBLIC"},
         "thumbnailImage":{"id":"img_2","url":"https://cdn.fourthwall.com/sample/hoodie.jpeg","width":800,"height":800},
         "images":[{"id":"img_2","url":"https://cdn.fourthwall.com/sample/hoodie.jpeg","width":800,"height":800}],
         "variants":[{"id":"var_1","name":"Black, M","sku":"HOOD-BM","unitPrice":{"value":45,"currency":"USD"},
           "attributes":{"description":"Black, M","color":{"name":"Black","swatch":"#000000"},"size":{"name":"M"}},
           "stock":{"type":"LIMITED","inStock":25},"weight":{"value":0.6,"unit":"kg"},"images":[]}],
         "createdAt":"2026-10-27T15:05:10.000+00:00","updatedAt":"2026-10-27T15:05:10.000+00:00"}
        """;

    private const string MemberData = """
        {"id":"7ad0c438-beda-4779-a885-0dc325a755c1","email":"member@example.com","nickname":"NewMember","createdAt":"2026-10-27T15:05:10.000+00:00",
         "subscription":{"type":"__STATE__","variant":{"id":"mtv_1","tierId":"mt_1","interval":"MONTHLY","amount":{"value":__AMOUNT__,"currency":"USD"},"offerId":"mof_1"}}}
        """;

    private const string DrawData = """
        {"id":"gdr_1","type":"INITIAL","giveawayId":"giv_voWcDCtfQUygxpuYx1HtNg","giveawayFriendlyId":"F2LGPYCV","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1",
         "offer":__OFFER__,"amounts":{"subtotal":{"value":10,"currency":"USD"},"total":{"value":20,"currency":"USD"}},
         "email":"gifter@example.com","username":"GenerousGifter","message":"Shirts for everyone","durationSeconds":60,
         "gifts":__GIFTS__,"createdAt":"2026-10-27T15:05:10.000+00:00"}
        """;

    private static string Draw(string gifts) => DrawData.Replace("__OFFER__", Offer, StringComparison.Ordinal).Replace("__GIFTS__", gifts, StringComparison.Ordinal);

    internal static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["orderPlaced"] = Event("ORDER_PLACED", "sampleorder", OrderData.Replace("__STATUS__", "CONFIRMED", StringComparison.Ordinal)),
        ["orderUpdated"] = Event("ORDER_UPDATED", "sampleorderupdate",
            "{\"order\":" + OrderData.Replace("__STATUS__", "SHIPPED", StringComparison.Ordinal) + ",\"update\":{\"type\":\"STATUS\"}}"),
        ["donation"] = Event("DONATION", "sampledonation", """
            {"id":"don_123","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","status":"COMPLETED","email":"supporter@example.com",
             "username":"KindSupporter","message":"Keep it up!","amounts":{"total":{"value":5,"currency":"USD"}},
             "createdAt":"2026-10-27T15:05:10.000+00:00","updatedAt":"2026-10-27T15:05:10.000+00:00"}
            """),
        ["anonymousDonation"] = Event("DONATION", "sampleanondonation", """
            {"id":"don_124","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","status":"COMPLETED","email":"quiet@example.com",
             "message":"No name, just thanks","amounts":{"total":{"value":3,"currency":"USD"}},
             "createdAt":"2026-10-27T15:05:10.000+00:00","updatedAt":"2026-10-27T15:05:10.000+00:00"}
            """),
        ["subscriptionPurchased"] = Event("SUBSCRIPTION_PURCHASED", "samplesub",
            MemberData.Replace("__STATE__", "ACTIVE", StringComparison.Ordinal).Replace("__AMOUNT__", "5", StringComparison.Ordinal)),
        ["subscriptionChanged"] = Event("SUBSCRIPTION_CHANGED", "samplesubchange",
            MemberData.Replace("__STATE__", "CANCELLED", StringComparison.Ordinal).Replace("__AMOUNT__", "5", StringComparison.Ordinal)),
        ["subscriptionExpired"] = Event("SUBSCRIPTION_EXPIRED", "samplesubexpired",
            MemberData.Replace("__STATE__", "NONE", StringComparison.Ordinal).Replace("__AMOUNT__", "5", StringComparison.Ordinal)),
        ["giftPurchase"] = Event("GIFT_PURCHASE", "samplegift", "{\"id\":\"giv_voWcDCtfQUygxpuYx1HtNg\",\"friendlyId\":\"F2LGPYCV\",\"shopId\":\"sh_7ad0c438-beda-4779-a885-0dc325a755c1\",\"offer\":" + Offer + """
            ,"amounts":{"subtotal":{"value":10,"currency":"USD"},"tax":{"value":1,"currency":"USD"},"total":{"value":17,"currency":"USD"},
              "profit":{"value":4,"currency":"USD"},"prepaidShipping":{"value":5,"currency":"USD"}},
             "email":"gifter@example.com","username":"GenerousGifter","message":"Shirts for everyone","quantity":2,
             "gifts":[{"id":"gft_EdJvIXu3SEiXe_QkPavHSA","status":"AVAILABLE","winner":null},{"id":"gft_EdJvIXu3SEiXe_QkPavHSB","status":"AVAILABLE","winner":null}],
             "createdAt":"2026-10-27T15:05:10.000+00:00"}
            """),
        ["giftDrawStarted"] = Event("GIFT_DRAW_STARTED", "sampledrawstart",
            Draw("""[{"id":"gft_1","status":"AVAILABLE"},{"id":"gft_2","status":"AVAILABLE"}]""")),
        ["giftDrawEnded"] = Event("GIFT_DRAW_ENDED", "sampledrawend", Draw("""
            [{"id":"gft_1","status":"AVAILABLE","winner":{"username":"LuckyViewer","selectedAt":"2026-10-27T15:06:10.000+00:00","redeemUri":"https://shop.example.com/gifts/gft_1"}},
             {"id":"gft_2","status":"AVAILABLE","winner":{"username":"","selectedAt":"2026-10-27T15:06:10.000+00:00","redeemUri":"https://shop.example.com/gifts/gft_2"}}]
            """)),
        ["thankYouSent"] = Event("THANK_YOU_SENT", "samplethanks", """
            {"id":"thx_1","mediaUrl":"https://cdn.fourthwall.com/sample/thanks.mp4",
             "contribution":{"type":"ORDER","id":"7b83a86f-9221-4416-b739-ef88870f999b","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1",
               "supporter":{"email":"supporter@example.com","username":"MerchFan","message":"message from supporter"}}}
            """),
        ["newsletterSubscribed"] = Event("NEWSLETTER_SUBSCRIBED", "samplenews", """{"email":"reader@example.com"}"""),
        ["productCreated"] = Event("PRODUCT_CREATED", "sampleproduct", ProductData),
        ["productUpdated"] = Event("PRODUCT_UPDATED", "sampleproductupdate", "{\"product\":{\"results\":[" + ProductData + "],\"total\":1,\"page\":0,\"size\":1,\"totalPages\":1}}"),
        ["promotionCreated"] = Event("PROMOTION_CREATED", "samplepromo", """
            {"type":"SHOP_SINGLE","id":"prm_1","code":"STREAM20","discount":{"type":"PERCENTAGE","percentage":20},"status":"Live",
             "requirements":{},"usageCount":0,"limits":{"maximumUsesNumber":10,"oneUsePerCustomer":true},"appliesTo":{"type":"ENTIRE_ORDER"}}
            """),
        ["promotionUpdated"] = Event("PROMOTION_UPDATED", "samplepromoupdate", """
            {"type":"SHOP_SINGLE","id":"prm_1","code":"STREAM20","discount":{"type":"PERCENTAGE","percentage":20},"status":"Live",
             "requirements":{},"usageCount":4,"limits":{"maximumUsesNumber":20,"oneUsePerCustomer":true},"appliesTo":{"type":"ENTIRE_ORDER"}}
            """),
        ["promotionStatusChanged"] = Event("PROMOTION_STATUS_CHANGED", "samplepromostatus", """
            {"type":"SHOP_SINGLE","id":"prm_1","code":"STREAM20","discount":{"type":"PERCENTAGE","percentage":20},"status":"AllUsed",
             "requirements":{},"usageCount":10,"limits":{"maximumUsesNumber":10,"oneUsePerCustomer":true},"appliesTo":{"type":"ENTIRE_ORDER"}}
            """),
        ["collectionUpdated"] = Event("COLLECTION_UPDATED", "samplecollection", """
            {"id":"col_1","shopId":"sh_7ad0c438-beda-4779-a885-0dc325a755c1","name":"Stream drop","slug":"stream-drop","description":"Only while live",
             "available":true,"state":{"type":"PUBLIC","available":true},"offerIds":["prod_hoodie","b2c201d3-8104-4b2a-b2c9-1f6b335b650a"]}
            """),
    };
}
