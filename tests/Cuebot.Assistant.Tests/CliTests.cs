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

    [Theory]
    [InlineData("grok")]
    [InlineData("gemini")]
    public async Task AI_CLIs_that_can_not_be_limited_to_Cuebots_tools_are_unavailable(string id)
    {
        var provider = new AcpProvider(id == "grok" ? AcpProvider.Grok : AcpProvider.Gemini, () => new AssistantProviderSettings());

        var status = await provider.GetStatusAsync();

        Assert.False(status.Ready);
        Assert.Contains("API key", status.Message);
        Assert.Throws<InvalidOperationException>(() => provider.StartSession(new AssistantContext("", null!, null, "", "/tmp")));
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
