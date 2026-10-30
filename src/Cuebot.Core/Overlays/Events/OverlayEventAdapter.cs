using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;

namespace Cuebot.Core.Overlays.Events;

/// <summary>An alert for overlay widgets, in StreamElements' shape: a listener such as "follower-latest" and its event.</summary>
/// <param name="Count">
/// How many the alert adds to session counts: 1 for most; for a gift bundle whose recipients arrive as their own alerts,
/// 0 (they are counted one by one); for a bundle without recipient alerts, the number of gifts.
/// </param>
public sealed record OverlayAlert(string Listener, JsonObject Event, int Count = 1);

/// <summary>A chat badge as StreamElements widgets read it (<c>badges[]</c> of a "message" event).</summary>
public sealed record OverlayBadge(string Type, string Version, string Url, string Description);

/// <summary>
/// Implemented by each platform module: turns that module's events into StreamElements-shaped alerts and its chat
/// badges into images, so overlay widgets made for StreamElements work with every platform.
/// </summary>
public interface IOverlayEventAdapter
{
    /// <summary>The platform as chat messages name it ("Twitch", "Kick", ...).</summary>
    string Platform { get; }

    /// <summary>The platform as StreamElements' <c>service</c> field names it ("twitch", "kick", ...).</summary>
    string Service { get; }

    /// <summary>The alert for one of this module's events, or null for events that are not alerts.</summary>
    OverlayAlert? ToAlert(BotEvent e);

    /// <summary>Badge images for a chat message from this platform, from the platform event's arguments.</summary>
    IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs);
}

/// <summary>Builds StreamElements alert events (the fields of <c>*-latest</c> listeners) from Cuebot event arguments.</summary>
public static class OverlayAlerts
{
    public const string Follower = "follower-latest";
    public const string Subscriber = "subscriber-latest";
    public const string Tip = "tip-latest";
    public const string Cheer = "cheer-latest";
    public const string Raid = "raid-latest";
    public const string Host = "host-latest";
    public const string SuperChat = "superchat-latest";
    public const string Kicks = "kicks-latest";
    public const string CharityDonation = "charityCampaignDonation-latest";

    /// <summary>Cuebot listeners for alerts StreamElements has no listener for (channel points, YouTube Jewels).</summary>
    public const string Redemption = "redemption-latest";
    public const string Jewels = "jewels-latest";

    /// <summary>
    /// <c>{name, displayName, amount, message, type, providerId, avatar, _id}</c> with the event's user, plus any
    /// <paramref name="extra"/> fields. <paramref name="type"/> is StreamElements' event type ("follower", "subscriber",
    /// "tip", "cheer", "raid", ...).
    /// </summary>
    public static JsonObject Event(BotEvent e, string type, object? amount = null, string? name = null, string? message = null,
        IEnumerable<KeyValuePair<string, JsonNode?>>? extra = null)
    {
        var a = e.Args;
        var displayName = name ?? Str(a, "user");
        var data = new JsonObject
        {
            ["name"] = displayName,
            ["displayName"] = displayName,
            ["amount"] = ToNode(amount ?? 1),
            ["message"] = message ?? Str(a, "message"),
            ["type"] = type,
            ["providerId"] = name is null ? Str(a, "userId") : "",
            ["avatar"] = name is null ? First(a, "userProfileUrl", "profileImageUrl", "userProfilePicture") : "",
            ["_id"] = Guid.NewGuid().ToString("N"),
            ["createdAt"] = e.Time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra) data[key] = value;
        }
        return data;
    }

    public static string Str(IReadOnlyDictionary<string, object?> a, string key) =>
        a.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public static string First(IReadOnlyDictionary<string, object?> a, params string[] keys) =>
        keys.Select(k => Str(a, k)).FirstOrDefault(v => v.Length > 0) ?? "";

    public static bool Bool(IReadOnlyDictionary<string, object?> a, string key) =>
        a.TryGetValue(key, out var v) && v switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var p) && p,
            _ => false,
        };

    public static double Number(IReadOnlyDictionary<string, object?> a, string key) =>
        a.TryGetValue(key, out var v) && v is not null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>A whole number stays an integer in JSON (5, not 5.0); anything else as given.</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        double d when d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue => JsonValue.Create((int)d),
        double d when d == Math.Floor(d) && Math.Abs(d) < long.MaxValue => JsonValue.Create((long)d),
        long l when l is >= int.MinValue and <= int.MaxValue => JsonValue.Create((int)l),
        double d => JsonValue.Create(d),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        decimal m => JsonValue.Create(m),
        bool b => JsonValue.Create(b),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>A JSON number as a double, whatever type it was stored as (null and non-numbers give <paramref name="fallback"/>).</summary>
    public static double NumberOf(JsonNode? node, double fallback = 0) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    /// <summary>StreamElements' tier: "1000", "2000", "3000" or "prime" (from "1", "2", "3", "Prime", "1000"...).</summary>
    public static string Tier(string tier) => tier.Trim().ToLowerInvariant() switch
    {
        "prime" => "prime",
        "2" or "2000" => "2000",
        "3" or "3000" => "3000",
        _ => "1000",
    };
}

/// <summary>
/// Badge icons Cuebot ships for platforms that report badges by name only (Kick, Rumble, YouTube), served by the
/// overlay server at <c>/runtime/badge-{name}.svg</c>.
/// </summary>
public static class BuiltInBadges
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "broadcaster", "moderator", "vip", "subscriber", "founder", "og", "verified", "staff", "gifter", "premium",
    };

    /// <summary>The badge for a built-in icon, or null when there is no icon by that name.</summary>
    public static OverlayBadge? Get(string name, string description, string version = "1") =>
        Names.Contains(name) ? new OverlayBadge(name, version, $"/runtime/badge-{name}.svg", description) : null;
}
