using Cuebot.Core.Chat;

namespace Cuebot.Rumble;

/// <summary>Maps Rumble chat badge slugs to Cuebot's platform-neutral chat roles.</summary>
internal static class RumbleBadges
{
    /// <summary>The channel owner's badge in their own chat.</summary>
    public const string Admin = "admin";
    public const string Moderator = "moderator";
    public const string Verified = "verified";

    /// <summary>Rumble Premium (a site-wide membership, not a subscription to this channel).</summary>
    public const string Premium = "premium";

    /// <summary>Badges of paying channel subscribers (Rumble subscriptions and Locals supporters).</summary>
    private static readonly string[] SubscriberBadges = ["recurring_subscription", "locals", "locals_supporter"];

    /// <param name="broadcaster">Username of the channel's owner; matched as well, since badges may be hidden.</param>
    public static ChatRoles Roles(IReadOnlyList<string> badges, string userName, string broadcaster)
    {
        var roles = ChatRoles.None;
        if (Has(badges, Admin) || (broadcaster.Length > 0 && string.Equals(userName, broadcaster, StringComparison.OrdinalIgnoreCase)))
            roles |= ChatRoles.Broadcaster;
        if (Has(badges, Moderator)) roles |= ChatRoles.Moderator;
        if (Has(badges, Verified)) roles |= ChatRoles.Verified;
        if (SubscriberBadges.Any(b => Has(badges, b))) roles |= ChatRoles.Subscriber;
        return roles;
    }

    public static bool Has(IReadOnlyList<string> badges, string badge) => badges.Contains(badge, StringComparer.OrdinalIgnoreCase);
}
