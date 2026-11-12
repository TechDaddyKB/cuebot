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

    /// <summary>Patreon choices beyond the webhook secret: the optional API connection, currency and stale deliveries.</summary>
    public PatreonOptions PatreonOptions { get; set; } = new();

    /// <summary>Fourthwall: the shop's webhook secret key (Settings → For developers → Webhooks).</summary>
    public WebhookServiceSettings Fourthwall { get; set; } = new();

    /// <summary>Fourthwall choices beyond the webhook secret: names on stream, gift links and the optional Open API connection.</summary>
    public FourthwallOptions FourthwallOptions { get; set; } = new();

    /// <summary>
    /// Shopify: the signing key. For webhooks created in the admin (Settings → Notifications → Webhooks) it is the key
    /// shown there under the webhook list ("Your webhooks are signed with ..."), one per store. Only for webhooks an
    /// existing custom app or a Dev Dashboard app subscribed itself is it the app's client secret.
    /// </summary>
    public WebhookServiceSettings Shopify { get; set; } = new();

    /// <summary>Shopify choices beyond the signing key: privacy, the merch counter and goal, low stock and the optional Admin API.</summary>
    public ShopifyOptions ShopifyOptions { get; set; } = new();

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

/// <summary>
/// Patreon options (webhooks.json). The API connection is optional: the creator's own tokens from
/// patreon.com/portal/registration/register-clients. With it Cuebot reads the campaign (currency, tiers, patron count),
/// syncs the member list, tells upgrades from downgrades and can create and watch the webhook. The campaign, tiers and
/// members it learnt are kept in patreon.json.
/// </summary>
public sealed class PatreonOptions
{
    /// <summary>The client's Client ID (needed to refresh the access token).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The client's Client Secret (needed to refresh the access token). A secret.</summary>
    [Secret]
    public string ClientSecret { get; set; } = "";

    /// <summary>The Creator's Access Token. A secret.</summary>
    [Secret]
    public string AccessToken { get; set; } = "";

    /// <summary>The Creator's Refresh Token. A secret.</summary>
    [Secret]
    public string RefreshToken { get; set; } = "";

    /// <summary>When the access token runs out, once Cuebot refreshed it (Patreon does not say for a pasted token).</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>
    /// The campaign's currency (like USD) used when neither the delivery nor the API names it. Empty: unknown until
    /// Cuebot learns it.
    /// </summary>
    public string Currency { get; set; } = "";

    /// <summary>
    /// A new pledge whose start and last charge are both older than this many hours is marked isStale (a backlog Patreon
    /// sends after a paused webhook is resumed); the Patreon triggers skip stale events unless told otherwise. 0 = off.
    /// </summary>
    public int StaleHours { get; set; } = 6;

    /// <summary>Tag linked Twitch / YouTube viewers with user variables (isPatron, patreonTier...).</summary>
    public bool LinkViewers { get; set; } = true;

    /// <summary>The webhook Cuebot created through the API (empty when it was made by hand in the portal).</summary>
    public string WebhookId { get; set; } = "";
}

/// <summary>
/// Fourthwall options (webhooks.json). The Open API connection is optional: the shop's API user from Settings → For
/// developers → Open API (only a SUPER ADMIN can make one), sent as HTTP Basic auth. With it Cuebot reads the shop, the
/// membership tiers and members, the products for the showcase widget, catches up on events no delivery brought, can set
/// up the webhook, mark the shop live while you stream and run gift draws from Twitch chat. What it learnt is kept in
/// fourthwall.json.
/// </summary>
public sealed class FourthwallOptions
{
    /// <summary>
    /// Fourthwall's alerts guide suggests letting creators hide supporter names on stream: when on, %user% (feed,
    /// speech, alerts) is "A supporter" for every order, donation, gift, membership and thank-you; %fw.username% keeps the
    /// real name for private use.
    /// </summary>
    public bool HideSupporterNames { get; set; }

    /// <summary>
    /// The shop's address (like myshop.fourthwall.com or shop.example.com), for gift claim links
    /// (https://&lt;shop&gt;/gifts/&lt;giftId&gt;). Learnt from the API when connected.
    /// </summary>
    public string ShopAddress { get; set; } = "";

    /// <summary>The Open API user name (Settings → For developers → Open API). A secret.</summary>
    [Secret]
    public string ApiUser { get; set; } = "";

    /// <summary>The Open API user's password. A secret.</summary>
    [Secret]
    public string ApiPassword { get; set; } = "";

    /// <summary>Tell Fourthwall the shop is streaming on Twitch while the Twitch stream is live (needed for stream gifts).</summary>
    public bool MarkStreamingLive { get; set; }

    /// <summary>
    /// Run Fourthwall gift draws in Cuebot: when a draw starts, viewers type <see cref="EntryCommand"/> in Twitch chat
    /// during the draw and Cuebot hands Fourthwall the entrants when it ends. Needs the gifting platform set to Open API.
    /// </summary>
    public bool RunGiftDraws { get; set; }

    /// <summary>What viewers type to enter a gift draw.</summary>
    public string EntryCommand { get; set; } = "!enter";

    /// <summary>
    /// With the API connected, every this many minutes Cuebot asks Fourthwall which events it sent and replays those that
    /// never arrived (relay down, wrong secret), marked isReplay. 0 = off.
    /// </summary>
    public int CatchUpMinutes { get; set; } = 10;

    /// <summary>The collection (slug) the Products widget shows; empty = every public product.</summary>
    public string ProductsCollection { get; set; } = "";

    /// <summary>The webhook configuration Cuebot created or repaired through the API.</summary>
    public string WebhookId { get; set; } = "";
}

/// <summary>
/// Shopify options (webhooks.json). Privacy first: buyers are shown by first name, their order note is never read out
/// unless asked for, and contact details (email, phone, addresses, IP) never reach actions, scripts or WebSocket clients.
/// The Admin API connection is optional: a Dev Dashboard app installed on your own store (shop domain, client id, client
/// secret: client credentials grant, 24-hour tokens Cuebot renews), or an Admin API access token an older custom app
/// already has. Sales, goal and stock Cuebot learnt are kept in shopify.json.
/// </summary>
public sealed class ShopifyOptions
{
    public const string FirstName = "First name";
    public const string FullName = "Full name";
    public const string Someone = "Someone";

    public const string NoMessage = "None";
    public const string CartAttribute = "Cart attribute";
    public const string OrderNote = "Order note";

    /// <summary>How the buyer is named in %user% (feed, speech, alerts): <see cref="FirstName"/> (default), <see cref="FullName"/> or <see cref="Someone"/>.</summary>
    public string NameOnStream { get; set; } = FirstName;

    /// <summary>
    /// Where %message% comes from. <see cref="NoMessage"/> (default): never, since the order note is the checkout's
    /// delivery instructions (gate codes, phone numbers). <see cref="CartAttribute"/>: the cart attribute named
    /// <see cref="MessageAttribute"/>, which a store can add as a "message for the stream" field.
    /// <see cref="OrderNote"/>: the order note (only if your checkout asks for a stream message there).
    /// </summary>
    public string MessageSource { get; set; } = NoMessage;

    /// <summary>The cart attribute (order <c>note_attributes</c> name) read as the stream message.</summary>
    public string MessageAttribute { get; set; } = "Stream message";

    /// <summary>
    /// On (default): email, phone, addresses, IP, browser details and order-status links are removed from the
    /// shopify.* arguments, %_json% and what the Streamer.bot-compatible WebSocket server forwards.
    /// </summary>
    public bool RedactContactDetails { get; set; } = true;

    /// <summary>Start the merch counter again when a stream goes live.</summary>
    public bool ResetSalesOnStreamStart { get; set; } = true;

    /// <summary>Low Stock runs when a variant's stock falls to this many or fewer (0 = only Sold Out).</summary>
    public int LowStockThreshold { get; set; } = 5;

    public ShopifyGoalSettings Goal { get; set; } = new();

    /// <summary>The store's myshopify.com address (like cue-shop.myshopify.com), for the Admin API.</summary>
    public string ShopDomain { get; set; } = "";

    /// <summary>The Dev Dashboard app's client id.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The Dev Dashboard app's client secret. A secret.</summary>
    [Secret]
    public string ClientSecret { get; set; } = "";

    /// <summary>Instead of a client id and secret: an Admin API access token (shpat_…) an existing custom app has. A secret.</summary>
    [Secret]
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// With the API connected, every this many minutes Cuebot asks Shopify for paid orders no delivery brought (relay
    /// down, Cuebot closed) and runs Order Paid for them, marked isReplay. 0 = off.
    /// </summary>
    public int CatchUpMinutes { get; set; } = 10;
}

/// <summary>The merch goal Cuebot keeps from Shopify's paid orders (this stream's counter).</summary>
public sealed class ShopifyGoalSettings
{
    public const string Items = "Items sold";
    public const string Revenue = "Revenue";

    public string Title { get; set; } = "Merch goal";

    /// <summary>0 = no goal.</summary>
    public double Target { get; set; }

    /// <summary><see cref="Items"/> (default) or <see cref="Revenue"/>.</summary>
    public string Measure { get; set; } = Items;
}
