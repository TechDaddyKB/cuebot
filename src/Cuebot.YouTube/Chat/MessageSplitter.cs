using System.Globalization;
using System.Text;

namespace Cuebot.YouTube.Chat;

/// <summary>Splits text into chunks YouTube accepts (200 characters per chat message), preferring word boundaries.</summary>
internal static class MessageSplitter
{
    public const int MaxLength = 200;

    public static IReadOnlyList<string> Split(string text, int max = MaxLength)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        if (Length(text) <= max) return [text];

        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Length(candidate) <= max)
            {
                current.Clear().Append(candidate);
                continue;
            }
            if (current.Length > 0) parts.Add(current.ToString());
            current.Clear();
            var rest = word;
            while (Length(rest) > max)
            {
                var cut = Cut(rest, max);
                parts.Add(rest[..cut]);
                rest = rest[cut..];
            }
            current.Append(rest);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Length in text elements, so emoji and combined characters are not cut in half.</summary>
    private static int Length(string s) => new StringInfo(s).LengthInTextElements;

    private static int Cut(string s, int maxElements)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        var count = 0;
        while (e.MoveNext())
        {
            if (count == maxElements) return e.ElementIndex;
            count++;
        }
        return s.Length;
    }
}
