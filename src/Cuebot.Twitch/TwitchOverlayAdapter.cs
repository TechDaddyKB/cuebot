using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Twitch;

/// <summary>
/// Twitch events as StreamElements overlay alerts, and Twitch chat badges as images (Twitch's own, loaded when the
/// broadcaster connects; Cuebot's built-in icons until then).
/// </summary>
internal sealed class TwitchOverlayAdapter(Func<IReadOnlyDictionary<string, (string Url, string Title)>> badges) : IOverlayEventAdapter
{
    /// <summary>Twitch badge sets with a built-in stand-in icon.</summary>
    private static readonly Dictionary<string, string> Fallbacks = new(StringComparer.Ordinal)
    {
        ["broadcaster"] = "broadcaster", ["moderator"] = "moderator", ["lead_moderator"] = "moderator", ["vip"] = "vip",
        ["subscriber"] = "subscriber", ["founder"] = "founder", ["staff"] = "staff", ["partner"] = "verified",
        ["sub-gifter"] = "gifter",
    };

    public TwitchOverlayAdapter(TwitchService service) : this(() => service.ChatBadges)
    {
    }

    public string Platform => TwitchChatAdapter.PlatformName;
    public string Service => "twitch";

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs)
    {
        var known = badges();
        var list = new List<OverlayBadge>();
        foreach (var entry in Str(platformArgs, "badges").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = entry.IndexOf('/');
            var (set, version) = slash < 0 ? (entry, "1") : (entry[..slash], entry[(slash + 1)..]);
            if (known.TryGetValue($"{set}/{version}", out var image)) list.Add(new OverlayBadge(set, version, image.Url, image.Title));
            else if (Fallbacks.TryGetValue(set, out var icon) && BuiltInBadges.Get(icon, set, version) is { } fallback)
                list.Add(fallback with { Type = set });
        }
        return list;
    }

    public OverlayAlert? ToAlert(BotEvent e)
    {
        var a = e.Args;
        switch (e.Type)
        {
            case TwitchEventTypes.Follow:
                return new(Follower, Event(e, "follower"));
            case TwitchEventTypes.Sub when !Bool(a, "isGift"):   // gifted subs arrive as subGift with the gifter
                return new(Subscriber, Event(e, "subscriber", 1, extra: Sub(a)));
            case TwitchEventTypes.Resub:
                return new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "cumulativeMonths")), extra: Sub(a)));
            case TwitchEventTypes.PrimePaidUpgrade or TwitchEventTypes.GiftPaidUpgrade:
                return new(Subscriber, Event(e, "subscriber", 1, extra: Sub(a)));
            case TwitchEventTypes.SubGift:
                return new(Subscriber, Event(e, "subscriber", 1, name: Str(a, "recipientUser"), extra:
                [
                    .. Sub(a),
                    new("gifted", true),
                    new("sender", Str(a, "user")),
                    new("isCommunityGift", Number(a, "subBombCount") > 0),
                    new("providerId", Str(a, "recipientUserId")),
                ]));
            case TwitchEventTypes.GiftBomb:
                return new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "total")), extra:
                [
                    .. Sub(a),
                    new("bulkGifted", true),
                    new("sender", Str(a, "user")),
                ]));
            case TwitchEventTypes.Cheer:
                return new(Cheer, Event(e, "cheer", Number(a, "bits")));
            case TwitchEventTypes.Raid:
                return new(Raid, Event(e, "raid", Number(a, "viewers")));
            case TwitchEventTypes.RewardRedemption:
                return new(Redemption, Event(e, "channelPointsRedemption", Number(a, "rewardCost"), message: Str(a, "userInput"), extra:
                [
                    new("redemption", Str(a, "rewardTitle")),
                    new("rewardId", Str(a, "rewardId")),
                ]));
            default:
                return null;
        }
    }

    private static KeyValuePair<string, JsonNode?>[] Sub(IReadOnlyDictionary<string, object?> a) =>
    [
        new("tier", Tier(Bool(a, "isPrime") ? "prime" : Str(a, "tier"))),
        new("gifted", false),
    ];
}
