using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Twitch.Triggers;

/// <summary>All Twitch trigger types: one per published event, plus the chat command trigger.</summary>
internal sealed class TwitchTriggers : ITriggerProvider
{
    public const string Category = "Twitch";

    private readonly CommandTriggerType _command;

    public TwitchTriggers() : this(TimeProvider.System)
    {
    }

    internal TwitchTriggers(TimeProvider time) => _command = new CommandTriggerType(new CooldownTracker(time));

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        _command,
        Event(TwitchEventTypes.ChatMessage, "Chat message",
            [new("contains", "Message contains") { Help = "Leave empty to match every message" }],
            (e, s) => s["contains"].Length == 0
                || e.Args.GetString("message").Contains(s["contains"], StringComparison.OrdinalIgnoreCase)),
        Event(TwitchEventTypes.Follow, "Follow"),
        Event(TwitchEventTypes.Sub, "Subscription"),
        Event(TwitchEventTypes.Resub, "Resubscription"),
        Event(TwitchEventTypes.GiftSub, "Gift subscriptions",
            [new("minTotal", "Minimum gifted", ParameterKind.Number) { DefaultValue = "1" }],
            (e, s) => e.Args.GetLong("total") >= s.GetInt("minTotal")),
        Event(TwitchEventTypes.Cheer, "Cheer",
            [
                new("minBits", "Minimum bits", ParameterKind.Number) { DefaultValue = "1" },
                new("maxBits", "Maximum bits", ParameterKind.Number) { DefaultValue = "0", Help = "0 means no maximum" },
            ],
            (e, s) => InRange(e.Args.GetLong("bits"), s.GetInt("minBits"), s.GetInt("maxBits"))),
        Event(TwitchEventTypes.Raid, "Raid",
            [new("minViewers", "Minimum viewers", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetLong("viewers") >= s.GetInt("minViewers")),
        Event(TwitchEventTypes.RewardRedemption, "Channel point reward",
            [new("rewardTitle", "Reward title") { Help = "Leave empty to match every reward" }],
            (e, s) => s["rewardTitle"].Trim().Length == 0
                || string.Equals(e.Args.GetString("rewardTitle"), s["rewardTitle"].Trim(), StringComparison.OrdinalIgnoreCase)),
        Event(TwitchEventTypes.StreamOnline, "Stream online"),
        Event(TwitchEventTypes.StreamOffline, "Stream offline"),
    ];

    private static bool InRange(long value, int min, int max) => value >= min && (max <= 0 || value <= max);

    /// <summary>Trigger whose type id equals the event type it listens to.</summary>
    private static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, Category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));
}
