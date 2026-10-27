using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Rumble.Triggers;

/// <summary>All Rumble trigger types: one per published event, plus the chat command trigger.</summary>
internal sealed class RumbleTriggers : ITriggerProvider
{
    public const string Category = "Rumble";

    private readonly CommandTriggerType _command;

    public RumbleTriggers() : this(TimeProvider.System)
    {
    }

    internal RumbleTriggers(TimeProvider time) => _command = new CommandTriggerType(new CooldownTracker(time));

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        _command,
        Event(RumbleEventTypes.ChatMessage, "Chat message",
            [new("contains", "Message contains") { Help = "Leave empty to match every message" }],
            (e, s) => s["contains"].Length == 0
                || e.Args.GetString("message").Contains(s["contains"], StringComparison.OrdinalIgnoreCase)),
        Event(RumbleEventTypes.Rant, "Rant",
            [new("minAmount", "Minimum amount ($)", ParameterKind.Number) { DefaultValue = "0" }],
            (e, s) => e.Args.GetDouble("amount") >= s.GetDouble("minAmount")),
        Event(RumbleEventTypes.Follow, "Follow"),
        Event(RumbleEventTypes.Subscribe, "Subscription"),
        Event(RumbleEventTypes.GiftSub, "Gift subscriptions",
            [new("minTotal", "Minimum gifted", ParameterKind.Number) { DefaultValue = "1" }],
            (e, s) => e.Args.GetDouble("total") >= s.GetInt("minTotal")),
        Event(RumbleEventTypes.StreamOnline, "Stream online"),
        Event(RumbleEventTypes.StreamOffline, "Stream offline"),
    ];

    /// <summary>Trigger whose type id equals the event type it listens to.</summary>
    private static EventTriggerType Event(string eventType, string displayName,
        IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
        new(eventType, Category, displayName, eventType, parameters,
            filter is null ? null : (e, settings, _) => filter(e, settings));
}
