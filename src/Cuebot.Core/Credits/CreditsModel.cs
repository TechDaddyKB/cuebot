using System.Text.Json;
using System.Text.Json.Serialization;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Credits;

/// <summary>
/// Credits for an overlay (end-of-stream roll). Serialized with <see cref="CreditsJson.Serialize"/> (camelCase):
/// <code>
/// {
///   "version": 1,
///   "isTest": false,
///   "startedAt": "2026-10-27T18:00:00+00:00",      // first activity since the last reset
///   "updatedAt": "2026-10-27T20:14:09+00:00",
///   "sections": [
///     { "id": "follows", "title": "Followers",
///       "entries": [ { "user": "Alice", "platform": "twitch", "count": 1, "amount": 0, "detail": "" } ] },
///     { "id": "cheers", "title": "Cheers",
///       "entries": [ { "user": "Bob", "platform": "twitch", "count": 2, "amount": 600, "detail": "600 bits" } ] }
///   ]
/// }
/// </code>
/// Sections appear in a fixed order (<see cref="CreditsSections.Order"/>) followed by custom sections, and only when
/// they have entries. Each user appears once per section (matched by platform + name, case-insensitive): count is how
/// often they did it, amount the sum (bits, money, raid viewers, gifted subs), detail the latest text (e.g. "$5.00",
/// the reward redeemed). Entries of cheers/tips/raids/gifts are sorted by amount (highest first), others by first
/// appearance. Custom sections (from "Add custom credit") use the id given there.
/// </summary>
public sealed class CreditsSnapshot
{
    public int Version { get; set; } = 1;
    public bool IsTest { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<CreditsSection> Sections { get; set; } = [];

    public CreditsSection? Section(string id) =>
        Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public int TotalEntries => Sections.Sum(s => s.Entries.Count);
}

public sealed class CreditsSection
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<CreditsEntry> Entries { get; set; } = [];
}

public sealed class CreditsEntry
{
    public string User { get; set; } = "";
    public string Platform { get; set; } = "";
    public int Count { get; set; } = 1;
    public double Amount { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>Section ids (stable, used by overlays and as argument suffixes: follows → %creditsFollows%).</summary>
public static class CreditsSections
{
    public const string Follows = "follows";
    public const string Subscriptions = "subscriptions";
    public const string GiftSubscriptions = "giftSubscriptions";
    public const string Cheers = "cheers";
    public const string Tips = "tips";
    public const string Raids = "raids";
    public const string Redemptions = "redemptions";
    public const string Moderators = "moderators";
    public const string Vips = "vips";
    public const string Subscribers = "subscribers";
    public const string Chatters = "chatters";

    public static readonly string[] Order =
        [Follows, Subscriptions, GiftSubscriptions, Cheers, Tips, Raids, Redemptions, Moderators, Vips, Subscribers, Chatters];

    /// <summary>Sections ranked by amount rather than by first appearance.</summary>
    public static readonly IReadOnlySet<string> RankedByAmount =
        new HashSet<string>([GiftSubscriptions, Cheers, Tips, Raids], StringComparer.OrdinalIgnoreCase);

    public static string Title(string id) => id switch
    {
        Follows => "Followers",
        Subscriptions => "Subscriptions",
        GiftSubscriptions => "Gifted subscriptions",
        Cheers => "Cheers",
        Tips => "Tips & donations",
        Raids => "Raids",
        Redemptions => "Redemptions",
        Moderators => "Moderators",
        Vips => "VIPs",
        Subscribers => "Subscribers in chat",
        Chatters => "Chatters",
        _ => id,
    };
}

public sealed class CreditsSettings
{
    /// <summary>Start fresh credits when a platform reports the stream went live.</summary>
    public bool ResetOnStreamOnline { get; set; } = true;

    /// <summary>Reset when nothing was recorded for this many hours (0 = never).</summary>
    public int AutoResetHours { get; set; } = 12;

    /// <summary>Section ids not recorded (e.g. "chatters" on busy channels).</summary>
    public List<string> DisabledSections { get; set; } = [];

    /// <summary>User names never credited (bots, the broadcaster's own accounts).</summary>
    public List<string> IgnoredUsers { get; set; } = [];

    /// <summary>Leave the broadcaster out of moderators/VIPs/subscribers/chatters.</summary>
    public bool IgnoreBroadcaster { get; set; } = true;
}

/// <summary>credits.json: settings plus the current credits (so a restart mid-stream keeps them).</summary>
public sealed class CreditsFile
{
    public int Version { get; set; } = 1;
    public CreditsSettings Settings { get; set; } = new();
    public CreditsSnapshot Current { get; set; } = new();}

/// <summary>Read side used by the WebSocket/HTTP server (GetCredits, TestCredits, ClearCredits) and the page.</summary>
public interface ICreditsSource
{
    /// <summary>A copy of the current credits.</summary>
    CreditsSnapshot Snapshot();

    /// <summary>Clears the current credits.</summary>
    void Reset();

    /// <summary>Sample credits (isTest = true) for designing overlays; the real credits are not changed.</summary>
    CreditsSnapshot Test();

    /// <summary>Raised (on any thread) after the credits change.</summary>
    event Action? Changed;
}

public static class CreditsJson
{
    private static readonly JsonSerializerOptions Compact = new(Json.Options) { WriteIndented = false };

    public static string Serialize(CreditsSnapshot snapshot, bool indented = false) =>
        JsonSerializer.Serialize(snapshot, indented ? Json.Options : Compact);
}
