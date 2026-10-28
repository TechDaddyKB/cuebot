using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Events;

namespace Cuebot.YouTube.Chat;

/// <summary>State the mapper needs across items of one chat: who the bot is, recent authors, polls seen.</summary>
internal sealed class ChatContext(string broadcastId, string liveChatId, ChatterDirectory chatters)
{
    private readonly HashSet<string> _polls = [];

    public string BroadcastId { get; } = broadcastId;
    public string LiveChatId { get; } = liveChatId;
    public ChatterDirectory Chatters { get; } = chatters;

    /// <summary>Channel id of the bot account, so its own messages can be flagged <c>isBotAccount</c>.</summary>
    public string BotChannelId { get; set; } = "";

    /// <summary>True the first time a poll id is seen.</summary>
    public bool FirstSightOfPoll(string id) => _polls.Add(id);
}

/// <summary>Result of mapping one chat item.</summary>
internal sealed record ChatItemResult(IReadOnlyList<BotEvent> Events, bool ChatEnded = false)
{
    public static ChatItemResult None { get; } = new([]);
}

/// <summary>
/// Maps liveChatMessages resources to <see cref="BotEvent"/>s. Pure apart from the <see cref="ChatContext"/>, which
/// remembers authors (to name the author of a deleted message) and polls (to tell started from updated).
/// </summary>
internal static class ChatItemMapper
{
    public const string Platform = "YouTube";

    /// <summary>
    /// Identity for de-duplication. Polls are durable items that YouTube re-sends with the same id as votes change,
    /// so their key includes the poll state.
    /// </summary>
    public static string DedupeKey(JsonElement item)
    {
        var id = item.Str("id");
        var snippet = item.Prop("snippet");
        if (snippet.Str("type") != "pollEvent") return id;
        var details = snippet.Prop("pollDetails").GetRawText();
        return id + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(details)), 0, 8);
    }

    public static ChatItemResult Map(JsonElement item, ChatContext ctx)
    {
        var snippet = item.Prop("snippet");
        var author = item.Prop("authorDetails");
        var type = snippet.Str("type");
        var id = item.Str("id");
        if (author.Str("channelId") is { Length: > 0 } authorId)
        {
            ctx.Chatters.Remember(authorId, author.Str("displayName"));
            ctx.Chatters.RememberDetails(authorId, author.Str("profileImageUrl"), author.Bool("isChatSponsor"));
        }

        switch (type)
        {
            case "textMessageEvent":
            {
                var text = snippet.At("textMessageDetails").Str("messageText");
                if (text.Length == 0) text = snippet.Str("displayMessage");
                var args = Base(item, ctx);
                args["message"] = text;
                args["rawInput"] = text;
                ctx.Chatters.RememberMessage(id, author.Str("channelId"), author.Str("displayName"), text);
                return One(YouTubeEventTypes.ChatMessage, args);
            }
            case "superChatEvent":
            {
                var d = snippet.Prop("superChatDetails");
                var args = Base(item, ctx);
                var comment = d.Str("userComment");
                args["message"] = comment;
                args["rawInput"] = comment;
                AddMoney(args, d);
                ctx.Chatters.RememberMessage(id, author.Str("channelId"), author.Str("displayName"), comment);
                return One(YouTubeEventTypes.SuperChat, args);
            }
            case "superStickerEvent":
            {
                var d = snippet.Prop("superStickerDetails");
                var meta = d.Prop("superStickerMetadata");
                var args = Base(item, ctx);
                args["stickerId"] = meta.Str("stickerId");
                args["stickerAltText"] = meta.Str("altText");
                args["stickerLanguage"] = meta.Str("language");
                args["message"] = meta.Str("altText");
                AddMoney(args, d);
                return One(YouTubeEventTypes.SuperSticker, args);
            }
            case "newSponsorEvent":
            {
                var d = snippet.Prop("newSponsorDetails");
                var args = Base(item, ctx);
                args["levelName"] = d.Str("memberLevelName");
                args["isUpgrade"] = d.Bool("isUpgrade");
                args["message"] = snippet.Str("displayMessage");
                return One(YouTubeEventTypes.NewSponsor, args);
            }
            case "memberMilestoneChatEvent":
            {
                var d = snippet.Prop("memberMilestoneChatDetails");
                var args = Base(item, ctx);
                args["months"] = d.Int("memberMonth");
                args["levelName"] = d.Str("memberLevelName");
                args["message"] = d.Str("userComment");
                args["rawInput"] = d.Str("userComment");
                return One(YouTubeEventTypes.MemberMilestone, args);
            }
            case "membershipGiftingEvent":
            {
                var d = snippet.Prop("membershipGiftingDetails");
                var args = Base(item, ctx);
                args["total"] = d.Int("giftMembershipsCount");
                args["levelName"] = d.Str("giftMembershipsLevelName");
                args["message"] = snippet.Str("displayMessage");
                return One(YouTubeEventTypes.MembershipGift, args);
            }
            case "giftMembershipReceivedEvent":
            {
                var d = snippet.Prop("giftMembershipReceivedDetails");
                var args = Base(item, ctx);
                var gifterId = d.Str("gifterChannelId");
                args["levelName"] = d.Str("memberLevelName");
                args["gifterId"] = gifterId;
                args["gifterName"] = ctx.Chatters.NameOf(gifterId) ?? "";
                if (ctx.Chatters.DetailsOf(gifterId) is { } gifter)
                {
                    // Streamer.bot: gifterProfileUrl, gifterIsSponsor
                    args["gifterProfileUrl"] = gifter.ProfileUrl;
                    args["gifterIsSponsor"] = gifter.IsSponsor;
                }
                args["giftId"] = d.Str("associatedMembershipGiftingMessageId");
                args["message"] = snippet.Str("displayMessage");
                return One(YouTubeEventTypes.GiftMembershipReceived, args);
            }
            case "giftEvent":
                return One(YouTubeEventTypes.JewelsGifted, JewelsArgs(item, ctx));
            case "messageDeletedEvent":
                return MessageDeleted(item, ctx);
            case "userBannedEvent":
                return UserBanned(item, ctx);
            case "sponsorOnlyModeStartedEvent":
                return One(YouTubeEventTypes.SponsorOnlyStarted, ModeratorArgs(item, ctx));
            case "sponsorOnlyModeEndedEvent":
                return One(YouTubeEventTypes.SponsorOnlyEnded, ModeratorArgs(item, ctx));
            case "pollEvent":
                return Poll(item, ctx);
            case "chatEndedEvent":
                return new ChatItemResult([], ChatEnded: true);
            default:
                return ChatItemResult.None;   // tombstone and types Cuebot does not handle
        }
    }

    private static Dictionary<string, object?> Base(JsonElement item, ChatContext ctx)
    {
        var snippet = item.Prop("snippet");
        var a = item.Prop("authorDetails");
        var channelId = a.Str("channelId");
        if (channelId.Length == 0) channelId = snippet.Str("authorChannelId");
        var member = a.Bool("isChatSponsor");
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform"] = Platform,
            ["user"] = a.Str("displayName"),
            ["userName"] = a.Str("displayName"),
            ["userId"] = channelId,
            ["userProfileUrl"] = a.Str("profileImageUrl"),
            ["channelUrl"] = a.Str("channelUrl"),
            ["messageId"] = item.Str("id"),
            ["publishedAt"] = snippet.Str("publishedAt"),
            ["broadcastId"] = ctx.BroadcastId,
            ["liveChatId"] = ctx.LiveChatId,
            ["isBroadcaster"] = a.Bool("isChatOwner"),
            ["isModerator"] = a.Bool("isChatModerator"),
            ["isSubscriber"] = member,
            ["isMember"] = member,
            ["isSponsor"] = member,
            ["isVerified"] = a.Bool("isVerified"),
            ["isBotAccount"] = ctx.BotChannelId.Length > 0 && channelId == ctx.BotChannelId,
        };
    }

    private static void AddMoney(Dictionary<string, object?> args, JsonElement details)
    {
        var micros = details.Long("amountMicros");
        args["amount"] = micros / 1_000_000d;
        args["amountMicros"] = micros;
        args["currency"] = details.Str("currency");
        args["amountText"] = details.Str("amountDisplayString");
        args["tier"] = details.Int("tier");
    }

    private static Dictionary<string, object?> ModeratorArgs(JsonElement item, ChatContext ctx)
    {
        var args = Base(item, ctx);
        args["moderator"] = args["user"];
        args["moderatorId"] = args["userId"];
        return args;
    }

    private static ChatItemResult MessageDeleted(JsonElement item, ChatContext ctx)
    {
        var deletedId = item.At("snippet", "messageDeletedDetails").Str("deletedMessageId");
        var args = ModeratorArgs(item, ctx);
        var target = ctx.Chatters.MessageAuthor(deletedId);
        args["messageId"] = deletedId;
        args["targetMessageId"] = deletedId;
        args["deletionEventId"] = item.Str("id");
        args["targetUser"] = target?.UserName ?? "";
        args["targetUserId"] = target?.UserId ?? "";
        args["targetMessage"] = target?.Text ?? "";
        var neutral = new Dictionary<string, object?>
        {
            ["platform"] = Platform,
            ["messageId"] = deletedId,
            ["userId"] = target?.UserId ?? "",
            ["user"] = target?.UserName ?? "",
        };
        return new ChatItemResult([
            new BotEvent(YouTubeEventTypes.MessageDeleted, args),
            new BotEvent(YouTubeEventTypes.NeutralMessageDeleted, neutral),
        ]);
    }

    private static ChatItemResult UserBanned(JsonElement item, ChatContext ctx)
    {
        var d = item.At("snippet", "userBannedDetails");
        var banned = d.Prop("bannedUserDetails");
        var temporary = d.Str("banType") == "temporary";
        var duration = d.Long("banDurationSeconds");
        var args = ModeratorArgs(item, ctx);
        args["user"] = banned.Str("displayName");
        args["userName"] = banned.Str("displayName");
        args["userId"] = banned.Str("channelId");
        args["userProfileUrl"] = banned.Str("profileImageUrl");
        args["channelUrl"] = banned.Str("channelUrl");
        args["banType"] = temporary ? "temporary" : "permanent";
        args["duration"] = temporary ? duration : 0L;
        foreach (var flag in (string[])["isBroadcaster", "isModerator", "isSubscriber", "isMember", "isSponsor", "isVerified", "isBotAccount"])
            args.Remove(flag);
        var neutral = new Dictionary<string, object?>
        {
            ["platform"] = Platform,
            ["userId"] = banned.Str("channelId"),
            ["user"] = banned.Str("displayName"),
            ["userName"] = banned.Str("displayName"),
        };
        if (temporary) neutral["duration"] = duration;
        return new ChatItemResult([
            new BotEvent(temporary ? YouTubeEventTypes.UserTimedOut : YouTubeEventTypes.UserBanned, args),
            new BotEvent(temporary ? YouTubeEventTypes.NeutralUserTimedOut : YouTubeEventTypes.NeutralUserBanned, neutral),
        ]);
    }

    private static ChatItemResult Poll(JsonElement item, ChatContext ctx)
    {
        var details = item.At("snippet", "pollDetails");
        var meta = details.Prop("metadata");
        var id = details.Str("id") is { Length: > 0 } pollId ? pollId : item.Str("id");
        var args = ModeratorArgs(item, ctx);
        args["pollId"] = id;
        args["pollMessageId"] = item.Str("id");
        args["pollTitle"] = meta.Str("questionText");
        args["pollStatus"] = meta.Str("status");
        var options = meta.Items("options").ToArray();
        long total = 0, best = -1;
        var winner = "";
        for (var i = 0; i < options.Length; i++)
        {
            var text = options[i].Str("optionText");
            var votes = options[i].Long("tally");
            var n = i.ToString(CultureInfo.InvariantCulture);
            args["pollChoice" + n + "Title"] = text;
            args["pollChoice" + n + "Votes"] = votes;
            total += votes;
            if (votes > best) { best = votes; winner = text; }
        }
        args["pollChoiceCount"] = options.Length;
        args["pollTotalVotes"] = total;
        args["pollWinningChoice"] = options.Length > 0 ? winner : "";
        args["pollWinningVotes"] = Math.Max(best, 0);

        var first = ctx.FirstSightOfPoll(id);
        var type = meta.Str("status") == "closed" ? YouTubeEventTypes.PollClosed
            : first ? YouTubeEventTypes.PollStarted
            : YouTubeEventTypes.PollUpdated;
        return One(type, args);
    }

    /// <summary>
    /// Jewels gifts (YouTube's vertical-live gifts). The API documents few fields for them, so the amount is read from
    /// whichever jewels count the details carry.
    /// </summary>
    private static Dictionary<string, object?> JewelsArgs(JsonElement item, ChatContext ctx)
    {
        var snippet = item.Prop("snippet");
        var details = snippet.Prop("giftDetails");
        if (!details.Exists()) details = snippet.Prop("giftEventDetails");
        var args = Base(item, ctx);
        long jewels = 0;
        foreach (var key in (string[])["jewelsAmount", "jewelsCount", "jewels", "amount"])
            if ((jewels = details.Long(key)) > 0) break;
        args["jewels"] = jewels;
        args["amount"] = jewels;
        args["giftName"] = details.Str("giftName") is { Length: > 0 } name ? name : details.Str("altText");
        args["comboCount"] = Math.Max(1, details.Int("comboCount"));
        // Streamer.bot's gift.* fields, when the details carry them under the same name.
        foreach (var field in (string[])["url", "durationInSeconds", "durationInNanoSeconds", "altText", "altTextLanguage"])
            if (details.Str(field) is { Length: > 0 } value) args["gift." + field] = value;
        if (details.Prop("hasVisualEffect").Exists()) args["gift.hasVisualEffect"] = details.Bool("hasVisualEffect");
        args["message"] = snippet.Str("displayMessage");
        return args;
    }

    private static ChatItemResult One(string type, Dictionary<string, object?> args) => new([new BotEvent(type, args)]);
}
