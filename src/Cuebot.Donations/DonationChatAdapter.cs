using System.Globalization;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Donations;

/// <summary>
/// Shows Streamlabs / StreamElements events in the activity feed. Tips become <see cref="FeedKind.Tip"/> items with
/// a formatted amount and the donor's message, which is also what event speech reads ("chat.activity").
/// </summary>
internal abstract class DonationChatAdapter(string platform, string prefix) : IChatEventAdapter
{
    public string Platform => platform;

    public ChatMessage? ToChatMessage(BotEvent e) => null;

    public FeedItem? ToFeedItem(BotEvent e) =>
        e.Type.StartsWith(prefix, StringComparison.Ordinal) ? Describe(e) : null;

    /// <summary>The feed item for a donations event, or null for connection events and unknown types.</summary>
    internal static FeedItem? Describe(BotEvent e)
    {
        var a = e.Args;
        var service = Get(a, "service");
        var user = Get(a, "user");
        var message = Get(a, "message");
        var amount = Get(a, "formattedAmount");
        FeedItem Item(FeedKind kind, string title, string? amountText = null) => new(service, kind, user, Test(a) + title, e.Time)
        {
            Detail = message.Length > 0 ? message : null,
            Amount = amountText is { Length: > 0 } ? amountText : null,
        };

        return e.Type switch
        {
            StreamlabsEventTypes.Donation => Item(FeedKind.Tip, $"{user} donated {amount}", amount),
            StreamElementsEventTypes.Tip => Item(FeedKind.Tip, $"{user} tipped {amount}", amount),
            StreamlabsEventTypes.CharityDonation or StreamElementsEventTypes.CharityDonation =>
                Item(FeedKind.Tip, $"{user} donated {amount} to charity", amount),
            StreamlabsEventTypes.Merch or StreamElementsEventTypes.Merch =>
                Item(FeedKind.Other, $"{user} bought {Get(a, "product")}", amount),
            StreamlabsEventTypes.LoyaltyStoreRedemption or StreamElementsEventTypes.Redemption =>
                Item(FeedKind.Redemption, $"{user} redeemed {First(a, "product", "item")}"),
            StreamlabsEventTypes.PrimeSubGift => Item(FeedKind.GiftSubscription, $"{user} gifted a Prime subscription"),
            StreamlabsEventTypes.Follow or StreamElementsEventTypes.Follow => Item(FeedKind.Follow, $"{user} followed"),
            StreamlabsEventTypes.Subscription or StreamElementsEventTypes.Subscriber or StreamElementsEventTypes.Sponsor =>
                Item(FeedKind.Subscription, $"{user} subscribed"),
            StreamElementsEventTypes.CommunityGift => Item(FeedKind.GiftSubscription, $"{user} gifted {Get(a, "gift")} subs"),
            StreamlabsEventTypes.Bits or StreamElementsEventTypes.Cheer => Item(FeedKind.Tip, $"{user} cheered {Get(a, "bits")} bits", amount),
            StreamlabsEventTypes.Raid or StreamElementsEventTypes.Raid => Item(FeedKind.Raid, $"{user} raided with {Get(a, "viewers")} viewers"),
            StreamlabsEventTypes.Host or StreamElementsEventTypes.Host => Item(FeedKind.Raid, $"{user} hosted with {Get(a, "viewers")} viewers"),
            StreamlabsEventTypes.Superchat or StreamElementsEventTypes.Superchat => Item(FeedKind.Tip, $"{user} sent a {amount} Super Chat", amount),
            StreamElementsEventTypes.Activity => Item(FeedKind.Other, $"{user}: {Get(a, "activityType")}"),
            _ => null,
        };
    }

    private static string Test(IReadOnlyDictionary<string, object?> a) =>
        a.TryGetValue("isTest", out var t) && t is true ? "[Test] " : "";

    private static string Get(IReadOnlyDictionary<string, object?> a, string key) =>
        a.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";

    private static string First(IReadOnlyDictionary<string, object?> a, params string[] keys) =>
        keys.Select(k => Get(a, k)).FirstOrDefault(v => v.Length > 0) ?? "";
}

internal sealed class StreamlabsChatAdapter() : DonationChatAdapter(Streamlabs.StreamlabsEventMapper.Service, "streamlabs.");

internal sealed class StreamElementsChatAdapter() : DonationChatAdapter(StreamElements.StreamElementsEventMapper.Service, "streamelements.");
