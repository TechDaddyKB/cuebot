using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.StreamDeck;

/// <summary>Stream Deck triggers (Streamer.bot: Elgato → Stream Deck → Action, Connected, Disconnected, Info).</summary>
internal sealed class StreamDeckTriggers : ITriggerProvider
{
    public const string Category = "Stream Deck";

    /// <summary>Choice label → plugin event names it matches.</summary>
    internal static readonly (string Label, string[] Events)[] EventChoices =
    [
        ("Any", []),
        ("Key down", ["keyDown"]),
        ("Key up", ["keyUp"]),
        ("Dial rotate", ["dialRotate"]),
        ("Dial press", ["dialDown"]),
        ("Dial release", ["dialUp"]),
        ("Touch tap", ["touchTap"]),
        ("Any key or dial press", ["keyDown", "dialDown", "touchTap"]),
    ];

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(StreamDeckHub.ActionEvent, Category, "Stream Deck key or dial used", StreamDeckHub.ActionEvent,
            [
                new("buttonIds", "Button IDs") { Help = "Comma-separated button ids (set on each key in Stream Deck); empty for any Cuebot trigger key or dial" },
                new("event", "Event", ParameterKind.Choice) { DefaultValue = "Key down", Choices = [.. EventChoices.Select(c => c.Label)] },
            ],
            (e, s, _) => MatchesAction(e, s["buttonIds"], s["event"])),
        new EventTriggerType(StreamDeckHub.ConnectedEvent, Category, "Stream Deck Connected", StreamDeckHub.ConnectedEvent),
        new EventTriggerType(StreamDeckHub.DisconnectedEvent, Category, "Stream Deck Disconnected", StreamDeckHub.DisconnectedEvent),
        new EventTriggerType(StreamDeckHub.InfoEvent, Category, "Stream Deck devices changed", StreamDeckHub.InfoEvent),
    ];

    internal static bool MatchesAction(BotEvent e, string buttonIds, string eventChoice)
    {
        var wanted = buttonIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var id = Text(e, "buttonId");
        if (wanted.Length > 0 && !wanted.Any(w => string.Equals(w, id, StringComparison.OrdinalIgnoreCase))) return false;
        var choice = EventChoices.FirstOrDefault(c => string.Equals(c.Label, eventChoice.Trim(), StringComparison.OrdinalIgnoreCase));
        var events = choice.Label is null ? [eventChoice.Trim()] : choice.Events;
        return events.Length == 0 || events.Contains(Text(e, "buttonEvent"), StringComparer.OrdinalIgnoreCase);
    }

    private static string Text(BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>
/// Stream Deck sub-actions. Each targets keys by button id; keys sharing an id change together. Type ids are stored in
/// actions.json and must never change. Each stores the number of visible keys it reached in <c>%streamDeckButtons%</c>.
/// </summary>
internal sealed class StreamDeckSubAction(
    StreamDeckHub hub,
    string typeId,
    string displayName,
    IReadOnlyList<ParameterDescriptor> extra,
    Func<string, ParameterValues, StreamDeckCommand> build) : ISubActionHandler
{
    public static readonly string[] TypeIds =
    [
        "streamDeck.setBackground", "streamDeck.setState", "streamDeck.setTitle", "streamDeck.setValue",
        "streamDeck.showAlert", "streamDeck.showOk", "streamDeck.toggleState",
    ];

    public string TypeId => typeId;
    public string Category => StreamDeckTriggers.Category;
    public string DisplayName => displayName;

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("buttonId", "Button ID")
        {
            Help = "The key's button id (shown in the key's settings in Stream Deck and on the Stream Deck page); supports %variables%",
            Suggestions = () => hub.KnownButtonIds,
        },
        .. extra,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var buttonId = parameters["buttonId"].Trim();
        if (buttonId.Length == 0) throw new ArgumentException("Button ID is required.");
        var count = await hub.SendCommandAsync(build(buttonId, parameters), ct).ConfigureAwait(false);
        context.Args["streamDeckButtons"] = count;
        return SubActionResult.Next;
    }

    public static IReadOnlyList<ISubActionHandler> Create(StreamDeckHub hub) =>
    [
        new StreamDeckSubAction(hub, "streamDeck.setBackground", "Set key picture",
            [
                new("image", "Image") { Help = "A PNG/JPEG/GIF/SVG file, an http(s) URL or a data: URI; empty restores the key's own image" },
                StateParameter(),
            ],
            (id, p) => new StreamDeckCommand("setImage", id, Image: StreamDeckProtocol.ResolveImage(p["image"]), State: OptionalState(p["state"]))),
        new StreamDeckSubAction(hub, "streamDeck.setState", "Set State",
            [new("state", "State", ParameterKind.Choice) { DefaultValue = "0", Choices = ["0", "1"], Help = "0 is the first state, 1 the second" }],
            (id, p) => new StreamDeckCommand("setState", id, State: OptionalState(p["state"]) ?? 0)),
        new StreamDeckSubAction(hub, "streamDeck.setTitle", "Set Title",
            [new("title", "Title", ParameterKind.Multiline) { Help = "Empty restores the key's own title" }, StateParameter()],
            (id, p) => new StreamDeckCommand("setTitle", id, Title: p["title"], State: OptionalState(p["state"]))),
        new StreamDeckSubAction(hub, "streamDeck.setValue", "Set key value",
            [
                new("value", "Value") { Help = "Shown as the key's title, or as the value on a dial's touch strip" },
                new("indicator", "Indicator (dials)") { Help = "Optional 0-100 for a dial's indicator bar" },
            ],
            (id, p) => new StreamDeckCommand("setValue", id, Value: p["value"], Indicator: OptionalIndicator(p["indicator"]))),
        new StreamDeckSubAction(hub, "streamDeck.showAlert", "Show Alert", [], (id, _) => new StreamDeckCommand("showAlert", id)),
        new StreamDeckSubAction(hub, "streamDeck.showOk", "Show Ok", [], (id, _) => new StreamDeckCommand("showOk", id)),
        new StreamDeckSubAction(hub, "streamDeck.toggleState", "Toggle State", [], (id, _) => new StreamDeckCommand("toggleState", id)),
    ];

    private static ParameterDescriptor StateParameter() =>
        new("state", "State", ParameterKind.Choice) { DefaultValue = "Current", Choices = ["Current", "0", "1"], Help = "Which state of a two-state key to change" };

    internal static int? OptionalState(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Equals("Current", StringComparison.OrdinalIgnoreCase) || t.Equals("Both", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is 0 or 1
            ? s
            : throw new ArgumentException($"State must be 0 or 1 (got '{text}').");
    }

    internal static double? OptionalIndicator(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100)
            : throw new ArgumentException($"Indicator must be a number from 0 to 100 (got '{text}').");
    }
}
