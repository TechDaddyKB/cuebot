using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.StreamApps.Common;
using E = Cuebot.StreamApps.Meld.MeldEvents;

namespace Cuebot.StreamApps.Meld;

/// <summary>Meld Studio triggers (category "Meld Studio"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class MeldTriggers(MeldStudioService meld) : ITriggerProvider
{
    public const string Category = "Meld Studio";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor SceneFilter() => new("scene", "Scenes")
        {
            Help = "Comma-separated names; leave empty for any scene",
            Suggestions = () => meld.Session.Scenes.Select(s => s.Name),
        };
        ParameterDescriptor LayerFilter() => new("layer", "Layers")
        {
            Help = "Comma-separated names or ids; leave empty for any layer",
            Suggestions = () => meld.Session.Layers.Select(l => l.Name).Distinct(),
        };
        ParameterDescriptor EffectFilter() => new("effect", "Effects")
        {
            Help = "Comma-separated names or ids; leave empty for any effect",
            Suggestions = () => meld.Session.Effects.Select(e => e.Name).Distinct(),
        };
        ParameterDescriptor TrackFilter() => new("track", "Tracks")
        {
            Help = "Comma-separated names or ids; leave empty for any track",
            Suggestions = () => meld.Session.Tracks.Select(t => meld.Session.DisplayName(t)).Distinct(),
        };
        ParameterDescriptor StateFilter(string on, string off) =>
            new("stateFilter", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", on, off] };

        return
        [
            Steps.Trigger(Category, E.Connected, "Connected"),
            Steps.Trigger(Category, E.Disconnected, "Disconnected"),
            Steps.Trigger(Category, E.SceneChanged, "Scene changed", [SceneFilter()],
                (e, s) => Steps.MatchesList(s["scene"], e.Text("meldStudio.sceneName"), e.Text("meldStudio.sceneId"))),
            Steps.Trigger(Category, E.RecordingStarted, "Recording started"),
            Steps.Trigger(Category, E.RecordingStopped, "Recording stopped"),
            Steps.Trigger(Category, E.StreamingStarted, "Streaming started"),
            Steps.Trigger(Category, E.StreamingStopped, "Streaming stopped"),
            Steps.Trigger(Category, E.Event, "Any Meld change (advanced)",
                [new("event", "Event", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", .. E.EventNames] }],
                (e, s) => s["event"] is "Any" or "" || string.Equals(s["event"], Kind(e), StringComparison.OrdinalIgnoreCase)),
            Steps.Trigger(Category, E.Event, "Layer visibility changed", [LayerFilter(), StateFilter("Visible", "Hidden")],
                (e, s) => Kind(e) == E.LayerVisibilityChanged && Item(e, s["layer"], "layer") && State(e, s, "visible", "Visible"),
                typeId: "meldstudio.layerVisibilityChanged"),
            Steps.Trigger(Category, E.Event, "Effect enabled state changed", [EffectFilter(), StateFilter("Enabled", "Disabled")],
                (e, s) => Kind(e) == E.EffectEnabledStateChanged && Item(e, s["effect"], "effect") && State(e, s, "enabled", "Enabled"),
                typeId: "meldstudio.effectEnabledStateChanged"),
            Steps.Trigger(Category, E.Event, "Track muted state changed", [TrackFilter(), StateFilter("Muted", "Not muted")],
                (e, s) => Kind(e) == E.TrackMutedStateChanged && Item(e, s["track"], "track") && State(e, s, "muted", "Muted"),
                typeId: "meldstudio.trackMutedStateChanged"),
            Steps.Trigger(Category, E.Event, "Track monitoring state changed", [TrackFilter(), StateFilter("Monitoring", "Not monitoring")],
                (e, s) => Kind(e) == E.TrackMonitoringStateChanged && Item(e, s["track"], "track") && State(e, s, "monitoring", "Monitoring"),
                typeId: "meldstudio.trackMonitoringStateChanged"),
            Steps.Trigger(Category, E.Event, "Track gain changed", [TrackFilter()],
                (e, s) => Kind(e) == E.TrackGainChanged && Item(e, s["track"], "track"),
                typeId: "meldstudio.trackGainChanged"),
            Steps.Trigger(Category, E.Event, "Scene staged", [SceneFilter()],
                (e, s) => Kind(e) == E.SceneStaged && Steps.MatchesList(s["scene"], e.Text("meldStudioEvent.sceneName"), e.Text("meldStudioEvent.sceneId")),
                typeId: "meldstudio.sceneStaged"),
        ];
    }

    private static string Kind(BotEvent e) => e.Text("meldStudioEvent.event");

    private static bool Item(BotEvent e, string filter, string prefix) =>
        Steps.MatchesList(filter, e.Text($"meldStudioEvent.{prefix}Name"), e.Text($"meldStudioEvent.{prefix}Id"));

    private static bool State(BotEvent e, ParameterValues s, string flag, string on) => s["stateFilter"] switch
    {
        "Any" or "" => true,
        var v when v == on => e.Flag($"meldStudioEvent.{flag}") == true,
        _ => e.Flag($"meldStudioEvent.{flag}") == false,
    };
}
