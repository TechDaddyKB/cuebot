using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Core.Overlays.Events;

/// <summary>
/// Sample events for trying widgets (the widget editor's Emulate buttons): a chat message or an alert as it would come
/// from Twitch, Kick, Rumble, YouTube or a donation service, in the platform modules' own event names and arguments,
/// so they go through the same conversion as real ones.
/// </summary>
public static class OverlayEmulator
{
    public static readonly IReadOnlyList<string> Platforms = ["twitch", "kick", "rumble", "youtube", "donations"];

    /// <summary>What each platform can emulate.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Kinds = new Dictionary<string, IReadOnlyList<string>>
    {
        ["twitch"] = ["chat", "follow", "sub", "resub", "gift", "cheer", "raid"],
        ["kick"] = ["chat", "follow", "sub", "resub", "gift", "kicks", "raid"],
        ["rumble"] = ["chat", "follow", "sub", "gift", "tip"],
        ["youtube"] = ["chat", "follow", "sub", "resub", "gift", "superchat"],
        ["donations"] = ["tip"],
    };

    private static readonly string[] Names = ["PixelPanda", "NightOwl", "ChillVibes", "GGWarrior", "StarDust", "LazyLlama", "MoonWalker", "CozyCat"];

    /// <summary>A sample event, or null when the platform cannot do that (e.g. Rumble Kicks).</summary>
    public static BotEvent? Create(string platform, string kind, Random? random = null)
    {
        platform = platform.ToLowerInvariant();
        if (!Kinds.TryGetValue(platform, out var kinds) || !kinds.Contains(kind)) return null;
        random ??= Random.Shared;
        var user = Names[random.Next(Names.Length)];
        if (kind == "chat") return Chat(platform, user);

        var (type, args) = (platform, kind) switch
        {
            ("twitch", "follow") => ("twitch.follow", Args()),
            ("twitch", "sub") => ("twitch.sub", Args(("tier", "1"))),
            ("twitch", "resub") => ("twitch.resub", Args(("tier", "1"), ("cumulativeMonths", 12), ("message", "A whole year!"))),
            ("twitch", "gift") => ("twitch.giftBomb", Args(("tier", "1"), ("total", 5))),
            ("twitch", "cheer") => ("twitch.cheer", Args(("bits", 500), ("message", "Cheer500 let's go"))),
            ("twitch", "raid") => ("twitch.raid", Args(("viewers", 42))),
            ("kick", "follow") => ("kick.follow", Args()),
            ("kick", "sub") => ("kick.subscription", Args(("months", 1))),
            ("kick", "resub") => ("kick.resubscription", Args(("months", 6))),
            ("kick", "gift") => ("kick.massGiftSubscription", Args(("gifts", 5))),
            ("kick", "kicks") => ("kick.kicksGifted", Args(("kicks", 100), ("giftName", "Rage Quit"), ("message", "gg"))),
            ("kick", "raid") => ("kick.host", Args(("viewers", 25))),
            ("rumble", "follow") => ("rumble.follow", Args()),
            ("rumble", "sub") => ("rumble.subscribe", Args(("amount", 5.0))),
            ("rumble", "gift") => ("rumble.giftSub", Args(("total", 3))),
            ("rumble", "tip") => ("rumble.rant", Args(("amount", 5.0), ("currency", "USD"), ("message", "Great stream!"))),
            ("youtube", "follow") => ("youtube.newSubscriber", Args()),
            ("youtube", "sub") => ("youtube.newSponsor", Args(("levelName", "Member"))),
            ("youtube", "resub") => ("youtube.memberMilestone", Args(("months", 6), ("levelName", "Member"), ("message", "Six months!"))),
            ("youtube", "gift") => ("youtube.membershipGift", Args(("total", 5), ("levelName", "Member"))),
            ("youtube", "superchat") => ("youtube.superChat", Args(("amount", 10.0), ("currency", "USD"), ("amountText", "$10.00"),
                ("message", "Love the stream"))),
            _ => ("streamelements.tip", Args(("amount", 10.0), ("currency", "USD"), ("formattedAmount", "$10.00"), ("message", "Keep it up"),
                ("service", "StreamElements"), ("isTest", true))),
        };
        args["user"] = user;
        args["userName"] = user.ToLowerInvariant();
        args["userId"] = "test-" + user.ToLowerInvariant();
        return new BotEvent(type, args);
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>A combined-chat message ("chat.message") with the platform's own arguments, badges and an emote.</summary>
    private static BotEvent Chat(string platform, string user)
    {
        var platformArgs = Args(("userName", user.ToLowerInvariant()));
        string message;
        switch (platform)
        {
            case "twitch":
                message = "Hello from Twitch Kappa";
                platformArgs["color"] = "#9146FF";
                platformArgs["badges"] = "moderator/1,subscriber/0";
                platformArgs["emotes"] = "25:18-22";
                break;
            case "kick":
                message = "Hello from Kick KEKW";
                platformArgs["rawMessage"] = "Hello from Kick [emote:37226:KEKW]";
                platformArgs["color"] = "#53FC18";
                platformArgs["badge"] = new List<Dictionary<string, object?>> { new() { ["id"] = "og", ["name"] = "OG", ["info"] = 0 } };
                break;
            case "rumble":
                message = "Hello from Rumble";
                platformArgs["badges"] = "verified,premium";
                break;
            default:
                message = "Hello from YouTube";
                platformArgs["isMember"] = true;
                break;
        }
        platformArgs["message"] = message;
        var name = platform switch { "twitch" => "Twitch", "kick" => "Kick", "rumble" => "Rumble", _ => "YouTube" };
        return new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform"] = name,
            ["user"] = user,
            ["userId"] = "test-" + user.ToLowerInvariant(),
            ["message"] = message,
            ["messageId"] = Guid.NewGuid().ToString("N"),
            ["isBroadcaster"] = false,
            ["isModerator"] = platform == "twitch",
            ["isVip"] = false,
            ["isSubscriber"] = platform is "twitch" or "youtube",
            [ChatHub.PlatformArgsKey] = platformArgs,
        });
    }
}
