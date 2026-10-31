using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Server.Mcp;

/// <summary>Ready-to-paste MCP client configuration for the Servers page.</summary>
public static class McpClientConfig
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Pasted into config files, not HTML: keep "<TOKEN>", "+" and non-ASCII paths readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The MCP endpoint URL for a port (host 127.0.0.1 unless another one is given).</summary>
    public static string Url(int port, string host = "127.0.0.1") => $"http://{host}:{port}{Api.ApiServer.McpPath}";

    /// <summary>Claude Code: <c>claude mcp add --transport http cuebot URL --header "Authorization: Bearer TOKEN"</c>.</summary>
    public static string ClaudeCodeCommand(string url, string token) =>
        $"claude mcp add --transport http cuebot {url} --header \"Authorization: Bearer {token}\"";

    /// <summary>A remote (Streamable HTTP) server entry, as used by .mcp.json files and most HTTP-capable clients.</summary>
    public static string HttpJson(string url, string token) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["cuebot"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = url,
                ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" },
            },
        },
    }.ToJsonString(Indented);

    /// <summary>
    /// A stdio server entry (Claude Desktop's claude_desktop_config.json and other clients that start a process): runs
    /// <c>Cuebot --mcp-stdio</c>, which connects to the running Cuebot.
    /// </summary>
    public static string StdioJson(string command, IEnumerable<string> args) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["cuebot"] = new JsonObject
            {
                ["command"] = command,
                ["args"] = new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
            },
        },
    }.ToJsonString(Indented);

    /// <summary>
    /// How to start this Cuebot with --mcp-stdio: the app host itself, or <c>dotnet Cuebot.dll</c> in development, plus
    /// <c>--data</c> when a non-default data folder is in use.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Args) StdioLaunch(string? processPath, string? entryAssembly, string? dataFolder)
    {
        var args = new List<string>();
        var command = processPath ?? "cuebot";
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage)) command = appImage;
        else if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssembly))
            args.Add(entryAssembly);
        args.Add(McpStdioBridge.Switch);
        if (!string.IsNullOrEmpty(dataFolder))
        {
            args.Add("--data");
            args.Add(dataFolder);
        }
        return (command, args);
    }
}
