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
            Trigger(ObsEvents.TransitionVideoEnded, "Stinger video ended", [transition], ("transition", "transitionName")),
            Trigger(ObsEvents.AudioMonitorChanged, "Audio monitoring changed", [input], ("input", "inputName")),
            Trigger(ObsEvents.AudioBalanceChanged, "Audio balance changed", [input], ("input", "inputName")),
            Trigger(ObsEvents.AudioSyncOffsetChanged, "Audio sync offset changed", [input], ("input", "inputName")),
            Trigger(ObsEvents.AudioTracksChanged, "Audio tracks changed", [input], ("input", "inputName")),
            Trigger(ObsEvents.InputSettingsChanged, "Source settings changed", [input], ("input", "inputName")),
            Trigger(ObsEvents.SourceOrderChanged, "Source order changed", [scene], ("scene", "sceneName")),
            Trigger(ObsEvents.SourceLockChanged, "Source locked / unlocked",
                [scene, source, new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Locked", "Unlocked"] }],
                MatchesState("locked", "Locked", "Unlocked"), ("scene", "sceneName"), ("source", "sourceName")),
            Trigger(ObsEvents.FilterAdded, "Filter added", [source, filter], ("source", "sourceName"), ("filter", "filterName")),
            Trigger(ObsEvents.FilterRemoved, "Filter removed", [source, filter], ("source", "sourceName"), ("filter", "filterName")),
            Trigger(ObsEvents.FilterRenamed, "Filter renamed", [source, filter], ("source", "sourceName"), ("filter", "filterName")),
            Trigger(ObsEvents.FilterOrderChanged, "Filter order changed", [source], ("source", "sourceName")),
            Trigger(ObsEvents.FilterSettingsChanged, "Filter settings changed", [source, filter], ("source", "sourceName"), ("filter", "filterName")),
            Trigger(ObsEvents.ProfileChanging, "Profile about to change"),
            Trigger(ObsEvents.SceneCollectionChanging, "Scene collection about to change"),
            Trigger(ObsEvents.SourceActiveChanged, "Source went live / left the live output",
                [input, new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Live", "Not live"] }],
                MatchesState("videoActive", "Live", "Not live"), ("input", "inputName")),
            Trigger(ObsEvents.SourceShowChanged, "Source shown / hidden anywhere",
                [input, new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Showing", "Not showing"] }],
                MatchesState("videoShowing", "Showing", "Not showing"), ("input", "inputName")),
            Trigger(ObsEvents.Talking, "Talking (input level)",
                [input with { Help = "The microphone (audio input); leave empty for any input" },
                 new ParameterDescriptor("threshold", "Level (dB)") { DefaultValue = "-30", Help = "Talking starts when the level goes above this, e.g. -30" },
                 new ParameterDescriptor("quietMs", "Quiet time (ms)") { DefaultValue = "300", Help = "Talking stops after this long below the level" },
                 new ParameterDescriptor("state", "State", ParameterKind.Choice) { DefaultValue = "Started", Choices = ["Any", "Started", "Stopped"] }],
                (e, settings) => ObsTalkingSettings.From(settings) == ObsTalkingSettings.FromEvent(e.Args) &&
                                 MatchesState("talking", "Started", "Stopped")(e, settings)),
            Trigger(ObsEvents.SourceTransformChanged, "Source moved or resized",
                [scene, source], ("scene", "sceneName"), ("source", "sourceName")),
            Trigger(ObsEvents.CustomEvent, "Custom OBS event (from overlays and other tools)",
                [new ParameterDescriptor("field", "Field") { Help = "A field of the event, like type or user.name; leave empty for every custom event" },
                 new ParameterDescriptor("value", "Value") { Help = "Only when the field has this value; empty means the field just has to exist" }],
                MatchesCustomEvent),
            Trigger(ObsEvents.Event, "Any OBS event (advanced)",
                [new ParameterDescriptor("eventType", "Event type") { Help = "e.g. SceneItemLockStateChanged; leave empty for every event" }],
                ("eventType", "eventType")),
            Trigger(ObsEvents.Connected, "Connected"),
            Trigger(ObsEvents.Disconnected, "Disconnected"),
            Trigger(ObsEvents.Exiting, "OBS is closing"),
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

    /// <summary>A custom event matches when the field (a dotted path) exists and, if a value is given, equals it (case-insensitive).</summary>
    internal static bool MatchesCustomEvent(BotEvent e, ParameterValues settings)
    {
        var field = settings["field"];
        if (string.IsNullOrWhiteSpace(field)) return true;
        if (ObsEventMapper.ValueAtPath(Convert.ToString(e.Args.GetValueOrDefault("eventData"), System.Globalization.CultureInfo.InvariantCulture) ?? "{}",
                field) is not { } value) return false;
        return string.IsNullOrWhiteSpace(settings["value"]) ||
               string.Equals(value, settings["value"].Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>High-volume obs-websocket events by name, with their subscription bit.</summary>
    internal static readonly IReadOnlyDictionary<string, int> HighVolumeEvents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["InputVolumeMeters"] = Protocol.ObsClient.InputVolumeMetersSubscription,
        ["InputActiveStateChanged"] = Protocol.ObsClient.InputActiveStateChangedSubscription,
        ["InputShowStateChanged"] = Protocol.ObsClient.InputShowStateChangedSubscription,
        ["SceneItemTransformChanged"] = Protocol.ObsClient.SceneItemTransformChangedSubscription,
    };

    /// <summary>The high-volume subscription each dedicated trigger needs.</summary>
    internal static readonly IReadOnlyDictionary<string, int> TriggerSubscriptions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [ObsEvents.SourceActiveChanged] = Protocol.ObsClient.InputActiveStateChangedSubscription,
        [ObsEvents.SourceShowChanged] = Protocol.ObsClient.InputShowStateChangedSubscription,
        [ObsEvents.Talking] = Protocol.ObsClient.InputVolumeMetersSubscription,
        [ObsEvents.SourceTransformChanged] = Protocol.ObsClient.SceneItemTransformChangedSubscription,
    };

    /// <summary>What one connection must subscribe to for a set of enabled triggers.</summary>
    internal sealed record SubscriptionPlan(int Bits, bool PublishVolumeMeters, IReadOnlyList<ObsTalkingSettings> Talking);

    /// <summary>
    /// The high-volume subscriptions (and talking detectors) the given enabled OBS triggers of one connection need.
    /// "Any OBS event" asks for a high-volume event only when its event type names it exactly.
    /// </summary>
    internal static SubscriptionPlan PlanSubscriptions(IEnumerable<TriggerDefinition> triggers)
    {
        var bits = 0;
        var meters = false;
        var talking = new List<ObsTalkingSettings>();
        foreach (var trigger in triggers)
        {
            if (trigger.Type == ObsEvents.Event && trigger.Settings.TryGetValue("eventType", out var name) &&
                HighVolumeEvents.TryGetValue(name.Trim(), out var bit))
            {
                bits |= bit;
                meters |= bit == Protocol.ObsClient.InputVolumeMetersSubscription;
            }
            else if (TriggerSubscriptions.TryGetValue(trigger.Type, out var needed))
            {
                bits |= needed;
            }
            if (trigger.Type == ObsEvents.Talking) talking.Add(ObsTalkingSettings.From(trigger.Settings));
        }
        return new SubscriptionPlan(bits, meters, talking.Distinct().ToArray());
    }

    internal static bool MatchesSceneFilter(BotEvent e, ParameterValues settings, IDictionary<string, object?> args) =>
        MatchesAll(e, settings, [("scene", "sceneName")]);
}
