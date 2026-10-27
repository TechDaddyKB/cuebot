namespace Cuebot.Server.Mcp;

/// <summary>The MCP server in servers.json ("mcp"). Served at <c>/mcp</c> by the API listener, with the API token.</summary>
public sealed class McpServerSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Destructive tools (delete_*, apply_import, send_chat_message) first answer with a confirmation token that the
    /// client must pass back in a second call, so the AI has to ask the user before anything is removed or sent.
    /// </summary>
    public bool RequireConfirmation { get; set; } = true;
}
