using System.Globalization;
using System.Text.Json;
using Cuebot.Kick.Events;

namespace Cuebot.Kick.Webhooks;

/// <summary>
/// Maps official webhook payloads (docs.kick.com/events/event-types, version 1) to <see cref="KickEvent"/>s.
/// The event name comes from the <c>Kick-Event-Type</c> header. Unknown types yield no events.
/// </summary>
public static class KickWebhookParser
{
    public const string Source = "webhook";

    /// <summary>Every webhook event Cuebot understands, for registering subscriptions.</summary>
    public static IReadOnlyList<(string Name, int Version)> SupportedEvents { get; } =
    [
        ("chat.message.sent", 1),
        ("channel.followed", 1),
        ("channel.subscription.new", 1),
        ("channel.subscription.renewal", 1),
        ("channel.subscription.gifts", 1),
        ("channel.reward.redemption.updated", 1),
        ("livestream.status.updated", 1),
        ("livestream.metadata.updated", 1),
        ("moderation.banned", 1),
        ("kicks.gifted", 1),
    ];

    public static IReadOnlyList<KickEvent> Parse(string eventType, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var e = doc.RootElement;
        var broadcaster = Actor(e.Prop("broadcaster"));
        return eventType switch
        {
            "chat.message.sent" => [Chat(e, broadcaster.Id)],
            "channel.followed" => [KickEventFactory.Follow(Source, Actor(e.Prop("follower")))],
            "channel.subscription.new" => [KickEventFactory.Subscription(Source, Actor(e.Prop("subscriber")), Months(e), isRenewal: false)],
            "channel.subscription.renewal" => [KickEventFactory.Subscription(Source, Actor(e.Prop("subscriber")), Months(e), isRenewal: true)],
            "channel.subscription.gifts" => KickEventFactory.Gifts(Source, Actor(e.Prop("gifter")), e.Items("giftees").Select(g => Actor(g)).ToArray()),
            "channel.reward.redemption.updated" => [Reward(e)],
            "livestream.status.updated" => [e.Bool("is_live")
                ? KickEventFactory.StreamOnline(Source, e.Str("title"), Time(e.Str("started_at")))
                : KickEventFactory.StreamOffline(Source, Time(e.Str("ended_at")))],
            "livestream.metadata.updated" => [Metadata(e.Prop("metadata"))],
            "moderation.banned" => [Ban(e)],
            "kicks.gifted" => [Kicks(e)],
            _ => [],
        };
    }

    private static KickEvent Chat(JsonElement e, long broadcasterId)
    {
        var reply = e.Prop("replies_to");
        return KickEventFactory.Chat(Source, new KickEventFactory.ChatInput(
            e.Str("message_id"), Actor(e.Prop("sender")), e.Str("content"), broadcasterId, Time(e.Str("created_at")),
            reply.Str("message_id") is { Length: > 0 } rid ? rid : null, reply.Str("content"),
            reply.Prop("sender") is { } rs ? Actor(rs) : null));
    }

    private static KickEvent Reward(JsonElement e)
    {
        var reward = e.Prop("reward");
        return KickEventFactory.Reward(Source, Actor(e.Prop("redeemer")), reward.Str("id"), reward.Str("title"), reward.Long("cost"),
            e.Str("user_input"), e.Str("status"), e.Str("id"));
    }

    private static KickEvent Metadata(JsonElement? m)
    {
        var category = m.Prop("category");
        return KickEventFactory.ChannelUpdate(Source, m.Str("title"), category.Str("name"), category.Long("id"), m.Str("language"),
            m.Bool("has_mature_content"));
    }

    private static KickEvent Ban(JsonElement e)
    {
        var meta = e.Prop("metadata");
        var created = Time(meta.Str("created_at"));
        var expires = Time(meta.Str("expires_at"));
        var duration = created is { } c && expires is { } x && x > c ? (long)Math.Round((x - c).TotalSeconds) : 0;
        return KickEventFactory.Ban(Source, Actor(e.Prop("banned_user")), Actor(e.Prop("moderator")), duration, meta.Str("reason"), expires);
    }

    private static KickEvent Kicks(JsonElement e)
    {
        var gift = e.Prop("gift");
        return KickEventFactory.Kicks(Source, Actor(e.Prop("sender")), gift.Long("amount"), gift.Str("name"), gift.Str("type"),
            gift.Str("tier"), gift.Str("message"));
    }

    private static int Months(JsonElement e) => (int)Math.Max(1, e.Long("duration"));

    internal static KickActor Actor(JsonElement? u)
    {
        var identity = u.Prop("identity");
        var badges = identity.Items("badges").Select(b => new KickBadge(b.Str("type"), b.Str("text"), (int)b.Long("count"))).ToArray();
        return new KickActor(u.Long("user_id"), u.Str("username"), u.Str("channel_slug"), identity.Str("username_color"), badges,
            u.Bool("is_verified"), u.Bool("is_anonymous"), u.Str("profile_picture"));
    }

    internal static DateTimeOffset? Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
