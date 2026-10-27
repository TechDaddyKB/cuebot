namespace Cuebot.Integrations;

/// <summary>Keeps tokens and API keys out of logs, status text and the UI.</summary>
public static class Secrets
{
    /// <summary>"••••••••3f9a" for a set secret, "" for none. Shows at most the last four characters of long secrets.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var tail = secret.Length >= 16 ? secret[^4..] : "";
        return new string('•', 8) + tail;
    }

    /// <summary>Replaces every occurrence of each secret in <paramref name="text"/> (an exception message, a URL).</summary>
    public static string Redact(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 4) continue;
            text = text.Replace(secret, "***", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal);
        }
        return text;
    }
}
