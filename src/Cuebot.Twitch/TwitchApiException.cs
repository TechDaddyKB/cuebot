namespace Cuebot.Twitch;

/// <summary>A Twitch API or OAuth request failed; the message is safe to show to the user.</summary>
public sealed class TwitchApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
