using System.Globalization;
using System.Text;
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

    public static bool ContainsLink(string text) => LinkPattern().IsMatch(text);

    /// <summary>Applies a URL mode; returns null when the message must be skipped.</summary>
    public static string? ApplyUrlMode(string text, UrlMode mode, string word = LinkWord) => mode switch
    {
        UrlMode.Replace => LinkPattern().Replace(text, word),
        UrlMode.Strip => LinkPattern().Replace(text, ""),
        UrlMode.Skip => ContainsLink(text) ? null : text,
        _ => text,
    };

    /// <summary>Applies a bad-word action (whole words or phrases, case-insensitive); null means skip the message.</summary>
    public static string? ApplyBadWords(string text, IEnumerable<string> badWords, BadWordAction action, string replacement = "beep")
    {
        if (action == BadWordAction.Disabled) return text;
        foreach (var word in badWords)
        {
            if (string.IsNullOrWhiteSpace(word)) continue;
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{N}}])";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            try
            {
                if (!regex.IsMatch(text)) continue;
                if (action == BadWordAction.Skip) return null;
                text = regex.Replace(text, action == BadWordAction.Replace ? replacement : "");
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological input: leave the text as it is for this word.
            }
        }
        return text;
    }

    /// <summary>Shortens runs of the same non-digit character to <paramref name="max"/> ("noooo" → "nooo" for 3).</summary>
    public static string CompressRepeatedCharacters(string text, int max)
    {
        if (max <= 0 || text.Length <= max) return text;
        var builder = new StringBuilder(text.Length);
        var run = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            run = i > 0 && text[i - 1] == c ? run + 1 : 1;
            if (run <= max || char.IsDigit(c) || char.IsWhiteSpace(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Shortens runs of the same word (case-insensitive) to <paramref name="max"/>; also collapses whitespace.</summary>
    public static string CompressRepeatedWords(string text, int max)
    {
        if (max <= 0) return text;
        var words = Words(text);
        var kept = new List<string>(words.Length);
        var run = 0;
        for (var i = 0; i < words.Length; i++)
        {
            run = i > 0 && string.Equals(words[i - 1], words[i], StringComparison.OrdinalIgnoreCase) ? run + 1 : 1;
            if (run <= max) kept.Add(words[i]);
        }
        return string.Join(' ', kept);
    }

    /// <summary>Keeps the first <paramref name="max"/> words; 0 or less is unlimited.</summary>
    public static string LimitWords(string text, int max)
    {
        if (max <= 0) return text;
        var words = Words(text);
        return words.Length <= max ? text : string.Join(' ', words.Take(max));
    }

    /// <summary>Removes Unicode emoji (pictographs, symbols, joiners, skin tones, variation selectors).</summary>
    public static string StripEmoji(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsEmoji(rune)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static bool IsEmoji(Rune rune)
    {
        var v = rune.Value;
        if (v is 0x200D or 0xFE0F or 0x20E3) return true;              // ZWJ, variation selector, keycap
        if (v is >= 0x1F000 and <= 0x1FAFF) return true;               // pictographs, emoticons, flags, skin tones
        if (v is >= 0x2600 and <= 0x27BF) return true;                 // misc symbols and dingbats
        if (v is >= 0xE0020 and <= 0xE007F) return true;               // tag characters (subdivision flags)
        return v is >= 0x2B00 and <= 0x2BFF && Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol;
    }

    /// <summary>
    /// Removes Twitch-style emote ranges ("id:start-end,start-end/id2:start-end", code point positions, inclusive).
    /// With <paramref name="keepFirst"/>, the first occurrence of each emote stays. <paramref name="allowed"/> names stay.
    /// </summary>
    public static string StripEmoteRanges(string text, string ranges, bool keepFirst = false, IEnumerable<string>? allowed = null)
    {
        if (string.IsNullOrWhiteSpace(ranges)) return text;
        var runes = text.EnumerateRunes().Select(r => r.ToString()).ToArray();
        var allowedSet = new HashSet<string>(allowed ?? [], StringComparer.Ordinal);
        var parsed = new List<(int Start, int End)>();
        foreach (var emote in ranges.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = emote.LastIndexOf(':');
            if (colon < 0) continue;
            foreach (var range in emote[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = range.IndexOf('-');
                if (dash < 0 || !int.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                    || start > end || end >= runes.Length) continue;
                parsed.Add((start, end));
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (start, end) in parsed.OrderBy(p => p.Start))
        {
            var name = string.Concat(runes[start..(end + 1)]);
            if (allowedSet.Contains(name)) continue;
            if (keepFirst && seen.Add(name)) continue;
            for (var i = start; i <= end; i++) runes[i] = "";
        }
        return string.Concat(runes);
    }

    /// <summary>Removes whole words that are emote names (case-sensitive, as emotes are).</summary>
    public static string StripEmoteNames(string text, IReadOnlySet<string> names, bool keepFirst = false, IEnumerable<string>? allowed = null)
    {
        if (names.Count == 0) return text;
        var allowedSet = new HashSet<string>(allowed ?? [], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = Words(text).Where(word =>
        {
            if (!names.Contains(word) || allowedSet.Contains(word)) return true;
            return keepFirst && seen.Add(word);
        });
        return string.Join(' ', kept);
    }

    /// <summary>Turns runs of whitespace (including line breaks) into single spaces and trims the ends.</summary>
    public static string CollapseWhitespace(string text) => WhitespacePattern().Replace(text, " ").Trim();

    /// <summary>Cuts the text to at most <paramref name="maxLength"/> characters; 0 or less means unlimited.</summary>
    public static string Truncate(string text, int maxLength) =>
        maxLength > 0 && text.Length > maxLength ? text[..maxLength].TrimEnd() : text;

    public static int CountWords(string text) => Words(text).Length;

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

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
