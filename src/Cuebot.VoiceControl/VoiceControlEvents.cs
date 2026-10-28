using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Matching;

namespace Cuebot.VoiceControl;

/// <summary>
/// Event types published on the bus. The "speechToText" prefix makes the Streamer.bot WebSocket mapping produce
/// Streamer.bot's own names (SpeechToText.Command / SpeechToText.Dictation) without a special case.
/// </summary>
public static class VoiceControlEvents
{
    public const string Command = "speechToText.command";
    public const string Dictation = "speechToText.dictation";
}

/// <summary>Builds the documented trigger variables (plus a few Cuebot extras) for a recognised utterance.</summary>
internal static class VoiceControlArgs
{
    public static Dictionary<string, object?> ForCommand(RecognitionResult result, CommandHit hit, string engine)
    {
        var args = Common(result, engine);
        args["spokenCommand"] = hit.Command.Phrase;
        args["spokenTextInput"] = hit.Match.Remainder;
        args["voiceCommandId"] = hit.Command.Id.ToString();
        args["voiceCommandName"] = hit.Command.Name;
        args["voiceCommandLocation"] = hit.Command.Location.ToString();
        args["voiceCommandMatched"] = hit.Match.Matched;
        args["voiceCommandSimilarity"] = hit.Match.Similarity;
        args["voiceCommandConfidencePercent"] = Percent(hit.Confidence);
        return args;
    }

    /// <summary>Dictation carries the first command the utterance fired, if any (empty otherwise).</summary>
    public static Dictionary<string, object?> ForDictation(RecognitionResult result, IReadOnlyList<CommandHit> hits, string engine)
    {
        var args = Common(result, engine);
        var first = hits.Count > 0 ? hits[0] : null;
        args["spokenCommand"] = first?.Command.Phrase ?? "";
        args["spokenTextInput"] = first?.Match.Remainder ?? "";
        args["voiceCommandId"] = first?.Command.Id.ToString() ?? "";
        args["voiceCommandName"] = first?.Command.Name ?? "";
        args["voiceCommandCount"] = hits.Count;
        return args;
    }

    private static Dictionary<string, object?> Common(RecognitionResult result, string engine)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["spokenText"] = result.Text,
            ["spokenTextConfidence"] = Math.Round(result.Confidence, 4),
            ["spokenTextConfidencePercent"] = Percent(result.Confidence),
            ["spokenTextFinal"] = result.IsFinal,
            ["spokenLanguage"] = result.Language ?? "",
            ["voiceEngine"] = engine,
        };
        var alternates = result.Alternates.Count > 0 ? result.Alternates : [new RecognitionAlternate(result.Text, result.Confidence)];
        for (var i = 0; i < alternates.Count; i++)
        {
            args[$"altPhraseText{i}"] = alternates[i].Text;
            args[$"altPhraseConfidence{i}"] = Math.Round(alternates[i].Confidence, 4);
            args[$"altPhraseConfidencePercent{i}"] = Percent(alternates[i].Confidence);
        }
        args["altPhraseCount"] = alternates.Count;
        return args;
    }

    public static int Percent(double confidence) => (int)Math.Round(Math.Clamp(confidence, 0, 1) * 100);
}
