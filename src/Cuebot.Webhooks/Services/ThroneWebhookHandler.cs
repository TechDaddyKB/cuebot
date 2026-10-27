using System.Globalization;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Throne wishlist webhooks (throne.com → Settings → Integrations → Webhooks). Throne signs every delivery with
/// Ed25519: <c>X-Signature-Timestamp</c> (Unix seconds) and <c>X-Signature-Ed25519</c> (hex), over
/// <c>timestamp + "." + raw body</c>, with Throne's published public key. Deliveries signed more than ten minutes
/// away from the time they reached Cuebot are refused (replays). Because the key is the same for every Throne creator,
/// the user may also set a Cuebot URL token (<c>…/throne/&lt;token&gt;</c>, <c>?token=</c> or <c>X-Cuebot-Token</c>);
/// when set it must match too. The body is <c>{ contract_version, event_id, event_type, data }</c>; dedupe on
/// <c>event_id</c>.
/// </summary>
public sealed class ThroneWebhookHandler : WebhookServiceBase
{
    public const string Route = "throne";

    /// <summary>Throne's webhook signing key (help.throne.com, "How do I set up webhook integration?").</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MCowBQYDK2VwAyEAPXbUfxh7XL4SYUVcfhmYMIbxvtR9E9LDd8gPJ1PwSD8=
        -----END PUBLIC KEY-----
        """;

    /// <summary>How far the signed timestamp may be from the time the delivery reached Cuebot.</summary>
    internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>Throne's name for a gifter who chose to stay anonymous.</summary>
    internal const string Anonymous = "Anonymous";

    private static readonly byte[] ThronePublicKey = WebhookVerification.Ed25519PublicKeyFromPem(PublicKeyPem);
    private readonly byte[] _publicKey;

    public ThroneWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log) : this(settings, bus, log, null) { }

    /// <param name="publicKey">The raw 32-byte Ed25519 verification key; Throne's own when null (tests pass their own).</param>
    internal ThroneWebhookHandler(JsonFile<WebhooksSettings> settings, EventBus bus, AppLog log, byte[]? publicKey)
        : base(settings, bus, log) => _publicKey = publicKey ?? ThronePublicKey;

    public override string Service => Route;
    public override string DisplayName => "Throne";
    public WebhookServiceSettings Settings => SettingsFile.Value.Throne;
    public override bool Enabled => Settings.Enabled;

    /// <summary>Always: Throne's signature is checked with its public key, so no secret has to be entered.</summary>
    public override bool IsConfigured => true;

    protected override string SecretName => "Throne signature (or the Cuebot URL token)";
    public override IReadOnlyList<string> TestEventKinds => ["giftPurchased", "contributionPurchased", "giftCrowdfunded"];

    private protected override bool Verify(WebhookDelivery delivery)
    {
        if (!CheckSignature(_publicKey, delivery)) return false;
        var token = Settings.Secret.Trim();
        return token.Length == 0 || WebhookVerification.TokensEqual(token, GivenToken(delivery, delivery.Path.Trim('/')));
    }

    /// <summary>The Ed25519 check of <c>timestamp "." body</c>, within ten minutes of <see cref="WebhookDelivery.ReceivedAt"/>.</summary>
    internal static bool CheckSignature(byte[] publicKey, WebhookDelivery delivery)
    {
        if (SignedAt(delivery) is not { } signedAt) return false;
        if ((delivery.ReceivedAt - signedAt).Duration() > MaxClockSkew) return false;
        byte[] signature;
        try { signature = Convert.FromHexString(delivery.Header("X-Signature-Ed25519").Trim()); }
        catch (FormatException) { return false; }
        if (signature.Length != WebhookVerification.Ed25519Signature) return false;
        var prefix = Encoding.UTF8.GetBytes(delivery.Header("X-Signature-Timestamp").Trim() + ".");
        return WebhookVerification.CheckEd25519(publicKey, [.. prefix, .. delivery.Body], signature);
    }

    /// <summary><c>X-Signature-Timestamp</c> (decimal Unix seconds) as a time, or null when missing or not a number.</summary>
    internal static DateTimeOffset? SignedAt(WebhookDelivery delivery)
    {
        var text = delivery.Header("X-Signature-Timestamp").Trim();
        if (text.Length == 0 || !text.All(char.IsAsciiDigit)) return null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The token from the path segment, <c>?token=</c> or the X-Cuebot-Token header (in that order).</summary>
    internal static string GivenToken(WebhookDelivery delivery, string pathToken)
    {
        if (pathToken.Length > 0) return Uri.UnescapeDataString(pathToken);
        if (FormData.Parse(delivery.Query).TryGetValue("token", out var query) && query.Length > 0) return query;
        return delivery.Header("X-Cuebot-Token").Trim();
    }

    private protected override IReadOnlyList<WebhookEvent> Map(WebhookDelivery delivery)
    {
        var root = JsonReaders.ParseBody(delivery.BodyText);
        var version = root.Str("contract_version");
        if (version != "1")
            Log.Warn(DisplayName, $"Unexpected webhook contract_version '{version}' (Cuebot reads version 1); some fields may be missing.");
        var e = Map(root, SignedAt(delivery) ?? delivery.ReceivedAt, isTest: false);
        if (e is null) return [];
        return [e with { Keys = e.Keys.Count > 0 ? e.Keys : [WebhookVerification.BodyHash(delivery.Body)] }];
    }

    private protected override IReadOnlyList<WebhookEvent>? Sample(string kind) =>
        SampleJson(kind) is { } json && Map(JsonReaders.ParseBody(json), DateTimeOffset.UtcNow, isTest: true) is { } e ? [e] : null;

    internal static string? EventType(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "gift_purchased" => ThroneEventTypes.GiftPurchased,
        "contribution_purchased" => ThroneEventTypes.ContributionPurchased,
        "gift_crowdfunded" => ThroneEventTypes.GiftCrowdfunded,
        _ => null,
    };

    internal static WebhookEvent? Map(JsonElement root, DateTimeOffset createdAt, bool isTest)
    {
        // Documented: event_type at the top level and the fields under data ("type" is read too, harmlessly).
        var kind = root.First("event_type", "type");
        var type = EventType(kind);
        if (type is null) return null;
        var data = root.Child("data") is { ValueKind: JsonValueKind.Object } d ? d : root;

        var a = NewArgs();
        a["throne.eventId"] = root.Str("event_id");
        a["throne.eventType"] = kind;
        a["throne.contractVersion"] = root.Str("contract_version");
        a["throne.creatorId"] = data.Str("creator_id");
        a["throne.creatorLogin"] = data.Str("creator_username");
        a["throne.gifterLogin"] = data.Str("gifter_username");
        a["throne.itemName"] = data.Str("item_name");
        a["throne.itemThumbnailUrl"] = data.Str("item_thumbnail_url");
        a["throne.message"] = data.Str("message");
        a["throne.isSurpriseGift"] = data.Bool("is_surprise_gift");
        a["throne.currency"] = data.Str("currency");
        a["throne.createdAt"] = createdAt;
        // Cents: "amount" on contributions, "price" on gifts and crowdfunded gifts.
        var cents = type == ThroneEventTypes.ContributionPurchased ? data.Number("amount") : data.Number("price");
        if (type == ThroneEventTypes.ContributionPurchased) a["throne.amount"] = cents;
        else a["throne.price"] = cents;

        var gifter = (string)a["throne.gifterLogin"]!;
        var anonymous = gifter.Length == 0 || string.Equals(gifter, Anonymous, StringComparison.OrdinalIgnoreCase);
        a["isAnonymous"] = anonymous;
        Common(a, anonymous ? Anonymous : gifter, cents / 100.0, (string)a["throne.currency"]!, (string)a["throne.message"]!, isTest);
        a["item"] = a["throne.itemName"];
        var id = root.Str("event_id");
        return new WebhookEvent(type, id.Length > 0 ? [id] : [], a);
    }

    /// <summary>A built-in sample in the shape of a real Throne delivery (with a fresh event_id), or null for an unknown kind.</summary>
    internal static string? SampleJson(string kind)
    {
        (string? type, string? data) = kind.ToLowerInvariant() switch
        {
            "giftpurchased" => ("gift_purchased", """
                {"creator_id":"8f3c1a2b4d5e6f708192a3b4","creator_username":"twitchuser123","gifter_username":"Lyfesaver74",
                 "message":"Happy Birthday!","item_name":"AirPods Max","item_thumbnail_url":"https://m.media-amazon.com/images/I/81jqUPkIVRL._AC_SX522_.jpg",
                 "price":10000,"currency":"USD","is_surprise_gift":false}
                """),
            "contributionpurchased" => ("contribution_purchased", """
                {"creator_id":"8f3c1a2b4d5e6f708192a3b4","creator_username":"twitchuser123","gifter_username":"Lyfesaver74",
                 "message":"Chipping in!","item_name":"Streaming PC","item_thumbnail_url":"https://example.com/pc.jpg",
                 "amount":1099,"currency":"USD"}
                """),
            "giftcrowdfunded" => ("gift_crowdfunded", """
                {"creator_id":"8f3c1a2b4d5e6f708192a3b4","creator_username":"twitchuser123","gifter_username":"Anonymous",
                 "item_name":"Streaming PC","item_thumbnail_url":"https://example.com/pc.jpg",
                 "price":149900,"currency":"USD","is_surprise_gift":false}
                """),
            _ => (null, null),
        };
        return type is null
            ? null
            : $$"""{"contract_version":"1","event_id":"{{Guid.NewGuid()}}","event_type":"{{type}}","data":{{data}}}""";
    }
}
