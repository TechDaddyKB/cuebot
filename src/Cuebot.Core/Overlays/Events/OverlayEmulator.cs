using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Core.Overlays.Events;

/// <summary>
/// Sample events for trying widgets (the widget editor's Emulate buttons, emulate_event): a chat message or an alert as it
/// would come from Twitch, Kick, Rumble, YouTube, a donation service (StreamElements, Streamlabs), Ko-fi, Patreon,
/// Fourthwall or Shopify, in the modules' own event names and arguments (as their handlers publish them), so they go
/// through the same conversion as real ones. Never a catch-up: an emulated alert always plays.
/// </summary>
public static class OverlayEmulator
{
    public static readonly IReadOnlyList<string> Platforms = ["twitch", "kick", "rumble", "youtube", "donations", "kofi", "patreon", "fourthwall", "shopify"];

    /// <summary>What each platform can emulate (kinds are lower-case).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Kinds = new Dictionary<string, IReadOnlyList<string>>
    {
        ["twitch"] = ["chat", "follow", "sub", "resub", "gift", "cheer", "raid"],
        ["kick"] = ["chat", "follow", "sub", "resub", "gift", "kicks", "raid"],
        ["rumble"] = ["chat", "follow", "sub", "gift", "tip"],
        ["youtube"] = ["chat", "follow", "sub", "resub", "gift", "superchat"],
        // StreamElements tip and merch, Streamlabs charity donation.
        ["donations"] = ["tip", "merch", "charity"],
        // Ko-fi donation, first membership payment, a later one, shop order, commission.
        ["kofi"] = ["tip", "membership", "resub", "shop", "commission"],
        // A free member joining, a new paying pledge, an upgrade.
        ["patreon"] = ["follow", "membership", "upgrade"],
        // A completed donation, a purchase, stream gifts bought for viewers.
        ["fourthwall"] = ["tip", "order", "gifts"],
        // A paid order.
        ["shopify"] = ["order"],
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
            ("donations", "tip") => ("streamelements.tip", Args(("amount", 10.0), ("currency", "USD"), ("formattedAmount", "$10.00"), ("message", "Keep it up"),
                ("service", "StreamElements"), ("isTest", true))),
            ("donations", "merch") => ("streamelements.merch", Args(("amount", 30.0), ("currency", "USD"), ("formattedAmount", "$30.00"),
                ("message", "Love the design"), ("product", "Cuebot hoodie"), ("merchItems", 1), ("merchQuantity", 1), ("service", "StreamElements"),
                ("platform", "StreamElements"), ("isBackfill", false))),
            ("donations", "charity") => ("streamlabs.charityDonation", Args(("amount", 20.0), ("currency", "USD"), ("formattedAmount", "$20.00"),
                ("message", "For a good cause"), ("charityName", "St. Jude Children's Research Hospital"), ("service", "Streamlabs"))),
            ("kofi", "tip") => ("kofi.donation", Kofi("Donation", 3.0, "Good luck with the stream!")),
            ("kofi", "membership") => ("kofi.subscription", Kofi("Subscription", 5.0, "Monthly coffee!", ("tier", "Bronze"), ("isFirstPayment", true),
                ("isSubscriptionPayment", true), ("months", 1))),
            ("kofi", "resub") => ("kofi.resubscription", Kofi("Subscription", 5.0, "", ("tier", "Bronze"), ("isFirstPayment", false),
                ("isSubscriptionPayment", true), ("months", 3))),
            ("kofi", "shop") => ("kofi.shopOrder", Kofi("Shop Order", 18.0, "", ("itemCount", 2), ("item0", "Sticker pack"), ("item0.quantity", 2.0),
                ("item1", "Poster"), ("item1.quantity", 1.0))),
            ("kofi", "commission") => ("kofi.commission", Kofi("Commission", 25.0, "A portrait of my cat please")),
            ("patreon", "follow") => ("patreon.followCreated", Patreon(0, "", "", ("isGifted", false), ("isFreeTrial", false))),
            ("patreon", "membership") => ("patreon.pledgeCreated", Patreon(5.0, "Supporter", "active_patron", ("isStale", false), ("ageHours", 0.0))),
            ("patreon", "upgrade") => ("patreon.pledgeUpdated", Patreon(10.0, "Super Supporter", "active_patron", ("direction", "upgrade"),
                ("previousAmount", 5m), ("previousTier", "Supporter"), ("updatedToTier", "Super Supporter"))),
            ("fourthwall", "tip") => ("fourthwall.donation", Fourthwall(5.0, "Keep up the great work!", ("isCompleted", true), ("fw.status", "COMPLETED"))),
            ("fourthwall", "order") => ("fourthwall.orderPlaced", Fourthwall(35.0, "", ("isPurchase", true), ("isCancelled", false), ("fw.status", "CONFIRMED"),
                ("fw.items", "Cuebot T-shirt, Sticker pack"), ("fw.itemCount", 2.0), ("fw.source", "ORDER"))),
            ("fourthwall", "gifts") => ("fourthwall.giftPurchase", Fourthwall(60.0, "Merch for chat!", ("fw.quantity", 3.0), ("fw.offer.name", "Cuebot T-shirt"))),
            ("shopify", "order") => ("shopify.orderPaid", Args(("amount", 42.0m), ("currency", "USD"), ("formattedAmount", "$42.00"), ("message", ""),
                ("items", "Cuebot mug, Sticker pack"), ("itemCount", 2), ("orderName", "#1001"), ("service", "Shopify"), ("isReplay", false))),
            _ => throw new InvalidOperationException($"No sample for {platform} {kind}"),
        };
        args["user"] = user;
        args["userName"] = user.ToLowerInvariant();
        args["userId"] = "test-" + user.ToLowerInvariant();
        return new BotEvent(type, args);
    }

    /// <summary>A Ko-fi payment as KofiWebhookHandler publishes it (a public support, delivered on time).</summary>
    private static Dictionary<string, object?> Kofi(string kofiType, double amount, string message, params (string Key, object? Value)[] more)
    {
        var args = Args(("amount", (decimal)amount), ("currency", "USD"), ("formattedAmount", FormattableString.Invariant($"${amount:0.00}")), ("message", message), ("service", "Ko-fi"),
            ("kofiType", kofiType), ("isPublic", true), ("isSubscriptionPayment", false), ("isReplay", false), ("delaySeconds", 0L),
            ("messageId", "test-" + Guid.NewGuid().ToString("N")[..12]), ("url", "https://ko-fi.com"));
        foreach (var (key, value) in more) args[key] = value;
        return args;
    }

    /// <summary>A Patreon member event as PatreonWebhookHandler publishes it: amount is the monthly pledge, never the patron's private name.</summary>
    private static Dictionary<string, object?> Patreon(double monthly, string tier, string status, params (string Key, object? Value)[] more)
    {
        var args = Args(("amount", (decimal)monthly), ("monthlyAmount", (decimal)monthly), ("chargeAmount", (decimal)monthly), ("pledgeCadence", 1),
            ("currency", "USD"), ("formattedAmount", FormattableString.Invariant($"${monthly:0.00}")), ("message", ""), ("service", "Patreon"), ("tier", tier),
            ("tiers", tier.Length > 0 ? new List<string> { tier } : new List<string>()), ("patronStatus", status), ("isAnonymous", false),
            ("privateName", ""), ("avatarUrl", ""), ("profileUrl", ""), ("isStale", false));
        foreach (var (key, value) in more) args[key] = value;
        return args;
    }

    /// <summary>A Fourthwall event as FourthwallWebhookHandler publishes it (a real, caught-live delivery).</summary>
    private static Dictionary<string, object?> Fourthwall(double amount, string message, params (string Key, object? Value)[] more)
    {
        var args = Args(("amount", (decimal)amount), ("currency", "USD"), ("formattedAmount", FormattableString.Invariant($"${amount:0.00}")), ("message", message), ("service", "Fourthwall"),
            ("fw.currency", "USD"), ("isAnonymous", false), ("isReplay", false), ("fw.testMode", false));
        foreach (var (key, value) in more) args[key] = value;
        return args;
    }

    /// <summary>
    /// The other events the platform sends with <paramref name="e"/>, for emulating it as a real event. A Twitch gift bomb
    /// is the chat notice (twitch.giftBomb, for overlays), the gift total (twitch.giftSub, for the feed, spending and
    /// credits) and one twitch.subGift per recipient (the subscriber alerts and stream totals count those).
    /// </summary>
    public static IReadOnlyList<BotEvent> Companions(BotEvent e)
    {
        if (e.Type != "twitch.giftBomb") return [];
        var anonymous = e.Args.TryGetValue("isAnonymous", out var a) && a is true;
        var total = e.Args.TryGetValue("total", out var t) && int.TryParse(Convert.ToString(t, System.Globalization.CultureInfo.InvariantCulture), out var n)
            ? Math.Clamp(n, 1, 100) : 1;
        var events = new List<BotEvent>
        {
            new("twitch.giftSub", new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase) { ["isAnonymous"] = anonymous }),
        };
        var gift = "test-gift-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < total; i++)
        {
            var recipient = Names[i % Names.Length] + (i < Names.Length ? "" : (i / Names.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            events.Add(new BotEvent("twitch.subGift", new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase)
            {
                ["isAnonymous"] = anonymous,
                ["recipientUser"] = recipient,
                ["recipientUserName"] = recipient.ToLowerInvariant(),
                ["recipientUserId"] = "test-" + recipient.ToLowerInvariant(),
                ["communityGiftId"] = gift,
                ["subBombCount"] = total,
            }));
        }
        return events;
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
