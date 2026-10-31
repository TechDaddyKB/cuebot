using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;

namespace Cuebot.YouTube.Triggers;

/// <summary>
/// All YouTube trigger types, one per published event (type id = event type). Chat commands, First Words and
/// Present Viewers come from the core Commands and Users features (pick platform "YouTube").
/// </summary>
internal sealed class YouTubeTriggers : ITriggerProvider
{
    public const string Category = "YouTube";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Event(YouTubeEventTypes.ChatMessage, "Chat message",
            [
                new("contains", "Message contains") { Help = "Leave empty to match every message" },
                new("ignoreBot", "Ignore the bot account", ParameterKind.Boolean) { DefaultValue = "true" },
            ],
            (e, s) => (!s.GetBool("ignoreBot") || !e.Args.GetBool("isBotAccount"))
                && (s["contains"].Length == 0 || e.Args.GetString("message").Contains(s["contains"], StringComparison.OrdinalIgnoreCase))),
        Event(YouTubeEventTypes.SuperChat, "Super Chat", MoneyParameters, MoneyFilter),
        Event(YouTubeEventTypes.SuperSticker, "Super Sticker", MoneyParameters, MoneyFilter),
        Event(YouTubeEventTypes.JewelsGifted, "Jewels gifted",
            [new("minJewels", "Minimum jewels", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("jewels") >= s.GetDouble("minJewels")),
        Event(YouTubeEventTypes.NewSponsor, "New channel member", LevelParameters.Append(
                new ParameterDescriptor("upgrades", "Upgrades", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Only upgrades", "No upgrades"] }).ToArray(),
            (e, s) => LevelMatches(e, s) && s["upgrades"] switch
            {
                "Only upgrades" => e.Args.GetBool("isUpgrade"),
                "No upgrades" => !e.Args.GetBool("isUpgrade"),
                _ => true,
            }),
        Event(YouTubeEventTypes.MemberMilestone, "Member milestone", LevelParameters.Append(
                new ParameterDescriptor("minMonths", "Minimum months", ParameterKind.Number) { DefaultValue = "0" }).ToArray(),
            (e, s) => LevelMatches(e, s) && e.Args.GetDouble("months") >= s.GetDouble("minMonths")),
        Event(YouTubeEventTypes.MembershipGift, "Membership gift", LevelParameters.Append(
                new ParameterDescriptor("minTotal", "Minimum gifted", ParameterKind.Number) { DefaultValue = "1" }).ToArray(),
            (e, s) => LevelMatches(e, s) && e.Args.GetDouble("total") >= s.GetDouble("minTotal")),
        Event(YouTubeEventTypes.GiftMembershipReceived, "Gift membership received", LevelParameters, LevelMatches),
        Event(YouTubeEventTypes.NewSubscriber, "New subscriber"),
        Event(YouTubeEventTypes.MessageDeleted, "Message deleted"),
        Event(YouTubeEventTypes.UserBanned, "User banned"),
        Event(YouTubeEventTypes.UserTimedOut, "User timed out",
            [new("minDuration", "Minimum duration (seconds)", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("duration") >= s.GetDouble("minDuration")),
        Event(YouTubeEventTypes.SponsorOnlyStarted, "Members-only mode started"),
        Event(YouTubeEventTypes.SponsorOnlyEnded, "Members-only mode ended"),
        Event(YouTubeEventTypes.PollStarted, "Poll started"),
        Event(YouTubeEventTypes.PollUpdated, "Poll updated"),
        Event(YouTubeEventTypes.PollClosed, "Poll closed"),
        Event(YouTubeEventTypes.BroadcastStarted, "Broadcast started"),
        Event(YouTubeEventTypes.BroadcastEnded, "Broadcast ended"),
        Event(YouTubeEventTypes.BroadcastMonitoringStarted, "Started watching a stream's chat"),
        Event(YouTubeEventTypes.BroadcastMonitoringEnded, "Stopped watching a stream's chat"),
        Event(YouTubeEventTypes.BroadcastAdded, "Broadcast added (scheduled)"),
        Event(YouTubeEventTypes.BroadcastRemoved, "Broadcast removed"),
        Event(YouTubeEventTypes.BroadcastUpdated, "Broadcast updated"),
        Event(YouTubeEventTypes.BroadcastStatisticsUpdated, "Broadcast statistics updated",
            [new("minViewers", "Minimum concurrent viewers", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("concurrentViewers") >= s.GetDouble("minViewers")),
        Event(YouTubeEventTypes.QuotaExceeded, "API quota exhausted"),
    ];

    private static readonly IReadOnlyList<ParameterDescriptor> MoneyParameters =
    [
        new("minAmount", "Minimum amount", ParameterKind.Number) { DefaultValue = "0", Help = "In the paid currency (YouTube reports the original currency)" },
        new("maxAmount", "Maximum amount (0 = no limit)", ParameterKind.Number) { DefaultValue = "0" },
        new("currency", "Currency (ISO code, empty = any)") { Help = "e.g. USD or EUR" },
        new("minTier", "Minimum tier", ParameterKind.Number) { DefaultValue = "0", Help = "YouTube's colour tier, 1 (lowest) and up" },
    ];

    private static readonly IReadOnlyList<ParameterDescriptor> LevelParameters =
    [
        new("level", "Membership level contains") { Help = "Leave empty for every level" },
    ];

    private static bool MoneyFilter(BotEvent e, ParameterValues s)
    {
        var amount = e.Args.GetDouble("amount");
        var max = s.GetDouble("maxAmount");
        return amount >= s.GetDouble("minAmount")
            && (max <= 0 || amount <= max)
            && (s["currency"].Trim().Length == 0 || string.Equals(e.Args.GetString("currency"), s["currency"].Trim(), StringComparison.OrdinalIgnoreCase))
            && e.Args.GetDouble("tier") >= s.GetDouble("minTier");
    }

    private static bool LevelMatches(BotEvent e, ParameterValues s) =>
        s["level"].Trim().Length == 0 || e.Args.GetString("levelName").Contains(s["level"].Trim(), StringComparison.OrdinalIgnoreCase);

    private static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, Category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));
}
