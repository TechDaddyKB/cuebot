namespace Cuebot.Webhooks;

/// <summary>Bot events published for Ko-fi webhooks.</summary>
public static class KofiEventTypes
{
    public const string Donation = "kofi.donation";
    public const string Subscription = "kofi.subscription";
    public const string Resubscription = "kofi.resubscription";
    public const string Commission = "kofi.commission";
    public const string ShopOrder = "kofi.shopOrder";
}

/// <summary>Bot events published for Patreon webhooks.</summary>
public static class PatreonEventTypes
{
    public const string FollowCreated = "patreon.followCreated";
    public const string FollowDeleted = "patreon.followDeleted";
    public const string PledgeCreated = "patreon.pledgeCreated";
    public const string PledgeUpdated = "patreon.pledgeUpdated";
    public const string PledgeDeleted = "patreon.pledgeDeleted";

    // Cuebot extras (Streamer.bot has no trigger for these Patreon events).
    public const string MemberUpdated = "patreon.memberUpdated";
    public const string PostPublished = "patreon.postPublished";
}

/// <summary>Bot events published for Shopify webhooks.</summary>
public static class ShopifyEventTypes
{
    public const string OrderCreated = "shopify.orderCreated";
    public const string OrderPaid = "shopify.orderPaid";
}

/// <summary>Bot events published for Fourthwall webhooks.</summary>
public static class FourthwallEventTypes
{
    public const string OrderPlaced = "fourthwall.orderPlaced";
    public const string OrderUpdated = "fourthwall.orderUpdated";
    public const string Donation = "fourthwall.donation";
    public const string GiftPurchase = "fourthwall.giftPurchase";
    public const string GiftDrawStarted = "fourthwall.giftDrawStarted";
    public const string GiftDrawEnded = "fourthwall.giftDrawEnded";
    public const string SubscriptionPurchased = "fourthwall.subscriptionPurchased";
    public const string SubscriptionChanged = "fourthwall.subscriptionChanged";
    public const string SubscriptionExpired = "fourthwall.subscriptionExpired";
    public const string NewsletterSubscribed = "fourthwall.newsletterSubscribed";
    public const string ThankYouSent = "fourthwall.thankYouSent";
    public const string ProductCreated = "fourthwall.productCreated";
    public const string ProductUpdated = "fourthwall.productUpdated";
}

/// <summary>Bot events published for Throne webhooks.</summary>
public static class ThroneEventTypes
{
    public const string GiftPurchased = "throne.giftPurchased";
    public const string ContributionPurchased = "throne.contributionPurchased";
    public const string GiftCrowdfunded = "throne.giftCrowdfunded";
}

/// <summary>Bot event published for user-defined (custom) webhooks.</summary>
public static class CustomWebhookEventTypes
{
    public const string Received = "webhook.custom";
}

/// <summary>A mapped webhook, ready to publish. It is a duplicate when any of <see cref="Keys"/> was seen before.</summary>
internal sealed record WebhookEvent(string Type, IReadOnlyList<string> Keys, Dictionary<string, object?> Args);

/// <summary>A line in the Webhooks page's "last events" list.</summary>
public sealed record WebhookRecord(DateTimeOffset Time, string Service, string Type, string Summary, bool IsTest);

/// <summary>The last delivery a service received (shown on the page; never contains secrets).</summary>
public sealed record WebhookDeliveryInfo(DateTimeOffset Time, Core.Webhooks.WebhookOutcome Outcome, string Detail);
