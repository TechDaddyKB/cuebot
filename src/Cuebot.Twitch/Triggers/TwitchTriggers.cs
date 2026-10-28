using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using T = Cuebot.Twitch.TwitchEventTypes;

namespace Cuebot.Twitch.Triggers;

/// <summary>All Twitch trigger types: one per published event, plus the chat command trigger.</summary>
internal sealed class TwitchTriggers : ITriggerProvider
{
    public const string Category = "Twitch";

    private static readonly IReadOnlyList<string> TierChoices = ["Any", "1", "2", "3"];

    private readonly CommandTriggerType _command;

    public TwitchTriggers() : this(TimeProvider.System)
    {
    }

    internal TwitchTriggers(TimeProvider time) => _command = new CommandTriggerType(new CooldownTracker(time));

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        _command,

        // Chat
        Event(T.ChatMessage, "Chat message",
            [
                new("contains", "Message contains") { Help = "Leave empty to match every message" },
                new("ignoreBot", "Ignore the bot account", ParameterKind.Boolean) { DefaultValue = "false" },
                new("ignoreSharedChat", "Ignore shared chat from other channels", ParameterKind.Boolean) { DefaultValue = "false" },
            ],
            (e, s) => Contains(e, "message", s["contains"])
                      && !(s.GetBool("ignoreBot") && e.Args.GetBool("isBot"))
                      && !(s.GetBool("ignoreSharedChat") && e.Args.GetBool("isSharedChat"))),
        Event(T.Announcement, "Announcement", [ContainsParameter()], (e, s) => Contains(e, "message", s["contains"])),
        Event(T.BitsBadgeTier, "Bits badge tier", [Min("minTier", "Minimum badge tier", "0")], (e, s) => AtLeast(e, "tier", s, "minTier")),
        Event(T.Whisper, "Whisper (broadcaster)", [ContainsParameter()], (e, s) => Contains(e, "message", s["contains"])),
        Event(T.BotWhisper, "Whisper (bot account)", [ContainsParameter()], (e, s) => Contains(e, "message", s["contains"])),
        Event(T.ChatMessageDeleted, "Chat message deleted"),
        Event(T.ChatCleared, "Chat cleared"),
        Event(T.UserMessagesCleared, "User's messages cleared"),
        Event(T.ChatSettingsUpdated, "Chat settings updated"),
        Event(T.EmoteModeOn, "Chat mode: emote-only on"),
        Event(T.EmoteModeOff, "Chat mode: emote-only off"),
        Event(T.FollowerModeOn, "Chat mode: follower-only on"),
        Event(T.FollowerModeOff, "Chat mode: follower-only off"),
        Event(T.FollowerModeChanged, "Chat mode: follower-only duration changed"),
        Event(T.SlowModeOn, "Chat mode: slow mode on"),
        Event(T.SlowModeOff, "Chat mode: slow mode off"),
        Event(T.SlowModeChanged, "Chat mode: slow mode delay changed"),
        Event(T.SubscriberModeOn, "Chat mode: subscriber-only on"),
        Event(T.SubscriberModeOff, "Chat mode: subscriber-only off"),
        Event(T.UniqueModeOn, "Chat mode: unique chat on"),
        Event(T.UniqueModeOff, "Chat mode: unique chat off"),
        Event(T.Pyramid, "Pyramid completed", [Min("minWidth", "Minimum width", "3")], (e, s) => AtLeast(e, "width", s, "minWidth")),
        Event(T.PyramidBroken, "Pyramid broken"),
        Event(T.WatchStreak, "Watch streak shared", [Min("minStreak", "Minimum streak", "0")], (e, s) => AtLeast(e, "streak", s, "minStreak")),
        Event(T.Modiversary, "Modiversary", [Min("minMonths", "Minimum months", "0")], (e, s) => AtLeast(e, "months", s, "minMonths")),

        // Channel
        Event(T.Follow, "Follow"),
        Event(T.StreamOnline, "Stream online"),
        Event(T.StreamOffline, "Stream offline"),
        Event(T.StreamUpdate, "Stream update (title / category)",
            [new("categoryName", "Category is") { Help = "Leave empty for every update" }],
            (e, s) => Equal(e, "categoryName", s["categoryName"])),
        Event(T.ViewerCountUpdate, "Viewer count update", [Min("minViewers", "Minimum viewers", "0")], (e, s) => AtLeast(e, "viewers", s, "minViewers")),
        Event(T.PresentViewers, "Present viewers"),

        // Subscriptions and bits
        Event(T.Sub, "Subscription", [TierParameter()], (e, s) => TierMatches(e, s)),
        Event(T.Resub, "Resubscription",
            [TierParameter(), Min("minMonths", "Minimum cumulative months", "0")],
            (e, s) => TierMatches(e, s) && AtLeast(e, "cumulativeMonths", s, "minMonths")),
        Event(T.GiftSub, "Gift subscriptions",
            [new("minTotal", "Minimum gifted", ParameterKind.Number) { DefaultValue = "1" }],
            (e, s) => e.Args.GetLong("total") >= s.GetInt("minTotal")),
        Event(T.SubGift, "Gift subscription (each recipient)", [TierParameter()], (e, s) => TierMatches(e, s)),
        Event(T.GiftBomb, "Gift bomb (community gift)",
            [TierParameter(), Min("minTotal", "Minimum gifted", "1")],
            (e, s) => TierMatches(e, s) && AtLeast(e, "total", s, "minTotal")),
        Event(T.GiftPaidUpgrade, "Gift paid upgrade"),
        Event(T.PrimePaidUpgrade, "Prime paid upgrade"),
        Event(T.PayItForward, "Pay it forward"),
        Event(T.Cheer, "Cheer",
            [
                new("minBits", "Minimum bits", ParameterKind.Number) { DefaultValue = "1" },
                new("maxBits", "Maximum bits", ParameterKind.Number) { DefaultValue = "0", Help = "0 means no maximum" },
            ],
            (e, s) => InRange(e.Args.GetLong("bits"), s.GetInt("minBits"), s.GetInt("maxBits"))),
        Event(T.BitsUsed, "Bits used", [Min("minBits", "Minimum bits", "1")], (e, s) => AtLeast(e, "bits", s, "minBits")),
        Event(T.PowerUp, "Power-up redemption",
            [new("powerUpType", "Power-up type", ParameterKind.Choice)
                { DefaultValue = "Any", Choices = ["Any", "gigantify_an_emote", "celebration", "message_effect"] }],
            (e, s) => s["powerUpType"] == "Any" || Equal(e, "powerUpType", s["powerUpType"])),
        Event(T.CustomPowerUpRedemption, "Custom power-up redemption",
            [new("powerUp", "Power-up") { Help = "Title or id of one of your custom power-ups; leave empty to match every one" }],
            (e, s) => Equal(e, "rewardTitle", s["powerUp"]) || Equal(e, "rewardId", s["powerUp"])),
        Event(T.SubCounterRollover, "Sub counter rollover"),

        // Raids
        Event(T.Raid, "Raid",
            [new("minViewers", "Minimum viewers", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetLong("viewers") >= s.GetInt("minViewers")),
        Event(T.RaidStart, "Raid started (outgoing)"),
        Event(T.RaidSend, "Raid sent (outgoing)"),
        Event(T.RaidCancelled, "Raid cancelled"),

        // Channel points
        Event(T.RewardRedemption, "Channel point reward",
            [new("rewardTitle", "Reward title") { Help = "Leave empty to match every reward" }],
            (e, s) => Equal(e, "rewardTitle", s["rewardTitle"])),
        Event(T.RewardRedemptionUpdated, "Reward redemption updated",
            [
                new("rewardTitle", "Reward title") { Help = "Leave empty to match every reward" },
                new("status", "Status", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "fulfilled", "canceled"] },
            ],
            (e, s) => Equal(e, "rewardTitle", s["rewardTitle"]) && (s["status"] == "Any" || Equal(e, "status", s["status"]))),
        Event(T.AutomaticRewardRedemption, "Automatic reward redemption",
            [new("rewardType", "Reward type") { Help = "e.g. send_highlighted_message; leave empty for every type" }],
            (e, s) => Equal(e, "rewardType", s["rewardType"])),
        Event(T.RewardCreated, "Reward created"),
        Event(T.RewardUpdated, "Reward updated", [new("rewardTitle", "Reward title")], (e, s) => Equal(e, "rewardTitle", s["rewardTitle"])),
        Event(T.RewardDeleted, "Reward deleted"),

        // Polls and predictions
        Event(T.PollCreated, "Poll created", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PollUpdated, "Poll updated", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PollCompleted, "Poll completed", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PollTerminated, "Poll terminated", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PollArchived, "Poll archived", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PredictionCreated, "Prediction created", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PredictionUpdated, "Prediction updated", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PredictionLocked, "Prediction locked", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PredictionCompleted, "Prediction completed", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),
        Event(T.PredictionCanceled, "Prediction canceled", [TitleParameter()], (e, s) => Contains(e, "title", s["title"])),

        // Hype train, goals, charity
        Event(T.HypeTrainStart, "Hype train start"),
        Event(T.HypeTrainUpdate, "Hype train update"),
        Event(T.HypeTrainLevelUp, "Hype train level up", [Min("minLevel", "Minimum level", "2")], (e, s) => AtLeast(e, "level", s, "minLevel")),
        Event(T.HypeTrainEnd, "Hype train end", [Min("minLevel", "Minimum level", "1")], (e, s) => AtLeast(e, "level", s, "minLevel")),
        Event(T.GoalBegin, "Goal begin"),
        Event(T.GoalProgress, "Goal progress"),
        Event(T.GoalEnd, "Goal end"),
        Event(T.CharityStarted, "Charity campaign started"),
        Event(T.CharityProgress, "Charity campaign progress"),
        Event(T.CharityDonation, "Charity donation"),
        Event(T.CharityCompleted, "Charity campaign completed"),

        // Ads, shoutouts, shield mode, shared chat
        Event(T.AdRun, "Ad run",
            [new("kind", "Ad kind", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Automatic", "Manual"] }],
            (e, s) => s["kind"] switch
            {
                "Automatic" => e.Args.GetBool("isAutomatic"),
                "Manual" => !e.Args.GetBool("isAutomatic"),
                _ => true,
            }),
        Event(T.UpcomingAd, "Upcoming ad (5 to 1 minutes before)",
            [new("minutes", "Minutes before", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "5", "4", "3", "2", "1"] }],
            (e, s) => s["minutes"] is "" or "Any" || e.Args.GetString("minutes") == s["minutes"]),
        Event(T.ShoutoutCreated, "Shoutout created"),
        Event(T.ShoutoutReceived, "Shoutout received"),
        Event(T.ShieldModeBegin, "Shield mode begin"),
        Event(T.ShieldModeEnd, "Shield mode end"),
        Event(T.SharedChatBegin, "Shared chat session begin"),
        Event(T.SharedChatUpdate, "Shared chat session update"),
        Event(T.SharedChatEnd, "Shared chat session end"),
        Event(T.SharedChatSub, "Shared chat: subscription"),
        Event(T.SharedChatResub, "Shared chat: resubscription"),
        Event(T.SharedChatSubGift, "Shared chat: gift subscription"),
        Event(T.SharedChatGiftBomb, "Shared chat: gift bomb"),
        Event(T.SharedChatGiftPaidUpgrade, "Shared chat: gift paid upgrade"),
        Event(T.SharedChatPrimePaidUpgrade, "Shared chat: prime paid upgrade"),
        Event(T.SharedChatPayItForward, "Shared chat: pay it forward"),
        Event(T.SharedChatRaid, "Shared chat: raid"),
        Event(T.SharedChatAnnouncement, "Shared chat: announcement"),
        Event(T.SharedChatModiversary, "Shared chat: modiversary"),
        Event(T.SharedChatMessageDeleted, "Shared chat: message deleted"),
        Event(T.SharedChatUserBanned, "Shared chat: user banned"),
        Event(T.SharedChatUserTimedOut, "Shared chat: user timed out"),
        Event(T.SharedChatUserUnbanned, "Shared chat: user unbanned"),
        Event(T.SharedChatUserUntimedOut, "Shared chat: user untimed out"),

        // Guest Star
        Event(T.GuestStarSessionBegin, "Guest Star session begin"),
        Event(T.GuestStarSessionEnd, "Guest Star session end"),
        Event(T.GuestStarGuestUpdate, "Guest Star guest update",
            [new("state", "Guest state", ParameterKind.Choice)
                { DefaultValue = "Any", Choices = ["Any", "invited", "accepted", "ready", "backstage", "live", "removed"] }],
            (e, s) => s["state"] is "" or "Any" || Equal(e, "state", s["state"])),
        Event(T.GuestStarSettingsUpdate, "Guest Star settings update"),

        // Moderation
        Event(T.UserBanned, "User banned"),
        Event(T.UserTimedOut, "User timed out", [Min("minDuration", "Minimum duration (seconds)", "0")],
            (e, s) => AtLeast(e, "duration", s, "minDuration")),
        Event(T.UserUnbanned, "User unbanned"),
        Event(T.UserUntimedOut, "User untimed out"),
        Event(T.ModeratorAdded, "Moderator added"),
        Event(T.ModeratorRemoved, "Moderator removed"),
        Event(T.VipAdded, "VIP added"),
        Event(T.VipRemoved, "VIP removed"),
        Event(T.UserWarned, "User warned"),
        Event(T.WarningAcknowledged, "Warning acknowledged"),
        Event(T.UnbanRequestCreated, "Unban request created"),
        Event(T.UnbanRequestApproved, "Unban request approved"),
        Event(T.UnbanRequestDenied, "Unban request denied"),
        Event(T.SuspiciousUserUpdate, "Suspicious user update"),
        Event(T.SuspiciousUserMessage, "Suspicious user message"),
        Event(T.AutomodMessageHeld, "AutoMod message held"),
        Event(T.AutomodMessageUpdated, "AutoMod message updated"),
        Event(T.BlockedTermsAdded, "Blocked terms added"),
        Event(T.BlockedTermsDeleted, "Blocked terms deleted"),
        Event(T.PermittedTermsAdded, "Permitted terms added"),
        Event(T.PermittedTermsDeleted, "Permitted terms deleted"),

        // Connections
        Event(T.BroadcasterAuthenticated, "Broadcaster signed in"),
        Event(T.BroadcasterConnected, "Broadcaster EventSub connected"),
        Event(T.BroadcasterDisconnected, "Broadcaster EventSub disconnected"),
        Event(T.BroadcasterChatConnected, "Broadcaster chat connected"),
        Event(T.BroadcasterChatDisconnected, "Broadcaster chat disconnected"),
        Event(T.BotConnected, "Bot account connected"),
        Event(T.BotDisconnected, "Bot account disconnected"),
    ];

    private static ParameterDescriptor ContainsParameter() =>
        new("contains", "Message contains") { Help = "Leave empty to match every message" };

    private static ParameterDescriptor TitleParameter() => new("title", "Title contains") { Help = "Leave empty to match every one" };

    private static ParameterDescriptor TierParameter() =>
        new("tier", "Tier", ParameterKind.Choice) { DefaultValue = "Any", Choices = TierChoices };

    private static ParameterDescriptor Min(string key, string label, string defaultValue) =>
        new(key, label, ParameterKind.Number) { DefaultValue = defaultValue };

    private static bool TierMatches(BotEvent e, ParameterValues s) =>
        s["tier"] is "" or "Any" || e.Args.GetString("tier") == s["tier"];

    private static bool AtLeast(BotEvent e, string arg, ParameterValues s, string setting) => e.Args.GetLong(arg) >= s.GetInt(setting);

    private static bool Contains(BotEvent e, string arg, string text) =>
        text.Length == 0 || e.Args.GetString(arg).Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool Equal(BotEvent e, string arg, string text) =>
        text.Trim().Length == 0 || string.Equals(e.Args.GetString(arg), text.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool InRange(long value, int min, int max) => value >= min && (max <= 0 || value <= max);

    /// <summary>Trigger whose type id equals the event type it listens to.</summary>
    private static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, Category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));
}
