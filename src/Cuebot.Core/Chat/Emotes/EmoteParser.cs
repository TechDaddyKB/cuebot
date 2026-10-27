using System.Globalization;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Chat.Emotes;

/// <summary>Finds emotes in message text: platform-reported ranges, inline tokens and third-party words.</summary>
public static partial class EmoteParser
{
    /// <summary>Twitch's emote CDN; "default" serves the animated GIF for animated emotes and a PNG otherwise.</summary>
    public const string TwitchImageTemplate = "https://static-cdn.jtvnw.net/emoticons/v2/{0}/default/dark/1.0";

    public const string KickImageTemplate = "https://files.kick.com/emotes/{0}/fullsize";

    public static string TwitchImageUrl(string id) => string.Format(CultureInfo.InvariantCulture, TwitchImageTemplate, id);

    public static string KickImageUrl(string id) => string.Format(CultureInfo.InvariantCulture, KickImageTemplate, id);

    /// <summary>
    /// Parses Twitch-format ranges ("id:0-4,6-10/id2:12-16"). Positions are inclusive and count Unicode code points
    /// (so an emoji before an emote counts once even though it is two UTF-16 chars); results use UTF-16 indices.
    /// Ranges outside the text or that do not fall on code point boundaries are skipped.
    /// </summary>
    public static IReadOnlyList<EmoteMatch> ParseTwitchRanges(string text, string? ranges)
    {
        if (string.IsNullOrWhiteSpace(ranges) || text.Length == 0) return [];
        var offsets = CodePointOffsets(text);
        var matches = new List<EmoteMatch>();
        foreach (var part in ranges.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var id = part[..colon];
            foreach (var range in part[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dash = range.IndexOf('-');
                if (dash <= 0
                    || !int.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                    || !int.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                    || last < first || last >= offsets.Length - 1)
                    continue;
                var start = offsets[first];
                var end = offsets[last + 1];
                var code = text[start..end];
                matches.Add(new EmoteMatch(start, end - start, code,
                    new Emote(code, id, EmoteProviders.Twitch, TwitchImageUrl(id)) { Platform = EmoteProviders.Twitch, Scope = "message" }));
            }
        }
        return Normalize(matches);
    }

    /// <summary>UTF-16 index of each code point, plus the text length as a final entry.</summary>
    internal static int[] CodePointOffsets(string text)
    {
        var offsets = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            offsets.Add(i);
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }
        offsets.Add(text.Length);
        return offsets.ToArray();
    }

    [GeneratedRegex(@"\[emote:(?<id>\d+):(?<name>[^\]\s]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex KickToken();

    /// <summary>Kick sends emotes inline as "[emote:37226:KEKW]".</summary>
    public static IReadOnlyList<EmoteMatch> ParseKickTokens(string text)
    {
        if (!text.Contains("[emote:", StringComparison.Ordinal)) return [];
        return KickToken().Matches(text).Select(m =>
        {
            var id = m.Groups["id"].Value;
            return new EmoteMatch(m.Index, m.Length, m.Value,
                new Emote(m.Groups["name"].Value, id, EmoteProviders.Kick, KickImageUrl(id)) { Platform = EmoteProviders.Kick, Scope = "message" });
        }).ToArray();
    }

    /// <summary>Emotes a module reported in structured form ("emoteList" argument), checked against the text.</summary>
    public static IReadOnlyList<EmoteMatch> FromPlatformList(string platform, string text, IEnumerable<PlatformEmote>? emotes)
    {
        if (emotes is null) return [];
        var matches = new List<EmoteMatch>();
        foreach (var e in emotes)
        {
            if (e.Start < 0 || e.Length <= 0 || e.Start + e.Length > text.Length) continue;
            matches.Add(new EmoteMatch(e.Start, e.Length, text.Substring(e.Start, e.Length),
                new Emote(e.Code, e.Id, platform, e.ImageUrl) { Platform = platform, Scope = "message", IsAnimated = e.IsAnimated }));
        }
        return Normalize(matches);
    }

    /// <summary>
    /// Third-party emotes: whitespace-separated words that exactly (case-sensitively) equal a known emote code,
    /// outside any range already claimed by <paramref name="taken"/>.
    /// </summary>
    public static IReadOnlyList<EmoteMatch> FindWords(string text, IReadOnlyDictionary<string, Emote> emotes, IReadOnlyList<EmoteMatch>? taken = null)
    {
        if (emotes.Count == 0 || text.Length == 0) return [];
        var matches = new List<EmoteMatch>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i == start) break;
            var word = text[start..i];
            if (emotes.TryGetValue(word, out var emote) && !Overlaps(taken, start, i))
                matches.Add(new EmoteMatch(start, i - start, word, emote));
        }
        return matches;
    }

    /// <summary>Merges match lists: sorted by position, overlapping later entries dropped.</summary>
    public static IReadOnlyList<EmoteMatch> Merge(params IReadOnlyList<EmoteMatch>[] lists) =>
        Normalize(lists.SelectMany(l => l));

    private static IReadOnlyList<EmoteMatch> Normalize(IEnumerable<EmoteMatch> matches)
    {
        var result = new List<EmoteMatch>();
        foreach (var m in matches.OrderBy(m => m.Start).ThenByDescending(m => m.Length))
            if (result.Count == 0 || m.Start >= result[^1].End) result.Add(m);
        return result;
    }

    private static bool Overlaps(IReadOnlyList<EmoteMatch>? taken, int start, int end) =>
        taken is not null && taken.Any(t => start < t.End && t.Start < end);

    /// <summary>
    /// Splits text into plain and emote pieces in order, for renderers. Text pieces carry a null emote.
    /// </summary>
    public static IReadOnlyList<(string Text, EmoteMatch? Emote)> Split(string text, IReadOnlyList<EmoteMatch> matches)
    {
        var parts = new List<(string, EmoteMatch?)>();
        var pos = 0;
        foreach (var m in matches.OrderBy(m => m.Start))
        {
            if (m.Start < pos || m.End > text.Length) continue;
            if (m.Start > pos) parts.Add((text[pos..m.Start], null));
            parts.Add((text.Substring(m.Start, m.Length), m));
            pos = m.End;
        }
        if (pos < text.Length) parts.Add((text[pos..], null));
        return parts;
    }
}
