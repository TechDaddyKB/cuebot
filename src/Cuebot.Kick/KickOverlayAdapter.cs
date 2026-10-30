using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Kick;

/// <summary>Kick events as StreamElements overlay alerts; Kick chat badges as Cuebot's built-in icons.</summary>
internal sealed class KickOverlayAdapter : IOverlayEventAdapter
{
    /// <summary>Kick badge types and the built-in icon each one gets.</summary>
    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["broadcaster"] = "broadcaster", ["moderator"] = "moderator", ["vip"] = "vip", ["og"] = "og", ["founder"] = "founder",
        ["subscriber"] = "subscriber", ["verified"] = "verified", ["staff"] = "staff", ["sub_gifter"] = "gifter",
    };

    public string Platform => KickChatAdapter.PlatformName;
    public string Service => "kick";

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs)
    {
        if (platformArgs.GetValueOrDefault("badge") is not System.Collections.IEnumerable badges || badges is string) return [];
        var list = new List<OverlayBadge>();
        foreach (var badge in badges.OfType<IReadOnlyDictionary<string, object?>>())
        {
            var type = Str(badge, "id");
            if (!Icons.TryGetValue(type, out var icon)) continue;
            var version = Number(badge, "info") is > 0 and var count ? ((long)count).ToString(System.Globalization.CultureInfo.InvariantCulture) : "1";
            var name = Str(badge, "name");
            if (BuiltInBadges.Get(icon, name.Length > 0 ? name : type, version) is { } image) list.Add(image with { Type = type.ToLowerInvariant() });
        }
        return list;
    }

    public OverlayAlert? ToAlert(BotEvent e)
    {
        var a = e.Args;
        return e.Type switch
        {
            KickEventTypes.Follow => new(Follower, Event(e, "follower")),
            KickEventTypes.Subscription => new(Subscriber, Event(e, "subscriber", 1, extra: [new("tier", "1000"), new("gifted", false)])),
            KickEventTypes.Resubscription => new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "months")),
                extra: [new("tier", "1000"), new("gifted", false)])),
            KickEventTypes.GiftSubscription => new(Subscriber, Event(e, "subscriber", 1, name: Str(a, "recipient"), extra:
            [
                new("tier", "1000"),
                new("gifted", true),
                new("sender", Str(a, "user")),
                new("isCommunityGift", Bool(a, "fromMassGift")),
                new("providerId", Str(a, "recipientId")),
            ])),
            // The recipients arrive as giftSubscription alerts and are counted there.
            KickEventTypes.MassGiftSubscription => new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "gifts")), extra:
            [
                new("tier", "1000"),
                new("gifted", false),
                new("bulkGifted", true),
                new("sender", Str(a, "user")),
            ]), Count: 0),
            KickEventTypes.KicksGifted => new(Kicks, Event(e, "kicks", Number(a, "kicks"), extra: [new("giftName", Str(a, "giftName"))])),
            KickEventTypes.Host => new(Raid, Event(e, "raid", Number(a, "viewers"))),
            KickEventTypes.RewardRedemption => new(Redemption, Event(e, "channelPointsRedemption", Number(a, "rewardCost"),
                message: Str(a, "userInput"), extra: [new("redemption", Str(a, "rewardTitle")), new("rewardId", Str(a, "rewardId"))])),
            _ => null,
        };
    }
}
