using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Scripting.Compat;

namespace Cuebot.Server.StreamerBot;

/// <summary>
/// The nested <c>data</c> shapes Streamer.bot clients read (<c>data.reward.title</c>, <c>data.message.message</c>,
/// <c>data.user.login</c>, ...), built from Cuebot's flat event arguments. Shapes follow the public docs: the official
/// client's event types (github.com/Streamerbot/client) and the wiki's WebSocket event pages. Each shaper only adds
/// fields; the flat arguments stay in <c>data</c> unless a documented field has the same name (Twitch chat's
/// <c>message</c> becomes the message object; the text stays in <c>text</c>).
/// </summary>
public static class StreamerBotPayloads
{
    private delegate void Shaper(EventArgsView a, JsonObject data);

    /// <summary>"Source.Type" → shaper. Events without one get the generic <c>user</c> object only.</summary>
    private static readonly Dictionary<string, Shaper> Shapers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Twitch
        ["Twitch.ChatMessage"] = TwitchChatMessage,
        ["Twitch.FirstWord"] = TwitchChatMessage,
        ["Twitch.Whisper"] = TwitchWhisper,
        ["Twitch.BotWhisper"] = TwitchWhisper,
        ["Twitch.Follow"] = TwitchFollow,
        ["Twitch.Cheer"] = TwitchCheer,
        ["Twitch.Sub"] = (a, d) => TwitchSub(a, d, resub: false),
        ["Twitch.ReSub"] = (a, d) => TwitchSub(a, d, resub: true),
        ["Twitch.GiftSub"] = TwitchGiftSub,
        ["Twitch.GiftBomb"] = TwitchGiftBomb,
        ["Twitch.Raid"] = TwitchRaid,
        ["Twitch.RewardRedemption"] = TwitchRedemption,
        ["Twitch.RewardRedemptionUpdated"] = TwitchRedemption,
        ["Twitch.Announcement"] = TwitchAnnouncement,
        ["Twitch.AdRun"] = TwitchAdRun,
        ["Twitch.PollCreated"] = TwitchPoll,
        ["Twitch.PollUpdated"] = TwitchPoll,
        ["Twitch.PollCompleted"] = TwitchPoll,
        ["Twitch.PollTerminated"] = TwitchPoll,
        ["Twitch.PollArchived"] = TwitchPoll,
        ["Twitch.PredictionCreated"] = TwitchPrediction,
        ["Twitch.PredictionUpdated"] = TwitchPrediction,
        ["Twitch.PredictionLocked"] = TwitchPrediction,
        ["Twitch.PredictionCompleted"] = TwitchPrediction,
        ["Twitch.PredictionCanceled"] = TwitchPrediction,
        ["Twitch.HypeTrainStart"] = TwitchHypeTrain,
        ["Twitch.HypeTrainUpdate"] = TwitchHypeTrain,
        ["Twitch.HypeTrainLevelUp"] = TwitchHypeTrain,
        ["Twitch.HypeTrainEnd"] = TwitchHypeTrain,
        ["Twitch.CharityStarted"] = TwitchCharity,
        ["Twitch.CharityProgress"] = TwitchCharity,
        ["Twitch.CharityCompleted"] = TwitchCharity,
        ["Twitch.CharityDonation"] = TwitchCharity,
        ["Twitch.ShoutoutCreated"] = TwitchShoutout,
        ["Twitch.UserBanned"] = (a, d) => TwitchBan(a, d, "ban"),
        ["Twitch.UserTimedOut"] = (a, d) => TwitchBan(a, d, "timeout"),
        ["Twitch.UserUnbanned"] = (a, d) => TwitchBan(a, d, "unban"),
        ["Twitch.UserUntimedOut"] = (a, d) => TwitchBan(a, d, "untimeout"),
        ["Twitch.ChatCleared"] = TwitchChatCleared,
        ["Twitch.RewardCreated"] = TwitchRewardCreated,
        ["Twitch.RewardUpdated"] = TwitchReward,
        ["Twitch.RewardDeleted"] = TwitchReward,
        ["Twitch.StreamOnline"] = TwitchStreamOnline,
        ["Twitch.StreamOffline"] = TwitchStreamOnline,
        ["Twitch.ChatMessageDeleted"] = TwitchMessageDeleted,
        ["Twitch.StreamUpdate"] = TwitchStreamUpdate,
        ["Twitch.ViewerCountUpdate"] = (a, d) => d["viewers"] = a.Int("viewers", "viewerCount"),
        ["Twitch.ShieldModeBegin"] = TwitchShieldMode,
        ["Twitch.ShieldModeEnd"] = TwitchShieldMode,
        ["Twitch.PyramidSuccess"] = TwitchPyramid,
        ["Twitch.PyramidBroken"] = TwitchPyramid,

        // YouTube
        ["YouTube.Message"] = YouTubeMessage,
        ["YouTube.FirstWords"] = YouTubeFirstWords,
        ["YouTube.SuperChat"] = YouTubeUserEvent,
        ["YouTube.SuperSticker"] = YouTubeUserEvent,
        ["YouTube.NewSponsor"] = YouTubeUserEvent,
        ["YouTube.MemberMileStone"] = YouTubeUserEvent,
        ["YouTube.MembershipGift"] = YouTubeUserEvent,
        ["YouTube.GiftMembershipReceived"] = YouTubeUserEvent,
        ["YouTube.NewSubscriber"] = YouTubeUserEvent,
        ["YouTube.MessageDeleted"] = YouTubeUserEvent,
        ["YouTube.UserBanned"] = YouTubeUserEvent,
        ["YouTube.UserTimedout"] = YouTubeUserEvent,
        ["YouTube.BroadcastStarted"] = YouTubeBroadcast,
        ["YouTube.BroadcastEnded"] = YouTubeBroadcast,
        ["YouTube.BroadcastUpdated"] = YouTubeBroadcast,
        ["YouTube.StatisticsUpdated"] = YouTubeStatistics,

        // Kick
        ["Kick.ChatMessage"] = KickChatMessage,
        ["Kick.FirstWords"] = KickChatMessage,

        // General
        ["Command.Triggered"] = Command,
        ["Command.Cooldown"] = Command,
        ["Misc.TimedAction"] = Timer,
        ["System.Cron"] = Timer,
        ["Pulsoid.HeartRatePulse"] = HeartRate,
        ["HypeRate.HeartRatePulse"] = HeartRate,
        ["StreamElements.Tip"] = StreamElementsTip,
        ["Streamlabs.Donation"] = StreamlabsDonation,
        ["Streamlabs.Merchandise"] = StreamlabsMerch,
        ["DonorDrive.Donation"] = DonorDriveDonation,
        ["DonorDrive.Incentive"] = DonorDriveDonation,
        ["DonorDrive.ProfileUpdated"] = DonorDriveProfile,
        ["SpeechToText.Command"] = SpeechToText,
        ["SpeechToText.Dictation"] = SpeechToText,
        ["FileWatcher.Changed"] = (a, d) => FileWatcher(a, d, changed: true),
        ["FileWatcher.Created"] = (a, d) => FileWatcher(a, d, changed: false),
        ["FileWatcher.Deleted"] = (a, d) => FileWatcher(a, d, changed: false),
        ["FileWatcher.Renamed"] = (a, d) => FileWatcher(a, d, changed: false),
        ["Obs.Connected"] = ObsConnection,
        ["Obs.Disconnected"] = ObsConnection,
        ["Shopify.OrderCreated"] = ShopifyOrder,
        ["Shopify.OrderPaid"] = ShopifyOrder,
    };

    /// <summary>Whether <paramref name="name"/> has a documented shape (otherwise only the generic user object is added).</summary>
    public static bool HasShape(StreamerBotEventName name) => Shapers.ContainsKey(name.ToString());

    /// <summary>Adds the documented fields of <paramref name="name"/> to <paramref name="data"/> (which already holds the flat args).</summary>
    public static void Apply(StreamerBotEventName name, IReadOnlyDictionary<string, object?> args, JsonObject data,
        StreamerBotPayloadServices? services = null)
    {
        var a = new EventArgsView(args, services);
        if (Shapers.TryGetValue(name.ToString(), out var shaper)) shaper(a, data);
        if (data["user"] is not JsonObject && a.Has("user", "userName", "userId"))
        {
            data["user"] = User(a, PlatformOf(name, a));
        }
    }

    // ---- Users ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The user object Streamer.bot attaches to chat-like events: <c>{id, login, name, display, role, subscribed, type}</c>
    /// (plus <c>display_name</c>, <c>color</c>, <c>badges</c>, <c>monthsSubscribed</c>).
    /// </summary>
    private static JsonObject User(EventArgsView a, string platform)
    {
        var display = a.Str("user", "displayName", "userName");
        var login = a.Str("userLogin", "userSlug", "userName", "user");
        if (platform != "youtube") login = login.ToLowerInvariant();
        var user = new JsonObject
        {
            ["id"] = a.Str("userId"),
            ["login"] = login,
            ["name"] = display,
            ["display"] = display,
            ["display_name"] = display,
            ["role"] = Role(a),
            ["subscribed"] = a.Bool("isSubscriber", "isMember", "isSponsor"),
            ["monthsSubscribed"] = a.Int("monthsSubscribed", "subscribedMonths"),
            ["color"] = a.Str("color"),
            ["badges"] = Badges(a),
            ["type"] = platform,
        };
        // Pronouns come from alejo.io, which only knows Twitch users. Only already looked-up users are filled in:
        // building a payload never starts a lookup.
        if (platform == "twitch") user["pronouns"] = StreamerBotPronouns.Cached(a.Services?.Pronouns, login);
        return user;
    }

    private static string PlatformOf(StreamerBotEventName name, EventArgsView a) =>
        a.Str("platform") is { Length: > 0 } p ? p.ToLowerInvariant()
        : name.Source is "Twitch" or "YouTube" or "Kick" ? name.Source.ToLowerInvariant()
        : "";

    /// <summary>1 viewer, 2 VIP, 3 moderator, 4 broadcaster.</summary>
    private static int Role(EventArgsView a) =>
        a.Bool("isBroadcaster") ? 4 : a.Bool("isModerator") ? 3 : a.Bool("isVip") ? 2 : 1;

    /// <summary>Badges as <c>[{name, version, imageUrl}]</c> from Cuebot's "set/version,set/version" list.</summary>
    private static JsonArray Badges(EventArgsView a)
    {
        var badges = new JsonArray();
        foreach (var badge in a.Str("badges").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = badge.IndexOf('/');
            badges.Add(new JsonObject
            {
                ["name"] = slash < 0 ? badge : badge[..slash],
                ["version"] = slash < 0 ? "1" : badge[(slash + 1)..],
                ["imageUrl"] = "",
            });
        }
        return badges;
    }

    /// <summary>Twitch emotes as <c>[{type, name, startIndex, endIndex, imageUrl}]</c> from the IRC-style position list.</summary>
    private static JsonArray TwitchEmotes(EventArgsView a, string text)
    {
        var emotes = new JsonArray();
        var runes = text.EnumerateRunes().ToArray();
        foreach (var group in a.Str("emotes").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = group.IndexOf(':');
            if (colon <= 0) continue;
            var id = group[..colon];
            foreach (var range in group[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = range.IndexOf('-');
                if (dash <= 0 || !int.TryParse(range[..dash], out var start) || !int.TryParse(range[(dash + 1)..], out var end)) continue;
                var name = start >= 0 && end < runes.Length && start <= end
                    ? string.Concat(runes[start..(end + 1)].Select(r => r.ToString()))
                    : "";
                emotes.Add(new JsonObject
                {
                    ["type"] = "Twitch",
                    ["id"] = id,
                    ["name"] = name,
                    ["startIndex"] = start,
                    ["endIndex"] = end,
                    ["imageUrl"] = $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0",
                    ["bits"] = 0,
                    ["color"] = "",
                });
            }
        }
        // 7TV, BTTV and FFZ emotes (Streamer.bot lists them with the Twitch ones), when Cuebot's emote sets are loaded.
        if (a.Services?.Emotes is { } service && text.Length > 0)
        {
            try
            {
                foreach (var match in service.FindEmotes(EmoteProviders.Twitch, text, a.Args))
                {
                    if (!EmoteProviders.ThirdParty.Contains(match.Provider)) continue;
                    var start = RuneIndex(text, match.Start);
                    emotes.Add(new JsonObject
                    {
                        ["type"] = match.Provider,
                        ["id"] = match.Emote.Id,
                        ["name"] = match.Code,
                        ["startIndex"] = start,
                        ["endIndex"] = start + RuneCount(text.AsSpan(match.Start, match.Length)) - 1,
                        ["imageUrl"] = match.ImageUrl,
                        ["bits"] = 0,
                        ["color"] = "",
                    });
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // A malformed emote argument only costs the third-party emotes.
            }
        }
        return emotes;
    }

    /// <summary>The code point index (what Twitch's emote positions count) of a UTF-16 index.</summary>
    private static int RuneIndex(string text, int utf16Index) =>
        RuneCount(text.AsSpan(0, Math.Clamp(utf16Index, 0, text.Length)));

    private static int RuneCount(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>Twitch's default cheermote tiers and colours (1, 100, 1000, 5000, 10000 bits).</summary>
    private static readonly (int Min, string Color)[] CheerTiers =
        [(10000, "#f43021"), (5000, "#0099fe"), (1000, "#1db2a5"), (100, "#9c3ee8"), (1, "#979797")];

    private static readonly System.Text.RegularExpressions.Regex CheerWord =
        new(@"^(?<prefix>[A-Za-z]+)(?<bits>[1-9][0-9]*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Cheermotes in a cheer message: words like "Cheer100" whose amounts add up to the cheer's bits (the event does
    /// not list them, so a message whose words do not add up gets none). Images use Twitch's public cheermote CDN.
    /// </summary>
    private static JsonArray CheerEmotes(string text, int bits)
    {
        var found = new JsonArray();
        if (bits <= 0 || text.Length == 0) return found;
        var total = 0L;
        var index = 0;
        foreach (var word in text.Split(' '))
        {
            var length = word.EnumerateRunes().Count();
            if (CheerWord.Match(word) is { Success: true } m && int.TryParse(m.Groups["bits"].Value, out var amount))
            {
                total += amount;
                var (tier, color) = CheerTiers.First(t => amount >= t.Min);
                found.Add(new JsonObject
                {
                    ["type"] = "Cheermote",
                    ["name"] = word,
                    ["bits"] = amount,
                    ["color"] = color,
                    ["startIndex"] = index,
                    ["endIndex"] = index + length - 1,
                    ["imageUrl"] = $"https://d3aqoihi2n8ty8.cloudfront.net/actions/{m.Groups["prefix"].Value.ToLowerInvariant()}/dark/animated/{tier}/2.gif",
                });
            }
            index += length + 1;
        }
        return total == bits ? found : [];
    }

    /// <summary>The legacy Twitch fields shared by chat-like payloads (userId, userName, displayName, role, ...).</summary>
    private static void TwitchSender(EventArgsView a, JsonObject d)
    {
        d["userId"] = a.Str("userId");
        d["userName"] = a.Str("userName", "user").ToLowerInvariant();
        d["displayName"] = a.Str("user", "userName");
        d["role"] = Role(a);
        d["isTest"] = a.Bool("isTest");
        d["user"] = User(a, "twitch");
    }

    private static int TierNumber(EventArgsView a) =>
        a.Bool("isPrime") ? 0 : int.TryParse(a.Str("tier", "subTier"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
            ? t >= 1000 ? t / 1000 : t
            : 1;

    // ---- Twitch --------------------------------------------------------------------------------------------------

    private static void TwitchChatMessage(EventArgsView a, JsonObject d)
    {
        var text = a.Str("message", "rawInput");
        var bits = a.Int("bits");
        d["message"] = new JsonObject
        {
            ["internal"] = a.Bool("isInternal", "internal"),
            ["msgId"] = a.Str("messageId", "msgId"),
            ["userId"] = a.Str("userId"),
            ["username"] = a.Str("userName", "user").ToLowerInvariant(),
            ["role"] = Role(a),
            ["subscriber"] = a.Bool("isSubscriber"),
            ["displayName"] = a.Str("user", "userName"),
            ["color"] = a.Str("color"),
            ["channel"] = a.Str("broadcasterUserName", "channel"),
            ["message"] = text,
            ["isHighlighted"] = a.Bool("isHighlighted"),
            ["isMe"] = a.Bool("isMe", "isAction"),
            ["isCustomReward"] = a.Str("rewardId").Length > 0,
            ["isAnonymous"] = false,
            ["isReply"] = a.Bool("isReply"),
            ["bits"] = bits,
            ["firstMessage"] = a.Bool("isFirstMessage", "firstMessage"),
            ["hasBits"] = bits > 0,
            ["emotes"] = TwitchEmotes(a, text),
            ["cheerEmotes"] = CheerEmotes(text, bits),
            ["badges"] = Badges(a),
            ["monthsSubscribed"] = a.Int("monthsSubscribed"),
            ["isTest"] = a.Bool("isTest"),
        };
        d["user"] = User(a, "twitch");
        d["messageId"] = a.Str("messageId", "msgId");
        d["text"] = text;
        d["isReply"] = a.Bool("isReply");
        if (a.Bool("isReply"))
        {
            d["reply"] = new JsonObject
            {
                ["msgId"] = a.Str("replyParentMessageId"),
                ["userId"] = a.Str("replyParentUserId"),
                ["userLogin"] = a.Str("replyParentUserName"),
                ["userName"] = a.Str("replyParentUser"),
                ["msgBody"] = a.Str("replyParentMessage"),
                ["threadMsgId"] = a.Str("replyThreadMessageId"),
            };
        }
    }

    private static void TwitchWhisper(EventArgsView a, JsonObject d)
    {
        var text = a.Str("message", "rawInput");
        d["message"] = new JsonObject
        {
            ["msgId"] = a.Str("messageId"),
            ["userId"] = a.Str("userId"),
            ["username"] = a.Str("userName", "user").ToLowerInvariant(),
            ["displayName"] = a.Str("user", "userName"),
            ["message"] = text,
            ["emotes"] = TwitchEmotes(a, text),
            ["badges"] = Badges(a),
        };
        d["text"] = text;
        d["user"] = User(a, "twitch");
    }

    private static void TwitchFollow(EventArgsView a, JsonObject d)
    {
        d["user_id"] = a.Str("userId");
        d["user_login"] = a.Str("userName", "user").ToLowerInvariant();
        d["user_name"] = a.Str("user", "userName");
        d["followed_at"] = a.Str("followedAt");
        TwitchSender(a, d);
    }

    private static void TwitchCheer(EventArgsView a, JsonObject d)
    {
        var text = a.Str("message");
        d["msgId"] = a.Str("messageId");
        d["username"] = a.Str("userName", "user").ToLowerInvariant();
        d["subscriber"] = a.Bool("isSubscriber");
        d["channel"] = a.Str("broadcasterUserName");
        d["message"] = text;
        d["isHighlighted"] = false;
        d["isMe"] = false;
        d["isCustomReward"] = false;
        d["isAnonymous"] = a.Bool("isAnonymous");
        d["isReply"] = false;
        d["bits"] = a.Int("bits");
        d["firstMessage"] = false;
        d["hasBits"] = true;
        d["emotes"] = TwitchEmotes(a, text);
        d["cheerEmotes"] = CheerEmotes(text, a.Int("bits"));
        d["badges"] = Badges(a);
        d["monthsSubscribed"] = a.Int("monthsSubscribed");
        d["internal"] = false;
        TwitchSender(a, d);
    }

    private static void TwitchSub(EventArgsView a, JsonObject d, bool resub)
    {
        var text = a.Str("message");
        d["subTier"] = TierNumber(a);
        d["isPrime"] = a.Bool("isPrime");
        d["color"] = a.Str("color");
        d["message"] = text;
        d["emotes"] = TwitchEmotes(a, text);
        d["badges"] = Badges(a);
        if (resub)
        {
            var streak = a.Int("streakMonths");
            d["cumulativeMonths"] = a.Int("cumulativeMonths", "months");
            d["streakMonths"] = streak;
            d["shareStreak"] = streak > 0;
        }
        TwitchSender(a, d);
    }

    private static void TwitchGiftSub(EventArgsView a, JsonObject d)
    {
        var bomb = a.Str("communityGiftId").Length > 0;
        d["isAnonymous"] = a.Bool("isAnonymous");
        d["totalSubsGifted"] = a.Int("cumulativeTotal", "totalSubsGifted");
        d["cumulativeMonths"] = a.Int("cumulativeMonths");
        d["monthsGifted"] = Math.Max(1, a.Int("durationMonths", "monthsGifted"));
        d["fromSubBomb"] = bomb;
        d["subBombCount"] = a.Int("subBombCount", "gifts") is > 0 and var count ? count : 1;
        d["recipientUserId"] = a.Str("recipientUserId");
        d["recipientUsername"] = a.Str("recipientUserName", "recipientUser").ToLowerInvariant();
        d["recipientDisplayName"] = a.Str("recipientUser", "recipientUserName");
        d["recipient"] = new JsonObject
        {
            ["id"] = a.Str("recipientUserId"),
            ["login"] = a.Str("recipientUserName", "recipientUser").ToLowerInvariant(),
            ["name"] = a.Str("recipientUser", "recipientUserName"),
            ["type"] = "twitch",
        };
        d["subTier"] = TierNumber(a);
        d["emotes"] = new JsonArray();
        d["badges"] = Badges(a);
        TwitchSender(a, d);
    }

    private static void TwitchGiftBomb(EventArgsView a, JsonObject d)
    {
        d["isAnonymous"] = a.Bool("isAnonymous");
        d["gifts"] = a.Int("total", "gifts");
        d["totalGifts"] = a.Int("cumulativeTotal", "totalGifts");
        d["subTier"] = TierNumber(a);
        d["badges"] = Badges(a);
        TwitchSender(a, d);
    }

    private static void TwitchRaid(EventArgsView a, JsonObject d)
    {
        var viewers = a.Int("viewers", "viewerCount");
        d["from_broadcaster_user_id"] = a.Str("userId");
        d["from_broadcaster_user_login"] = a.Str("userName", "user").ToLowerInvariant();
        d["from_broadcaster_user_name"] = a.Str("user", "userName");
        d["to_broadcaster_user_id"] = a.Str("broadcasterId", "broadcasterUserId");
        d["to_broadcaster_user_login"] = a.Str("broadcasterUserName", "broadcasterLogin");
        d["to_broadcaster_user_name"] = a.Str("broadcaster", "broadcasterUserName");
        d["viewers"] = viewers;
        d["viewerCount"] = viewers;
        d["profileImage"] = a.Str("userProfileUrl", "profileImageUrl", "profileImage");
        TwitchSender(a, d);
    }

    private static void TwitchRedemption(EventArgsView a, JsonObject d)
    {
        d["id"] = a.Str("redemptionId");
        d["user_id"] = a.Str("userId");
        d["user_login"] = a.Str("userName", "user").ToLowerInvariant();
        d["user_name"] = a.Str("user", "userName");
        d["user_input"] = a.Str("userInput", "rawInput");
        d["status"] = a.Str("status") is { Length: > 0 } status ? status : "unfulfilled";
        d["redeemed_at"] = a.Str("redeemedAt");
        d["reward"] = new JsonObject
        {
            ["id"] = a.Str("rewardId"),
            ["title"] = a.Str("rewardTitle", "rewardName"),
            ["cost"] = a.Int("rewardCost"),
            ["prompt"] = a.Str("rewardPrompt"),
        };
        d["user"] = User(a, "twitch");
    }

    private static void TwitchAnnouncement(EventArgsView a, JsonObject d)
    {
        var text = a.Str("message");
        d["msgId"] = a.Str("messageId");
        d["subscriber"] = a.Bool("isSubscriber");
        d["color"] = a.Str("color");
        d["message"] = text;
        d["emotes"] = TwitchEmotes(a, text);
        d["badges"] = Badges(a);
        d["monthsSubscribed"] = a.Int("monthsSubscribed");
        d["announcementColor"] = a.Str("announcementColor");
        TwitchSender(a, d);
    }

    private static void TwitchAdRun(EventArgsView a, JsonObject d)
    {
        d["length_seconds"] = a.Int("duration", "length");
        d["timestamp"] = a.Str("startedAt");
        d["is_automatic"] = a.Bool("isAutomatic");
        d["requester_user_id"] = a.Str("userId");
        d["requester_user_login"] = a.Str("userName").ToLowerInvariant();
        d["requester_user_name"] = a.Str("user", "userName");
    }

    private static void TwitchPoll(EventArgsView a, JsonObject d)
    {
        var choices = new JsonArray();
        JsonObject? winner = null;
        for (var i = 0; i < a.Int("choiceCount"); i++)
        {
            var choice = new JsonObject
            {
                ["id"] = a.Str($"choice{i}Id"),
                ["title"] = a.Str($"choice{i}Title"),
                ["votes"] = a.Int($"choice{i}Votes"),
                ["channel_points_votes"] = a.Int($"choice{i}ChannelPointsVotes"),
                ["bits_votes"] = 0,
            };
            if (winner is null && a.Str($"choice{i}Title") == a.Str("winningChoice")) winner = (JsonObject)choice.DeepClone();
            choices.Add(choice);
        }
        d["id"] = a.Str("pollId");
        d["title"] = a.Str("title");
        d["choices"] = choices;
        d["status"] = a.Str("status");
        d["started_at"] = a.Str("startedAt");
        d["ends_at"] = a.Str("endsAt");
        d["ended_at"] = a.Str("endedAt");
        // channel_points_voting is only in the raw EventSub payload Cuebot keeps as poll._json.
        var voting = a.Json("poll._json")?["channel_points_voting"] as JsonObject;
        d["channel_points_voting"] = new JsonObject
        {
            ["is_enabled"] = voting?["is_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            ["amount_per_vote"] = Number(voting?["amount_per_vote"]),
        };
        if (winner is not null) d["winningChoice"] = winner;
    }

    /// <summary>A JSON number as a long (0 for anything else).</summary>
    private static long Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue<long>(out var n) ? n : 0;

    private static void TwitchPrediction(EventArgsView a, JsonObject d)
    {
        var raw = a.Json("prediction._json");
        var outcomes = new JsonArray();
        JsonObject? winner = null;
        for (var i = 0; i < a.Int("outcomeCount"); i++)
        {
            var outcome = new JsonObject
            {
                ["id"] = a.Str($"outcome{i}Id"),
                ["title"] = a.Str($"outcome{i}Title"),
                ["color"] = a.Str($"outcome{i}Color"),
                ["users"] = a.Int($"outcome{i}Users"),
                ["channel_points"] = a.Int($"outcome{i}ChannelPoints"),
                ["top_predictors"] = TopPredictors(raw, a.Str($"outcome{i}Id")),
            };
            if (a.Str("winningOutcomeId") is { Length: > 0 } wid && wid == a.Str($"outcome{i}Id")) winner = (JsonObject)outcome.DeepClone();
            outcomes.Add(outcome);
        }
        d["id"] = a.Str("predictionId");
        d["title"] = a.Str("title");
        d["outcomes"] = outcomes;
        d["status"] = a.Str("status");
        d["started_at"] = a.Str("startedAt");
        d["locks_at"] = a.Str("locksAt");
        d["locked_at"] = a.Str("lockedAt");
        d["ended_at"] = a.Str("endedAt");
        d["winning_outcome_id"] = a.Str("winningOutcomeId");
        if (winner is not null) d["winning_outcome"] = winner;
    }

    /// <summary>An outcome's top predictors from the raw EventSub payload (prediction._json), in the documented shape.</summary>
    private static JsonArray TopPredictors(JsonNode? raw, string outcomeId)
    {
        var predictors = new JsonArray();
        if (raw?["outcomes"] is not JsonArray outcomes) return predictors;
        foreach (var outcome in outcomes)
        {
            if (outcome?["id"]?.ToString() != outcomeId || outcome["top_predictors"] is not JsonArray top) continue;
            foreach (var p in top)
            {
                predictors.Add(new JsonObject
                {
                    ["user_id"] = p?["user_id"]?.ToString() ?? "",
                    ["user_login"] = p?["user_login"]?.ToString() ?? "",
                    ["user_name"] = p?["user_name"]?.ToString() ?? "",
                    ["channel_points_used"] = Number(p?["channel_points_used"]),
                    ["channel_points_won"] = Number(p?["channel_points_won"]),
                });
            }
        }
        return predictors;
    }

    private static void TwitchHypeTrain(EventArgsView a, JsonObject d)
    {
        var top = new JsonArray();
        foreach (var (prefix, type) in new[] { ("topBits", "bits"), ("topSub", "subscription"), ("topOther", "other") })
        {
            if (a.Str(prefix + "User").Length == 0) continue;
            top.Add(new JsonObject
            {
                ["user_id"] = a.Str(prefix + "UserId"),
                ["user_login"] = a.Str(prefix + "User").ToLowerInvariant(),
                ["user_name"] = a.Str(prefix + "User"),
                ["type"] = type,
                ["total"] = a.Int(prefix + "Total"),
            });
        }
        d["id"] = a.Str("hypeTrainId");
        d["level"] = a.Int("level");
        d["total"] = a.Int("total");
        d["progress"] = a.Int("progress");
        d["goal"] = a.Int("goal");
        // EventSub v2 hype trains no longer report the last contribution; the documented object stays, empty.
        d["last_contribution"] = new JsonObject { ["user_id"] = "", ["user_login"] = "", ["user_name"] = "", ["total"] = 0 };
        d["top_contributions"] = top;
        d["started_at"] = Time(a, "startedAt");
        d["expires_at"] = Time(a, "expiresAt");
        d["ended_at"] = a.Str("endedAt");
        d["cooldown_ends_at"] = a.Str("cooldownEndsAt");
        d["isTest"] = a.Bool("isTest");
    }

    private static void TwitchCharity(EventArgsView a, JsonObject d)
    {
        var currency = a.Str("currency");
        // Minor units as Twitch sends them (charity.amount.valueMinor), else the decimal amount in cents.
        JsonObject Money(string key) => new()
        {
            ["value"] = a.Has($"charity.{key}.valueMinor") ? a.Long($"charity.{key}.valueMinor") : (long)Math.Round(a.Double(key) * 100),
            ["decimal_places"] = 2,
            ["currency"] = a.Str($"charity.{key}.currency") is { Length: > 0 } c ? c : currency,
        };
        d["id"] = a.Str("campaignId");
        d["campaign_id"] = a.Str("campaignId");
        d["charity_name"] = a.Str("charityName");
        d["charity_description"] = a.Str("charityDescription");
        d["charity_website"] = a.Str("charityWebsite");
        d["charity_logo"] = a.Str("charityLogo");
        d["current_amount"] = Money("currentAmount");
        d["target_amount"] = Money("targetAmount");
        d["amount"] = Money("amount");
        d["user_id"] = a.Str("userId");
        d["user_login"] = a.Str("userName").ToLowerInvariant();
        d["user_name"] = a.Str("user", "userName");
        d["started_at"] = Time(a, "charity.startedAt", "startedAt");
        d["stopped_at"] = Time(a, "charity.stoppedAt", "stoppedAt");
    }

    /// <summary>A time argument (DateTime, DateTimeOffset or text) as ISO 8601 text; "" when missing.</summary>
    private static string Time(EventArgsView a, params string[] keys)
    {
        foreach (var key in keys)
        {
            switch (a.Raw(key))
            {
                case DateTimeOffset o: return o.ToString("o", CultureInfo.InvariantCulture);
                case DateTime t: return t.ToString("o", CultureInfo.InvariantCulture);
                case string { Length: > 0 } s: return s;
            }
        }
        return "";
    }

    private static void TwitchShoutout(EventArgsView a, JsonObject d)
    {
        d["moderator_user_id"] = a.Str("moderatorUserId");
        d["moderator_user_login"] = a.Str("moderatorUserName");
        d["moderator_user_name"] = a.Str("moderator");
        d["to_broadcaster_user_id"] = a.Str("userId");
        d["to_broadcaster_user_login"] = a.Str("userName").ToLowerInvariant();
        d["to_broadcaster_user_name"] = a.Str("user", "userName");
        d["viewer_count"] = a.Int("viewers", "viewerCount");
        d["started_at"] = a.Str("startedAt");
        d["cooldown_ends_at"] = Time(a, "cooldownEndsAt");
        d["target_cooldown_ends_at"] = Time(a, "targetCooldownEndsAt");
    }

    private static void TwitchBan(EventArgsView a, JsonObject d, string action)
    {
        d["target_user_display"] = a.Str("user", "userName");
        d["target_user_id"] = a.Str("userId");
        d["target_user_login"] = a.Str("userName", "user").ToLowerInvariant();
        d["created_at"] = Time(a, "bannedAt", "createdAt");
        d["created_by_user_id"] = a.Str("moderatorUserId");
        d["created_by"] = a.Str("moderatorUserName", "moderator");
        d["moderation_action"] = action;
        d["type"] = "chat_login_moderation";
        d["duration"] = a.Int("duration");
        d["reason"] = a.Str("reason");
        d["isTest"] = a.Bool("isTest");
    }

    private static void TwitchMessageDeleted(EventArgsView a, JsonObject d)
    {
        d["targetMessageId"] = a.Str("messageId", "targetMessageId");
        d["message"] = a.Str("message", "targetMessage");
        d["userName"] = a.Str("userName", "user").ToLowerInvariant();
        d["role"] = Role(a);
        d["isTest"] = a.Bool("isTest");
    }

    private static void TwitchStreamUpdate(EventArgsView a, JsonObject d)
    {
        d["channelId"] = a.Str("userId", "broadcasterId");
        d["channel"] = a.Str("userName", "broadcasterUserName");
        d["status"] = a.Str("title");
        d["oldStatus"] = a.Str("previousTitle", "oldTitle");
        d["game"] = new JsonObject { ["id"] = a.Str("gameId", "categoryId"), ["name"] = a.Str("gameName", "categoryName") };
        d["oldGame"] = new JsonObject
        {
            ["id"] = a.Str("previousGameId", "oldGameId"),
            ["name"] = a.Str("previousGameName", "oldGameName"),
        };
    }

    private static void TwitchShieldMode(EventArgsView a, JsonObject d)
    {
        d["moderator_user_id"] = a.Str("userId");
        d["moderator_user_login"] = a.Str("userName").ToLowerInvariant();
        d["moderator_user_name"] = a.Str("user", "userName");
        d["started_at"] = Time(a, "startedAt");
        d["ended_at"] = Time(a, "endedAt");
    }

    private static void TwitchPyramid(EventArgsView a, JsonObject d)
    {
        var width = a.Int("width");
        d["width"] = width;
        d["total"] = a.Int("total") is > 0 and var total ? total : Math.Max(0, width * 2 - 1);
        d["emote"] = a.Str("emote");
        d["ownerId"] = a.Str("userId");
        d["ownerUsername"] = a.Str("userName", "user").ToLowerInvariant();
        d["ownerDisplay"] = a.Str("user", "userName");
        var broken = a.Str("brokenBy").Length > 0;
        d["user"] = new JsonObject
        {
            ["display"] = broken ? a.Str("brokenBy") : a.Str("user"),
            ["id"] = broken ? a.Str("brokenByUserId") : a.Str("userId"),
            ["name"] = (broken ? a.Str("brokenBy") : a.Str("userName", "user")).ToLowerInvariant(),
            ["role"] = 1,
            ["subscribed"] = false,
            ["type"] = "twitch",
        };
    }

    private static void TwitchChatCleared(EventArgsView a, JsonObject d)
    {
        // Streamer.bot sends the clear in its moderation shape, with no target user.
        d["target_user_id"] = "";
        d["target_user_login"] = "";
        d["created_by_user_id"] = a.Str("moderatorUserId", "userId");
        d["created_by"] = a.Str("moderatorUserName", "moderator", "userName").ToLowerInvariant();
        d["moderation_action"] = "clear";
        d["type"] = "chat_channel_moderation";
        d["duration"] = 0;
        d["isTest"] = a.Bool("isTest");
    }

    /// <summary>Reward created: Streamer.bot's own reward record (name, group, counters) plus the EventSub fields.</summary>
    private static void TwitchRewardCreated(EventArgsView a, JsonObject d)
    {
        TwitchReward(a, d);
        d["name"] = a.Str("rewardTitle", "rewardName");
        d["group"] = a.Str("rewardGroup");
        d["userInput"] = a.Bool("isUserInputRequired", "requiresUserInput");
        d["persistCounter"] = false;
        d["counter"] = a.Int("counter");
        d["persistUserCounter"] = false;
        d["backgroundColor"] = a.Str("backgroundColor");
        d["userCounters"] = new JsonObject();
    }

    /// <summary>Reward updated/deleted: the EventSub reward fields plus the older names the wiki shows.</summary>
    private static void TwitchReward(EventArgsView a, JsonObject d)
    {
        static JsonObject Limit(int value, string field) => new() { ["is_enabled"] = value > 0, [field] = value };
        d["id"] = a.Str("rewardId");
        d["is_enabled"] = a.Bool("isEnabled");
        d["is_paused"] = a.Bool("isPaused");
        d["is_in_stock"] = a.Bool("isInStock");
        d["title"] = a.Str("rewardTitle", "rewardName");
        d["cost"] = a.Int("rewardCost");
        d["prompt"] = a.Str("rewardPrompt");
        d["is_user_input_required"] = a.Bool("isUserInputRequired", "requiresUserInput");
        d["should_redemptions_skip_request_queue"] = a.Bool("skipsQueue");
        d["cooldown_expires_at"] = a.Str("cooldownExpiresAt");
        d["max_per_stream"] = Limit(a.Int("maxPerStream"), "value");
        d["max_per_user_per_stream"] = Limit(a.Int("maxPerUserPerStream"), "value");
        d["global_cooldown"] = Limit(a.Int("globalCooldown"), "seconds");
        d["background_color"] = a.Str("backgroundColor");
        d["backgroundColor"] = a.Str("backgroundColor");
        d["default_image"] = new JsonObject { ["url_1x"] = "", ["url_2x"] = "", ["url_4x"] = "" };
        d["rewardId"] = a.Str("rewardId");
        d["channelId"] = a.Str("broadcasterId", "userId");
        d["dateTime"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        d["inputRequired"] = a.Bool("isUserInputRequired", "requiresUserInput");
        d["enabled"] = a.Bool("isEnabled");
        d["paused"] = a.Bool("isPaused");
        d["subOnly"] = false;
    }

    private static void TwitchStreamOnline(EventArgsView a, JsonObject d)
    {
        d["id"] = a.Str("streamId", "broadcastId");
        d["type"] = a.Str("streamType") is { Length: > 0 } type ? type : "live";
        d["started_at"] = Time(a, "startedAt");
    }

    // ---- YouTube -------------------------------------------------------------------------------------------------

    /// <summary>The YouTube user object: <c>{id, name, url, profileImageUrl, isOwner, isModerator, isSponsor, isVerified}</c>.</summary>
    private static JsonObject YouTubeUser(EventArgsView a)
    {
        var user = User(a, "youtube");
        user["url"] = a.Str("channelUrl") is { Length: > 0 } url ? url
            : a.Str("userId") is { Length: > 0 } id ? $"https://www.youtube.com/channel/{id}" : "";
        user["profileImageUrl"] = a.Str("userProfileUrl", "profileImageUrl");
        user["isOwner"] = a.Bool("isBroadcaster");
        user["isModerator"] = a.Bool("isModerator");
        user["isSponsor"] = a.Bool("isSponsor", "isMember");
        user["isVerified"] = a.Bool("isVerified");
        return user;
    }

    private static void YouTubeMessage(EventArgsView a, JsonObject d)
    {
        var text = a.Str("message", "rawInput");
        d["eventId"] = a.Str("messageId");
        d["message"] = text;
        d["text"] = text;
        d["publishedAt"] = Time(a, "publishedAt");
        d["user"] = YouTubeUser(a);
    }

    private static void YouTubeFirstWords(EventArgsView a, JsonObject d)
    {
        d["message"] = a.Str("message");
        d["action"] = false;
        d["user"] = YouTubeUser(a);
    }

    private static void YouTubeUserEvent(EventArgsView a, JsonObject d)
    {
        if (a.Str("messageId").Length > 0) d["eventId"] = a.Str("messageId");
        d["user"] = YouTubeUser(a);
    }

    private static void YouTubeBroadcast(EventArgsView a, JsonObject d)
    {
        d["id"] = a.Str("broadcastId");
        d["channelId"] = a.Str("userId");
        d["title"] = a.Str("broadcastTitle", "title");
        d["description"] = a.Str("broadcastDescription");
        d["privacy"] = a.Str("broadcastPrivacy");
        d["status"] = a.Str("broadcastStatus");
        d["publishedAt"] = a.Str("broadcastActualStart", "broadcastScheduledStart");
    }

    private static void YouTubeStatistics(EventArgsView a, JsonObject d)
    {
        d["likeCount"] = a.Long("likeCount");
        d["dislikeCount"] = a.Long("dislikeCount");
        d["viewCount"] = a.Long("viewCount");
        d["commentCount"] = a.Long("commentCount");
        d["favoriteCount"] = a.Long("favoriteCount");
        d["concurrentViewers"] = a.Long("concurrentViewers", "viewerCount");
    }

    // ---- Kick ----------------------------------------------------------------------------------------------------

    private static void KickChatMessage(EventArgsView a, JsonObject d)
    {
        var text = a.Str("message", "rawInput");
        d["messageId"] = a.Str("messageId", "msgId");
        d["text"] = text;
        var user = User(a, "kick");
        user["profilePicture"] = a.Str("userProfilePicture");
        d["user"] = user;
    }

    // ---- General -------------------------------------------------------------------------------------------------

    private static void Command(EventArgsView a, JsonObject d)
    {
        d["command"] = a.Str("command");
        d["counter"] = a.Int("counter");
        d["userCounter"] = a.Int("userCounter");
        d["message"] = a.Str("input", "rawInput");
        if (a.Has("remaining"))
        {
            var left = a.Int("remaining");
            var kind = a.Str("cooldownType");
            d["cooldownLeft"] = left;
            d["globalCooldownLeft"] = kind.Equals("Global", StringComparison.OrdinalIgnoreCase) ? left : 0;
            d["userCooldownLeft"] = kind.Equals("User", StringComparison.OrdinalIgnoreCase) ? left : 0;
        }
        var user = User(a, a.Str("platform").ToLowerInvariant());
        d["user"] = user;
    }

    private static void Timer(EventArgsView a, JsonObject d)
    {
        d["id"] = a.Str("timerId");
        d["name"] = a.Str("timerName");
        d["counter"] = a.Int("counter", "timerCounter");
        d["interval"] = a.Int("interval", "timerInterval");
    }

    private static void HeartRate(EventArgsView a, JsonObject d)
    {
        d["heartRate"] = a.Int("heartRate");
        d["measuredAt"] = a.Has("measuredAt") ? a.Long("measuredAt") : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static void StreamElementsTip(EventArgsView a, JsonObject d)
    {
        d["id"] = a.Str("tipId", "id");
        d["provider"] = "streamelements";
        d["username"] = a.Str("tipUsername", "user");
        d["amount"] = a.Double("tipAmount", "amount");
        d["currency"] = a.Str("tipCurrency", "currency");
        d["message"] = a.Str("tipMessage", "message");
        d["isTest"] = a.Bool("isTest");
    }

    private static void StreamlabsDonation(EventArgsView a, JsonObject d)
    {
        d["from"] = a.Str("donationFrom", "user", "userName");
        d["amount"] = a.Double("donationAmount", "amount");
        d["formattedAmount"] = a.Str("donationFormattedAmount", "formattedAmount");
        d["currency"] = a.Str("donationCurrency", "currency");
        d["message"] = a.Str("donationMessage", "message");
        d["isTest"] = a.Bool("isTest");
    }

    private static void StreamlabsMerch(EventArgsView a, JsonObject d)
    {
        d["from"] = a.Str("merchandiseFrom", "user", "userName");
        d["product"] = a.Str("merchandiseProduct", "product");
        d["message"] = a.Str("merchandiseMessage", "message");
        d["image"] = a.Str("merchandiseImageUrl", "imageUrl");
        d["isTest"] = a.Bool("isTest");
    }

    /// <summary>DonorDrive donation (and incentive): the documented <c>donation</c> object.</summary>
    private static void DonorDriveDonation(EventArgsView a, JsonObject d)
    {
        d["donation"] = new JsonObject
        {
            ["displayName"] = a.Str("donorName", "user"),
            ["donorId"] = a.Str("donorId"),
            ["links"] = new JsonObject { ["recipient"] = a.Str("profileUrl") },
            ["eventId"] = a.Long("eventId"),
            ["createdDateUtc"] = Time(a, "createdAt"),
            ["recipientName"] = a.Str("recipientName", "profileName"),
            ["message"] = a.Str("donorMessage", "message"),
            ["participantId"] = a.Long("participantId"),
            ["amount"] = a.Double("donorAmount", "amount"),
            ["avatarImageUrl"] = a.Str("donorAvatarUrl"),
            ["teamId"] = a.Long("teamId"),
            ["donationId"] = a.Str("donationId"),
        };
    }

    private static void DonorDriveProfile(EventArgsView a, JsonObject d)
    {
        d["profile"] = new JsonObject
        {
            ["fundraisingGoal"] = a.Double("goal"),
            ["eventName"] = a.Str("eventName"),
            ["links"] = new JsonObject { ["recipient"] = a.Str("profileUrl") },
            ["eventId"] = a.Long("eventId"),
            ["sumDonations"] = a.Double("raised"),
            ["createdDateUtc"] = Time(a, "createdAt"),
            ["numAwardedBadges"] = a.Int("numAwardedBadges"),
            ["captainDisplayName"] = a.Str("captainDisplayName"),
            ["avatarImageUrl"] = a.Str("avatarUrl"),
            ["teamId"] = a.Long("teamId"),
            ["sumPledges"] = a.Double("sumPledges"),
            ["numDonations"] = a.Int("numDonations", "donations"),
        };
    }

    /// <summary>Largest file whose contents a FileWatcher.Changed payload carries (Streamer.bot's lines/data).</summary>
    private const int MaxWatchedFileBytes = 256 * 1024;

    private static void FileWatcher(EventArgsView a, JsonObject d, bool changed)
    {
        var path = a.Str("fullPath", "filePath");
        d["fullPath"] = path;
        d["fileName"] = a.Str("fileName") is { Length: > 0 } file ? file : Path.GetFileName(path);
        d["name"] = Path.GetFileNameWithoutExtension(path);
        if (a.Has("oldFullPath", "oldFilePath"))
        {
            d["oldFullPath"] = a.Str("oldFullPath", "oldFilePath");
            d["oldFileName"] = a.Str("oldFileName");
        }
        if (!changed || path.Length == 0) return;
        // Streamer.bot sends the new contents: "data" when the file is a JSON object (top-level values), else "lines".
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxWatchedFileBytes) return;
            var text = File.ReadAllText(path);
            if (text.TrimStart().StartsWith('{'))
            {
                try
                {
                    if (JsonNode.Parse(text) is JsonObject json)
                    {
                        d["data"] = json;
                        return;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON after all: sent as lines.
                }
            }
            d["lines"] = new JsonArray([.. text.ReplaceLineEndings("\n").Split('\n').Select(l => (JsonNode?)JsonValue.Create(l))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Locked or gone: the event goes out without the contents.
        }
    }

    /// <summary>OBS connection events: Streamer.bot's dotted <c>obs.*</c> keys.</summary>
    private static void ObsConnection(EventArgsView a, JsonObject d)
    {
        // Cuebot names connections rather than giving them ids: the name stands in for obs.id.
        d["obs.id"] = a.Str("obsConnection");
        d["obs.name"] = a.Str("obsConnection");
        d["obs.host"] = a.Str("host");
        d["obs.studioVersion"] = a.Str("studioVersion");
        d["obs.websocketVersion"] = a.Str("websocketVersion");
    }

    /// <summary>Shopify orders: Streamer.bot forwards the webhook's order object (top-level fields) plus <c>event</c>.</summary>
    private static void ShopifyOrder(EventArgsView a, JsonObject d)
    {
        if (a.Json("_json") is JsonObject order)
        {
            foreach (var (key, value) in order) d[key] = value?.DeepClone();
        }
        d["event"] = a.Str("shopify.event");
    }

    private static void SpeechToText(EventArgsView a, JsonObject d)
    {
        d["text"] = a.Str("text", "message", "rawInput");
        d["confidence"] = a.Double("confidence");
        if (d["alternatives"] is not JsonArray) d["alternatives"] = new JsonArray();
    }

    /// <summary>Read access to flat event args with fallbacks between alternative names.</summary>
    private readonly struct EventArgsView(IReadOnlyDictionary<string, object?> args, StreamerBotPayloadServices? services)
    {
        public IReadOnlyDictionary<string, object?> Args => args;

        public StreamerBotPayloadServices? Services => services;

        /// <summary>The argument as stored (no conversion).</summary>
        public object? Raw(string key) => Get(key);

        /// <summary>A JSON argument (raw payload text or a JSON value) parsed; null when missing or not JSON.</summary>
        public JsonNode? Json(string key)
        {
            try
            {
                return Get(key) switch
                {
                    string { Length: > 0 } text when text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[') => JsonNode.Parse(text),
                    JsonElement e => JsonNode.Parse(e.GetRawText()),
                    JsonNode n => n.DeepClone(),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private object? Get(string key)
        {
            if (args.TryGetValue(key, out var value)) return value;
            foreach (var (k, v) in args)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }

        public bool Has(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (Get(key) is not null) return true;
            }
            return false;
        }

        public string Str(params string[] keys)
        {
            foreach (var key in keys)
            {
                var text = Get(key) switch
                {
                    null => null,
                    string s => s,
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    var v => v.ToString(),
                };
                if (!string.IsNullOrEmpty(text)) return text;
            }
            return "";
        }

        public long Long(params string[] keys)
        {
            foreach (var key in keys)
            {
                switch (Get(key))
                {
                    case null: continue;
                    case int i: return i;
                    case long l: return l;
                    case double d: return (long)d;
                    case decimal m: return (long)m;
                    case bool: continue;
                    case var v when long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n):
                        return n;
                    case var v when double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var f):
                        return (long)f;
                }
            }
            return 0;
        }

        public int Int(params string[] keys) => (int)Math.Clamp(Long(keys), int.MinValue, int.MaxValue);

        public double Double(params string[] keys)
        {
            foreach (var key in keys)
            {
                switch (Get(key))
                {
                    case null or bool: continue;
                    case double d: return d;
                    case float f: return f;
                    case decimal m: return (double)m;
                    case int i: return i;
                    case long l: return l;
                    case var v when double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n):
                        return n;
                }
            }
            return 0;
        }

        public bool Bool(params string[] keys)
        {
            foreach (var key in keys)
            {
                switch (Get(key))
                {
                    case bool b when b: return true;
                    case string s when bool.TryParse(s, out var parsed) && parsed: return true;
                }
            }
            return false;
        }
    }
}
