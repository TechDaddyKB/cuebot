namespace Cuebot.Server.Api;

/// <summary>
/// The local REST API (<c>/api/v1</c>) in servers.json ("api"). Off by default; binds 127.0.0.1 unless LAN access is
/// allowed. Every request needs <see cref="Token"/> as a bearer token. The MCP endpoint (<c>/mcp</c>) is served by the
/// same listener with the same token (see <see cref="Mcp.McpServerSettings"/>).
/// </summary>
public sealed class ApiServerSettings
{
    public const int DefaultPort = 7475;

    public bool Enabled { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = DefaultPort;

    /// <summary>Bearer token for the REST API and MCP. Generated when the server first starts; never logged.</summary>
    public string Token { get; set; } = "";
}
