using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Events;

namespace Cuebot.Twitch.EventSub;

/// <summary>Translates EventSub notification bodies into Cuebot <see cref="BotEvent"/>s. Pure; no I/O.</summary>
internal static class EventSubMapper
{
    private const string AnonymousName = "Anonymous";

    /// <summary>
    /// Returns null for subscription types (or notice types) Cuebot does not handle. <paramref name="broadcasterId"/>
    /// tells an outgoing raid from an incoming one.
    /// </summary>
    public static BotEvent? Map(string subscriptionType, JsonElement e, string broadcasterId = "") => subscriptionType switch
    {
        // Chat
        "channel.chat.message" => ChatMessage(e),
        "channel.chat.notification" => ChatNotification(e),
        "channel.chat.message_delete" => Event(TwitchEventTypes.ChatMessageDeleted, User(e, "target_user"), a =>
        {
            a["messageId"] = e.Str("message_id");
        }),
        "channel.chat.clear" => Event(TwitchEventTypes.ChatCleared, User(e, "broadcaster_user")),
        "channel.chat.clear_user_messages" => Event(TwitchEventTypes.UserMessagesCleared, User(e, "target_user")),
        "channel.chat_settings.update" => ChatSettings(e),
        "user.whisper.message" => Whisper(e),

        // Channel
        "channel.follow" => Event(TwitchEventTypes.Follow, User(e, "user"), a => a["followedAt"] = e.Str("followed_at")),
        "channel.update" => StreamUpdate(e),
        "stream.online" => StreamOnline(e),
        "stream.offline" => new BotEvent(TwitchEventTypes.StreamOffline, User(e, "broadcaster_user")),
        "channel.raid" => Raid(e, broadcasterId),
        "channel.ad_break.begin" => AdBreak(e),
        "channel.shared_chat.begin" => SharedChatSession(TwitchEventTypes.SharedChatBegin, e),
        "channel.shared_chat.update" => SharedChatSession(TwitchEventTypes.SharedChatUpdate, e),
        "channel.shared_chat.end" => SharedChatSession(TwitchEventTypes.SharedChatEnd, e),

        // Subscriptions and bits
        "channel.subscribe" => Sub(e),
        "channel.subscription.message" => Resub(e),
        "channel.subscription.gift" => GiftSub(e),
        "channel.cheer" => Cheer(e),
        "channel.bits.use" => BitsUse(e),

        // Channel points
        "channel.channel_points_custom_reward_redemption.add" => RewardRedemption(TwitchEventTypes.RewardRedemption, e),
        "channel.channel_points_custom_reward_redemption.update" => RewardRedemption(TwitchEventTypes.RewardRedemptionUpdated, e),
        "channel.channel_points_automatic_reward_redemption.add" => AutomaticReward(e),
        "channel.channel_points_custom_reward.add" => Reward(TwitchEventTypes.RewardCreated, e),
        "channel.channel_points_custom_reward.update" => Reward(TwitchEventTypes.RewardUpdated, e),
        "channel.channel_points_custom_reward.remove" => Reward(TwitchEventTypes.RewardDeleted, e),

        // Polls and predictions
        "channel.poll.begin" => Poll(TwitchEventTypes.PollCreated, e),
        "channel.poll.progress" => Poll(TwitchEventTypes.PollUpdated, e),
        "channel.poll.end" => Poll(e.Str("status").ToLowerInvariant() switch
        {
            "terminated" => TwitchEventTypes.PollTerminated,
            "archived" => TwitchEventTypes.PollArchived,
            _ => TwitchEventTypes.PollCompleted,
        }, e),
        "channel.prediction.begin" => Prediction(TwitchEventTypes.PredictionCreated, e),
        "channel.prediction.progress" => Prediction(TwitchEventTypes.PredictionUpdated, e),
        "channel.prediction.lock" => Prediction(TwitchEventTypes.PredictionLocked, e),
        "channel.prediction.end" => Prediction(e.Str("status").Equals("canceled", StringComparison.OrdinalIgnoreCase)
            ? TwitchEventTypes.PredictionCanceled
            : TwitchEventTypes.PredictionCompleted, e),

        // Hype train, goals, charity
        "channel.hype_train.begin" => HypeTrain(TwitchEventTypes.HypeTrainStart, e),
        "channel.hype_train.progress" => HypeTrain(TwitchEventTypes.HypeTrainUpdate, e),
        "channel.hype_train.end" => HypeTrain(TwitchEventTypes.HypeTrainEnd, e),
        "channel.goal.begin" => Goal(TwitchEventTypes.GoalBegin, e),
        "channel.goal.progress" => Goal(TwitchEventTypes.GoalProgress, e),
        "channel.goal.end" => Goal(TwitchEventTypes.GoalEnd, e),
        "channel.charity_campaign.start" => Charity(TwitchEventTypes.CharityStarted, e),
        "channel.charity_campaign.progress" => Charity(TwitchEventTypes.CharityProgress, e),
        "channel.charity_campaign.donate" => Charity(TwitchEventTypes.CharityDonation, e),
        "channel.charity_campaign.stop" => Charity(TwitchEventTypes.CharityCompleted, e),

        // Shoutouts, shield mode
        "channel.shoutout.create" => Shoutout(TwitchEventTypes.ShoutoutCreated, e, "to_broadcaster_user"),
        "channel.shoutout.receive" => Shoutout(TwitchEventTypes.ShoutoutReceived, e, "from_broadcaster_user"),
        "channel.shield_mode.begin" => Event(TwitchEventTypes.ShieldModeBegin, User(e, "moderator_user"),
            a => a["startedAt"] = e.Str("started_at")),
        "channel.shield_mode.end" => Event(TwitchEventTypes.ShieldModeEnd, User(e, "moderator_user"),
            a => a["endedAt"] = e.Str("ended_at")),

        // Moderation
        "channel.ban" => Ban(e),
        "channel.unban" => Event(TwitchEventTypes.UserUnbanned, User(e, "user"), a => Moderator(a, e)),
        "channel.moderator.add" => new BotEvent(TwitchEventTypes.ModeratorAdded, User(e, "user")),
        "channel.moderator.remove" => new BotEvent(TwitchEventTypes.ModeratorRemoved, User(e, "user")),
        "channel.vip.add" => new BotEvent(TwitchEventTypes.VipAdded, User(e, "user")),
        "channel.vip.remove" => new BotEvent(TwitchEventTypes.VipRemoved, User(e, "user")),
        "channel.warning.send" => Event(TwitchEventTypes.UserWarned, User(e, "user"), a =>
        {
            Moderator(a, e);
            a["reason"] = e.Str("reason");
            a["chatRulesCited"] = string.Join(", ", e.Strings("chat_rules_cited"));
        }),
        "channel.warning.acknowledge" => new BotEvent(TwitchEventTypes.WarningAcknowledged, User(e, "user")),
        "channel.unban_request.create" => Event(TwitchEventTypes.UnbanRequestCreated, User(e, "user"), a =>
        {
            a["requestId"] = e.Str("id");
            a["message"] = e.Str("text");
            a["createdAt"] = e.Str("created_at");
        }),
        "channel.unban_request.resolve" => UnbanResolve(e),
        "channel.suspicious_user.update" => Event(TwitchEventTypes.SuspiciousUserUpdate, User(e, "user"), a =>
        {
            Moderator(a, e);
            a["lowTrustStatus"] = e.Str("low_trust_status");
        }),
        "channel.suspicious_user.message" => SuspiciousMessage(e),
        "automod.message.hold" => Event(TwitchEventTypes.AutomodMessageHeld, User(e, "user"), a =>
        {
            a["messageId"] = e.Str("message_id");
            a["message"] = Text(e);
            a["category"] = e.Str("category");
            a["level"] = e.Int("level");
            a["heldAt"] = e.Str("held_at");
        }),
        "automod.message.update" => Event(TwitchEventTypes.AutomodMessageUpdated, User(e, "user"), a =>
        {
            Moderator(a, e);
            a["messageId"] = e.Str("message_id");
            a["message"] = Text(e);
            a["category"] = e.Str("category");
            a["level"] = e.Int("level");
            a["status"] = e.Str("status");
        }),
        "automod.terms.update" => AutomodTerms(e),
        "channel.moderate" => SharedChatModeration(e),

        // Guest Star
        "channel.guest_star_session.begin" => Event(TwitchEventTypes.GuestStarSessionBegin, User(e, "broadcaster_user"), a =>
        {
            a["sessionId"] = e.Str("session_id");
            a["startedAt"] = e.Str("started_at");
        }),
        "channel.guest_star_session.end" => Event(TwitchEventTypes.GuestStarSessionEnd, User(e, "broadcaster_user"), a =>
        {
            a["sessionId"] = e.Str("session_id");
            a["startedAt"] = e.Str("started_at");
            a["endedAt"] = e.Str("ended_at");
            Host(a, e);
        }),
        "channel.guest_star_guest.update" => GuestStarGuest(e),
        "channel.guest_star_settings.update" => Event(TwitchEventTypes.GuestStarSettingsUpdate, User(e, "broadcaster_user"), a =>
        {
            a["isModeratorSendLiveEnabled"] = e.Bool("is_moderator_send_live_enabled");
            a["slotCount"] = e.Int("slot_count");
            a["isBrowserSourceAudioEnabled"] = e.Bool("is_browser_source_audio_enabled");
            a["groupLayout"] = e.Str("group_layout");
        }),
        _ => null,
    };

    // ---------------------------------------------------------------- chat

    private static BotEvent ChatMessage(JsonElement e)
    {
        var args = User(e, "chatter_user");
        var text = Text(e);
        args["message"] = text;
        args["rawInput"] = text;
        args["messageId"] = e.Str("message_id");
        args["messageKind"] = "Message";
        Roles(args, e, "badges");
        args["bits"] = e.Child("cheer") is { } cheer ? cheer.Int("bits") : 0;
        args["color"] = e.Str("color");

        var type = e.Str("message_type");
        args["messageType"] = type.Length > 0 ? type : "text";
        args["isHighlighted"] = type == "channel_points_highlighted";
        args["isUserIntro"] = type == "user_intro";
        args["isPowerUp"] = type.StartsWith("power_ups_", StringComparison.Ordinal);
        args["rewardId"] = e.Str("channel_points_custom_reward_id");
        Emotes(args, e);

        var reply = e.Child("reply");
        args["isReply"] = reply is not null;
        if (reply is { } r)
        {
            args["replyParentMessageId"] = r.Str("parent_message_id");
            args["replyParentUserId"] = r.Str("parent_user_id");
            args["replyParentUserName"] = r.Str("parent_user_login");
            args["replyParentUser"] = r.Str("parent_user_name");
            args["replyParentMessage"] = r.Str("parent_message_body");
            args["replyThreadMessageId"] = r.Str("thread_message_id");
        }

        SharedChatSource(args, e);
        return new BotEvent(TwitchEventTypes.ChatMessage, args);
    }

    private static BotEvent? ChatNotification(JsonElement e)
    {
        var notice = e.Str("notice_type");
        var anonymous = e.Bool("chatter_is_anonymous");
        var args = anonymous ? AnonymousUser() : User(e, "chatter_user");
        args["isAnonymous"] = anonymous;
        args["message"] = Text(e);
        args["systemMessage"] = e.Str("system_message");
        args["messageId"] = e.Str("message_id");
        args["noticeType"] = notice;
        args["color"] = e.Str("color");
        Roles(args, e, "badges");
        Emotes(args, e);

        var shared = notice.StartsWith("shared_chat_", StringComparison.Ordinal);
        if (shared) SharedChatSource(args, e);
        var detail = e.Child(notice) ?? default;

        switch (shared ? notice["shared_chat_".Length..] : notice)
        {
            case "announcement":
                args["messageKind"] = "Announcement";
                args["announcementColor"] = detail.Str("color").ToLowerInvariant();
                return new BotEvent(shared ? TwitchEventTypes.SharedChatAnnouncement : TwitchEventTypes.Announcement, args);
            case "bits_badge_tier" when !shared:
                args["tier"] = detail.Int("tier");
                return new BotEvent(TwitchEventTypes.BitsBadgeTier, args);
            case "sub" when shared:
                args["tier"] = Tier(detail.Str("sub_tier"));
                args["isPrime"] = detail.Bool("is_prime");
                args["durationMonths"] = detail.Int("duration_months");
                return new BotEvent(TwitchEventTypes.SharedChatSub, args);
            case "resub" when shared:
                args["tier"] = Tier(detail.Str("sub_tier"));
                args["isPrime"] = detail.Bool("is_prime");
                args["cumulativeMonths"] = detail.Int("cumulative_months");
                args["streakMonths"] = detail.Int("streak_months");
                args["durationMonths"] = detail.Int("duration_months");
                return new BotEvent(TwitchEventTypes.SharedChatResub, args);
            case "sub_gift":
                args["recipientUser"] = detail.Str("recipient_user_name");
                args["recipientUserName"] = detail.Str("recipient_user_login");
                args["recipientUserId"] = detail.Str("recipient_user_id");
                args["tier"] = Tier(detail.Str("sub_tier"));
                args["durationMonths"] = detail.Int("duration_months");
                args["cumulativeTotal"] = detail.Int("cumulative_total");
                args["communityGiftId"] = detail.Str("community_gift_id");
                return new BotEvent(shared ? TwitchEventTypes.SharedChatSubGift : TwitchEventTypes.SubGift, args);
            case "community_sub_gift":
                args["total"] = detail.Int("total");
                args["tier"] = Tier(detail.Str("sub_tier"));
                args["cumulativeTotal"] = detail.Int("cumulative_total");
                args["communityGiftId"] = detail.Str("id");
                return new BotEvent(shared ? TwitchEventTypes.SharedChatGiftBomb : TwitchEventTypes.GiftBomb, args);
            case "gift_paid_upgrade":
                Gifter(args, detail);
                return new BotEvent(shared ? TwitchEventTypes.SharedChatGiftPaidUpgrade : TwitchEventTypes.GiftPaidUpgrade, args);
            case "prime_paid_upgrade":
                args["tier"] = Tier(detail.Str("sub_tier"));
                return new BotEvent(shared ? TwitchEventTypes.SharedChatPrimePaidUpgrade : TwitchEventTypes.PrimePaidUpgrade, args);
            case "pay_it_forward":
                Gifter(args, detail);
                return new BotEvent(shared ? TwitchEventTypes.SharedChatPayItForward : TwitchEventTypes.PayItForward, args);
            case "raid" when shared:
                args["raider"] = detail.Str("user_name");
                args["raiderUserName"] = detail.Str("user_login");
                args["raiderUserId"] = detail.Str("user_id");
                args["viewers"] = detail.Int("viewer_count");
                return new BotEvent(TwitchEventTypes.SharedChatRaid, args);
            case "unraid" when !shared:
                return new BotEvent(TwitchEventTypes.RaidCancelled, args);
            case "watch_streak" when !shared:
                args["streak"] = detail.Int("streak_count");
                args["watchStreak"] = detail.Int("streak_count");
                args["channelPointsAwarded"] = detail.Int("channel_points_awarded");
                return new BotEvent(TwitchEventTypes.WatchStreak, args);
            case "modiversary":
                args["months"] = detail.Int("months");
                return new BotEvent(shared ? TwitchEventTypes.SharedChatModiversary : TwitchEventTypes.Modiversary, args);
            default:
                // sub, resub, raid and charity donations have dedicated EventSub types.
                return null;
        }
    }

    private static void Gifter(Dictionary<string, object?> args, JsonElement detail)
    {
        var anonymous = detail.Bool("gifter_is_anonymous");
        args["gifterIsAnonymous"] = anonymous;
        args["gifter"] = anonymous ? AnonymousName : detail.Str("gifter_user_name");
        args["gifterUserName"] = anonymous ? "" : detail.Str("gifter_user_login");
        args["gifterUserId"] = anonymous ? "" : detail.Str("gifter_user_id");
    }

    private static BotEvent ChatSettings(JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["emoteMode"] = e.Bool("emote_mode");
        args["followerMode"] = e.Child("follower_mode_duration_minutes") is { ValueKind: JsonValueKind.Number };
        args["followerModeDuration"] = e.Int("follower_mode_duration_minutes");
        args["slowMode"] = e.Child("slow_mode_wait_time_seconds") is { ValueKind: JsonValueKind.Number };
        args["slowModeWaitTime"] = e.Int("slow_mode_wait_time_seconds");
        args["subscriberMode"] = e.Bool("subscriber_mode");
        args["uniqueChatMode"] = e.Bool("unique_chat_mode");
        return new BotEvent(TwitchEventTypes.ChatSettingsUpdated, args);
    }

    /// <summary>Reads chat settings back from a "twitch.chatSettingsUpdated" event's args.</summary>
    public static TwitchChatSettings ChatSettingsOf(IReadOnlyDictionary<string, object?> args) => new(
        args.GetValueOrDefault("emoteMode") is true, args.GetValueOrDefault("followerMode") is true,
        args.GetValueOrDefault("followerModeDuration") is int f ? f : 0, args.GetValueOrDefault("slowMode") is true,
        args.GetValueOrDefault("slowModeWaitTime") is int s ? s : 0, args.GetValueOrDefault("subscriberMode") is true,
        args.GetValueOrDefault("uniqueChatMode") is true);

    private static BotEvent Whisper(JsonElement e)
    {
        var args = User(e, "from_user");
        var text = (e.Child("whisper") ?? default).Str("text");
        args["message"] = text;
        args["rawInput"] = text;
        args["messageId"] = e.Str("whisper_id");
        args["messageKind"] = "Whisper";
        args["toUser"] = e.Str("to_user_name");
        args["toUserName"] = e.Str("to_user_login");
        args["toUserId"] = e.Str("to_user_id");
        return new BotEvent(TwitchEventTypes.Whisper, args);
    }

    /// <summary>Badge roles plus the raw badge list ("set/id,set/id") and subscriber months.</summary>
    private static void Roles(Dictionary<string, object?> args, JsonElement e, string badgesField)
    {
        var badges = e.Items(badgesField).ToArray();
        var sets = badges.Select(b => b.Str("set_id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        args["isBroadcaster"] = sets.Contains("broadcaster")
            || (e.Str("chatter_user_id").Length > 0 && e.Str("chatter_user_id") == e.Str("broadcaster_user_id"));
        args["isModerator"] = sets.Contains("moderator");
        args["isVip"] = sets.Contains("vip");
        args["isSubscriber"] = sets.Contains("subscriber") || sets.Contains("founder");
        args["isFounder"] = sets.Contains("founder");
        args["isArtist"] = sets.Contains("artist-badge");
        args["badges"] = string.Join(",", badges.Select(b => $"{b.Str("set_id")}/{b.Str("id")}"));
        var sub = badges.FirstOrDefault(b => b.Str("set_id") is "subscriber" or "founder");
        args["monthsSubscribed"] = int.TryParse(sub.ValueKind == JsonValueKind.Object ? sub.Str("info") : "", NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var months) ? months : 0;
    }

    /// <summary>
    /// Emote positions in the Twitch IRC format ("id:0-4,6-10/id2:12-16", code points, inclusive) plus names and count,
    /// computed from the message fragments.
    /// </summary>
    internal static void Emotes(Dictionary<string, object?> args, JsonElement e)
    {
        var ranges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var names = new List<string>();
        var position = 0;
        var count = 0;
        foreach (var fragment in (e.Child("message") ?? default).Items("fragments"))
        {
            var text = fragment.Str("text");
            var length = text.EnumerateRunes().Count();
            if (fragment.Str("type") == "emote" && fragment.Child("emote") is { } emote && length > 0)
            {
                var id = emote.Str("id");
                if (!ranges.TryGetValue(id, out var list)) ranges[id] = list = [];
                list.Add($"{position}-{position + length - 1}");
                if (!names.Contains(text, StringComparer.Ordinal)) names.Add(text);
                count++;
            }
            position += length;
        }
        args["emotes"] = string.Join("/", ranges.Select(r => $"{r.Key}:{string.Join(",", r.Value)}"));
        args["emoteNames"] = string.Join(",", names);
        args["emoteCount"] = count;
    }

    /// <summary>Shared chat: where the message really comes from, and whether that is another channel.</summary>
    private static void SharedChatSource(Dictionary<string, object?> args, JsonElement e)
    {
        var source = e.Str("source_broadcaster_user_id");
        var channel = e.Str("broadcaster_user_id");
        args["broadcasterId"] = channel;
        args["broadcasterUserName"] = e.Str("broadcaster_user_login");
        args["sourceBroadcasterId"] = source;
        args["sourceBroadcasterUserName"] = e.Str("source_broadcaster_user_login");
        args["sourceBroadcaster"] = e.Str("source_broadcaster_user_name");
        args["sourceMessageId"] = e.Str("source_message_id");
        args["isSharedChat"] = source.Length > 0 && channel.Length > 0 && source != channel;
    }

    // ---------------------------------------------------------------- channel

    private static BotEvent StreamUpdate(JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["title"] = e.Str("title");
        args["language"] = e.Str("language");
        args["categoryId"] = e.Str("category_id");
        args["categoryName"] = e.Str("category_name");
        args["gameId"] = e.Str("category_id");
        args["gameName"] = e.Str("category_name");
        args["contentLabels"] = string.Join(",", e.Strings("content_classification_labels"));
        return new BotEvent(TwitchEventTypes.StreamUpdate, args);
    }

    private static BotEvent StreamOnline(JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["streamType"] = e.Str("type");
        args["startedAt"] = e.Str("started_at");
        return new BotEvent(TwitchEventTypes.StreamOnline, args);
    }

    private static BotEvent Raid(JsonElement e, string broadcasterId)
    {
        var outgoing = broadcasterId.Length > 0 && e.Str("from_broadcaster_user_id") == broadcasterId
                                                && e.Str("to_broadcaster_user_id") != broadcasterId;
        var args = User(e, outgoing ? "to_broadcaster_user" : "from_broadcaster_user");
        args["viewers"] = e.Int("viewers");
        return new BotEvent(outgoing ? TwitchEventTypes.RaidSend : TwitchEventTypes.Raid, args);
    }

    private static BotEvent AdBreak(JsonElement e)
    {
        var args = User(e, "requester_user");
        args["duration"] = e.Int("duration_seconds");
        args["startedAt"] = e.Str("started_at");
        args["isAutomatic"] = e.Bool("is_automatic");
        return new BotEvent(TwitchEventTypes.AdRun, args);
    }

    private static BotEvent SharedChatSession(string type, JsonElement e)
    {
        var args = User(e, "host_broadcaster_user");
        args["sessionId"] = e.Str("session_id");
        var participants = e.Items("participants").ToArray();
        args["participants"] = string.Join(",", participants.Select(p => p.Str("broadcaster_user_login")));
        args["participantCount"] = participants.Length;
        args["isHost"] = e.Str("host_broadcaster_user_id") == e.Str("broadcaster_user_id");
        return new BotEvent(type, args);
    }

    // ---------------------------------------------------------------- subs and bits

    private static BotEvent Sub(JsonElement e)
    {
        var args = User(e, "user");
        args["tier"] = Tier(e);
        args["isGift"] = e.Bool("is_gift");
        return new BotEvent(TwitchEventTypes.Sub, args);
    }

    private static BotEvent Resub(JsonElement e)
    {
        var args = User(e, "user");
        args["tier"] = Tier(e);
        args["message"] = Text(e);
        args["cumulativeMonths"] = e.Int("cumulative_months");
        args["streakMonths"] = e.Int("streak_months");
        args["durationMonths"] = e.Int("duration_months");
        Emotes(args, e);
        return new BotEvent(TwitchEventTypes.Resub, args);
    }

    private static BotEvent GiftSub(JsonElement e)
    {
        var anonymous = e.Bool("is_anonymous");
        var args = anonymous ? AnonymousUser() : User(e, "user");
        args["isAnonymous"] = anonymous;
        args["total"] = e.Int("total");
        args["tier"] = Tier(e);
        args["cumulativeTotal"] = e.Int("cumulative_total");
        return new BotEvent(TwitchEventTypes.GiftSub, args);
    }

    private static BotEvent Cheer(JsonElement e)
    {
        var anonymous = e.Bool("is_anonymous");
        var args = anonymous ? AnonymousUser() : User(e, "user");
        args["isAnonymous"] = anonymous;
        args["bits"] = e.Int("bits");
        args["message"] = e.Str("message");
        return new BotEvent(TwitchEventTypes.Cheer, args);
    }

    private static BotEvent BitsUse(JsonElement e)
    {
        var args = User(e, "user");
        var type = e.Str("type");
        args["bits"] = e.Int("bits");
        args["bitsType"] = type;
        args["message"] = Text(e);
        Emotes(args, e);
        var powerUp = e.Child("power_up") ?? default;
        args["powerUpType"] = powerUp.Str("type");
        var emote = powerUp.Child("emote") ?? default;
        args["powerUpEmoteId"] = emote.Str("id");
        args["powerUpEmoteName"] = emote.Str("name");
        args["messageEffectId"] = powerUp.Str("message_effect_id");
        return new BotEvent(type == "power_up" ? TwitchEventTypes.PowerUp : TwitchEventTypes.BitsUsed, args);
    }

    // ---------------------------------------------------------------- channel points

    private static BotEvent RewardRedemption(string type, JsonElement e)
    {
        var reward = e.Child("reward") ?? default;
        var args = User(e, "user");
        args["rewardId"] = reward.Str("id");
        args["rewardTitle"] = reward.Str("title");
        args["rewardCost"] = reward.Int("cost");
        args["rewardPrompt"] = reward.Str("prompt");
        args["userInput"] = e.Str("user_input");
        args["redemptionId"] = e.Str("id");
        args["status"] = e.Str("status");
        args["redeemedAt"] = e.Str("redeemed_at");
        return new BotEvent(type, args);
    }

    private static BotEvent AutomaticReward(JsonElement e)
    {
        var reward = e.Child("reward") ?? default;
        var args = User(e, "user");
        args["rewardType"] = reward.Str("type");
        var cost = reward.Int("channel_points");
        args["rewardCost"] = cost > 0 ? cost : reward.Int("cost");
        var emote = reward.Child("emote") ?? (reward.Child("unlocked_emote") ?? default);
        args["emoteId"] = emote.Str("id");
        args["emoteName"] = emote.Str("name");
        args["message"] = Text(e);
        args["userInput"] = Text(e);
        args["redemptionId"] = e.Str("id");
        args["redeemedAt"] = e.Str("redeemed_at");
        return new BotEvent(TwitchEventTypes.AutomaticRewardRedemption, args);
    }

    private static BotEvent Reward(string type, JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["rewardId"] = e.Str("id");
        args["rewardTitle"] = e.Str("title");
        args["rewardCost"] = e.Int("cost");
        args["rewardPrompt"] = e.Str("prompt");
        args["isEnabled"] = e.Bool("is_enabled");
        args["isPaused"] = e.Bool("is_paused");
        args["isInStock"] = e.Bool("is_in_stock");
        args["backgroundColor"] = e.Str("background_color");
        args["isUserInputRequired"] = e.Bool("is_user_input_required");
        return new BotEvent(type, args);
    }

    // ---------------------------------------------------------------- polls and predictions

    private static BotEvent Poll(string type, JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["pollId"] = e.Str("id");
        args["title"] = e.Str("title");
        args["status"] = e.Str("status");
        args["startedAt"] = e.Str("started_at");
        args["endsAt"] = e.Str("ends_at");
        args["endedAt"] = e.Str("ended_at");
        var choices = e.Items("choices").ToArray();
        args["choiceCount"] = choices.Length;
        var total = 0;
        var winner = "";
        var best = -1;
        for (var i = 0; i < choices.Length; i++)
        {
            var votes = choices[i].Int("votes");
            total += votes;
            args[$"choice{i}Id"] = choices[i].Str("id");
            args[$"choice{i}Title"] = choices[i].Str("title");
            args[$"choice{i}Votes"] = votes;
            args[$"choice{i}ChannelPointsVotes"] = choices[i].Int("channel_points_votes");
            if (votes > best)
            {
                best = votes;
                winner = choices[i].Str("title");
            }
        }
        args["totalVotes"] = total;
        args["winningChoice"] = winner;
        args["winningChoiceVotes"] = Math.Max(best, 0);
        return new BotEvent(type, args);
    }

    private static BotEvent Prediction(string type, JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["predictionId"] = e.Str("id");
        args["title"] = e.Str("title");
        args["status"] = e.Str("status");
        args["startedAt"] = e.Str("started_at");
        args["locksAt"] = e.Str("locks_at");
        args["lockedAt"] = e.Str("locked_at");
        args["endedAt"] = e.Str("ended_at");
        var outcomes = e.Items("outcomes").ToArray();
        var winningId = e.Str("winning_outcome_id");
        args["outcomeCount"] = outcomes.Length;
        args["winningOutcomeId"] = winningId;
        args["winningOutcome"] = "";
        var users = 0;
        var points = 0;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var o = outcomes[i];
            users += o.Int("users");
            points += o.Int("channel_points");
            args[$"outcome{i}Id"] = o.Str("id");
            args[$"outcome{i}Title"] = o.Str("title");
            args[$"outcome{i}Color"] = o.Str("color");
            args[$"outcome{i}Users"] = o.Int("users");
            args[$"outcome{i}ChannelPoints"] = o.Int("channel_points");
            if (o.Str("id") == winningId && winningId.Length > 0) args["winningOutcome"] = o.Str("title");
        }
        args["totalUsers"] = users;
        args["totalChannelPoints"] = points;
        return new BotEvent(type, args);
    }

    // ---------------------------------------------------------------- hype train, goals, charity

    private static BotEvent HypeTrain(string type, JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["hypeTrainId"] = e.Str("id");
        args["level"] = Math.Max(1, e.Int("level"));
        args["total"] = e.Int("total");
        args["progress"] = e.Int("progress");
        args["goal"] = e.Int("goal");
        args["percent"] = e.Int("goal") > 0 ? Math.Min(100, e.Int("progress") * 100 / e.Int("goal")) : 0;
        var trainType = e.Str("type");
        args["trainType"] = trainType.Length > 0 ? trainType : "regular";
        args["isGoldenKappa"] = trainType == "golden_kappa" || e.Bool("is_golden_kappa_train");
        args["isSharedTrain"] = e.Bool("is_shared_train");
        args["allTimeHighLevel"] = e.Int("all_time_high_level");
        args["startedAt"] = e.Str("started_at");
        args["expiresAt"] = e.Str("expires_at");
        args["endedAt"] = e.Str("ended_at");
        args["cooldownEndsAt"] = e.Str("cooldown_ends_at");

        var contributions = e.Items("top_contributions").ToArray();
        var top = contributions.OrderByDescending(c => c.Int("total")).FirstOrDefault();
        args["topContributor"] = top.ValueKind == JsonValueKind.Object ? top.Str("user_name") : "";
        args["topContributorTotal"] = top.ValueKind == JsonValueKind.Object ? top.Int("total") : 0;
        foreach (var (kind, prefix) in new[] { ("bits", "topBits"), ("subscription", "topSub"), ("other", "topOther") })
        {
            var c = contributions.FirstOrDefault(x => x.Str("type") == kind);
            args[prefix + "User"] = c.ValueKind == JsonValueKind.Object ? c.Str("user_name") : "";
            args[prefix + "UserId"] = c.ValueKind == JsonValueKind.Object ? c.Str("user_id") : "";
            args[prefix + "Total"] = c.ValueKind == JsonValueKind.Object ? c.Int("total") : 0;
        }
        return new BotEvent(type, args);
    }

    private static BotEvent Goal(string type, JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["goalId"] = e.Str("id");
        args["goalType"] = e.Str("type");
        args["description"] = e.Str("description");
        args["currentAmount"] = e.Int("current_amount");
        args["targetAmount"] = e.Int("target_amount");
        args["isAchieved"] = e.Bool("is_achieved");
        args["startedAt"] = e.Str("started_at");
        args["endedAt"] = e.Str("ended_at");
        return new BotEvent(type, args);
    }

    private static BotEvent Charity(string type, JsonElement e)
    {
        var donation = type == TwitchEventTypes.CharityDonation;
        var args = donation ? User(e, "user") : User(e, "broadcaster_user");
        args["campaignId"] = e.Str(donation ? "campaign_id" : "id");
        args["charityName"] = e.Str("charity_name");
        args["charityDescription"] = e.Str("charity_description");
        args["charityWebsite"] = e.Str("charity_website");
        var amount = e.Child("amount") ?? default;
        args["amount"] = TwitchApi.Money(amount);
        args["currency"] = amount.ValueKind == JsonValueKind.Object ? amount.Str("currency") : (e.Child("current_amount") ?? default).Str("currency");
        args["currentAmount"] = TwitchApi.Money(e.Child("current_amount") ?? default);
        args["targetAmount"] = TwitchApi.Money(e.Child("target_amount") ?? default);
        return new BotEvent(type, args);
    }

    private static BotEvent Shoutout(string type, JsonElement e, string userPrefix)
    {
        var args = User(e, userPrefix);
        Moderator(args, e);
        args["viewers"] = e.Int("viewer_count");
        args["startedAt"] = e.Str("started_at");
        args["cooldownEndsAt"] = e.Str("cooldown_ends_at");
        args["targetCooldownEndsAt"] = e.Str("target_cooldown_ends_at");
        return new BotEvent(type, args);
    }

    // ---------------------------------------------------------------- moderation

    private static BotEvent Ban(JsonElement e)
    {
        var args = User(e, "user");
        Moderator(args, e);
        var permanent = e.Bool("is_permanent");
        args["reason"] = e.Str("reason");
        args["isPermanent"] = permanent;
        args["bannedAt"] = e.Str("banned_at");
        args["endsAt"] = e.Str("ends_at");
        var duration = 0;
        if (!permanent && DateTimeOffset.TryParse(e.Str("banned_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
                       && DateTimeOffset.TryParse(e.Str("ends_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to))
            duration = (int)Math.Round((to - from).TotalSeconds);
        args["duration"] = duration;
        return new BotEvent(permanent ? TwitchEventTypes.UserBanned : TwitchEventTypes.UserTimedOut, args);
    }

    private static BotEvent? UnbanResolve(JsonElement e)
    {
        var status = e.Str("status").ToLowerInvariant();
        if (status is not ("approved" or "denied")) return null;
        var args = User(e, "user");
        Moderator(args, e);
        args["requestId"] = e.Str("id");
        args["resolutionText"] = e.Str("resolution_text");
        args["status"] = status;
        return new BotEvent(status == "approved" ? TwitchEventTypes.UnbanRequestApproved : TwitchEventTypes.UnbanRequestDenied, args);
    }

    private static BotEvent SuspiciousMessage(JsonElement e)
    {
        var args = User(e, "user");
        args["lowTrustStatus"] = e.Str("low_trust_status");
        args["sharedBanChannelIds"] = string.Join(",", e.Strings("shared_ban_channel_ids"));
        args["types"] = string.Join(",", e.Strings("types"));
        args["banEvasionEvaluation"] = e.Str("ban_evasion_evaluation");
        var message = e.Child("message") ?? default;
        args["message"] = message.Str("text");
        args["messageId"] = message.Str("message_id");
        return new BotEvent(TwitchEventTypes.SuspiciousUserMessage, args);
    }

    private static BotEvent? AutomodTerms(JsonElement e)
    {
        var type = e.Str("action") switch
        {
            "add_blocked" => TwitchEventTypes.BlockedTermsAdded,
            "remove_blocked" => TwitchEventTypes.BlockedTermsDeleted,
            "add_permitted" => TwitchEventTypes.PermittedTermsAdded,
            "remove_permitted" => TwitchEventTypes.PermittedTermsDeleted,
            _ => null,
        };
        if (type is null) return null;
        var args = User(e, "moderator_user");
        var terms = e.Strings("terms");
        args["terms"] = string.Join(", ", terms);
        args["termCount"] = terms.Count;
        args["fromAutomod"] = e.Bool("from_automod");
        return new BotEvent(type, args);
    }

    /// <summary>
    /// channel.moderate: only the shared_chat_* actions (moderation in another channel of the shared chat session);
    /// the broadcaster's own bans, timeouts and deletions arrive through their dedicated subscriptions.
    /// </summary>
    private static BotEvent? SharedChatModeration(JsonElement e)
    {
        var action = e.Str("action");
        var type = action switch
        {
            "shared_chat_ban" => TwitchEventTypes.SharedChatUserBanned,
            "shared_chat_timeout" => TwitchEventTypes.SharedChatUserTimedOut,
            "shared_chat_unban" => TwitchEventTypes.SharedChatUserUnbanned,
            "shared_chat_untimeout" => TwitchEventTypes.SharedChatUserUntimedOut,
            "shared_chat_delete" => TwitchEventTypes.SharedChatMessageDeleted,
            _ => null,
        };
        if (type is null) return null;
        var detail = e.Child(action) ?? default;
        var args = User(detail, "user");
        Moderator(args, e);
        args["action"] = action;
        args["reason"] = detail.Str("reason");
        args["broadcasterId"] = e.Str("broadcaster_user_id");
        args["sourceBroadcasterId"] = e.Str("source_broadcaster_user_id");
        args["sourceBroadcasterUserName"] = e.Str("source_broadcaster_user_login");
        args["sourceBroadcaster"] = e.Str("source_broadcaster_user_name");
        args["isSharedChat"] = true;
        if (type == TwitchEventTypes.SharedChatUserTimedOut)
        {
            args["expiresAt"] = detail.Str("expires_at");
            args["duration"] = DateTimeOffset.TryParse(detail.Str("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out var expires) ? Math.Max(0, (int)Math.Round((expires - DateTimeOffset.UtcNow).TotalSeconds)) : 0;
        }
        if (type == TwitchEventTypes.SharedChatMessageDeleted)
        {
            args["messageId"] = detail.Str("message_id");
            args["message"] = detail.Str("message_body");
        }
        return new BotEvent(type, args);
    }

    // ---------------------------------------------------------------- guest star

    private static BotEvent GuestStarGuest(JsonElement e)
    {
        var args = User(e, "guest_user");
        Moderator(args, e);
        Host(args, e);
        args["sessionId"] = e.Str("session_id");
        args["slotId"] = e.Str("slot_id");
        args["state"] = e.Str("state");
        args["hostVideoEnabled"] = e.Bool("host_video_enabled");
        args["hostAudioEnabled"] = e.Bool("host_audio_enabled");
        args["hostVolume"] = e.Int("host_volume");
        return new BotEvent(TwitchEventTypes.GuestStarGuestUpdate, args);
    }

    private static void Host(Dictionary<string, object?> args, JsonElement e)
    {
        args["host"] = e.Str("host_user_name");
        args["hostUserName"] = e.Str("host_user_login");
        args["hostUserId"] = e.Str("host_user_id");
    }

    private static void Moderator(Dictionary<string, object?> args, JsonElement e)
    {
        args["moderator"] = e.Str("moderator_user_name");
        args["moderatorUserName"] = e.Str("moderator_user_login");
        args["moderatorUserId"] = e.Str("moderator_user_id");
    }

    // ---------------------------------------------------------------- helpers

    private static BotEvent Event(string type, Dictionary<string, object?> args, Action<Dictionary<string, object?>>? fill = null)
    {
        fill?.Invoke(args);
        return new BotEvent(type, args);
    }

    /// <summary>The text of a <c>message</c> field that is either an object with <c>text</c> or a plain string.</summary>
    private static string Text(JsonElement e) => e.Child("message") switch
    {
        { ValueKind: JsonValueKind.Object } message => message.Str("text"),
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? "",
        _ => "",
    };

    /// <summary>Reads <c>{prefix}_name</c>, <c>{prefix}_login</c> and <c>{prefix}_id</c> into user, userName, userId.</summary>
    private static Dictionary<string, object?> User(JsonElement e, string prefix) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = e.Str($"{prefix}_name"),
        ["userName"] = e.Str($"{prefix}_login"),
        ["userId"] = e.Str($"{prefix}_id"),
    };

    private static Dictionary<string, object?> AnonymousUser() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = AnonymousName,
        ["userName"] = "",
        ["userId"] = "",
    };

    private static string Tier(JsonElement e) => Tier(e.Str("tier"));

    /// <summary>EventSub tiers are "1000"/"2000"/"3000"; Cuebot exposes "1"/"2"/"3".</summary>
    private static string Tier(string tier) =>
        tier.Length == 4 && tier.EndsWith("000", StringComparison.Ordinal) ? tier[..1] : tier;
}
