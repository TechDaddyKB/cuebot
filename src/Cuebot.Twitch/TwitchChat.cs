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
        return ChatMessage.Create(Platform, Str(e, "messageId"), Str(e, "user"), Str(e, "userId"), Str(e, "message"), e.Time) with
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
                : Item(e, FeedKind.Subscription, user, $"{user} subscribed (tier {Str(e, "tier")})") with { Spend = Sub(e) },
            TwitchEventTypes.Resub => Item(e, FeedKind.Subscription, user,
                $"{user} resubscribed for {Int(e, "cumulativeMonths")} months", Str(e, "message")) with { Spend = Sub(e) },
            TwitchEventTypes.GiftSub => Item(e, FeedKind.GiftSubscription, user,
                $"{(Bool(e, "isAnonymous") ? "An anonymous gifter" : user)} gifted {Int(e, "total")} tier {Str(e, "tier")} subs")
                with { Spend = FeedSpend.Units(SpendUnit.GiftSubscription, Int(e, "total"), "Gift subs", TierOf(e)) },
            TwitchEventTypes.GiftPaidUpgrade => Item(e, FeedKind.Subscription, user, $"{user} continued the sub gifted by {Str(e, "gifter")}")
                with { Spend = Sub(e) },
            TwitchEventTypes.PrimePaidUpgrade => Item(e, FeedKind.Subscription, user, $"{user} upgraded from Prime to tier {Str(e, "tier")}")
                with { Spend = Sub(e) },
            TwitchEventTypes.PayItForward => Item(e, FeedKind.GiftSubscription, user, $"{user} paid forward the sub gifted by {Str(e, "gifter")}")
                with { Spend = FeedSpend.Units(SpendUnit.GiftSubscription, 1, "Gift subs", TierOf(e)) },
            TwitchEventTypes.Cheer => Item(e, FeedKind.Tip, user, $"{user} cheered {Int(e, "bits")} bits", Str(e, "message"))
                with { Amount = $"{Int(e, "bits")} bits", Spend = Bits(e, "Cheer") },
            TwitchEventTypes.PowerUp => Item(e, FeedKind.Tip, user, $"{user} used a power-up ({PowerUpName(e)})", Str(e, "message"))
                with { Amount = $"{Int(e, "bits")} bits", Spend = Bits(e, "Power-up") },
            TwitchEventTypes.CustomPowerUpRedemption => Item(e, FeedKind.Tip, user, $"{user} used {Str(e, "rewardTitle")}", Str(e, "userInput"))
                with { Amount = $"{Int(e, "bits")} bits", Spend = Bits(e, "Power-up") },
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
                with
                {
                    Amount = $"{Str(e, "amount")} {Str(e, "currency")}".Trim(),
                    Spend = FeedSpend.Money(Dbl(e, "amount"), Str(e, "currency"), "Charity", charity: true),
                },
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

    private static double Dbl(BotEvent e, string key) =>
        double.TryParse(Str(e, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>"1", "2" or "3"; events that do not say (upgrades, pay it forward) count as tier 1.</summary>
    private static string TierOf(BotEvent e) => Str(e, "tier") switch { "2" => "2", "3" => "3", _ => "1" };

    private static FeedSpend? Sub(BotEvent e) => FeedSpend.Units(SpendUnit.Subscription, 1, "Subscription", TierOf(e));

    private static FeedSpend? Bits(BotEvent e, string label) => FeedSpend.Units(SpendUnit.Bits, Int(e, "bits"), label);
}

/// <summary>Lets the combined chat post to Twitch (from the bot account when configured).</summary>
internal sealed class TwitchChatSender(TwitchService twitch) : IChatSender
{
    public string Platform => TwitchChatAdapter.PlatformName;
    public bool CanSend => twitch.State == Cuebot.Core.ConnectionState.Connected;
    public bool CanSendAsBot => twitch.Api.IsBotReady;
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

    public async Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default) =>
        await CheckModeratorAsync(userId, ct).ConfigureAwait(false) == true;

    /// <summary>Null when signed out, without a moderator-read scope, or when the lookup failed (not cached, asked again next time).</summary>
    public async Task<bool?> CheckModeratorAsync(string userId, CancellationToken ct = default)
    {
        if (userId.Length == 0) return null;
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
            return null;
        }
        lock (_cache) _cache[userId] = (result, _time.GetUtcNow() + CacheFor);
        return result;
    }
}

/// <summary>
/// Moderation for the combined chat, as the broadcaster (Helix moderation): delete, timeout (1 s to 14 days), ban,
/// unban, warn, clear, the four chat modes and pins. Each tool is offered while connected and only when the broadcaster
/// granted its scope; otherwise it says which permission is missing.
/// </summary>
internal sealed class TwitchChatModerator(Func<TwitchApi> api, Func<bool> connected) : Cuebot.Core.Chat.Moderation.IChatModerator
{
    /// <summary>Helix's longest timeout and reason.</summary>
    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(1_209_600);
    internal const int MaxReason = 500;

    public string Platform => TwitchChatAdapter.PlatformName;

    public Cuebot.Core.Chat.Moderation.ChatModerationActions Available
    {
        get
        {
            var actions = Cuebot.Core.Chat.Moderation.ChatModerationActions.None;
            if (!connected()) return actions;
            if (api().HasBroadcasterScope(TwitchScopes.ManageChatMessages))
                actions |= Cuebot.Core.Chat.Moderation.ChatModerationActions.DeleteMessage;
            if (api().HasBroadcasterScope(TwitchScopes.ManageBannedUsers))
                actions |= Cuebot.Core.Chat.Moderation.ChatModerationActions.Timeout | Cuebot.Core.Chat.Moderation.ChatModerationActions.Ban;
            return actions;
        }
    }

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => api().DeleteMessageAsync(messageId, ct);

    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) =>
        api().TimeoutAsync(userId, (int)Math.Ceiling(duration.TotalSeconds), reason ?? "", ct);

    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) =>
        api().BanAsync(userId, reason ?? "", ct);

    public Cuebot.Core.Chat.Moderation.ModerationCapability Capability(Cuebot.Core.Chat.Moderation.ModerationAction action)
    {
        if (!connected()) return Cuebot.Core.Chat.Moderation.ModerationCapability.NotAvailable(action, "Connect Twitch first.");
        var scope = ScopeFor(action);
        if (!api().HasBroadcasterScope(scope))
            return Cuebot.Core.Chat.Moderation.ModerationCapability.NotAvailable(action,
                $"Sign in to Twitch again and allow \"{TwitchScopes.Catalog.FirstOrDefault(c => c.Scope == scope)?.Description ?? scope}\".");
        var works = Cuebot.Core.Chat.Moderation.ModerationCapability.Works(action);
        return action switch
        {
            Cuebot.Core.Chat.Moderation.ModerationAction.Timeout => works with
            {
                MinDuration = TimeSpan.FromSeconds(1), MaxDuration = MaxTimeout, MaxReasonLength = MaxReason,
            },
            Cuebot.Core.Chat.Moderation.ModerationAction.Ban or Cuebot.Core.Chat.Moderation.ModerationAction.Warn => works with { MaxReasonLength = MaxReason },
            Cuebot.Core.Chat.Moderation.ModerationAction.SlowMode => works with { Note = "3 to 120 seconds between messages." },
            Cuebot.Core.Chat.Moderation.ModerationAction.FollowersOnly => works with { Note = "Viewers must have followed for 0 minutes to 90 days." },
            Cuebot.Core.Chat.Moderation.ModerationAction.Pin => works with { Note = "Stays pinned until the stream ends." },
            _ => works,
        };
    }

    private static string ScopeFor(Cuebot.Core.Chat.Moderation.ModerationAction action) => action switch
    {
        Cuebot.Core.Chat.Moderation.ModerationAction.Timeout or Cuebot.Core.Chat.Moderation.ModerationAction.Ban
            or Cuebot.Core.Chat.Moderation.ModerationAction.Unban => TwitchScopes.ManageBannedUsers,
        Cuebot.Core.Chat.Moderation.ModerationAction.Warn => TwitchScopes.ManageWarnings,
        Cuebot.Core.Chat.Moderation.ModerationAction.SlowMode or Cuebot.Core.Chat.Moderation.ModerationAction.FollowersOnly
            or Cuebot.Core.Chat.Moderation.ModerationAction.SubscribersOnly or Cuebot.Core.Chat.Moderation.ModerationAction.EmoteOnly
            => TwitchScopes.ManageChatSettings,
        _ => TwitchScopes.ManageChatMessages,   // delete, clear, pin, unpin
    };

    public async Task<Cuebot.Core.Chat.Moderation.ModerationOutcome> ExecuteAsync(Cuebot.Core.Chat.Moderation.ModerationRequest r,
        CancellationToken ct = default)
    {
        var capability = Capability(r.Action);
        if (!capability.CanRun) return Cuebot.Core.Chat.Moderation.ModerationOutcome.Failed(capability.Note);
        var twitch = api();
        var reason = r.Reason ?? "";
        switch (r.Action)
        {
            case Cuebot.Core.Chat.Moderation.ModerationAction.Delete:
                await twitch.DeleteMessageAsync(r.MessageId, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.Timeout:
                var applied = capability.Apply(r.Duration ?? TimeSpan.FromMinutes(10));
                await twitch.TimeoutAsync(r.UserId, (int)applied.TotalSeconds, reason, ct).ConfigureAwait(false);
                return Cuebot.Core.Chat.Moderation.ModerationOutcome.Done(applied);
            case Cuebot.Core.Chat.Moderation.ModerationAction.Ban:
                await twitch.BanAsync(r.UserId, reason, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.Unban:
                await twitch.UnbanAsync(r.UserId, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.Warn:
                await twitch.WarnAsync(r.UserId, reason.Length > 0 ? reason : "Please follow the chat rules.", ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.Clear:
                await twitch.ClearChatAsync(ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.SlowMode:
                await twitch.UpdateChatSettingsAsync(new ChatSettingsUpdate
                {
                    SlowMode = r.Enabled, SlowModeWaitTime = r.Enabled ? Math.Clamp(r.ModeValue ?? 30, 3, 120) : null,
                }, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.FollowersOnly:
                await twitch.UpdateChatSettingsAsync(new ChatSettingsUpdate
                {
                    FollowerMode = r.Enabled, FollowerModeDuration = r.Enabled ? Math.Clamp(r.ModeValue ?? 0, 0, 129_600) : null,
                }, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.SubscribersOnly:
                await twitch.UpdateChatSettingsAsync(new ChatSettingsUpdate { SubscriberMode = r.Enabled }, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.EmoteOnly:
                await twitch.UpdateChatSettingsAsync(new ChatSettingsUpdate { EmoteMode = r.Enabled }, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.Pin:
                await twitch.PinMessageAsync(r.MessageId, 0, ct).ConfigureAwait(false);
                break;
            case Cuebot.Core.Chat.Moderation.ModerationAction.Unpin:
                await twitch.UnpinMessageAsync(r.MessageId, ct).ConfigureAwait(false);
                break;
        }
        return Cuebot.Core.Chat.Moderation.ModerationOutcome.Done();
    }
}

/// <summary>The broadcaster and bot accounts may only be acted against by the streamer.</summary>
internal sealed class TwitchProtectedAccounts(Func<TwitchApi> api) : Cuebot.Core.Chat.Moderation.IProtectedAccounts
{
    public string? WhyProtected(string platform, string userId, string userName)
    {
        if (!string.Equals(platform, TwitchChatAdapter.PlatformName, StringComparison.OrdinalIgnoreCase) || userId.Length == 0) return null;
        var twitch = api();
        if (userId == twitch.Broadcaster.UserId) return "This is your own Twitch account.";
        return twitch.IsBotReady && userId == twitch.Bot.UserId ? "This is your Twitch bot account." : null;
    }
}
