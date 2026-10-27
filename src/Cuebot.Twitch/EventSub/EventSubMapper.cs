using System.Text.Json;
using Cuebot.Core.Events;

namespace Cuebot.Twitch.EventSub;

/// <summary>Translates EventSub notification bodies into Cuebot <see cref="BotEvent"/>s. Pure; no I/O.</summary>
internal static class EventSubMapper
{
    private const string AnonymousName = "Anonymous";

    /// <summary>Returns null for subscription types Cuebot does not handle.</summary>
    public static BotEvent? Map(string subscriptionType, JsonElement e) => subscriptionType switch
    {
        "channel.chat.message" => ChatMessage(e),
        "channel.follow" => new BotEvent(TwitchEventTypes.Follow, User(e, "user")),
        "channel.subscribe" => Sub(e),
        "channel.subscription.message" => Resub(e),
        "channel.subscription.gift" => GiftSub(e),
        "channel.cheer" => Cheer(e),
        "channel.raid" => Raid(e),
        "channel.channel_points_custom_reward_redemption.add" => RewardRedemption(e),
        "stream.online" => StreamOnline(e),
        "stream.offline" => new BotEvent(TwitchEventTypes.StreamOffline, User(e, "broadcaster_user")),
        _ => null,
    };

    private static BotEvent ChatMessage(JsonElement e)
    {
        var badges = e.Items("badges").Select(b => b.Str("set_id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = (e.Child("message") ?? default).Str("text");
        var args = User(e, "chatter_user");
        args["message"] = text;
        args["rawInput"] = text;
        args["messageId"] = e.Str("message_id");
        args["isBroadcaster"] = badges.Contains("broadcaster") || e.Str("chatter_user_id") == e.Str("broadcaster_user_id");
        args["isModerator"] = badges.Contains("moderator");
        args["isVip"] = badges.Contains("vip");
        args["isSubscriber"] = badges.Contains("subscriber") || badges.Contains("founder");
        args["bits"] = e.Child("cheer") is { } cheer ? cheer.Int("bits") : 0;
        args["color"] = e.Str("color");
        return new BotEvent(TwitchEventTypes.ChatMessage, args);
    }

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
        args["message"] = (e.Child("message") ?? default).Str("text");
        args["cumulativeMonths"] = e.Int("cumulative_months");
        args["streakMonths"] = e.Int("streak_months");
        args["durationMonths"] = e.Int("duration_months");
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

    private static BotEvent Raid(JsonElement e)
    {
        var args = User(e, "from_broadcaster_user");
        args["viewers"] = e.Int("viewers");
        return new BotEvent(TwitchEventTypes.Raid, args);
    }

    private static BotEvent RewardRedemption(JsonElement e)
    {
        var reward = e.Child("reward") ?? default;
        var args = User(e, "user");
        args["rewardId"] = reward.Str("id");
        args["rewardTitle"] = reward.Str("title");
        args["rewardCost"] = reward.Int("cost");
        args["userInput"] = e.Str("user_input");
        args["redemptionId"] = e.Str("id");
        return new BotEvent(TwitchEventTypes.RewardRedemption, args);
    }

    private static BotEvent StreamOnline(JsonElement e)
    {
        var args = User(e, "broadcaster_user");
        args["streamType"] = e.Str("type");
        args["startedAt"] = e.Str("started_at");
        return new BotEvent(TwitchEventTypes.StreamOnline, args);
    }

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

    /// <summary>EventSub tiers are "1000"/"2000"/"3000"; Cuebot exposes "1"/"2"/"3".</summary>
    private static string Tier(JsonElement e)
    {
        var tier = e.Str("tier");
        return tier.Length == 4 && tier.EndsWith("000", StringComparison.Ordinal) ? tier[..1] : tier;
    }
}
