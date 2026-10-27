namespace Cuebot.Rumble.Chat;

/// <summary>
/// Rumble shows video and live stream ids in base 36 (lowercase digits and letters, as in "rumble.com/v5abc12");
/// the chat service addresses the same stream by that number in base 10.
/// </summary>
internal static class ChatId
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Converts a base-36 stream id to the base-10 chat id; null when the id is not valid base 36.</summary>
    public static long? FromStreamId(string streamId)
    {
        var text = streamId.Trim().ToLowerInvariant();
        if (text.Length is 0 or > 12) return null;   // 36^12 still fits in a long
        long value = 0;
        foreach (var c in text)
        {
            var digit = Digits.IndexOf(c, StringComparison.Ordinal);
            if (digit < 0) return null;
            value = value * 36 + digit;
        }
        return value;
    }
}
