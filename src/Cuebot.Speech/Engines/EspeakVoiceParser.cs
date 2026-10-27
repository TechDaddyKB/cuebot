namespace Cuebot.Speech;

/// <summary>Parses the table printed by <c>espeak-ng --voices</c>.</summary>
internal static class EspeakVoiceParser
{
    // Columns: Pty Language Age/Gender VoiceName File [Other Languages]
    private const int LanguageColumn = 1;
    private const int NameColumn = 3;
    private const int MinimumColumns = 5;

    /// <summary>One voice per language code (the value accepted by <c>-v</c>), in listing order.</summary>
    public static IReadOnlyList<VoiceInfo> Parse(string output)
    {
        var voices = new List<VoiceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < MinimumColumns || !int.TryParse(columns[0], out _)) continue; // header or blank
            var language = columns[LanguageColumn];
            if (!seen.Add(language)) continue;
            voices.Add(new VoiceInfo(language, columns[NameColumn].Replace('_', ' '), language));
        }
        return voices;
    }
}
