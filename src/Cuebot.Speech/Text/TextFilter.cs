using System.Text.RegularExpressions;

namespace Cuebot.Speech;

/// <summary>Pure text clean-up applied before speaking.</summary>
public static partial class TextFilter
{
    /// <summary>Word used in place of removed links.</summary>
    public const string LinkWord = "link";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    [GeneratedRegex(@"\b(?:https?://|www\.)\S+|\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|net|org|tv|gg|io|co|me|ly|be|xyz|dev|app|info)\b(?:/\S*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    /// <summary>
    /// Applies replacements in order. Literal ones match case-insensitively; regular expressions that are invalid
    /// or exceed the time limit are skipped.
    /// </summary>
    public static string ApplyReplacements(string text, IEnumerable<WordReplacement> replacements)
    {
        foreach (var replacement in replacements)
        {
            if (string.IsNullOrEmpty(replacement.Find)) continue;
            text = replacement.IsRegex
                ? ReplaceRegex(text, replacement)
                : text.Replace(replacement.Find, replacement.Replace, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>Replaces every URL or bare domain with <see cref="LinkWord"/>.</summary>
    public static string ReplaceLinks(string text) => LinkPattern().Replace(text, LinkWord);

    /// <summary>Turns runs of whitespace (including line breaks) into single spaces and trims the ends.</summary>
    public static string CollapseWhitespace(string text) => WhitespacePattern().Replace(text, " ").Trim();

    /// <summary>Cuts the text to at most <paramref name="maxLength"/> characters; 0 or less means unlimited.</summary>
    public static string Truncate(string text, int maxLength) =>
        maxLength > 0 && text.Length > maxLength ? text[..maxLength].TrimEnd() : text;

    private static string ReplaceRegex(string text, WordReplacement replacement)
    {
        try
        {
            return Regex.Replace(text, replacement.Find, replacement.Replace,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return text; // invalid pattern
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }
}
