using System.Globalization;
using System.Text.Json;
using Cuebot.Kick.Events;
using Cuebot.Kick.Webhooks;

namespace Cuebot.Kick.Pusher;

/// <summary>
/// UNOFFICIAL: decodes the events kick.com's website receives over Pusher (see PROTOCOL.md for the payloads).
/// Unknown events and malformed payloads yield nothing; Kick changes these without notice.
/// </summary>
internal static class KickPusherDecoder
{
    public const string Source = "realtime";

    public static IReadOnlyList<KickEvent> Decode(PusherFrame frame, long broadcasterUserId)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(frame.Data.Length == 0 ? "{}" : frame.Data);
        }
        catch (JsonException)
        {
            return [];
        }
        using (doc)
        {
            var d = doc.RootElement;
            return frame.ShortEvent switch
            {
                "ChatMessageEvent" => Chat(d, broadcasterUserId) is { } chat ? [chat] : [],
                "MessageDeletedEvent" => d.Prop("message").Str("id") is { Length: > 0 } id ? [KickEventFactory.MessageDeleted(Source, id)] : [],
                "UserBannedEvent" => [Ban(d)],
                "UserUnbannedEvent" => [KickEventFactory.Unban(Source, Actor(d.Prop("user")), Actor(d.Prop("unbanned_by")))],
                "SubscriptionEvent" => d.Str("username") is { Length: > 0 } name
                    ? [KickEventFactory.Subscription(Source, new KickActor(0, name), (int)Math.Max(1, d.Long("months")))]
                    : [],
                "GiftedSubscriptionsEvent" => KickEventFactory.Gifts(Source, Gifter(d.Str("gifter_username")),
                    d.Items("gifted_usernames").Select(n => new KickActor(0, n.AsString())).Where(a => a.UserName.Length > 0).ToArray(),
                    d.Long("gifter_total")),
                "StreamHostEvent" => d.Str("host_username") is { Length: > 0 } host
                    ? [KickEventFactory.Host(Source, host, d.Long("number_viewers"), d.Str("optional_message"))]
                    : [],
                "ChatroomClearEvent" => [KickEventFactory.ChatCleared(Source, d.Str("id"))],
                "StreamerIsLive" => [Live(d.Prop("livestream"))],
                "StopStreamBroadcast" => [KickEventFactory.StreamOffline(Source)],
                "FollowersUpdated" => Follow(d) is { } follow ? [follow] : [],
                "KicksGifted" => Kicks(d) is { } kicks ? [kicks] : [],
                "RewardRedeemedEvent" => [Reward(d)],
                "LivestreamUpdated" => Updated(d.Prop("livestream")) is { } update ? [update] : [],
                _ => [],
            };
        }
    }

    private static KickEvent? Chat(JsonElement d, long broadcasterUserId)
    {
        var id = d.Str("id");
        if (id.Length == 0) return null;
        var meta = d.Prop("metadata");
        var isReply = d.Str("type") == "reply";
        var original = meta.Prop("original_message");
        return KickEventFactory.Chat(Source, new KickEventFactory.ChatInput(
            id, Actor(d.Prop("sender")), d.Str("content"), broadcasterUserId, KickWebhookParser.Time(d.Str("created_at")),
            isReply && original.Str("id") is { Length: > 0 } rid ? rid : null, original.Str("content"),
            isReply && meta.Prop("original_sender") is { } os ? Actor(os) : null));
    }

    private static KickEvent Ban(JsonElement d)
    {
        var permanent = d.Bool("permanent");
        var minutes = d.Long("duration");
        var expires = KickWebhookParser.Time(d.Str("expires_at"));
        return KickEventFactory.Ban(Source, Actor(d.Prop("user")), Actor(d.Prop("banned_by")),
            permanent ? 0 : Math.Max(1, minutes) * 60, d.Str("reason"), expires);
    }

    private static KickEvent Live(JsonElement? livestream) =>
        KickEventFactory.StreamOnline(Source, livestream.Str("session_title"), KickWebhookParser.Time(livestream.Str("created_at")),
            livestream.Long("id"));

    /// <summary>Only "followed" updates that name the follower become follow events (others just carry the count).</summary>
    private static KickEvent? Follow(JsonElement d)
    {
        var name = d.Str("username");
        if (name.Length == 0 || (d.Prop("followed") is not null && !d.Bool("followed"))) return null;
        return KickEventFactory.Follow(Source, new KickActor(0, name), d.Long("followersCount"));
    }

    private static KickEvent? Kicks(JsonElement d)
    {
        var gift = d.Prop("gift");
        var sender = d.Prop("sender");
        if (gift is null || sender is null) return null;
        return KickEventFactory.Kicks(Source, new KickActor(sender.Long("id"), sender.Str("username"), Color: sender.Str("username_color")),
            gift.Long("amount"), gift.Str("name"), gift.Str("type"), gift.Str("tier"), d.Str("message"));
    }

    private static KickEvent Reward(JsonElement d) =>
        KickEventFactory.Reward(Source, new KickActor(d.Long("user_id"), d.Str("username")), d.Str("reward_id"), d.Str("reward_title"),
            d.Long("reward_cost"), d.Str("user_input"), "pending", d.Str("id"));

    private static KickEvent? Updated(JsonElement? livestream)
    {
        if (livestream is null) return null;
        var category = livestream.Items("categories").FirstOrDefault();
        return KickEventFactory.ChannelUpdate(Source, livestream.Str("session_title"), category.Str("name"), category.Long("id"),
            livestream.Str("language"), livestream.Bool("is_mature"));
    }

    private static KickActor Gifter(string name) =>
        name.Length == 0 || name.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
            ? new KickActor(0, "", IsAnonymous: true)
            : new KickActor(0, name);

    internal static KickActor Actor(JsonElement? u)
    {
        var identity = u.Prop("identity");
        var badges = identity.Items("badges").Select(b => new KickBadge(b.Str("type"), b.Str("text"), (int)b.Long("count"))).ToArray();
        return new KickActor(u.Long("id"), u.Str("username"), u.Str("slug"), identity.Str("color"), badges);
    }

    /// <summary>The public channels the website listens on for a channel and its chatroom.</summary>
    public static IReadOnlyList<string> ChannelsFor(long channelId, long chatroomId)
    {
        var list = new List<string>();
        if (chatroomId > 0)
        {
            var room = chatroomId.ToString(CultureInfo.InvariantCulture);
            list.Add($"chatrooms.{room}.v2");
            list.Add($"chatroom_{room}");
        }
        if (channelId > 0)
        {
            var channel = channelId.ToString(CultureInfo.InvariantCulture);
            list.Add($"channel.{channel}");
            list.Add($"channel_{channel}");
        }
        return list;
    }
}
