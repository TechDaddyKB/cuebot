using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Obs.SubActions;

namespace Cuebot.Obs;

/// <summary>Triggers for the events published by <see cref="ObsService"/>.</summary>
public sealed class ObsTriggers(ObsService obs) : ITriggerProvider
{
    private const string Category = ObsSubActionSupport.Category;

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(ObsEvents.SceneChanged, Category, "Scene changed", ObsEvents.SceneChanged,
            [new ParameterDescriptor("scene", "Scene") { Suggestions = () => obs.Scenes, Help = "Leave empty for any scene" }],
            MatchesSceneFilter),
        new EventTriggerType(ObsEvents.StreamStarted, Category, "Stream started", ObsEvents.StreamStarted),
        new EventTriggerType(ObsEvents.StreamStopped, Category, "Stream stopped", ObsEvents.StreamStopped),
        new EventTriggerType(ObsEvents.RecordStarted, Category, "Recording started", ObsEvents.RecordStarted),
        new EventTriggerType(ObsEvents.RecordStopped, Category, "Recording stopped", ObsEvents.RecordStopped),
        new EventTriggerType(ObsEvents.Connected, Category, "Connected", ObsEvents.Connected),
        new EventTriggerType(ObsEvents.Disconnected, Category, "Disconnected", ObsEvents.Disconnected),
    ];

    internal static bool MatchesSceneFilter(BotEvent e, ParameterValues settings, IDictionary<string, object?> args) =>
        string.IsNullOrWhiteSpace(settings["scene"]) ||
        string.Equals(e.Args.GetValueOrDefault("sceneName") as string, settings["scene"], StringComparison.OrdinalIgnoreCase);
}
