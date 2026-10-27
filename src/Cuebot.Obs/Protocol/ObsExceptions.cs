namespace Cuebot.Obs.Protocol;

/// <summary>The connection to OBS could not be established or was lost.</summary>
public class ObsConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>OBS rejected the password (or a password is required but none was configured).</summary>
public sealed class ObsAuthenticationException(string message) : ObsConnectionException(message);

/// <summary>OBS answered a request with a failure status.</summary>
public sealed class ObsRequestException(string requestType, int code, string? comment)
    : Exception($"OBS request '{requestType}' failed (code {code}){(string.IsNullOrEmpty(comment) ? "" : $": {comment}")}")
{
    public string RequestType { get; } = requestType;

    /// <summary>obs-websocket <c>RequestStatus</c> code, e.g. 600 for "resource not found".</summary>
    public int Code { get; } = code;

    public string? Comment { get; } = comment;
}
