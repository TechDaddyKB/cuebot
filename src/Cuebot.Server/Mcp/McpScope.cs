namespace Cuebot.Server.Mcp;

/// <summary>
/// The tools one Assistant conversation may use instead of Cuebot's own (see <see cref="McpServer.RegisterAssistant"/>):
/// a mod's conversation from the mod portal gets tools that act as that mod, checked like the portal's own pages.
/// </summary>
public sealed class McpScope
{
    /// <summary>The guides a scoped conversation may read.</summary>
    public static readonly IReadOnlySet<string> Guides = new HashSet<string>(StringComparer.Ordinal) { McpCatalog.ActionsDocUri, McpCatalog.VariablesUri };

    private readonly Dictionary<string, McpTool> _byName;

    public McpScope(IReadOnlyList<McpTool> tools, string instructions)
    {
        Tools = tools;
        Instructions = instructions;
        _byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<McpTool> Tools { get; }

    /// <summary>What an MCP client is told at initialize.</summary>
    public string Instructions { get; }

    public McpTool? Find(string name) => _byName.GetValueOrDefault(name);
}
