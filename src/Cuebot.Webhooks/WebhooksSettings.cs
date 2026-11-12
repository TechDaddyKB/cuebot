using Cuebot.Core.Storage;
namespace Cuebot.Webhooks;

/// <summary>
/// Persisted webhook service configuration (webhooks.json). Secrets are only used to verify deliveries locally:
/// they never leave the PC, are masked in the UI and never written to the log.
/// </summary>
public sealed class WebhooksSettings
{
    /// <summary>
    /// Ko-fi: the Verification Token from https://ko-fi.com/manage/webhooks (Settings → API → Webhooks → Advanced).
    /// </summary>
    public WebhookServiceSettings Kofi { get; set; } = new();

    /// <summary>Ko-fi choices beyond the token: private supporters, product names, the goal and membership tracking.</summary>
    public KofiOptions KofiOptions { get; set; } = new();

    /// <summary>Patreon: the webhook's secret (patreon.com/portal → My Webhooks).</summary>
    public WebhookServiceSettings Patreon { get; set; } = new();

    /// <summary>Fourthwall: the shop's webhook secret key (Settings → For developers → Webhooks).</summary>
    public WebhookServiceSettings Fourthwall { get; set; } = new();

    /// <summary>
    /// Shopify: the signing key. For webhooks created in the admin (Settings → Notifications → Webhooks) it is the key
    /// shown there ("Your webhooks will be signed with ..."); for an app's webhooks it is the app's client secret.
    /// </summary>
    public WebhookServiceSettings Shopify { get; set; } = new();

    /// <summary>
    /// Throne signs its webhooks (Ed25519, checked with Throne's public key), so no secret is needed. The secret here is
    /// an optional Cuebot-made URL token: when set, deliveries must also carry it as <c>…/throne/&lt;token&gt;</c> (or
    /// <c>?token=&lt;token&gt;</c>), since Throne's key is shared by every creator.
    /// </summary>
    public WebhookServiceSettings Throne { get; set; } = new();

    /// <summary>User-defined hooks, called at <c>…/custom/&lt;name&gt;</c> with the hook's token.</summary>
    public List<CustomWebhookSettings> Custom { get; set; } = [];

    /// <summary>
    /// Outgoing: the IFTTT Webhooks key used by the IFTTT Notification sub-actions when they give none
    /// (ifttt.com/maker_webhooks → Documentation). A secret.
    /// </summary>
    [Secret]
    public string IftttKey { get; set; } = "";
}

public sealed class WebhookServiceSettings
{
    /// <summary>Deliveries are verified but ignored (no events) while switched off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The signing secret / verification token. A secret.</summary>
    [Secret]
    public string Secret { get; set; } = "";
}

public sealed class CustomWebhookSettings
{
    /// <summary>Route name (letters, digits, dashes, underscores), matched case-insensitively.</summary>
    public string Name { get; set; } = "";

    /// <summary>Sent as <c>?token=</c> or the <c>X-Cuebot-Token</c> header. A secret.</summary>
    [Secret]
    public string Token { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>Ko-fi options (webhooks.json). The goal's progress, members and commissions are kept in kofi.json.</summary>
public sealed class KofiOptions
{
    /// <summary>
    /// Off (the default): a support the supporter marked private (<c>is_public: false</c>) arrives as "Anonymous" with no
    /// message in %user%, %message%, overlays, the feed and speech; the real name and message stay only in
    /// %privateName% / %privateMessage%. On: they are shown like public ones (the feed and credits still hide them).
    /// </summary>
    public bool ShowPrivateSupporters { get; set; }

    /// <summary>
    /// Product names (and pictures) for shop orders, by Ko-fi's <c>direct_link_code</c>: Ko-fi's webhook names a
    /// product only by that code when it has no variation.
    /// </summary>
    public List<KofiProduct> Products { get; set; } = [];

    /// <summary>A member whose last payment is older than this many days is reported as probably lapsed (Ko-fi sends no cancellation).</summary>
    public int LapseDays { get; set; } = 35;

    /// <summary>A delivery that reaches Cuebot more than this many minutes after the relay got it is marked isReplay.</summary>
    public int LateMinutes { get; set; } = 10;

    public KofiGoalSettings Goal { get; set; } = new();
}

public sealed class KofiProduct
{
    /// <summary>Ko-fi's <c>direct_link_code</c> (the end of the product's link, e.g. 1a2b3c4d5e).</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
}

/// <summary>The Ko-fi goal Cuebot keeps (Ko-fi has no API to read the goal on your page, so it is counted from webhooks).</summary>
public sealed class KofiGoalSettings
{
    public string Title { get; set; } = "Ko-fi goal";

    /// <summary>0 = no goal.</summary>
    public double Target { get; set; }

    /// <summary>What was raised before Cuebot started counting (typed in from Ko-fi), added to the progress.</summary>
    public double StartAmount { get; set; }

    /// <summary>Only payments in this currency count (Cuebot does not convert); empty counts every currency.</summary>
    public string Currency { get; set; } = "";

    public bool CountDonations { get; set; } = true;
    public bool CountMemberships { get; set; } = true;
    public bool CountCommissions { get; set; } = true;
    public bool CountShopOrders { get; set; } = true;
}
