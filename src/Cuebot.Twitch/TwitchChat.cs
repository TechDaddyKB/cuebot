using System.Globalization;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;

namespace Cuebot.Twitch;

/// <summary>Maps Twitch events onto the combined chat and activity feed.</summary>
internal sealed class TwitchChatAdapter : IChatEventAdapter
{
    public const string PlatformName = "Twitch";

    public string Platform => PlatformName;

    public ChatMessage? ToChatMessage(BotEvent e)
    {
        if (e.Type is not (TwitchEventTypes.ChatMessage or TwitchEventTypes.Announcement)) return null;
        var bits = Int(e, "bits");
        return new ChatMessage(Platform, Str(e, "messageId"), Str(e, "user"), Str(e, "userId"), Str(e, "message"), e.Time)
        {
            Roles = (Bool(e, "isBroadcaster") ? ChatRoles.Broadcaster : 0)
                    | (Bool(e, "isModerator") ? ChatRoles.Moderator : 0)
                    | (Bool(e, "isVip") ? ChatRoles.Vip : 0)
                    | (Bool(e, "isSubscriber") ? ChatRoles.Subscriber : 0),
            Color = Str(e, "color") is { Length: > 0 } color ? color : null,
            Amount = bits > 0 ? $"{bits} bits" : null,
        };
    }

    public FeedItem? ToFeedItem(BotEvent e)
    {
        var user = Str(e, "user");
        return e.Type switch
        {
            TwitchEventTypes.Follow => Item(e, FeedKind.Follow, user, $"{user} followed"),
            TwitchEventTypes.Sub => Bool(e, "isGift")
                ? null // the gifter's channel.subscription.gift event already covers gifted subs
                : Item(e, FeedKind.Subscription, user, $"{user} subscribed (tier {Str(e, "tier")})"),
            TwitchEventTypes.Resub => Item(e, FeedKind.Subscription, user,
                $"{user} resubscribed for {Int(e, "cumulativeMonths")} months", Str(e, "message")),
            TwitchEventTypes.GiftSub => Item(e, FeedKind.GiftSubscription, user,
                $"{(Bool(e, "isAnonymous") ? "An anonymous gifter" : user)} gifted {Int(e, "total")} tier {Str(e, "tier")} subs"),
            TwitchEventTypes.GiftPaidUpgrade => Item(e, FeedKind.Subscription, user, $"{user} continued the sub gifted by {Str(e, "gifter")}"),
            TwitchEventTypes.PrimePaidUpgrade => Item(e, FeedKind.Subscription, user, $"{user} upgraded from Prime to tier {Str(e, "tier")}"),
            TwitchEventTypes.PayItForward => Item(e, FeedKind.GiftSubscription, user, $"{user} paid forward the sub gifted by {Str(e, "gifter")}"),
            TwitchEventTypes.Cheer => Item(e, FeedKind.Tip, user, $"{user} cheered {Int(e, "bits")} bits", Str(e, "message"))
                with { Amount = $"{Int(e, "bits")} bits" },
            TwitchEventTypes.PowerUp => Item(e, FeedKind.Tip, user, $"{user} used a power-up ({PowerUpName(e)})", Str(e, "message"))
                with { Amount = $"{Int(e, "bits")} bits" },
            TwitchEventTypes.CustomPowerUpRedemption => Item(e, FeedKind.Tip, user, $"{user} used {Str(e, "rewardTitle")}", Str(e, "userInput"))
                with { Amount = $"{Int(e, "bits")} bits" },
            TwitchEventTypes.BitsBadgeTier => Item(e, FeedKind.Other, user, $"{user} reached the {Int(e, "tier")} bits badge"),
            TwitchEventTypes.Raid => Item(e, FeedKind.Raid, user, $"{user} raided with {Int(e, "viewers")} viewers"),
            TwitchEventTypes.RaidSend => Item(e, FeedKind.Raid, user, $"Raided {user} with {Int(e, "viewers")} viewers"),
            TwitchEventTypes.RewardRedemption => Item(e, FeedKind.Redemption, user,
                $"{user} redeemed {Str(e, "rewardTitle")}", Str(e, "userInput")),
            TwitchEventTypes.AutomaticRewardRedemption => Item(e, FeedKind.Redemption, user,
                $"{user} redeemed {Humanize(Str(e, "rewardType"))}", Str(e, "message")),
            TwitchEventTypes.StreamOnline => Item(e, FeedKind.StreamOnline, user, "Stream went live"),
            TwitchEventTypes.StreamOffline => Item(e, FeedKind.StreamOffline, user, "Stream ended"),
            TwitchEventTypes.HypeTrainStart => Item(e, FeedKind.Other, user, "Hype train started"),
            TwitchEventTypes.HypeTrainLevelUp => Item(e, FeedKind.Other, user, $"Hype train reached level {Int(e, "level")}"),
            TwitchEventTypes.HypeTrainEnd => Item(e, FeedKind.Other, user, $"Hype train ended at level {Int(e, "level")}"),
            TwitchEventTypes.PollCreated => Item(e, FeedKind.Other, user, $"Poll started: {Str(e, "title")}"),
            TwitchEventTypes.PollCompleted or TwitchEventTypes.PollTerminated => Item(e, FeedKind.Other, user,
                $"Poll ended: {Str(e, "title")}", $"Winner: {Str(e, "winningChoice")} ({Int(e, "winningChoiceVotes")} votes)"),
            TwitchEventTypes.PredictionCreated => Item(e, FeedKind.Other, user, $"Prediction started: {Str(e, "title")}"),
            TwitchEventTypes.PredictionLocked => Item(e, FeedKind.Other, user, $"Prediction locked: {Str(e, "title")}"),
            TwitchEventTypes.PredictionCompleted => Item(e, FeedKind.Other, user, $"Prediction resolved: {Str(e, "title")}",
                $"Winner: {Str(e, "winningOutcome")}"),
            TwitchEventTypes.PredictionCanceled => Item(e, FeedKind.Other, user, $"Prediction cancelled: {Str(e, "title")}"),
            TwitchEventTypes.ShoutoutCreated => Item(e, FeedKind.Other, user, $"Shoutout to {user}"),
            TwitchEventTypes.ShoutoutReceived => Item(e, FeedKind.Other, user, $"{user} gave a shoutout"),
            TwitchEventTypes.AdRun => Item(e, FeedKind.Other, user, $"{Int(e, "duration")}s ad break started"),
            TwitchEventTypes.CharityDonation => Item(e, FeedKind.Tip, user, $"{user} donated to {Str(e, "charityName")}")
                with { Amount = $"{Str(e, "amount")} {Str(e, "currency")}".Trim() },
            TwitchEventTypes.GoalEnd => Item(e, FeedKind.Other, user,
                Bool(e, "isAchieved") ? $"Goal achieved: {Str(e, "description")}" : $"Goal ended: {Str(e, "description")}"),
            TwitchEventTypes.UserBanned => Item(e, FeedKind.Other, user, $"{user} was banned", Str(e, "reason")),
            TwitchEventTypes.UserTimedOut => Item(e, FeedKind.Other, user, $"{user} was timed out for {Int(e, "duration")}s", Str(e, "reason")),
            TwitchEventTypes.ShieldModeBegin => Item(e, FeedKind.Other, user, "Shield mode turned on"),
            TwitchEventTypes.ShieldModeEnd => Item(e, FeedKind.Other, user, "Shield mode turned off"),
            TwitchEventTypes.SharedChatBegin => Item(e, FeedKind.Other, user, "Shared chat started"),
            TwitchEventTypes.SharedChatEnd => Item(e, FeedKind.Other, user, "Shared chat ended"),
            _ => null,
        };
    }

    private static string PowerUpName(BotEvent e) => Str(e, "powerUpType") switch
    {
        "gigantify_an_emote" => $"gigantified {Str(e, "powerUpEmoteName")}",
        "celebration" => "celebration",
        "message_effect" => "message effect",
        var other => Humanize(other),
    };

    private static string Humanize(string snake) => snake.Replace('_', ' ');

    private FeedItem Item(BotEvent e, FeedKind kind, string user, string title, string? detail = null) =>
        new(Platform, kind, user, title, e.Time) { Detail = string.IsNullOrEmpty(detail) ? null : detail };

    private static string Str(BotEvent e, string key) =>
        e.Args.GetValueOrDefault(key) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    private static int Int(BotEvent e, string key) =>
        int.TryParse(Str(e, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static bool Bool(BotEvent e, string key) => e.Args.GetValueOrDefault(key) is true;
}

/// <summary>Lets the combined chat post to Twitch (from the bot account when configured).</summary>
internal sealed class TwitchChatSender(TwitchService twitch) : IChatSender
{
    public string Platform => TwitchChatAdapter.PlatformName;
    public bool CanSend => twitch.State == Cuebot.Core.ConnectionState.Connected;
    public Task SendAsync(string text, CancellationToken ct = default) => twitch.SendChatMessageAsync(text, ct);

    public Task SendAsync(string text, ChatSendOptions options, CancellationToken ct = default) =>
        twitch.Api.SendChatMessageAsync(text,
            options.Bot switch { true => TwitchSender.Bot, false => TwitchSender.Broadcaster, null => TwitchSender.Default },
            string.IsNullOrWhiteSpace(options.ReplyTo) ? null : options.ReplyTo.Trim(), options.Internal, ct);
}

/// <summary>
/// Whether a user moderates the broadcaster's channel (Helix moderation/moderators), cached for a few minutes. Used
/// for whispered commands, whose events carry no badges. False when signed out or without a moderator-read scope.
/// </summary>
internal sealed class TwitchChannelRoles(Func<TwitchApi> api, Cuebot.Core.Logging.AppLog log, TimeProvider? time = null) : Cuebot.Core.Chat.Moderation.IChannelRoles
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, (bool IsModerator, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);

    public string Platform => TwitchChatAdapter.PlatformName;

    public async Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default)
    {
        if (userId.Length == 0) return false;
        lock (_cache)
            if (_cache.TryGetValue(userId, out var hit) && hit.Until > _time.GetUtcNow()) return hit.IsModerator;
        bool result;
        try
        {
            result = await api().IsModeratorAsync(userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TwitchApiException or HttpRequestException or InvalidOperationException)
        {
            log.Warn("Twitch", $"Could not check whether user {userId} is a moderator: {ex.Message}");
            return false;
        }
        lock (_cache) _cache[userId] = (result, _time.GetUtcNow() + CacheFor);
        return result;
    }
}
