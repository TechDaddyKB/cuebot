using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Ko-fi (https://ko-fi.com/manage/webhooks, also Settings → API → Webhooks). Ko-fi POSTs
/// <c>application/x-www-form-urlencoded</c> with one field, <c>data</c>, holding JSON; the JSON's
/// <c>verification_token</c> must equal the token shown in Ko-fi's webhook settings. Ko-fi sends payments only (no
/// cancellations, no goal progress). Types: Donation, Subscription (first payment → subscription, later →
/// resubscription), Commission, Shop Order. Dedupe on <c>message_id</c> only (retries reuse it). Ko-fi's test buttons
/// send a fresh <c>message_id</c> but always the placeholder transaction id <see cref="TestTransactionId"/>, which marks
/// them isTest.
/// <para>
/// Privacy: a support marked private (<c>is_public: false</c>) is "Anonymous" with no message in every argument unless
/// <see cref="KofiOptions.ShowPrivateSupporters"/> is on; the real values are only in privateName / privateMessage.
/// The supporter's <c>email</c> and the shop order's <c>shipping</c> address are personal data and deliberately never
/// become arguments (do not flatten the whole payload).
/// </para>
/// </summary>
public sealed class KofiWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "kofi";

    public override string Service => Route;
    public override string DisplayName => "Ko-fi";
    public WebhookServiceSettings Settings => SettingsFile.Value.Kofi;
    public KofiOptions Options => SettingsFile.Value.KofiOptions;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Ko-fi verification token";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    /// <summary>The goal, members and commissions (in memory unless the app gives it kofi.json).</summary>
    public KofiTracker Tracker { get; internal set; } = new(settings, bus, log);

    /// <summary>Now, for telling late (replayed) deliveries; tests set their own.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.TokensEqual(Settings.Secret.Trim(), ReadData(delivery).Str("verification_token"));

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        if (Map(ReadData(delivery), isTest: false, Options) is not { } e) return [];
        // A delivery the relay kept while Cuebot was closed (own relay: up to 24 h) arrives late. Triggers can skip
        // those ("Skip late deliveries") while the feed, totals and the goal still count the money.
        var delay = Clock.GetUtcNow() - delivery.ReceivedAt;
        e.Args["isReplay"] = delay > TimeSpan.FromMinutes(Math.Max(1, Options.LateMinutes));
        e.Args["delaySeconds"] = (long)Math.Max(0, delay.TotalSeconds);
        return [e];
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var json) && Map(JsonReaders.ParseBody(json), isTest: true, Options) is { } e ? [e] : null;

    private protected override IReadOnlyList<BotEvent> BeforePublish(WebhookEvent e) => Tracker.Record(e.Type, e.Args);

    /// <summary>The JSON in the form field <c>data</c> (a raw JSON body is accepted too).</summary>
    internal static JsonElement ReadData(WebhookDelivery delivery)
    {
        var text = delivery.BodyText.Trim();
        if (!text.StartsWith('{')) text = FormData.Parse(text).GetValueOrDefault("data", "");
        return JsonReaders.ParseBody(text);
    }

    /// <summary>The <c>kofi_transaction_id</c> of every delivery sent by Ko-fi's webhook test buttons.</summary>
    internal const string TestTransactionId = "00000000-1111-2222-3333-444444444444";

    internal static WebhookEvent? Map(JsonElement data, bool isTest, KofiOptions? options = null)
    {
        options ??= new KofiOptions();
        isTest |= string.Equals(data.Str("kofi_transaction_id").Trim(), TestTransactionId, StringComparison.OrdinalIgnoreCase);
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

        var from = data.Str("from_name").Trim();
        var message = data.Str("message");
        // Ko-fi always sends is_public; a payload without it is treated as public (older fixtures, other senders).
        var isPublic = !data.Has("is_public") || data.Bool("is_public");
        var hide = !isPublic && !options.ShowPrivateSupporters;
        var amount = data.Number("amount");
        var currency = data.Str("currency");
        var a = NewArgs();
        Common(a, hide || from.Length == 0 ? "Anonymous" : from, amount, currency, hide ? "" : message, isTest);
        a["from"] = hide ? "" : from;
        a["isPublic"] = isPublic;
        if (!isPublic)
        {
            // Only for deliberate private use (a log, a DM to yourself); never put these on stream.
            a["privateName"] = from;
            a["privateMessage"] = message;
            a["privateShown"] = !hide;
        }
        a["messageId"] = data.Str("message_id");
        a["timestamp"] = JsonReaders.Time(data.Str("timestamp"));
        a["kofiType"] = kind;
        a["url"] = data.Str("url");
        a["transactionId"] = data.Str("kofi_transaction_id");
        a["isSubscriptionPayment"] = data.Bool("is_subscription_payment");
        a["isReplay"] = false;
        // Sent when the supporter linked Discord (memberships with Discord roles). Discord sub-actions take
        // %discordUserId% as the member; %discordMention% pings them in a channel message.
        if (data.Str("discord_username") is { Length: > 0 } discordName) a["discordUsername"] = discordName;
        if (data.Str("discord_userid") is { Length: > 0 } discordId)
        {
            a["discordUserId"] = discordId;
            a["discordMention"] = $"<@{discordId}>";
        }
        if (type is KofiEventTypes.Subscription or KofiEventTypes.Resubscription)
        {
            a["tier"] = data.Str("tier_name");
            a["isFirstPayment"] = type == KofiEventTypes.Subscription;
        }
        if (type == KofiEventTypes.ShopOrder)
        {
            var items = data.Items("shop_items").ToArray();
            a["itemCount"] = items.Length;
            var products = options.Products.Where(p => p.Code.Trim().Length > 0)
                .GroupBy(p => p.Code.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var code = item.Str("direct_link_code").Trim();
                var variation = item.Str("variation_name").Trim();
                var product = products.GetValueOrDefault(code);
                var productName = product?.Name.Trim() ?? "";
                // Ko-fi names a product only by its code when it has no variation: use the name from settings, or
                // say it is an item code rather than show a bare "1a2b3c4d5e".
                a[$"item{i}"] = productName.Length > 0 && variation.Length > 0 ? $"{productName} ({variation})"
                    : productName.Length > 0 ? productName
                    : variation.Length > 0 ? variation
                    : code.Length > 0 ? $"item {code}" : "item";
                a[$"item{i}.code"] = code;
                a[$"item{i}.variation"] = variation;
                a[$"item{i}.image"] = product?.ImageUrl.Trim() ?? "";
                a[$"item{i}.quantity"] = item.Has("quantity") ? item.Number("quantity") : 1.0;
            }
        }
        return new WebhookEvent(type, [data.Str("message_id")], a);
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
        ["privateDonation"] = """
            {"verification_token":"","message_id":"4a1fac0c-f960-4506-a60e-824979a74e7a","timestamp":"2026-10-27T13:04:50Z",
             "type":"Donation","is_public":false,"from_name":"Quiet Supporter","message":"Please keep this between us","amount":"10.00",
             "url":"https://ko-fi.com/Home/CoffeeShop?txid=00000000-1111-2222-3333-444444444444",
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
