namespace Cuebot.Kick;

/// <summary>Kick rejected a request. <see cref="Status"/> is the HTTP status (0 when there was no response).</summary>
public sealed class KickApiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}
