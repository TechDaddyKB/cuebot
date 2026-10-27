using System.Globalization;
using System.Text;

namespace Cuebot.Kick;

/// <summary>Splits outgoing chat into Kick-sized messages (500 grapheme clusters, at most 2048 UTF-8 bytes).</summary>
internal static class ChatText
{
    public const int MaxGraphemes = 500;
    public const int MaxBytes = 2048;

    public static IReadOnlyList<string> Split(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        var parts = new List<string>();
        var current = new StringBuilder();
        var count = 0;
        var bytes = 0;
        var lastSpace = -1;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (count + 1 > MaxGraphemes || bytes + size > MaxBytes)
            {
                if (lastSpace > 0)
                {
                    var head = current.ToString(0, lastSpace).TrimEnd();
                    var tail = current.ToString(lastSpace, current.Length - lastSpace).TrimStart();
                    parts.Add(head);
                    current.Clear().Append(tail);
                }
                else
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                count = new StringInfo(current.ToString()).LengthInTextElements;
                bytes = Encoding.UTF8.GetByteCount(current.ToString());
                lastSpace = -1;
            }
            if (char.IsWhiteSpace(element[0])) lastSpace = current.Length;
            current.Append(element);
            count++;
            bytes += size;
        }
        if (current.ToString().Trim() is { Length: > 0 } rest) parts.Add(rest);
        return parts.Where(p => p.Length > 0).ToArray();
    }
}
