using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Users;

namespace Cuebot.Kick.Triggers;

/// <summary>
/// Kick triggers (Streamer.bot's Kick list plus a few extras). First Words and Present Viewers listen to the
/// core user registry's events, filtered to Kick.
/// </summary>
internal sealed class KickTriggers : ITriggerProvider
{
    public const string Category = "Kick";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Event(KickEventTypes.ChatMessage, "Chat message",
            [new("contains", "Message contains") { Help = "Leave empty to match every message" }],
            (e, s) => s["contains"].Length == 0 || e.Args.GetString("message").Contains(s["contains"], StringComparison.OrdinalIgnoreCase)),
        KickOnly("kick.firstWords", "First words", UserEvents.FirstWords),
        KickOnly("kick.presentViewers", "People in chat (every few minutes)", UserEvents.PresentViewers),
        Event(KickEventTypes.Follow, "Follow"),
        Event(KickEventTypes.StreamOnline, "Stream online"),
        Event(KickEventTypes.StreamOffline, "Stream offline"),
        Event(KickEventTypes.ViewerCountUpdate, "Viewer count update"),
        Event(KickEventTypes.ChannelUpdate, "Channel update"),
        Event(KickEventTypes.RewardRedemption, "Reward redemption",
            [
                new("reward", "Reward title") { Help = "Leave empty for every reward" },
                new("status", "Status", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "pending", "accepted", "rejected"] },
            ],
            (e, s) => (s["reward"].Length == 0 || string.Equals(e.Args.GetString("rewardTitle"), s["reward"].Trim(), StringComparison.OrdinalIgnoreCase))
                      && (s["status"] == "Any" || string.Equals(e.Args.GetString("status"), s["status"], StringComparison.OrdinalIgnoreCase))),
        Event(KickEventTypes.KicksGifted, "Kicks gifted",
            [new("minKicks", "Minimum Kicks", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("kicks") >= s.GetDouble("minKicks")),
        Event(KickEventTypes.UserBanned, "User banned"),
        Event(KickEventTypes.UserTimedOut, "User timed out"),
        Event(KickEventTypes.UserUnbanned, "User unbanned"),
        Event(KickEventTypes.MessageDeleted, "Message deleted"),
        Event(KickEventTypes.Subscription, "Subscription"),
        Event(KickEventTypes.Resubscription, "Resubscription",
            [new("minMonths", "Minimum months", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("months") >= s.GetDouble("minMonths")),
        Event(KickEventTypes.GiftSubscription, "Gift subscription",
            [new("ignoreMass", "Ignore gifts that are part of a mass gift", ParameterKind.Boolean) { DefaultValue = "false" }],
            (e, s) => !(s.GetBool("ignoreMass") && e.Args.GetBool("fromMassGift"))),
        Event(KickEventTypes.MassGiftSubscription, "Mass gift subscription",
            [new("minGifts", "Minimum gifts", ParameterKind.Number) { DefaultValue = "1" }],
            (e, s) => e.Args.GetDouble("gifts") >= s.GetDouble("minGifts")),
        Event(KickEventTypes.Host, "Raid / host",
            [new("minViewers", "Minimum viewers", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("viewers") >= s.GetDouble("minViewers")),
        Event(KickEventTypes.SevenTvEmoteAdded, "7TV emote added", EmoteName, EmoteMatches),
        Event(KickEventTypes.SevenTvEmoteRemoved, "7TV emote removed", EmoteName, EmoteMatches),
        Event(KickEventTypes.BroadcasterAuthenticated, "Broadcaster authenticated"),
        Event(KickEventTypes.ChatConnected, "Chat connected"),
        Event(KickEventTypes.ChatDisconnected, "Chat disconnected"),
    ];

    private static readonly IReadOnlyList<ParameterDescriptor> EmoteName =
        [new("emote", "Emote name") { Help = "Leave empty for every emote" }];

    private static bool EmoteMatches(BotEvent e, ParameterValues s) =>
        s["emote"].Trim().Length == 0 || string.Equals(e.Args.GetString("emoteName"), s["emote"].Trim(), StringComparison.Ordinal);

    /// <summary>Trigger whose type id equals the event type it listens to.</summary>
    private static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, Category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));

    /// <summary>A core (any-platform) event, only when it is about Kick.</summary>
    private static EventTriggerType KickOnly(string typeId, string displayName, string eventType) =>
        new(typeId, Category, displayName, eventType, null,
            (e, _, _) => string.Equals(e.Args.GetString("platform"), KickChatAdapter.PlatformName, StringComparison.OrdinalIgnoreCase));
}
