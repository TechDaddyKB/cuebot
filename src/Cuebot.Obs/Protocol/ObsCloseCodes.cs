using System.Net.WebSockets;

namespace Cuebot.Obs.Protocol;

/// <summary>Human-readable descriptions of obs-websocket close codes.</summary>
internal static class ObsCloseCodes
{
    public const int AuthenticationFailed = 4009;
    private const int UnsupportedRpcVersion = 4010;
    private const int SessionInvalidated = 4011;

    public static string Describe(WebSocketCloseStatus? status, string? description) => (int?)status switch
    {
        null => "The connection to OBS was lost.",
        AuthenticationFailed => "OBS rejected the password (authentication failed).",
        UnsupportedRpcVersion => "This OBS version does not support obs-websocket RPC version 1.",
        SessionInvalidated => "OBS ended the session (the client was disconnected from OBS).",
        (int)WebSocketCloseStatus.NormalClosure or (int)WebSocketCloseStatus.EndpointUnavailable =>
            "OBS closed the connection.",
        var code => $"OBS closed the connection (code {code}{(string.IsNullOrEmpty(description) ? "" : $": {description}")}).",
    };
}
