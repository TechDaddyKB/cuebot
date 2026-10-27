using System.Globalization;
using System.Text.RegularExpressions;

namespace Cuebot.Kick.Events;

/// <summary>
/// A decoded Kick event, independent of how it arrived (real-time websocket, webhook relay or API poll).
/// <see cref="DedupeKey"/> identifies the same occurrence across sources and duplicate deliveries.
/// </summary>
public sealed record KickEvent(string Type, IReadOnlyDictionary<string, object?> Args, string DedupeKey)
{
    /// <summary>Where it came from: "realtime", "webhook" or "api".</summary>
    public string Source => Args.TryGetValue("source", out var s) ? s as string ?? "" : "";
}

/// <summary>A badge shown next to a chatter (type as Kick names it: broadcaster, moderator, vip, og, founder, subscriber, ...).</summary>
internal sealed record KickBadge(string Type, string Text, int Count);

/// <summary>Who sent something; ids are 0 when Kick did not include them.</summary>
internal sealed record KickActor(long Id, string UserName, string Slug = "", string Color = "", IReadOnlyList<KickBadge>? Badges = null,
    bool IsVerified = false, bool IsAnonymous = false, string ProfilePicture = "");

/// <summary>Builds argument dictionaries with the module's naming conventions.</summary>
internal static partial class KickArgs
{
    public const string Platform = "Kick";

    public static Dictionary<string, object?> New(string source) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["platform"] = Platform,
        ["source"] = source,
    };

    /// <summary>Adds user, userName, userId, displayName (and optional extras) for the main person of an event.</summary>
    public static Dictionary<string, object?> WithUser(this Dictionary<string, object?> args, KickActor actor, string prefix = "")
    {
        var name = actor.IsAnonymous || actor.UserName.Length == 0 ? (actor.IsAnonymous ? "Anonymous" : "") : actor.UserName;
        if (prefix.Length == 0)
        {
            args["user"] = name;
            args["userName"] = name;
            args["displayName"] = name;
            args["userId"] = actor.Id > 0 ? Id(actor.Id) : "";
            if (actor.Slug.Length > 0) args["userSlug"] = actor.Slug;
            if (actor.ProfilePicture.Length > 0) args["userProfilePicture"] = actor.ProfilePicture;
            args["isAnonymous"] = actor.IsAnonymous;
        }
        else
        {
            args[prefix] = name;
            args[prefix + "UserName"] = name;
            args[prefix + "Id"] = actor.Id > 0 ? Id(actor.Id) : "";
        }
        return args;
    }

    /// <summary>Role flags and badge details of a chatter.</summary>
    public static Dictionary<string, object?> WithBadges(this Dictionary<string, object?> args, KickActor actor, long broadcasterUserId)
    {
        var badges = actor.Badges ?? [];
        bool Has(string type) => badges.Any(b => string.Equals(b.Type, type, StringComparison.OrdinalIgnoreCase));
        var sub = badges.FirstOrDefault(b => string.Equals(b.Type, "subscriber", StringComparison.OrdinalIgnoreCase));
        args["isBroadcaster"] = Has("broadcaster") || (broadcasterUserId > 0 && actor.Id == broadcasterUserId);
        args["isModerator"] = Has("moderator");
        args["isVip"] = Has("vip");
        args["isOg"] = Has("og");
        args["isFounder"] = Has("founder");
        args["isSubscriber"] = sub is not null || Has("founder");
        args["isVerified"] = actor.IsVerified || Has("verified");
        args["isStaff"] = Has("staff");
        args["subscribedMonths"] = sub?.Count ?? 0;
        args["badges"] = string.Join(", ", badges.Select(b => b.Type));
        args["color"] = actor.Color;
        return args;
    }

    /// <summary>Message text with Kick's emote tokens ("[emote:37226:KEKW]") replaced by the emote name.</summary>
    public static string PlainText(string content) => EmoteToken().Replace(content, m => m.Groups["name"].Value);

    public static int EmoteCount(string content) => EmoteToken().Count(content);

    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    public static string Lower(string s) => s.ToLowerInvariant();

    [GeneratedRegex(@"\[emote:(?<id>\d+):(?<name>[^\]]*)\]")]
    private static partial Regex EmoteToken();
}
