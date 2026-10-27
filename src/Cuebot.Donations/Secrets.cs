namespace Cuebot.Donations;

/// <summary>Keeps tokens out of logs, status text and the UI.</summary>
public static class Secrets
{
    /// <summary>"••••••••3f9a" for a set token, "" for none. Shows at most the last four characters of long tokens.</summary>
    public static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        var tail = token.Length >= 16 ? token[^4..] : "";
        return new string('•', 8) + tail;
    }

    /// <summary>Replaces every occurrence of the token in <paramref name="text"/> (an exception message, a URL).</summary>
    public static string Redact(string? text, string? token)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (string.IsNullOrEmpty(token) || token.Length < 4) return text;
        return text.Replace(token, "***", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);
    }
}
