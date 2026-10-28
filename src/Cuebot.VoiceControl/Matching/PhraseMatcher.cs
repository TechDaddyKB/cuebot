namespace Cuebot.VoiceControl.Matching;

/// <summary>Where and how well a command phrase was found in a recognised utterance.</summary>
/// <param name="Similarity">1 = the words are identical (after normalisation); lower values are fuzzy matches.</param>
/// <param name="Matched">The part of the utterance that matched, as recognised.</param>
/// <param name="Remainder">What was said after the phrase (Streamer.bot's <c>spokenTextInput</c>).</param>
internal sealed record PhraseMatch(double Similarity, string Matched, string Remainder);

/// <summary>
/// Finds command phrases in recognised text. Matching is on normalised words (see <see cref="PhraseText"/>) and
/// tolerates small differences: a window of the utterance about as long as the phrase matches when its edit
/// distance is within <c>tolerance</c> of the phrase length, compared both with and without spaces so
/// "count down" matches "countdown".
/// </summary>
internal static class PhraseMatcher
{
    /// <param name="tolerance">0-0.5: share of characters that may differ (0 = the words must be identical).</param>
    public static PhraseMatch? Match(string phrase, CommandLocation location, string spoken, double tolerance)
    {
        var wanted = PhraseText.Tokenize(phrase);
        var heard = PhraseText.Tokenize(spoken);
        if (wanted.Count == 0 || heard.Count == 0) return null;
        tolerance = Math.Clamp(tolerance, 0, 0.5);
        var target = string.Join(' ', wanted.Select(t => t.Normalized));
        var n = wanted.Count;

        (double Similarity, int Start, int Length)? best = null;
        void Consider(int start, int length)
        {
            if (length <= 0 || start < 0 || start + length > heard.Count) return;
            var candidate = string.Join(' ', heard.Skip(start).Take(length).Select(t => t.Normalized));
            if (Score(target, candidate, tolerance) is not { } similarity) return;
            if (best is null || similarity > best.Value.Similarity + 1e-9
                || (Math.Abs(similarity - best.Value.Similarity) <= 1e-9 && length == n && best.Value.Length != n))
                best = (similarity, start, length);
        }

        switch (location)
        {
            case CommandLocation.Exact:
                Consider(0, heard.Count);
                break;
            case CommandLocation.Start:
                for (var length = n - 1; length <= n + 1; length++) Consider(0, length);
                break;
            default:
                for (var start = 0; start < heard.Count; start++)
                    for (var length = n - 1; length <= n + 1; length++) Consider(start, length);
                break;
        }

        if (best is not { } b) return null;
        var first = heard[b.Start];
        var last = heard[b.Start + b.Length - 1];
        var matched = spoken[first.Start..last.End];
        var remainder = location == CommandLocation.Exact ? "" : TrimRemainder(spoken[last.End..]);
        return new PhraseMatch(Math.Round(b.Similarity, 4), matched, remainder);
    }

    /// <summary>
    /// Similarity (1 - edit distance / longer length) when <paramref name="candidate"/> is close enough to
    /// <paramref name="target"/>, else null. Close enough: at most ⌊tolerance × target length⌋ edits, compared with
    /// spaces and without them (the better wins). Counting whole edits keeps short phrases strict: "lights on" never
    /// matches "lights off".
    /// </summary>
    internal static double? Score(string target, string candidate, double tolerance)
    {
        if (target == candidate) return 1;
        double? best = null;
        foreach (var (x, y) in new[] { (target, candidate), (target.Replace(" ", ""), candidate.Replace(" ", "")) })
        {
            if (x == y) return 1;
            var distance = Levenshtein(x, y);
            if (distance > (int)Math.Floor(tolerance * x.Length + 1e-9)) continue;
            var ratio = 1 - (double)distance / Math.Max(x.Length, y.Length);
            if (best is null || ratio > best) best = ratio;
        }
        return best;
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static string TrimRemainder(string text) =>
        text.TrimStart(' ', '\t', ',', '.', ';', ':', '!', '?', '-', '–', '—', '…').TrimEnd();
}
