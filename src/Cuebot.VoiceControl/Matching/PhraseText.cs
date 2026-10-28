using System.Globalization;
using System.Text;

namespace Cuebot.VoiceControl.Matching;

/// <summary>One word of a recognised phrase: its normalised form for matching and where it sits in the original text.</summary>
internal readonly record struct PhraseToken(string Normalized, int Start, int End);

/// <summary>
/// Text normalisation for matching: case, accents, punctuation and apostrophes are ignored, and English number words
/// up to twenty match digits ("scene two" = "Scene 2."), because recognisers disagree on all of these.
/// </summary>
internal static class PhraseText
{
    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    /// <summary>The words of <paramref name="text"/> with their positions; words that are only punctuation are dropped.</summary>
    public static IReadOnlyList<PhraseToken> Tokenize(string? text)
    {
        var tokens = new List<PhraseToken>();
        if (string.IsNullOrEmpty(text)) return tokens;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSeparator(text[i])) i++;
            var start = i;
            while (i < text.Length && !IsSeparator(text[i])) i++;
            if (i == start) continue;
            var normalized = NormalizeWord(text.AsSpan(start, i - start));
            if (normalized.Length > 0) tokens.Add(new PhraseToken(normalized, start, i));
        }
        return tokens;
    }

    /// <summary>The normalised words joined by single spaces (what two phrases are compared on).</summary>
    public static string Normalize(string? text) => string.Join(' ', Tokenize(text).Select(t => t.Normalized));

    // Hyphens and slashes split words ("count-down" = "count down"); everything else non-alphanumeric is stripped.
    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is '-' or '–' or '—' or '/' or '_' or '…';

    private static string NormalizeWord(ReadOnlySpan<char> word)
    {
        var decomposed = word.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        var result = sb.ToString();
        var number = Array.IndexOf(NumberWords, result);
        return number >= 0 ? number.ToString(CultureInfo.InvariantCulture) : result;
    }
}
