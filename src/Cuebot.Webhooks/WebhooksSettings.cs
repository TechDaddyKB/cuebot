namespace Cuebot.Webhooks;

/// <summary>
/// Persisted webhook service configuration (webhooks.json). Secrets are only used to verify deliveries locally:
/// they never leave the PC, are masked in the UI and never written to the log.
/// </summary>
public sealed class WebhooksSettings
{
    /// <summary>Ko-fi: the Verification Token from ko-fi.com → Settings → API → Webhooks → Advanced.</summary>
    public WebhookServiceSettings Kofi { get; set; } = new();

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
    public string IftttKey { get; set; } = "";
}

public sealed class WebhookServiceSettings
{
    /// <summary>Deliveries are verified but ignored (no events) while switched off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The signing secret / verification token. A secret.</summary>
    public string Secret { get; set; } = "";
}

public sealed class CustomWebhookSettings
{
    /// <summary>Route name (letters, digits, dashes, underscores), matched case-insensitively.</summary>
    public string Name { get; set; } = "";

    /// <summary>Sent as <c>?token=</c> or the <c>X-Cuebot-Token</c> header. A secret.</summary>
    public string Token { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
