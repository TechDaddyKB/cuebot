using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Assistant.Cli;

namespace Cuebot.Assistant.Tests;

public sealed class JsonLineRpcTests
{
    /// <summary>A peer on the other end of two in-process pipes: reads our lines, writes its own.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _toUs = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromUs = new(PipeDirection.In);
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;

        public Peer(bool jsonRpcField = true)
        {
            Rpc = new JsonLineRpc(new AnonymousPipeClientStream(PipeDirection.In, _toUs.ClientSafePipeHandle),
                new AnonymousPipeClientStream(PipeDirection.Out, _fromUs.ClientSafePipeHandle), jsonRpcField);
            _writer = new StreamWriter(_toUs, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _reader = new StreamReader(_fromUs, new UTF8Encoding(false));
        }

        public JsonLineRpc Rpc { get; }

        public async Task<JsonObject> ReadAsync() => (JsonObject)JsonNode.Parse((await _reader.ReadLineAsync())!)!;

        public Task WriteAsync(string json) => _writer.WriteLineAsync(json);

        public void Close() => _toUs.Dispose();

        public async ValueTask DisposeAsync()
        {
            await Rpc.DisposeAsync();
            _toUs.Dispose();
            _fromUs.Dispose();
        }
    }

    [Fact]
    public async Task Matches_answers_to_requests_and_queues_the_peers_messages_in_order()
    {
        await using var peer = new Peer();
        var first = peer.Rpc.RequestAsync("one", new JsonObject { ["a"] = 1 });
        var second = peer.Rpc.RequestAsync("two");
        var r1 = await peer.ReadAsync();
        var r2 = await peer.ReadAsync();
        Assert.Equal("2.0", r1["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, r1["params"]!["a"]!.GetValue<int>());

        await peer.WriteAsync("""{"jsonrpc":"2.0","method":"note","params":{"n":1}}""");
        await peer.WriteAsync("not json: a warning line");
        await peer.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = r2["id"]!.DeepClone(), ["result"] = new JsonObject { ["answer"] = "two" } }.ToJsonString());
        await peer.WriteAsync("""{"jsonrpc":"2.0","id":"x7","method":"ask","params":{}}""");
        await peer.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = r1["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -1, ["message"] = "nope" } }.ToJsonString());

        Assert.Equal("two", (await second)!["answer"]!.GetValue<string>());
        Assert.Equal("one: nope", (await Assert.ThrowsAsync<RpcException>(() => first)).Message);
        var note = await peer.Rpc.Incoming.ReadAsync();
        Assert.False(note.IsRequest);
        Assert.Equal(1, note.Params["n"]!.GetValue<int>());
        var ask = await peer.Rpc.Incoming.ReadAsync();
        Assert.True(ask.IsRequest);

        await peer.Rpc.RespondAsync(ask.Id, new JsonObject { ["ok"] = true });
        var answer = await peer.ReadAsync();
        Assert.Equal("x7", answer["id"]!.GetValue<string>());
        Assert.True(answer["result"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Codex_style_messages_leave_out_the_jsonrpc_field()
    {
        await using var peer = new Peer(jsonRpcField: false);
        _ = peer.Rpc.NotifyAsync("initialized");
        var sent = await peer.ReadAsync();
        Assert.False(sent.ContainsKey("jsonrpc"));
        Assert.Equal("initialized", sent["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_closed_connection_fails_waiting_requests_and_ends_the_message_stream()
    {
        await using var peer = new Peer();
        var waiting = peer.Rpc.RequestAsync("slow");
        await peer.ReadAsync();
        peer.Close();

        await Assert.ThrowsAsync<IOException>(() => waiting);
        Assert.False(await peer.Rpc.Incoming.WaitToReadAsync());
        Assert.NotNull(peer.Rpc.CloseReason);
    }
}

public sealed class CliProviderTests
{
    [Fact]
    public void Claude_Code_runs_restricted_to_Cuebots_MCP_server_without_the_token_on_the_command_line()
    {
        var context = new AssistantContext("Be helpful.", null!, null, "claude-sonnet-5-5", "/tmp");
        var mcp = new McpEndpoint("http://127.0.0.1:7475/mcp", "cb_secret-token");

        var args = ClaudeCodeProvider.Arguments(context, mcp);

        Assert.DoesNotContain(args, a => a.Contains("cb_secret-token", StringComparison.Ordinal));
        Assert.Contains("--restricted", args);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--disable-slash-commands", args);
        Assert.Equal("mcp__cuebot", args[args.IndexOf("--allowedTools") + 1]);
        Assert.Equal("Be helpful.", args[args.IndexOf("--system-prompt") + 1]);
        Assert.Equal("claude-sonnet-5-5", args[args.IndexOf("--model") + 1]);
        var config = JsonNode.Parse(args[args.IndexOf("--mcp-config") + 1])!["mcpServers"]!["cuebot"]!;
        Assert.Equal("http://127.0.0.1:7475/mcp", config["url"]!.GetValue<string>());
        Assert.Equal("Bearer ${CUEBOT_MCP_TOKEN}", config["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public void Codex_runs_without_its_command_file_browser_and_image_tools()
    {
        foreach (var feature in new[] { "shell_tool", "unified_exec", "browser_use", "computer_use", "apps", "plugins", "multi_agent", "image_generation" })
            Assert.Contains(feature, CodexProvider.DisabledFeatures);
    }

    [Fact]
    public void The_locator_takes_a_configured_program_or_searches_PATH()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-cli-").FullName;
        try
        {
            var program = Path.Combine(dir, OperatingSystem.IsWindows() ? "fakeai.exe" : "fakeai");
            File.WriteAllText(program, "");
            Assert.Equal(program, CliLocator.Find("other", program));
            Assert.Equal(program, CliLocator.Find("fakeai", "", [dir]));
            Assert.Null(CliLocator.Find("fakeai-missing", "", [dir]));
            Assert.Null(CliLocator.Find("fakeai", Path.Combine(dir, "nope")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class GrokIsolationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-grok-test-").FullName;
    private string GrokHome => Path.Combine(_root, "grok-home");
    private string Scratch => Path.Combine(_root, "scratch");

    public GrokIsolationTests()
    {
        Directory.CreateDirectory(GrokHome);
        Directory.CreateDirectory(Scratch);
    }

    private void SignIn(DateTimeOffset expires) => File.WriteAllText(Path.Combine(GrokHome, "auth.json"), new JsonObject
    {
        ["https://auth.x.ai::user"] = new JsonObject
        {
            ["key"] = "access-token-123",
            ["refresh_token"] = "refresh-token-456",
            ["expires_at"] = expires.ToString("O"),
            ["oidc_issuer"] = "https://auth.x.ai",
        },
    }.ToJsonString());

    [Fact]
    public void Each_conversation_gets_a_private_throwaway_home_and_only_the_access_token()
    {
        SignIn(DateTimeOffset.UtcNow.AddHours(1));
        var before = File.ReadAllText(Path.Combine(GrokHome, "auth.json"));

        string home;
        using (var launch = GrokIsolation.Prepare(new AssistantContext("Help with Cuebot.", null!, null, "", "/tmp"), GrokHome, Scratch))
        {
            home = launch.Environment["GROK_HOME"];
            Assert.StartsWith(Scratch, home);
            Assert.True(launch.Authenticate);
            Assert.True(launch.InstructionsInSession);
            Assert.Equal("1", launch.Environment["GROK_DISABLE_AUTOUPDATER"]);
            foreach (var tool in new[] { "CLAUDE", "CURSOR", "CODEX" })
                foreach (var kind in new[] { "SKILLS", "RULES", "AGENTS", "MCPS", "HOOKS", "SESSIONS" })
                    Assert.Equal("false", launch.Environment[$"GROK_{tool}_{kind}_ENABLED"]);
            var profile = launch.SessionMeta!["agentProfile"]!;
            Assert.Equal("search_tool, use_tool", profile["tools"]!.GetValue<string>());
            Assert.Equal("Help with Cuebot.", profile["prompt"]!.GetValue<string>());

            var token = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "cuebot-token.json")))!;
            Assert.Equal("access-token-123", token["access_token"]!.GetValue<string>());
            Assert.Equal("https://auth.x.ai", token["issuer"]!.GetValue<string>());
            Assert.InRange(token["expires_in"]!.GetValue<long>(), 3000, 3600);
            Assert.DoesNotContain("refresh-token-456", Directory.EnumerateFiles(home).Select(File.ReadAllText).Aggregate("", string.Concat));
            Assert.Contains("auth_provider_command", File.ReadAllText(Path.Combine(home, "config.toml")));
            Assert.DoesNotContain("mcp_servers", File.ReadAllText(Path.Combine(home, "config.toml")));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(home));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home, "cuebot-token.json")));
                var script = Path.Combine(home, "cuebot-token.sh");
                Assert.True(File.GetUnixFileMode(script).HasFlag(UnixFileMode.UserExecute));
                using var cat = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script) { RedirectStandardOutput = true })!;
                Assert.Equal("access-token-123", JsonNode.Parse(cat.StandardOutput.ReadToEnd())!["access_token"]!.GetValue<string>());
            }
        }

        Assert.False(Directory.Exists(home));
        Assert.Equal(before, File.ReadAllText(Path.Combine(GrokHome, "auth.json")));
    }

    [Fact]
    public void Without_a_usable_sign_in_nothing_is_started()
    {
        var context = new AssistantContext("", null!, null, "", "/tmp");
        Assert.Contains("Not signed in", Assert.Throws<InvalidOperationException>(() => GrokIsolation.Prepare(context, GrokHome, Scratch)).Message);

        SignIn(DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Contains("refreshing", Assert.Throws<InvalidOperationException>(() => GrokIsolation.Prepare(context, GrokHome, Scratch)).Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public sealed class AntigravityIsolationTests : IDisposable
{
    [Fact]
    public void Hook_commands_quote_for_sh_and_for_PowerShell()
    {
        Assert.Equal("'/opt/Cue bot/cuebot' 'it'\\''s' '--assistant-tool-gate'",
            AntigravityIsolation.HookCommand(["/opt/Cue bot/cuebot", "it's", "--assistant-tool-gate"], windows: false));
        // Windows: a plain path runs as is in PowerShell, cmd and sh alike; anything else uses PowerShell's call operator.
        Assert.Equal(@"C:\Cuebot\Cuebot.exe --assistant-tool-gate",
            AntigravityIsolation.HookCommand([@"C:\Cuebot\Cuebot.exe", "--assistant-tool-gate"], windows: true));
        Assert.Equal(@"& 'C:\Program Files\Kris''s Cuebot\Cuebot.exe' '--assistant-tool-gate'",
            AntigravityIsolation.HookCommand([@"C:\Program Files\Kris's Cuebot\Cuebot.exe", "--assistant-tool-gate"], windows: true));
    }

    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-agy-test-").FullName;

    private static string Call(string name, JsonObject args) =>
        new JsonObject { ["lastUserInput"] = "use cuebot_cuebot please", ["toolCall"] = new JsonObject { ["name"] = name, ["args"] = args } }.ToJsonString();

    private static string Decision(string answer) => JsonNode.Parse(answer)!["decision"]!.GetValue<string>();

    [Fact]
    public void The_gate_allows_only_Cuebots_MCP_server_and_its_tool_descriptions()
    {
        var schemas = Path.Combine(_root, "mcp", AntigravityIsolation.ServerName);
        Directory.CreateDirectory(schemas);
        File.WriteAllText(Path.Combine(schemas, "list_actions.json"), "{}");

        var allowed = AntigravityIsolation.Decide(Call("call_mcp_tool", new JsonObject { ["ServerName"] = "cuebot_cuebot", ["ToolName"] = "list_actions", ["Arguments"] = new JsonObject() }), schemas);
        Assert.Equal("allow", Decision(allowed));
        Assert.Equal("mcp(cuebot_cuebot/list_actions)", JsonNode.Parse(allowed)!["permissionOverrides"]![0]!.GetValue<string>());
        Assert.Equal("allow", Decision(AntigravityIsolation.Decide(Call("read_file", new JsonObject { ["Path"] = Path.Combine(schemas, "list_actions.json") }), schemas)));

        // The user's words mention the server: only the call's own fields count.
        Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("call_mcp_tool", new JsonObject { ["ServerName"] = "other_server", ["ToolName"] = "x" }), schemas)));
        Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("call_mcp_tool", new JsonObject { ["ServerName"] = "cuebot_cuebot", ["ToolName"] = "a) mcp(other/*" }), schemas)));
        Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("read_file", new JsonObject { ["Path"] = Path.Combine(schemas, "..", "..", "settings.json") }), schemas)));
        Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("read_file", new JsonObject { ["Path"] = "/etc/passwd" }), schemas)));
        Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("run_command", new JsonObject { ["CommandLine"] = "cat ~/.ssh/id_rsa" }), schemas)));
        Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("search_web", new JsonObject { ["query"] = "x" }), schemas)));
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(schemas, "escape.json"), "/etc/passwd");
            Assert.Equal("deny", Decision(AntigravityIsolation.Decide(Call("read_file", new JsonObject { ["Path"] = Path.Combine(schemas, "escape.json") }), schemas)));
        }

        using var output = new StringWriter();
        Assert.Equal(0, AntigravityIsolation.RunGate(new StringReader("not json"), output));
        Assert.Equal("deny", Decision(output.ToString()));
    }

    [Fact]
    public void Each_conversation_gets_a_private_workspace_with_the_instructions_server_and_gate()
    {
        var mcp = new McpEndpoint("http://127.0.0.1:7475/mcp", "cb_token");
        string dir;
        using (var workspace = AntigravityIsolation.Prepare(new AssistantContext("Help with Cuebot.", null!, null, "", "/tmp"), mcp,
                   ["/opt/Cue bot/cuebot", "it's", AntigravityIsolation.GateSwitch], _root))
        {
            dir = workspace.Directory;
            Assert.Equal("Help with Cuebot.\n", File.ReadAllText(Path.Combine(dir, "AGENTS.md")));
            var plugin = Path.Combine(dir, ".agents", "plugins", "cuebot");
            var server = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "mcp_config.json")))!["mcpServers"]!["cuebot"]!;
            Assert.Equal("http://127.0.0.1:7475/mcp", server["serverUrl"]!.GetValue<string>());
            Assert.Equal("Bearer cb_token", server["headers"]!["Authorization"]!.GetValue<string>());
            var hook = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "hooks.json")))!["cuebot-only"]!["PreToolUse"]![0]!;
            Assert.Equal("*", hook["matcher"]!.GetValue<string>());
            Assert.Equal(AntigravityIsolation.HookCommand(["/opt/Cue bot/cuebot", "it's", "--assistant-tool-gate"], OperatingSystem.IsWindows()),
                hook["hooks"]![0]!["command"]!.GetValue<string>());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(plugin, "mcp_config.json")));
            }
        }
        Assert.False(Directory.Exists(dir));

        var settings = JsonNode.Parse(AntigravityIsolation.SessionSettings())!;
        Assert.Equal("mcp(cuebot_cuebot/*)", settings["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
