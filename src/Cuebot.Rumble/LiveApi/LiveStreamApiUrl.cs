using System.Text.RegularExpressions;

namespace Cuebot.Rumble.LiveApi;

/// <summary>Validation and log-safe display of the user's private Live Stream API URL.</summary>
internal static partial class LiveStreamApiUrl
{
    /// <summary>Parses the configured URL; throws <see cref="InvalidOperationException"/> with a user-facing reason.</summary>
    public static Uri Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("Enter your Rumble Live Stream API URL first (rumble.com → Account → Live Stream API).");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("The Rumble Live Stream API URL must be a full https:// address.");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new InvalidOperationException("The Rumble Live Stream API URL must use https:// (http is only allowed for local relays).");
        return uri;
    }

    /// <summary>The URL with the value of every <c>key</c> (and similar) query parameter replaced by "***".</summary>
    public static string Redact(string url) => SecretParameter().Replace(url, m => m.Groups[1].Value + "***");

    [GeneratedRegex(@"([?&](?:key|api_key|apikey|token)=)[^&#]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParameter();
}
