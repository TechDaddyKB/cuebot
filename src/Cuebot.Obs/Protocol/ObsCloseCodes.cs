using System.Net.WebSockets;

namespace Cuebot.Obs.Protocol;

/// <summary>Human-readable descriptions of obs-websocket close codes.</summary>
internal static class ObsCloseCodes
{
    public const int AuthenticationFailed = 4009;
    public const int UnsupportedRpcVersion = 4010;
    private const int SessionInvalidated = 4011;

    public static string Describe(WebSocketCloseStatus? status, string? description) => (int?)status switch
    {
        null => "The connection to OBS was lost.",
        AuthenticationFailed => "OBS rejected the password (authentication failed).",
        UnsupportedRpcVersion => "OBS and Cuebot do not speak the same obs-websocket version (RPC 1). Update OBS (28 or newer) or Cuebot.",
        SessionInvalidated => "OBS ended the session (someone disconnected Cuebot under Tools > WebSocket Server Settings > Connected clients).",
        (int)WebSocketCloseStatus.NormalClosure or (int)WebSocketCloseStatus.EndpointUnavailable =>
            "OBS closed the connection.",
        var code => $"OBS closed the connection (code {code}{(string.IsNullOrEmpty(description) ? "" : $": {description}")}).",
    };
}
