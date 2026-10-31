using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;
using E = Cuebot.Integrations.VTubeStudio.VTubeStudioEvents;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>VTube Studio triggers (category "VTube Studio").</summary>
internal sealed class VTubeStudioTriggers(VTubeStudioService vts) : ITriggerProvider
{
    public const string Category = "VTube Studio";

    private static readonly string[] ItemEventTypes = ["Any", "Added", "Removed", "DroppedPinned", "DroppedUnpinned", "Clicked", "Locked", "Unlocked"];

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor ModelFilter() => new("modelName", "Model names")
        {
            Help = "Comma-separated; leave empty for any model",
            Suggestions = () => vts.Models.Select(m => m.Name),
        };

        return
        [
            Steps.Trigger(Category, E.Connected, "Connected"),
            Steps.Trigger(Category, E.Disconnected, "Disconnected"),
            Steps.Trigger(Category, E.ModelLoaded, "Model loaded", [ModelFilter()], (e, s) => Steps.MatchesList(s["modelName"], e.Text("modelName"))),
            Steps.Trigger(Category, E.ModelUnloaded, "Model unloaded"),
            Steps.Trigger(Category, E.HotkeyTriggered, "Hotkey triggered",
                [
                    new("hotkeyName", "Hotkey names") { Help = "Comma-separated names or ids; leave empty for any", Suggestions = () => vts.Hotkeys.Select(h => h.Name) },
                    new("hotkeyAction", "Hotkey action") { Help = "e.g. TriggerAnimation, ToggleExpression; leave empty for any" },
                    new("ignoreApi", "Ignore hotkeys triggered by plugins", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                (e, s) => Steps.MatchesList(s["hotkeyName"], e.Text("hotkeyName"), e.Text("hotkeyId"))
                    && Steps.MatchesList(s["hotkeyAction"], e.Text("hotkeyAction"))
                    && !(s.GetBool("ignoreApi") && e.Flag("triggeredByApi") == true)),
            Steps.Trigger(Category, E.TrackingStatusChanged, "Tracking status changed"),
            Steps.Trigger(Category, E.TrackingLost, "Face tracking lost"),
            Steps.Trigger(Category, E.TrackingFound, "Face tracking found"),
            Steps.Trigger(Category, E.BackgroundChanged, "Background changed",
                [new("backgroundName", "Background names") { Help = "Comma-separated; leave empty for any" }],
                (e, s) => Steps.MatchesList(s["backgroundName"], e.Text("backgroundName"))),
            Steps.Trigger(Category, E.ModelConfigChanged, "Model configuration changed"),
            Steps.Trigger(Category, E.ModelMoved, "Model moved"),
            Steps.Trigger(Category, E.ModelAnimation, "Model animation",
                [
                    new("animationEventType", "Animation event", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Start", "End", "Custom"] },
                    new("animationName", "Animation names") { Help = "Comma-separated; leave empty for any" },
                    new("ignoreIdle", "Ignore idle animations", ParameterKind.Boolean) { DefaultValue = "true" },
                ],
                (e, s) => (s["animationEventType"] is "Any" or "" || string.Equals(s["animationEventType"], e.Text("animationEventType"), StringComparison.OrdinalIgnoreCase))
                    && Steps.MatchesList(s["animationName"], e.Text("animationName"))
                    && !(s.GetBool("ignoreIdle", true) && e.Flag("isIdleAnimation") == true)),
            Steps.Trigger(Category, E.ItemEvent, "Item event",
                [
                    new("itemEventType", "Item event", ParameterKind.Choice) { DefaultValue = "Any", Choices = ItemEventTypes },
                    ItemFileFilter(),
                ],
                (e, s) => ItemMatches(e, s["itemEventType"], s)),
            Steps.Trigger(Category, E.ItemEvent, "Item added", [ItemFileFilter()], (e, s) => ItemMatches(e, "Added", s), typeId: "vtubestudio.itemAdded"),
            Steps.Trigger(Category, E.ItemEvent, "Item removed", [ItemFileFilter()], (e, s) => ItemMatches(e, "Removed", s), typeId: "vtubestudio.itemRemoved"),
            Steps.Trigger(Category, E.ModelClicked, "Model clicked",
                [
                    new("mouseButton", "Mouse button", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Left", "Right", "Middle"] },
                    new("onlyOnModel", "Only clicks on the model", ParameterKind.Boolean) { DefaultValue = "true" },
                ],
                (e, s) => (s["mouseButton"] is "Any" or "" || s["mouseButton"] == e.Text("mouseButton"))
                    && !(s.GetBool("onlyOnModel", true) && e.Flag("modelWasClicked") != true)),
            Steps.Trigger(Category, E.ExpressionToggled, "Expression toggled",
                [new("expressionName", "Expression names") { Help = "Names or files, comma-separated; leave empty for any", Suggestions = () => vts.Expressions.Select(x => x.DisplayName) },
                 Steps.StateFilter()],
                (e, s) => Steps.MatchesList(s["expressionName"], e.Text("expressionName"), e.Text("expressionFile")) && Steps.MatchesState(e, s, "active")),
            Steps.Trigger(Category, E.PostProcessingChanged, "Post-processing changed"),
            Steps.Trigger(Category, E.Any, "Any VTube Studio message (advanced)",
                [new("eventName", "API event names") { Help = "e.g. ModelMovedEvent; comma-separated, empty for any" }],
                (e, s) => Steps.MatchesList(s["eventName"], e.Text("eventName"))),
        ];

        ParameterDescriptor ItemFileFilter() => new("itemFileName", "Item files")
        {
            Help = "Comma-separated file names; leave empty for any",
            Suggestions = () => vts.ItemFiles.Select(f => f.FileName),
        };
    }

    private static bool ItemMatches(Cuebot.Core.Events.BotEvent e, string type, ParameterValues s) =>
        (type is "Any" or "" || string.Equals(type, e.Text("itemEventType"), StringComparison.OrdinalIgnoreCase))
        && Steps.MatchesList(s["itemFileName"], e.Text("itemFileName"));
}
