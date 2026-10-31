using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Obs.SubActions;

namespace Cuebot.Obs;

/// <summary>
/// Triggers for the events published by the OBS connections. Every trigger can be limited to one connection;
/// text filters are case-insensitive and empty means "any".
/// </summary>
public sealed class ObsTriggers(ObsService obs) : ITriggerProvider
{
    private const string Category = ObsSubActionSupport.Category;

    public IEnumerable<ITriggerType> GetTriggers()
    {
        var scene = new ParameterDescriptor("scene", "Scene") { Suggestions = () => obs.All(c => c.Scenes), Help = "Leave empty for any scene" };
        var source = new ParameterDescriptor("source", "Source") { Suggestions = () => obs.All(c => c.Inputs.Concat(c.Scenes)), Help = "Leave empty for any source" };
        var input = new ParameterDescriptor("input", "Input") { Suggestions = () => obs.All(c => c.Inputs), Help = "Leave empty for any input" };
        var transition = new ParameterDescriptor("transition", "Transition") { Suggestions = () => obs.All(c => c.Transitions), Help = "Leave empty for any transition" };
        var filter = new ParameterDescriptor("filter", "Filter") { Help = "Leave empty for any filter" };

        return
        [
            Trigger(ObsEvents.SceneChanged, "Scene changed", [scene], ("scene", "sceneName")),
            Trigger(ObsEvents.PreviewSceneChanged, "Preview scene changed", [scene], ("scene", "sceneName")),
            Trigger(ObsEvents.StreamStarted, "Stream started"),
            Trigger(ObsEvents.StreamStopped, "Stream stopped"),
            Trigger(ObsEvents.RecordStarted, "Recording started"),
            Trigger(ObsEvents.RecordStopped, "Recording stopped"),
            Trigger(ObsEvents.RecordPaused, "Recording paused"),
            Trigger(ObsEvents.RecordResumed, "Recording resumed"),
            Trigger(ObsEvents.RecordFileChanged, "Recording file changed"),
            Trigger(ObsEvents.ReplayBufferStarted, "Replay buffer started"),
            Trigger(ObsEvents.ReplayBufferStopped, "Replay buffer stopped"),
            Trigger(ObsEvents.ReplayBufferSaved, "Replay buffer saved"),
            Trigger(ObsEvents.VirtualCamStarted, "Virtual camera started"),
            Trigger(ObsEvents.VirtualCamStopped, "Virtual camera stopped"),
            Trigger(ObsEvents.StudioModeChanged, "Studio mode changed",
                [new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Enabled", "Disabled"] }],
                MatchesState("studioModeEnabled", "Enabled", "Disabled")),
            Trigger(ObsEvents.TransitionStarted, "Transition started", [transition], ("transition", "transitionName")),
            Trigger(ObsEvents.TransitionEnded, "Transition ended", [transition], ("transition", "transitionName")),
            Trigger(ObsEvents.TransitionChanged, "Current transition changed", [transition], ("transition", "transitionName")),
            Trigger(ObsEvents.TransitionDurationChanged, "Transition duration changed"),
            Trigger(ObsEvents.SourceVisibilityChanged, "Source visibility changed",
                [scene, source, new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Visible", "Hidden"] }],
                MatchesState("visible", "Visible", "Hidden"), ("scene", "sceneName"), ("source", "sourceName")),
            Trigger(ObsEvents.FilterStateChanged, "Filter state changed",
                [source, filter, new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Enabled", "Disabled"] }],
                MatchesState("filterEnabled", "Enabled", "Disabled"), ("source", "sourceName"), ("filter", "filterName")),
            Trigger(ObsEvents.MediaStarted, "Media playback started", [input], ("input", "inputName")),
            Trigger(ObsEvents.MediaEnded, "Media playback ended", [input], ("input", "inputName")),
            Trigger(ObsEvents.MediaAction, "Media action triggered", [input], ("input", "inputName")),
            Trigger(ObsEvents.InputMuteChanged, "Input mute changed",
                [input, new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Muted", "Unmuted"] }],
                MatchesState("inputMuted", "Muted", "Unmuted"), ("input", "inputName")),
            Trigger(ObsEvents.InputVolumeChanged, "Input volume changed", [input], ("input", "inputName")),
            Trigger(ObsEvents.ProfileChanged, "Profile changed"),
            Trigger(ObsEvents.SceneCollectionChanged, "Scene collection changed"),
            Trigger(ObsEvents.ScreenshotSaved, "Screenshot saved"),
            Trigger(ObsEvents.VendorEvent, "Plugin message (advanced)",
                [new ParameterDescriptor("vendor", "Vendor name") { Help = "Leave empty for any vendor" },
                 new ParameterDescriptor("vendorEventType", "Event type") { Help = "Leave empty for any event type" }],
                ("vendor", "vendorName"), ("vendorEventType", "vendorEventType")),
            Trigger(ObsEvents.Event, "Any OBS event (advanced)",
                [new ParameterDescriptor("eventType", "Event type") { Help = "e.g. InputActiveStateChanged; leave empty for every event" }],
                ("eventType", "eventType")),
            Trigger(ObsEvents.Connected, "Connected"),
            Trigger(ObsEvents.Disconnected, "Disconnected"),
        ];
    }

    private EventTriggerType Trigger(string type, string name, params (string Setting, string Arg)[] filters) =>
        Trigger(type, name, [], null, filters);

    private EventTriggerType Trigger(string type, string name, IReadOnlyList<ParameterDescriptor> parameters,
        params (string Setting, string Arg)[] filters) => Trigger(type, name, parameters, null, filters);

    private EventTriggerType Trigger(string type, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<BotEvent, ParameterValues, bool>? extra, params (string Setting, string Arg)[] filters)
    {
        (string, string)[] all = [(ObsSubAction.ConnectionKey, ObsEventMapper.ConnectionArg), .. filters];
        return new EventTriggerType(type, Category, name, type, [ObsSubAction.ConnectionParameter(obs), .. parameters],
            (e, settings, _) => MatchesAll(e, settings, all) && (extra?.Invoke(e, settings) ?? true));
    }

    /// <summary>True when every non-empty setting equals its event argument (case-insensitive).</summary>
    internal static bool MatchesAll(BotEvent e, ParameterValues settings, IEnumerable<(string Setting, string Arg)> filters) =>
        filters.All(f => string.IsNullOrWhiteSpace(settings[f.Setting]) ||
                         string.Equals(Convert.ToString(e.Args.GetValueOrDefault(f.Arg), System.Globalization.CultureInfo.InvariantCulture),
                             settings[f.Setting].Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Filters a boolean argument through a "state" choice of Any / <paramref name="whenTrue"/> / <paramref name="whenFalse"/>.</summary>
    internal static Func<BotEvent, ParameterValues, bool> MatchesState(string arg, string whenTrue, string whenFalse) => (e, settings) =>
    {
        var state = settings["state"];
        var value = e.Args.GetValueOrDefault(arg) is true;
        return string.Equals(state, whenTrue, StringComparison.OrdinalIgnoreCase) ? value
            : !string.Equals(state, whenFalse, StringComparison.OrdinalIgnoreCase) || !value;
    };

    internal static bool MatchesSceneFilter(BotEvent e, ParameterValues settings, IDictionary<string, object?> args) =>
        MatchesAll(e, settings, [("scene", "sceneName")]);
}
