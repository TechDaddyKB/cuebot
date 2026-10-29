using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Cli;

/// <summary>A conversation's throwaway Antigravity workspace; disposing deletes it.</summary>
public sealed class AntigravityWorkspace(string directory) : IDisposable
{
    public string Directory { get; } = directory;

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the OS temp cleaner gets it
        }
    }
}

/// <summary>
/// Runs Antigravity CLI limited to Cuebot. Checked against agy 1.4.10: its print mode reads files and runs commands
/// without asking, and offers about sixty built-in tools (shell, files, browser, web, images, subagents, scheduling).
/// Antigravity's own lifecycle hooks are the gate: each conversation runs in a throwaway workspace (temp folder,
/// owner-only) holding
/// <list type="bullet">
/// <item><c>AGENTS.md</c> with Cuebot's instructions (Antigravity reads it as project rules);</item>
/// <item>a <c>cuebot</c> plugin (<c>.agents/plugins/cuebot</c>) with Cuebot's MCP server (URL and bearer token, owner-only
///   file) and a <c>PreToolUse</c> hook for every tool that runs <c>Cuebot --assistant-tool-gate</c>.</item>
/// </list>
/// The gate (<see cref="Decide"/>) allows only calls to Cuebot's MCP server (granting that one MCP tool for the call,
/// since print mode can not ask) and reads of the tool descriptions Antigravity keeps for that server; every other tool
/// call is hard-denied. The streamer's Antigravity settings and sign-in are not changed.
/// </summary>
public static class AntigravityIsolation
{
    /// <summary>The startup switch that runs <see cref="RunGate"/> instead of the app.</summary>
    public const string GateSwitch = "--assistant-tool-gate";

    /// <summary>Antigravity names a plugin's MCP server "&lt;plugin&gt;_&lt;server&gt;".</summary>
    public const string ServerName = McpEndpoint.ServerName + "_" + McpEndpoint.ServerName;

    /// <summary>Where Antigravity keeps the tool descriptions of Cuebot's server (it reads them before calling a tool).</summary>
    public static string SchemaDirectory(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "mcp", ServerName);

    /// <summary>Session-only settings (<c>agy --settings</c>, never saved): Cuebot's MCP server's tools are allowed.</summary>
    public static string SessionSettings() =>
        new JsonObject { ["permissions"] = new JsonObject { ["allow"] = new JsonArray($"mcp({ServerName}/*)") } }.ToJsonString();

    public static bool IsGateRequested(IReadOnlyList<string> args) => args.Count > 0 && args[0] == GateSwitch;

    /// <summary>
    /// How to run this Cuebot's gate: the AppImage, the Cuebot program, or <c>dotnet Cuebot.dll</c> in development; null
    /// when this process is not the Cuebot app (tests pass their own).
    /// </summary>
    public static IReadOnlyList<string>? CurrentGateCommand()
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage) return [appImage, GateSwitch];
        if (Environment.ProcessPath is not { } process) return null;
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        if (entry?.GetName().Name != "Cuebot") return null;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [process, entry.Location, GateSwitch]
            : [process, GateSwitch];
    }

    /// <summary>Reads one PreToolUse request from <paramref name="input"/> and writes the decision. Never throws: anything odd is denied.</summary>
    public static int RunGate(TextReader input, TextWriter output)
    {
        string decision;
        try
        {
            decision = Decide(input.ReadToEnd());
        }
        catch (Exception)
        {
            decision = Deny();
        }
        output.Write(decision);
        output.Flush();
        return 0;
    }

    /// <summary>The hook's answer (JSON) for one PreToolUse request.</summary>
    public static string Decide(string request, string? schemaDirectory = null)
    {
        var call = (JsonNode.Parse(request) as JsonObject)?["toolCall"] as JsonObject;
        var name = call?["name"]?.GetValue<string>();
        var args = call?["args"] as JsonObject;
        if (name == "call_mcp_tool" && args?["ServerName"]?.GetValue<string>() == ServerName
            && args["ToolName"]?.GetValue<string>() is { Length: > 0 } tool && tool.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return Allow($"mcp({ServerName}/{tool})");
        if (name is "read_file" or "view_file" && (args?["Path"] ?? args?["AbsolutePath"])?.GetValue<string>() is { Length: > 0 } path
            && IsInside(path, schemaDirectory ?? SchemaDirectory()))
            return Allow($"read_file({path})");
        return Deny();
    }

    private static bool IsInside(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal)) return false;
        // no symlink inside the folder may point elsewhere
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) is null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Allow(string grant) =>
        new JsonObject { ["decision"] = "allow", ["permissionOverrides"] = new JsonArray(grant) }.ToJsonString();

    private static string Deny() =>
        new JsonObject { ["decision"] = "deny", ["reason"] = "Cuebot's assistant can only use Cuebot's tools." }.ToJsonString();

    /// <summary>Sets up one conversation's workspace (see the class summary).</summary>
    /// <param name="gate">The command that runs the gate, e.g. ["/usr/bin/cuebot", "--assistant-tool-gate"].</param>
    public static AntigravityWorkspace Prepare(AssistantContext context, McpEndpoint mcp, IReadOnlyList<string> gate, string? scratchRoot = null)
    {
        var root = Path.Combine(scratchRoot ?? Path.GetTempPath(), "cuebot-agy-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(root);
        var workspace = new AntigravityWorkspace(root);
        try
        {
            WritePrivate(Path.Combine(root, "AGENTS.md"), context.Instructions + "\n");
            var plugin = Path.Combine(root, ".agents", "plugins", McpEndpoint.ServerName);
            CreatePrivateDirectory(Path.Combine(root, ".agents"));
            CreatePrivateDirectory(Path.Combine(root, ".agents", "plugins"));
            CreatePrivateDirectory(plugin);
            WritePrivate(Path.Combine(plugin, "plugin.json"), new JsonObject { ["name"] = McpEndpoint.ServerName }.ToJsonString());
            WritePrivate(Path.Combine(plugin, "mcp_config.json"), new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [McpEndpoint.ServerName] = new JsonObject
                    {
                        ["serverUrl"] = mcp.Url,
                        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + mcp.Token },
                    },
                },
            }.ToJsonString());
            WritePrivate(Path.Combine(plugin, "hooks.json"), new JsonObject
            {
                ["cuebot-only"] = new JsonObject
                {
                    ["PreToolUse"] = new JsonArray(new JsonObject
                    {
                        ["matcher"] = "*",
                        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = ShellCommand(gate), ["timeout"] = 20 }),
                    }),
                },
            }.ToJsonString());
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>A command line for sh, every word single-quoted.</summary>
    internal static string ShellCommand(IEnumerable<string> words) => string.Join(" ", words.Select(w => "'" + w.Replace("'", "'\\''") + "'"));

    private static void CreatePrivateDirectory(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivate(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }
}
