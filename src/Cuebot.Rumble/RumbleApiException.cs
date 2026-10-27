namespace Cuebot.Rumble;

/// <summary>A Rumble request failed; the message is safe to show to the user (it never contains keys or cookies).</summary>
public sealed class RumbleApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
