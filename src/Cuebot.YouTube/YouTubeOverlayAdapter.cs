using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.YouTube;

/// <summary>
/// YouTube events as StreamElements overlay alerts. Memberships use "subscriber-latest" (not StreamElements'
/// YouTube-only "sponsor-latest") so widgets made for Twitch subscribers show them; channel subscribers are follows.
/// YouTube reports roles, not badge images, so badges are Cuebot's built-in icons.
/// </summary>
internal sealed class YouTubeOverlayAdapter : IOverlayEventAdapter
{
    public string Platform => YouTubeChatAdapter.PlatformName;
    public string Service => "youtube";

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs)
    {
        var list = new List<OverlayBadge>();
        void Add(string flag, string icon, string type, string description)
        {
            if (Bool(platformArgs, flag) && BuiltInBadges.Get(icon, description) is { } badge) list.Add(badge with { Type = type });
        }
        Add("isBroadcaster", "broadcaster", "owner", "Owner");
        Add("isModerator", "moderator", "moderator", "Moderator");
        Add("isMember", "subscriber", "member", "Member");
        Add("isVerified", "verified", "verified", "Verified");
        return list;
    }

    public OverlayAlert? ToAlert(BotEvent e)
    {
        var a = e.Args;
        return e.Type switch
        {
            YouTubeEventTypes.NewSubscriber => new(Follower, Event(e, "follower")),
            YouTubeEventTypes.NewSponsor => new(Subscriber, Event(e, "subscriber", 1, message: "", extra: Member(a))),
            YouTubeEventTypes.MemberMilestone => new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "months")), extra: Member(a))),
            // The recipients arrive as giftMembershipReceived alerts and are counted there.
            YouTubeEventTypes.MembershipGift => new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "total")), message: "", extra:
            [
                .. Member(a),
                new("bulkGifted", true),
                new("sender", Str(a, "user")),
            ]), Count: 0),
            YouTubeEventTypes.GiftMembershipReceived => new(Subscriber, Event(e, "subscriber", 1, message: "", extra:
            [
                .. Member(a),
                new("gifted", true),
                new("sender", Str(a, "gifterName")),
                new("isCommunityGift", true),
            ])),
            YouTubeEventTypes.SuperChat or YouTubeEventTypes.SuperSticker => new(SuperChat, Event(e, "superchat", Number(a, "amount"), extra:
            [
                new("currency", Str(a, "currency")),
                new("formattedAmount", Str(a, "amountText")),
                new("sticker", e.Type == YouTubeEventTypes.SuperSticker),
            ])),
            YouTubeEventTypes.JewelsGifted => new(Jewels, Event(e, "jewels", Number(a, "amount"), extra: [new("giftName", Str(a, "giftName"))])),
            _ => null,
        };
    }

    private static KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>[] Member(IReadOnlyDictionary<string, object?> a) =>
    [
        new("tier", "1000"),
        new("gifted", false),
        new("level", Str(a, "levelName")),
    ];
}
