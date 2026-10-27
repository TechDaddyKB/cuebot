namespace Cuebot.Twitch.EventSub;

/// <summary>One EventSub subscription Cuebot creates for the signed-in broadcaster.</summary>
internal sealed record EventSubSubscriptionSpec(
    string Type,
    string Version,
    string? RequiredScope,
    Func<string, IReadOnlyDictionary<string, string>> BuildCondition);

/// <summary>The EventSub subscriptions Cuebot needs, each with the scope that authorizes it.</summary>
internal static class EventSubSubscriptions
{
    public static IReadOnlyList<EventSubSubscriptionSpec> All { get; } =
    [
        new("channel.chat.message", "1", TwitchScopes.ReadChat,
            id => new Dictionary<string, string> { ["broadcaster_user_id"] = id, ["user_id"] = id }),
        new("channel.follow", "2", TwitchScopes.ReadFollowers,
            id => new Dictionary<string, string> { ["broadcaster_user_id"] = id, ["moderator_user_id"] = id }),
        Broadcaster("channel.subscribe", TwitchScopes.ReadSubscriptions),
        Broadcaster("channel.subscription.message", TwitchScopes.ReadSubscriptions),
        Broadcaster("channel.subscription.gift", TwitchScopes.ReadSubscriptions),
        Broadcaster("channel.cheer", TwitchScopes.ReadBits),
        new("channel.raid", "1", null, id => new Dictionary<string, string> { ["to_broadcaster_user_id"] = id }),
        Broadcaster("channel.channel_points_custom_reward_redemption.add", TwitchScopes.ReadRedemptions),
        Broadcaster("stream.online", null),
        Broadcaster("stream.offline", null),
    ];

    private static EventSubSubscriptionSpec Broadcaster(string type, string? scope) =>
        new(type, "1", scope, id => new Dictionary<string, string> { ["broadcaster_user_id"] = id });
}
