using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Throne wishlist webhooks (throne.com → Account → Settings → Integrations → Webhooks). Throne documents no
/// signature, so the URL pasted into Throne must carry a Cuebot-generated token, either as an extra path segment
/// (<c>…/throne/&lt;token&gt;</c>), as <c>?token=&lt;token&gt;</c> or in an <c>X-Cuebot-Token</c> header; it is
/// compared in constant time. The payload shape is not publicly documented, so it is read tolerantly: the event type
/// from <c>type</c>/<c>event</c>/<c>event_type</c>, the fields from <c>data</c>/<c>payload</c> or the top level, in
/// snake_case or camelCase (<c>gifter_username</c>, <c>item_name</c>, <c>amount</c>/<c>price</c> in cents...).
/// </summary>
public sealed class ThroneWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log)
    : WebhookServiceBase(settings, bus, log)
{
    public const string Route = "throne";

    public override string Service => Route;
    public override string DisplayName => "Throne";
    public WebhookServiceSettings Settings => SettingsFile.Value.Throne;
    public override bool Enabled => Settings.Enabled;
    public override bool IsConfigured => Settings.Secret.Trim().Length > 0;
    protected override string SecretName => "Throne URL token";
    public override IReadOnlyList<string> TestEventKinds => [.. Samples.Keys];

    private protected override bool Verify(WebhookDelivery delivery) =>
        WebhookVerification.TokensEqual(Settings.Secret.Trim(), GivenToken(delivery, delivery.Path.Trim('/')));

    /// <summary>The token from the path segment, <c>?token=</c> or the X-Cuebot-Token header (in that order).</summary>
    internal static string GivenToken(WebhookDelivery delivery, string pathToken)
    {
        if (pathToken.Length > 0) return Uri.UnescapeDataString(pathToken);
        if (FormData.Parse(delivery.Query).TryGetValue("token", out var query) && query.Length > 0) return query;
        return delivery.Header("X-Cuebot-Token").Trim();
    }

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var e = Map(JsonReaders.ParseBody(delivery.BodyText), isTest: false);
        if (e is null) return [];
        return [e with { Keys = e.Keys.Count > 0 ? e.Keys : [WebhookVerification.BodyHash(delivery.Body)] }];
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        Samples.TryGetValue(kind, out var json) && Map(JsonReaders.ParseBody(json), isTest: true) is { } e ? [e] : null;

    internal static string? EventType(string kind) =>
        new string(kind.Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
        {
            "giftpurchased" or "giftpurchase" => ThroneEventTypes.GiftPurchased,
            "contributionpurchased" or "contributionpurchase" or "contribution" => ThroneEventTypes.ContributionPurchased,
            "giftcrowdfunded" or "crowdfundcompleted" or "crowdfundinggiftcompleted" => ThroneEventTypes.GiftCrowdfunded,
            _ => null,
        };

    internal static WebhookEvent? Map(JsonElement root, bool isTest)
    {
        var kind = root.First("type", "event", "event_type", "eventType", "topic");
        var data = root.Child("data") is { ValueKind: JsonValueKind.Object } d ? d
            : root.Child("payload") is { ValueKind: JsonValueKind.Object } p ? p : root;
        if (kind.Length == 0) kind = data.First("type", "event", "event_type", "eventType");
        var type = EventType(kind);
        if (type is null) return null;

        string F(params string[] names) => data.First(names);
        double N(params string[] names) => names.Select(n => data.Number(n)).FirstOrDefault(v => v != 0);

        var a = NewArgs();
        a["throne.eventType"] = kind;
        a["throne.creatorId"] = F("creator_id", "creatorId");
        a["throne.creatorLogin"] = F("creator_login", "creatorLogin", "creator_username", "creatorUsername");
        a["throne.gifterLogin"] = F("gifter_login", "gifterLogin", "gifter_username", "gifterUsername", "gifter_name", "gifterName");
        a["throne.itemName"] = F("item_name", "itemName");
        a["throne.itemThumbnailUrl"] = F("item_thumbnail_url", "itemThumbnailUrl", "item_image_url", "itemImageUrl");
        a["throne.message"] = F("message");
        a["throne.isSurpriseGift"] = data.Bool("is_surprise_gift") || data.Bool("isSurpriseGift");
        a["throne.currency"] = F("currency");
        a["throne.createdAt"] = JsonReaders.Time(F("created_at", "createdAt", "timestamp"));
        var cents = type == ThroneEventTypes.ContributionPurchased ? N("amount", "price") : N("price", "amount");
        if (type == ThroneEventTypes.ContributionPurchased) a["throne.amount"] = cents;
        else a["throne.price"] = cents;

        var gifter = (string)a["throne.gifterLogin"]!;
        Common(a, gifter.Length > 0 ? gifter : "Someone", cents / 100.0, (string)a["throne.currency"]!, (string)a["throne.message"]!, isTest);
        a["item"] = a["throne.itemName"];
        var id = root.First("id", "event_id", "eventId") is { Length: > 0 } rid ? rid : data.First("id", "event_id", "eventId", "order_id", "orderId");
        return new WebhookEvent(type, id.Length > 0 ? [id] : [], a);
    }

    internal static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["giftPurchased"] = """
            {"type":"gift_purchased","data":{"creator_id":"twitch:12345678","creator_username":"twitchuser123","gifter_username":"Lyfesaver74",
             "message":"Happy Birthday!","item_name":"AirPods Max","item_thumbnail_url":"https://m.media-amazon.com/images/I/81jqUPkIVRL._AC_SX522_.jpg",
             "is_surprise_gift":false,"price":10000,"currency":"USD","created_at":"2026-10-27T15:01:00Z"}}
            """,
        ["contributionPurchased"] = """
            {"type":"contribution_purchased","data":{"creator_id":"twitch:12345678","creator_username":"twitchuser123","gifter_username":"Lyfesaver74",
             "message":"Chipping in!","item_name":"Streaming PC","item_thumbnail_url":"https://example.com/pc.jpg",
             "is_surprise_gift":false,"amount":1099,"currency":"USD","created_at":"2026-10-27T15:02:00Z"}}
            """,
        ["giftCrowdfunded"] = """
            {"type":"gift_crowdfunded","data":{"creator_id":"twitch:12345678","creator_username":"twitchuser123",
             "item_name":"Streaming PC","item_thumbnail_url":"https://example.com/pc.jpg","is_surprise_gift":false,
             "price":149900,"currency":"USD","created_at":"2026-10-27T15:03:00Z"}}
            """,
    };
}
