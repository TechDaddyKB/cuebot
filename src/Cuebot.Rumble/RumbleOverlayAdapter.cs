using Cuebot.Core.Events;
using Cuebot.Core.Overlays.Events;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Rumble;

/// <summary>Rumble events as StreamElements overlay alerts; Rumble chat badges as Cuebot's built-in icons.</summary>
internal sealed class RumbleOverlayAdapter : IOverlayEventAdapter
{
    /// <summary>Rumble badge slugs and the built-in icon (and StreamElements badge type) each one gets.</summary>
    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        [RumbleBadges.Admin] = "broadcaster", [RumbleBadges.Moderator] = "moderator", [RumbleBadges.Verified] = "verified",
        [RumbleBadges.Premium] = "premium", ["recurring_subscription"] = "subscriber", ["locals"] = "subscriber",
        ["locals_supporter"] = "subscriber",
    };

    public string Platform => RumbleChatAdapter.PlatformName;
    public string Service => "rumble";

    public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs)
    {
        var list = new List<OverlayBadge>();
        foreach (var slug in Str(platformArgs, "badges").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Icons.TryGetValue(slug, out var icon) && BuiltInBadges.Get(icon, slug) is { } badge && list.All(b => b.Type != icon))
                list.Add(badge);
        }
        return list;
    }

    public OverlayAlert? ToAlert(BotEvent e)
    {
        var a = e.Args;
        return e.Type switch
        {
            RumbleEventTypes.Follow => new(Follower, Event(e, "follower")),
            RumbleEventTypes.Subscribe => new(Subscriber, Event(e, "subscriber", 1, extra: [new("tier", "1000"), new("gifted", false)])),
            // Rumble reports gifted subs only as a bundle, so the bundle counts every gift.
            RumbleEventTypes.GiftSub => new(Subscriber, Event(e, "subscriber", Math.Max(1, Number(a, "total")), extra:
            [
                new("tier", "1000"),
                new("gifted", false),
                new("bulkGifted", true),
                new("sender", Str(a, "user")),
            ]), Count: (int)Math.Max(1, Number(a, "total"))),
            RumbleEventTypes.Rant => new(Tip, Event(e, "tip", Number(a, "amount"), extra:
                [new("currency", Str(a, "currency") is { Length: > 0 } c ? c : Money.Currency)])),
            _ => null,
        };
    }
}
