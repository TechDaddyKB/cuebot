using Cuebot.VoiceControl.Engines;

namespace Cuebot.VoiceControl.Matching;

/// <summary>A command that fired for an utterance.</summary>
/// <param name="Confidence">Recognition confidence times phrase similarity, 0-1 (what the threshold is checked against).</param>
internal sealed record CommandHit(VoiceCommand Command, PhraseMatch Match, double Confidence);

/// <summary>Decides which commands an utterance fires. Pure; the service publishes the events.</summary>
internal static class CommandEvaluator
{
    /// <summary>
    /// Commands are checked in list order. Every enabled command whose phrase matches with enough confidence fires,
    /// except that a "Stop After" command ends the check. Interim results only fire "Stop After" commands with the
    /// Start or Anywhere location (Exact needs the pause after the phrase); a command fired early is not fired again
    /// by the final result, and if it was a "Stop After" command the final result fires nothing more.
    /// </summary>
    /// <param name="firedEarly">Ids of commands interim results of this utterance already fired.</param>
    public static IReadOnlyList<CommandHit> Evaluate(RecognitionResult result, IEnumerable<VoiceCommand> commands,
        int globalThreshold, int tolerancePercent, IReadOnlyCollection<Guid>? firedEarly = null)
    {
        var hits = new List<CommandHit>();
        if (string.IsNullOrWhiteSpace(result.Text)) return hits;
        var list = commands.ToList();
        firedEarly ??= [];
        if (result.IsFinal && list.Any(c => c.StopAfter && firedEarly.Contains(c.Id))) return hits;

        foreach (var command in list)
        {
            if (!command.Enabled || string.IsNullOrWhiteSpace(command.Phrase) || firedEarly.Contains(command.Id)) continue;
            if (!result.IsFinal && (!command.StopAfter || command.Location == CommandLocation.Exact)) continue;
            var match = PhraseMatcher.Match(command.Phrase, command.Location, result.Text, tolerancePercent / 100.0);
            if (match is null) continue;
            var confidence = Math.Clamp(result.Confidence, 0, 1) * match.Similarity;
            if (confidence * 100 + 1e-9 < ThresholdFor(command, globalThreshold)) continue;
            hits.Add(new CommandHit(command, match, confidence));
            if (command.StopAfter) break;
        }
        return hits;
    }

    public static int ThresholdFor(VoiceCommand command, int globalThreshold) =>
        Math.Clamp(command.OverrideGlobal ? command.ConfidenceThreshold : globalThreshold, 0, 100);

    /// <summary>Whether interim results are worth computing: some enabled command can fire from them.</summary>
    public static bool NeedsInterimResults(IEnumerable<VoiceCommand> commands) =>
        commands.Any(c => c.Enabled && c.StopAfter && c.Location != CommandLocation.Exact && !string.IsNullOrWhiteSpace(c.Phrase));
}
