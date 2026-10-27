using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Ko-fi (ko-fi.com → Settings → API → Webhooks). Ko-fi POSTs <c>application/x-www-form-urlencoded</c> with one
/// field, <c>data</c>, holding JSON; the JSON's <c>verification_token</c> must equal the token shown in Ko-fi's
/// webhook settings. Types: Donation, Subscription (first payment → subscription, later → resubscription),
/// Commission, Shop Order. Dedupe on <c>message_id</c>.
/// </summary>
public sealed class KofiWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "kofi";

    public override string Service => Route;
    public override string DisplayName => "Ko-fi";
    public WebhookServiceSettings Settings => SettingsFile.Value.Kofi;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Ko-fi verification token";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.TokensEqual(Settings.Secret.Trim(), ReadData(delivery).Str("verification_token"));

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery) =>
        Map(ReadData(delivery), isTest: false) is { } e ? [e] : [];

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var json) && Map(JsonReaders.ParseBody(json), isTest: true) is { } e ? [e] : null;

    /// <summary>The JSON in the form field <c>data</c> (a raw JSON body is accepted too).</summary>
    internal static JsonElement ReadData(WebhookDelivery delivery)
    {
        var text = delivery.BodyText.Trim();
        if (!text.StartsWith('{')) text = FormData.Parse(text).GetValueOrDefault("data", "");
        return JsonReaders.ParseBody(text);
    }

    internal static WebhookEvent? Map(JsonElement data, bool isTest)
    {
        var kind = data.Str("type");
        var type = kind.ToLowerInvariant() switch
        {
            "donation" => KofiEventTypes.Donation,
            "subscription" => data.Bool("is_first_subscription_payment") ? KofiEventTypes.Subscription : KofiEventTypes.Resubscription,
            "commission" => KofiEventTypes.Commission,
            "shop order" or "shoporder" or "shop_order" => KofiEventTypes.ShopOrder,
            _ => null,
        };
        if (type is null) return null;

        var from = data.Str("from_name");
        var amount = data.Number("amount");
        var currency = data.Str("currency");
        var a = NewArgs();
        Common(a, from.Length > 0 ? from : "Anonymous", amount, currency, data.Str("message"), isTest);
        a["from"] = from;
        a["isPublic"] = data.Bool("is_public");
        a["messageId"] = data.Str("message_id");
        a["timestamp"] = JsonReaders.Time(data.Str("timestamp"));
        a["kofiType"] = kind;
        a["url"] = data.Str("url");
        a["transactionId"] = data.Str("kofi_transaction_id");
        if (type is KofiEventTypes.Subscription or KofiEventTypes.Resubscription)
        {
            a["tier"] = data.Str("tier_name");
            a["isFirstPayment"] = type == KofiEventTypes.Subscription;
        }
        if (type == KofiEventTypes.ShopOrder)
        {
            var items = data.Items("shop_items").ToArray();
            a["itemCount"] = items.Length;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                a[$"item{i}"] = item.First("variation_name", "direct_link_code");
                a[$"item{i}.code"] = item.Str("direct_link_code");
                a[$"item{i}.quantity"] = item.Number("quantity");
            }
        }
        return new WebhookEvent(type, [data.Str("message_id"), data.Str("kofi_transaction_id") is { Length: > 0 } tx ? $"{kind}:{tx}" : ""], a);
    }

    internal static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["donation"] = """
            {"verification_token":"","message_id":"3a1fac0c-f960-4506-a60e-824979a74e74","timestamp":"2026-10-27T13:04:30Z",
             "type":"Donation","is_public":true,"from_name":"Ko-fi Team","message":"Good luck with the stream!","amount":"3.00",
             "url":"https://ko-fi.com/Home/CoffeeShop?txid=00000000-1111-2222-3333-444444444444","email":"someone@example.com",
             "currency":"USD","is_subscription_payment":false,"is_first_subscription_payment":false,
             "kofi_transaction_id":"00000000-1111-2222-3333-444444444444","shop_items":null,"tier_name":null,"shipping":null}
            """,
        ["subscription"] = """
            {"verification_token":"","message_id":"5b2fac0c-f960-4506-a60e-824979a74e75","timestamp":"2026-10-27T13:05:30Z",
             "type":"Subscription","is_public":true,"from_name":"Coffee Lover","message":"Monthly coffee!","amount":"5.00",
             "currency":"USD","is_subscription_payment":true,"is_first_subscription_payment":true,
             "kofi_transaction_id":"10000000-1111-2222-3333-444444444444","tier_name":"Bronze"}
            """,
        ["resubscription"] = """
            {"verification_token":"","message_id":"6c2fac0c-f960-4506-a60e-824979a74e76","timestamp":"2026-10-27T13:06:30Z",
             "type":"Subscription","is_public":true,"from_name":"Coffee Lover","message":null,"amount":"5.00",
             "currency":"USD","is_subscription_payment":true,"is_first_subscription_payment":false,
             "kofi_transaction_id":"20000000-1111-2222-3333-444444444444","tier_name":"Bronze"}
            """,
        ["commission"] = """
            {"verification_token":"","message_id":"7d2fac0c-f960-4506-a60e-824979a74e77","timestamp":"2026-10-27T13:07:30Z",
             "type":"Commission","is_public":true,"from_name":"Art Fan","message":"A portrait of my cat please","amount":"25.00",
             "currency":"EUR","is_subscription_payment":false,"is_first_subscription_payment":false,
             "kofi_transaction_id":"30000000-1111-2222-3333-444444444444"}
            """,
        ["shopOrder"] = """
            {"verification_token":"","message_id":"8e2fac0c-f960-4506-a60e-824979a74e78","timestamp":"2026-10-27T13:08:30Z",
             "type":"Shop Order","is_public":true,"from_name":"Merch Buyer","message":null,"amount":"18.00",
             "currency":"USD","is_subscription_payment":false,"is_first_subscription_payment":false,
             "kofi_transaction_id":"40000000-1111-2222-3333-444444444444",
             "shop_items":[{"direct_link_code":"1a2b3c4d5e","variation_name":"Sticker pack","quantity":2},
                           {"direct_link_code":"9z8y7x6w5v","variation_name":"Poster","quantity":1}]}
            """,
    };
}
