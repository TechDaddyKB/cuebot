namespace Cuebot.Donations;

/// <summary>Bot event types published by the Streamlabs connection.</summary>
public static class StreamlabsEventTypes
{
    public const string Connected = "streamlabs.connected";
    public const string Disconnected = "streamlabs.disconnected";
    public const string Donation = "streamlabs.donation";
    public const string CharityDonation = "streamlabs.charityDonation";
    public const string Merch = "streamlabs.merch";
    public const string LoyaltyStoreRedemption = "streamlabs.loyaltyStoreRedemption";
    public const string PrimeSubGift = "streamlabs.primeSubGift";

    // Relayed platform events (published only with RelayPlatformEvents).
    public const string Follow = "streamlabs.follow";
    public const string Subscription = "streamlabs.subscription";
    public const string Bits = "streamlabs.bits";
    public const string Raid = "streamlabs.raid";
    public const string Host = "streamlabs.host";
    public const string Superchat = "streamlabs.superchat";
}

/// <summary>Bot event types published by the StreamElements connection.</summary>
public static class StreamElementsEventTypes
{
    public const string Connected = "streamelements.connected";
    public const string Disconnected = "streamelements.disconnected";
    public const string Tip = "streamelements.tip";
    public const string Merch = "streamelements.merch";
    public const string Redemption = "streamelements.redemption";

    // Relayed platform activities (published only with RelayPlatformEvents).
    public const string Follow = "streamelements.follow";
    public const string Subscriber = "streamelements.subscriber";
    public const string Cheer = "streamelements.cheer";
    public const string Raid = "streamelements.raid";
    public const string Host = "streamelements.host";
    public const string Superchat = "streamelements.superchat";
    public const string Sponsor = "streamelements.sponsor";
    public const string CommunityGift = "streamelements.communityGift";
    public const string CharityDonation = "streamelements.charityDonation";

    /// <summary>Any other relayed activity type; <c>activityType</c> says which.</summary>
    public const string Activity = "streamelements.activity";
}

/// <summary>Bot event types published by the TipeeeStream connection.</summary>
public static class TipeeeStreamEventTypes
{
    public const string Connected = "tipeeestream.connected";
    public const string Disconnected = "tipeeestream.disconnected";
    public const string Donation = "tipeeestream.donation";

    // Platform events TipeeeStream relays (published only with RelayPlatformEvents).
    public const string Follow = "tipeeestream.follow";
    public const string Subscription = "tipeeestream.subscription";
    public const string Cheer = "tipeeestream.cheer";
    public const string Raid = "tipeeestream.raid";
    public const string Host = "tipeeestream.host";
}

/// <summary>Bot event types published by the DonorDrive (Extra Life, ...) poller.</summary>
public static class DonorDriveEventTypes
{
    public const string Connected = "donordrive.connected";
    public const string Disconnected = "donordrive.disconnected";
    public const string Donation = "donordrive.donation";
    public const string Incentive = "donordrive.incentive";
    public const string ProfileUpdated = "donordrive.profileUpdated";
}

/// <summary>Bot event types published by the TreatStream connection.</summary>
public static class TreatStreamEventTypes
{
    public const string Authenticated = "treatstream.authenticated";
    public const string Connected = "treatstream.connected";
    public const string Disconnected = "treatstream.disconnected";
    public const string Treat = "treatstream.treat";
}

/// <summary>Bot event types published by the Pally.gg connection.</summary>
public static class PallyEventTypes
{
    public const string Connected = "pally.connected";
    public const string Disconnected = "pally.disconnected";
    public const string CampaignTip = "pally.campaignTip";
}

/// <summary>A mapped service event, ready to publish. It is a duplicate when any of <see cref="Keys"/> was seen before.</summary>
internal sealed record DonationEvent(string Type, IReadOnlyList<string> Keys, Dictionary<string, object?> Args, bool Relayed = false);

/// <summary>A line in the page's "last events" list.</summary>
public sealed record DonationRecord(DateTimeOffset Time, string Type, string Summary, bool IsTest);
