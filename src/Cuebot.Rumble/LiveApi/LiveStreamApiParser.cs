using System.Text.Json;

namespace Cuebot.Rumble.LiveApi;

/// <summary>
/// Parses Live Stream API JSON into a <see cref="LiveStreamSnapshot"/>. Rumble pads empty lists with placeholder
/// entries whose username is empty; those are dropped.
/// </summary>
internal static class LiveStreamApiParser
{
    public static LiveStreamSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    public static LiveStreamSnapshot Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.Child("followers") is null && root.Child("livestreams") is null)
            throw new JsonException("Not a Rumble Live Stream API response");

        var followers = root.Child("followers") ?? default;
        var subscribers = root.Child("subscribers") ?? default;
        var gifts = root.Child("gifted_subs") ?? default;
        var channelName = root.Str("channel_name");
        return new LiveStreamSnapshot(
            root.Long("now") is var now and > 0 ? DateTimeOffset.FromUnixTimeSeconds(now) : DateTimeOffset.UtcNow,
            root.Str("username"),
            channelName.Length > 0 ? channelName : null,
            followers.Int("num_followers"),
            subscribers.Int("num_subscribers"),
            WithUser(followers.Items("recent_followers"), "username")
                .Select(f => new ApiFollower(f.Str("username"), f.Time("followed_on"))).ToArray(),
            WithUser(subscribers.Items("recent_subscribers"), "username")
                .Select(s => new ApiSubscriber(s.Str("username"), s.Long("amount_cents"), s.Time("subscribed_on"))).ToArray(),
            WithUser(gifts.Items("recent_gifted_subs"), "purchased_by")
                .Select(g => new ApiGiftedSub(g.Str("purchased_by"), g.Int("total_gifts"), g.Int("remaining_gifts"),
                    g.Str("gift_type"), g.Long("video_id"))).ToArray(),
            root.Items("livestreams").Where(s => s.Str("id").Length > 0).Select(ParseLivestream).ToArray());
    }

    private static ApiLivestream ParseLivestream(JsonElement stream)
    {
        var chat = stream.Child("chat") ?? default;
        return new ApiLivestream(
            stream.Str("id"),
            stream.Str("title"),
            stream.Bool("is_live"),
            stream.Time("created_on"),
            stream.Int("watching_now"),
            WithUser(chat.Items("recent_messages"), "username")
                .Select(m => new ApiChatMessage(m.Str("username"), m.Str("text"), m.Time("created_on"), m.Strings("badges"))).ToArray(),
            WithUser(chat.Items("recent_rants"), "username")
                .Select(r => new ApiRant(r.Str("username"), r.Str("text"), r.Time("created_on"), r.Long("amount_cents"), r.Strings("badges")))
                .ToArray());
    }

    private static IEnumerable<JsonElement> WithUser(IEnumerable<JsonElement> items, string userField) =>
        items.Where(i => i.Str(userField).Length > 0);
}
