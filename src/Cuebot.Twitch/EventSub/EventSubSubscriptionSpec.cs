namespace Cuebot.Twitch.EventSub;

/// <summary>
/// One EventSub subscription Cuebot creates. <see cref="Scopes"/> lists the scopes that each authorize it (any one
/// is enough; empty means none is needed). <see cref="AlsoRequired"/> lists further groups of which one scope each
/// must be granted too (channel.moderate needs a whole set).
/// </summary>
internal sealed record EventSubSubscriptionSpec(
    string Type,
    string Version,
    IReadOnlyList<string> Scopes,
    Func<string, IReadOnlyDictionary<string, string>> BuildCondition,
    IReadOnlyList<IReadOnlyList<string>>? AlsoRequired = null)
{
    public bool IsAuthorized(IReadOnlySet<string> granted) => MissingScope(granted) is null;

    /// <summary>A scope to grant so this subscription can be created; null when it is authorized.</summary>
    public string? MissingScope(IReadOnlySet<string> granted)
    {
        if (Scopes.Count > 0 && !Scopes.Any(granted.Contains)) return Scopes[^1];
        return AlsoRequired?.FirstOrDefault(group => !group.Any(granted.Contains))?[^1];
    }
}

/// <summary>The EventSub subscriptions Cuebot needs, each with the scopes that authorize it.</summary>
internal static class EventSubSubscriptions
{
    private const string ReadPolls = "channel:read:polls";
    private const string ReadPredictions = "channel:read:predictions";
    private const string ReadShoutouts = "moderator:read:shoutouts";
    private const string ReadShieldMode = "moderator:read:shield_mode";
    private const string ReadWhispers = "user:read:whispers";
    private const string ReadVips = "channel:read:vips";
    private const string ReadWarnings = "moderator:read:warnings";
    private const string ReadUnbanRequests = "moderator:read:unban_requests";

    private static readonly string[] Redemptions = [TwitchScopes.ReadRedemptions, TwitchScopes.ManageRedemptions];
    private static readonly string[] Polls = [ReadPolls, TwitchScopes.ManagePolls];
    private static readonly string[] Predictions = [ReadPredictions, TwitchScopes.ManagePredictions];
    private static readonly string[] GuestStar =
        ["channel:read:guest_star", "moderator:read:guest_star", "moderator:manage:guest_star", TwitchScopes.ManageGuestStar];

    /// <summary>channel.moderate v2 needs one scope of every group (read or manage) besides moderator:read:moderators.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<string>> ModerateScopes =
    [
        ["moderator:read:blocked_terms", TwitchScopes.ManageBlockedTerms],
        ["moderator:read:chat_settings", TwitchScopes.ManageChatSettings],
        [ReadUnbanRequests, TwitchScopes.ManageUnbanRequests],
        ["moderator:read:banned_users", TwitchScopes.ManageBannedUsers],
        ["moderator:read:chat_messages", TwitchScopes.ManageChatMessages],
        [ReadWarnings, TwitchScopes.ManageWarnings],
        [TwitchScopes.ReadVips],
    ];

    /// <summary>Subscriptions of the broadcaster session (conditions built from the broadcaster user id).</summary>
    public static IReadOnlyList<EventSubSubscriptionSpec> All { get; } =
    [
        // Chat
        WithUser("channel.chat.message", TwitchScopes.ReadChat),
        WithUser("channel.chat.notification", TwitchScopes.ReadChat),
        WithUser("channel.chat.message_delete", TwitchScopes.ReadChat),
        WithUser("channel.chat.clear", TwitchScopes.ReadChat),
        WithUser("channel.chat.clear_user_messages", TwitchScopes.ReadChat),
        WithUser("channel.chat_settings.update", TwitchScopes.ReadChat),
        new("user.whisper.message", "1", [ReadWhispers, TwitchScopes.ManageWhispers], id => Condition(("user_id", id))),

        // Channel
        WithModerator("channel.follow", "2", TwitchScopes.ReadFollowers),
        Broadcaster("channel.update", "2"),
        Broadcaster("stream.online"),
        Broadcaster("stream.offline"),
        new("channel.raid", "1", [], id => Condition(("to_broadcaster_user_id", id))),
        new("channel.raid", "1", [], id => Condition(("from_broadcaster_user_id", id))),
        new("channel.ad_break.begin", "1", [TwitchScopes.ReadAds], id => Condition(("broadcaster_id", id))),
        Broadcaster("channel.shared_chat.begin"),
        Broadcaster("channel.shared_chat.update"),
        Broadcaster("channel.shared_chat.end"),

        // Subscriptions and bits
        Broadcaster("channel.subscribe", "1", TwitchScopes.ReadSubscriptions),
        Broadcaster("channel.subscription.message", "1", TwitchScopes.ReadSubscriptions),
        Broadcaster("channel.subscription.gift", "1", TwitchScopes.ReadSubscriptions),
        Broadcaster("channel.cheer", "1", TwitchScopes.ReadBits),
        Broadcaster("channel.bits.use", "1", TwitchScopes.ReadBits),
        Broadcaster("channel.custom_power_up_redemption.add", "1", TwitchScopes.ReadBits),

        // Channel points, polls, predictions
        Broadcaster("channel.channel_points_custom_reward_redemption.add", "1", Redemptions),
        Broadcaster("channel.channel_points_custom_reward_redemption.update", "1", Redemptions),
        Broadcaster("channel.channel_points_automatic_reward_redemption.add", "2", Redemptions),
        Broadcaster("channel.channel_points_custom_reward.add", "1", Redemptions),
        Broadcaster("channel.channel_points_custom_reward.update", "1", Redemptions),
        Broadcaster("channel.channel_points_custom_reward.remove", "1", Redemptions),
        Broadcaster("channel.poll.begin", "1", Polls),
        Broadcaster("channel.poll.progress", "1", Polls),
        Broadcaster("channel.poll.end", "1", Polls),
        Broadcaster("channel.prediction.begin", "1", Predictions),
        Broadcaster("channel.prediction.progress", "1", Predictions),
        Broadcaster("channel.prediction.lock", "1", Predictions),
        Broadcaster("channel.prediction.end", "1", Predictions),

        // Hype train, goals, charity
        Broadcaster("channel.hype_train.begin", "2", TwitchScopes.ReadHypeTrain),
        Broadcaster("channel.hype_train.progress", "2", TwitchScopes.ReadHypeTrain),
        Broadcaster("channel.hype_train.end", "2", TwitchScopes.ReadHypeTrain),
        Broadcaster("channel.goal.begin", "1", TwitchScopes.ReadGoals),
        Broadcaster("channel.goal.progress", "1", TwitchScopes.ReadGoals),
        Broadcaster("channel.goal.end", "1", TwitchScopes.ReadGoals),
        Broadcaster("channel.charity_campaign.start", "1", TwitchScopes.ReadCharity),
        Broadcaster("channel.charity_campaign.progress", "1", TwitchScopes.ReadCharity),
        Broadcaster("channel.charity_campaign.donate", "1", TwitchScopes.ReadCharity),
        Broadcaster("channel.charity_campaign.stop", "1", TwitchScopes.ReadCharity),

        // Moderation
        WithModerator("channel.shoutout.create", "1", ReadShoutouts, TwitchScopes.ManageShoutouts),
        WithModerator("channel.shoutout.receive", "1", ReadShoutouts, TwitchScopes.ManageShoutouts),
        WithModerator("channel.shield_mode.begin", "1", ReadShieldMode, TwitchScopes.ManageShieldMode),
        WithModerator("channel.shield_mode.end", "1", ReadShieldMode, TwitchScopes.ManageShieldMode),
        Broadcaster("channel.ban", "1", TwitchScopes.ChannelModerate),
        Broadcaster("channel.unban", "1", TwitchScopes.ChannelModerate),
        Broadcaster("channel.moderator.add", "1", TwitchScopes.ModerationRead),
        Broadcaster("channel.moderator.remove", "1", TwitchScopes.ModerationRead),
        Broadcaster("channel.vip.add", "1", ReadVips, TwitchScopes.ManageVips),
        Broadcaster("channel.vip.remove", "1", ReadVips, TwitchScopes.ManageVips),
        WithModerator("channel.warning.send", "1", ReadWarnings, TwitchScopes.ManageWarnings),
        WithModerator("channel.warning.acknowledge", "1", ReadWarnings, TwitchScopes.ManageWarnings),
        WithModerator("channel.unban_request.create", "1", ReadUnbanRequests, TwitchScopes.ManageUnbanRequests),
        WithModerator("channel.unban_request.resolve", "1", ReadUnbanRequests, TwitchScopes.ManageUnbanRequests),
        WithModerator("channel.suspicious_user.update", "1", TwitchScopes.ReadSuspiciousUsers),
        WithModerator("channel.suspicious_user.message", "1", TwitchScopes.ReadSuspiciousUsers),
        WithModerator("automod.message.hold", "1", TwitchScopes.ManageAutomod),
        WithModerator("automod.message.update", "1", TwitchScopes.ManageAutomod),
        WithModerator("automod.terms.update", "1", TwitchScopes.ManageAutomod),
        // Only its shared_chat_* actions are used (bans, timeouts and deletions in the other shared chat channels).
        new("channel.moderate", "2", [TwitchScopes.ReadModerators],
            id => Condition(("broadcaster_user_id", id), ("moderator_user_id", id)), ModerateScopes),

        // Guest Star (Twitch beta: version "beta")
        WithModerator("channel.guest_star_session.begin", "beta", GuestStar),
        WithModerator("channel.guest_star_session.end", "beta", GuestStar),
        WithModerator("channel.guest_star_guest.update", "beta", GuestStar),
        WithModerator("channel.guest_star_settings.update", "beta", GuestStar),
    ];

    /// <summary>Subscriptions of the bot account session: only its whispers.</summary>
    public static IReadOnlyList<EventSubSubscriptionSpec> Bot { get; } =
    [
        new("user.whisper.message", "1", [ReadWhispers, TwitchScopes.ManageWhispers], id => Condition(("user_id", id))),
    ];

    private static EventSubSubscriptionSpec Broadcaster(string type, string version = "1", params string[] scopes) =>
        new(type, version, scopes, id => Condition(("broadcaster_user_id", id)));

    private static EventSubSubscriptionSpec WithUser(string type, string scope) =>
        new(type, "1", [scope], id => Condition(("broadcaster_user_id", id), ("user_id", id)));

    private static EventSubSubscriptionSpec WithModerator(string type, string version, params string[] scopes) =>
        new(type, version, scopes, id => Condition(("broadcaster_user_id", id), ("moderator_user_id", id)));

    private static Dictionary<string, string> Condition(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
