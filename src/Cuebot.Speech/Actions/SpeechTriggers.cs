using Cuebot.Core.Actions;

namespace Cuebot.Speech;

/// <summary>Triggers fired when speech starts and finishes (optionally filtered by alias) and when its state changes.</summary>
internal sealed class SpeechTriggers(SpeechService speech) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Create("speech.started", "Speech started", SpeechEvents.Started),
        Create("speech.finished", "Speech finished", SpeechEvents.Finished),
        new EventTriggerType("speech.stateChanged", SpeechCategories.Speech, "Speech state changed", SpeechEvents.StateChanged),
    ];

    private EventTriggerType Create(string typeId, string displayName, string eventType) =>
        new(typeId, SpeechCategories.Speech, displayName, eventType,
            [
                new ParameterDescriptor("alias", "Voice alias")
                {
                    Suggestions = () => speech.Settings.Aliases.Select(a => a.Name),
                    Help = "Leave empty for any voice",
                },
            ],
            (e, settings, _) => MatchesAlias(e.Args.GetValueOrDefault("alias") as string, settings["alias"]));

    internal static bool MatchesAlias(string? spokenAlias, string filter) =>
        string.IsNullOrWhiteSpace(filter) || string.Equals(spokenAlias, filter.Trim(), StringComparison.OrdinalIgnoreCase);
}
