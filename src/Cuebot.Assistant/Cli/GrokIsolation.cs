using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Cli;

/// <summary>What an ACP app needs for one conversation beyond its command line; disposing cleans up.</summary>
public sealed class AcpLaunch(IReadOnlyDictionary<string, string> environment, JsonObject? sessionMeta, bool authenticate,
    bool instructionsInSession, string? scratchDirectory = null) : IDisposable
{
    public IReadOnlyDictionary<string, string> Environment { get; } = environment;

    /// <summary><c>_meta</c> for <c>session/new</c>.</summary>
    public JsonObject? SessionMeta { get; } = sessionMeta;

    /// <summary>Call ACP <c>authenticate</c> before <c>session/new</c>.</summary>
    public bool Authenticate { get; } = authenticate;

    /// <summary>The instructions travel in <see cref="SessionMeta"/>, so the first message does not carry them.</summary>
    public bool InstructionsInSession { get; } = instructionsInSession;

    public void Dispose()
    {
        if (scratchDirectory is null) return;
        try
        {
            Directory.Delete(scratchDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the OS temp cleaner gets it
        }
    }
}

/// <summary>
/// Runs Grok CLI limited to Cuebot. Checked against Grok CLI 1.0.67: in its own home Grok loads the streamer's MCP servers,
/// skills and settings, and imports Claude, Cursor and Codex setups (including their instruction files), and its built-in
/// file and shell tools run without asking. So each conversation gets:
/// <list type="bullet">
/// <item>a throwaway <c>GROK_HOME</c> in the temp folder (owner-only): none of the streamer's Grok MCP servers, skills or settings;</item>
/// <item>every Claude/Cursor/Codex import switched off (<c>GROK_*_ENABLED=false</c>; <c>0</c> is not honoured);</item>
/// <item>an agent profile whose tools are only <c>search_tool</c> and <c>use_tool</c> (MCP tools: only Cuebot's server is
///   connected), carrying Cuebot's instructions;</item>
/// <item>the streamer's existing Grok sign-in through Grok's documented external auth provider: a small command that prints
///   the current access token, read from <c>~/.grok/auth.json</c>. Cuebot never writes that file and never uses its refresh
///   token, so the streamer's sign-in is untouched; the throwaway home (with Grok's copy of the access token) is deleted
///   when the conversation ends.</item>
/// </list>
/// </summary>
public static class GrokIsolation
{
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(2);
    private static readonly string[] ImportedTools = ["CLAUDE", "CURSOR", "CODEX"];
    private static readonly string[] ImportedKinds = ["SKILLS", "RULES", "AGENTS", "MCPS", "HOOKS", "SESSIONS"];

    /// <summary>The streamer's Grok folder (their own <c>GROK_HOME</c>, else <c>~/.grok</c>).</summary>
    public static string StreamerHome(string? overrideHome = null) =>
        overrideHome ?? (System.Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".grok"));

    /// <summary>The current access token of the streamer's Grok sign-in, or null (with why).</summary>
    public static (string? Token, DateTimeOffset ExpiresAt, string? Issuer, string Problem) ReadSignIn(string? grokHome = null, DateTimeOffset? now = null)
    {
        JsonObject? entry;
        try
        {
            entry = (JsonNode.Parse(File.ReadAllText(Path.Combine(StreamerHome(grokHome), "auth.json"))) as JsonObject)?
                .Select(kv => kv.Value as JsonObject).FirstOrDefault(e => e?["key"] is JsonValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            entry = null;
        }
        if (entry?["key"]?.GetValue<string>() is not { Length: > 0 } token)
            return (null, default, null, "Not signed in: run grok in a terminal and sign in");
        var expires = DateTimeOffset.TryParse(entry["expires_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e)
            ? e
            : DateTimeOffset.MaxValue;
        if (expires - (now ?? DateTimeOffset.UtcNow) < MinimumLifetime)
            return (null, expires, null, "Grok's sign-in needs refreshing: open grok in a terminal once, then press Refresh");
        return (token, expires, entry["oidc_issuer"]?.GetValue<string>(), "");
    }

    public static (bool, string) SignedIn()
    {
        var (token, _, _, problem) = ReadSignIn();
        return token is null ? (false, problem) : (true, "Signed in to Grok");
    }

    /// <summary>Sets up one conversation (see the class summary). The caller disposes the result when the conversation ends.</summary>
    public static AcpLaunch Prepare(AssistantContext context, string? grokHome = null, string? scratchRoot = null)
    {
        var (token, expires, issuer, problem) = ReadSignIn(grokHome);
        if (token is null) throw new InvalidOperationException("Grok CLI: " + problem);

        var home = Path.Combine(scratchRoot ?? Path.GetTempPath(), "cuebot-grok-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(home);
        try
        {
            var tokenFile = Path.Combine(home, "cuebot-token.json");
            WritePrivate(tokenFile, new JsonObject
            {
                ["access_token"] = token,
                ["expires_in"] = (long)Math.Max(0, (expires == DateTimeOffset.MaxValue ? TimeSpan.FromHours(1) : expires - DateTimeOffset.UtcNow).TotalSeconds),
                ["issuer"] = issuer,
            }.ToJsonString());
            string command;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                command = Path.Combine(home, "cuebot-token.cmd");
                WritePrivate(command, $"@type \"{tokenFile}\"\r\n");
            }
            else
            {
                command = Path.Combine(home, "cuebot-token.sh");
                WritePrivate(command, $"#!/bin/sh\nexec cat '{tokenFile.Replace("'", "'\\''")}'\n");
                File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            WritePrivate(Path.Combine(home, "config.toml"), $"[auth]\nauth_provider_command = {TomlString(command)}\nauth_provider_label = \"Cuebot\"\n");

            var environment = new Dictionary<string, string> { ["GROK_HOME"] = home, ["GROK_DISABLE_AUTOUPDATER"] = "1" };
            foreach (var tool in ImportedTools)
                foreach (var kind in ImportedKinds)
                    environment[$"GROK_{tool}_{kind}_ENABLED"] = "false";

            var meta = new JsonObject
            {
                ["agentProfile"] = new JsonObject
                {
                    ["name"] = "cuebot-assistant",
                    ["description"] = "Cuebot's setup assistant",
                    ["tools"] = "search_tool, use_tool",
                    ["prompt"] = context.Instructions,
                },
            };
            return new AcpLaunch(environment, meta, authenticate: true, instructionsInSession: true, scratchDirectory: home);
        }
        catch
        {
            new AcpLaunch(new Dictionary<string, string>(), null, false, false, home).Dispose();
            throw;
        }
    }

    internal static string TomlString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void CreatePrivateDirectory(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Directory.CreateDirectory(path);   // %TEMP% is per user
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
