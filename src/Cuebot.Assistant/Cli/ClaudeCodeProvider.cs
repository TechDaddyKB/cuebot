using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Cli;

/// <summary>
/// Claude Code in headless stream-json mode with the streamer's own Claude sign-in. Each conversation runs one process
/// restricted to Cuebot: <c>--restricted</c> and <c>--tools ""</c> (no commands, files or web), only Cuebot's MCP server
/// (<c>--strict-mcp-config</c>; the token comes from the environment), Cuebot's system prompt, no slash commands, and not
/// saved to Claude Code's session history. The streamer's Claude Code settings are not changed.
/// </summary>
public sealed class ClaudeCodeProvider(Func<AssistantProviderSettings> settings) : IAssistantProvider
{
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(30);
    private (AssistantProviderStatus Status, DateTimeOffset At)? _status;

    public string Id => "claude";
    public string Name => "Claude Code";
    public AssistantProviderKind Kind => AssistantProviderKind.Cli;

    private string? Executable => CliLocator.Find("claude", settings().Command);

    public async Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (_status is { } cached && DateTimeOffset.UtcNow - cached.At < StatusLifetime) return cached.Status;
        if (Executable is not { } claude) return AssistantProviderStatus.Unavailable("Claude Code is not installed (claude.com/code), or set its path");
        var output = await RunAsync(claude, ["auth", "status"], ct).ConfigureAwait(false);
        AssistantProviderStatus status;
        try
        {
            status = JsonNode.Parse(output)?["loggedIn"]?.GetValue<bool>() == true
                ? AssistantProviderStatus.Available("Signed in to Claude Code")
                : AssistantProviderStatus.Unavailable("Not signed in: run claude in a terminal and sign in");
        }
        catch (System.Text.Json.JsonException)
        {
            status = AssistantProviderStatus.Unavailable("Could not read Claude Code's sign-in status");
        }
        _status = (status, DateTimeOffset.UtcNow);
        return status;
    }

    private static async Task<string> RunAsync(string path, string[] args, CancellationToken ct)
    {
        await using var process = CliProcess.Start(path, args, Path.GetTempPath());
        process.Input.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var reader = new StreamReader(process.Output, new UTF8Encoding(false));
        return await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
    }

    public IAssistantSession StartSession(AssistantContext context) => new Session(this, context);

    /// <summary>The command line for one conversation (no secrets: the MCP token is read from the environment).</summary>
    internal static List<string> Arguments(AssistantContext context, McpEndpoint mcp)
    {
        var mcpConfig = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpEndpoint.ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = mcp.Url,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer ${" + McpEndpoint.TokenVariable + "}" },
                },
            },
        };
        List<string> args =
        [
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--restricted", "--tools", "", "--strict-mcp-config", "--mcp-config", mcpConfig.ToJsonString(),
            "--allowedTools", "mcp__" + McpEndpoint.ServerName, "--permission-mode", "dontAsk",
            "--disable-slash-commands", "--no-session-persistence", "--system-prompt", context.Instructions,
        ];
        if (context.Model.Length > 0) args.AddRange(["--model", context.Model]);
        return args;
    }

    private sealed class Session(ClaudeCodeProvider provider, AssistantContext context) : IAssistantSession
    {
        private const string ToolPrefix = "mcp__" + McpEndpoint.ServerName + "__";
        private CliProcess? _process;
        private StreamReader? _output;
        private StreamWriter? _input;

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var (input, output) = Connect();
            await input.WriteLineAsync(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                },
            }.ToJsonString()).ConfigureAwait(false);

            // Stop: ask Claude Code to interrupt the turn; kill it if it does not stop in time.
            var stopped = false;
            using var cancel = ct.Register(() =>
            {
                stopped = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await input.WriteLineAsync(new JsonObject
                        {
                            ["type"] = "control_request",
                            ["request_id"] = "cuebot-stop",
                            ["request"] = new JsonObject { ["subtype"] = "interrupt" },
                        }.ToJsonString()).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                    if (_process is { HasExited: false } running) await running.DisposeAsync().ConfigureAwait(false);
                });
            });

            var wroteText = false;
            var tools = new Dictionary<string, string>();
            while (await output.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                switch (message?["type"]?.GetValue<string>())
                {
                    case "stream_event" when message["event"] is JsonObject e:
                        if (e["type"]?.GetValue<string>() == "message_start" && wroteText) yield return new AssistantText("\n\n");
                        if (e["type"]?.GetValue<string>() == "content_block_delta" && e["delta"]?["type"]?.GetValue<string>() == "text_delta"
                            && e["delta"]?["text"]?.GetValue<string>() is { Length: > 0 } delta)
                        {
                            wroteText = true;
                            yield return new AssistantText(delta);
                        }
                        break;
                    case "assistant":
                        foreach (var block in message["message"]?["content"] as JsonArray ?? [])
                        {
                            if (block?["type"]?.GetValue<string>() != "tool_use") continue;
                            var id = block["id"]?.GetValue<string>() ?? "";
                            var name = block["name"]?.GetValue<string>() ?? "tool";
                            tools[id] = name.StartsWith(ToolPrefix, StringComparison.Ordinal) ? name[ToolPrefix.Length..] : name;
                            yield return new AssistantToolStarted(id, tools[id]);
                        }
                        break;
                    case "user":
                        foreach (var block in message["message"]?["content"] as JsonArray ?? [])
                        {
                            if (block?["type"]?.GetValue<string>() != "tool_result") continue;
                            var id = block["tool_use_id"]?.GetValue<string>() ?? "";
                            yield return new AssistantToolFinished(id, tools.GetValueOrDefault(id, "tool"), block["is_error"]?.GetValue<bool>() == true);
                        }
                        break;
                    case "result":
                        if (stopped) throw new OperationCanceledException(ct);
                        if (message["is_error"]?.GetValue<bool>() == true)
                            yield return new AssistantError("Claude Code: " + (message["result"]?.GetValue<string>() is { Length: > 0 } r ? r : message["subtype"]?.GetValue<string>()));
                        yield break;
                }
            }
            if (stopped) throw new OperationCanceledException(ct);
            var tail = _process?.ErrorTail ?? "";
            await DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Claude Code stopped" + (tail.Length > 0 ? ": " + (tail.Length > 400 ? tail[^400..] : tail) : "."));
        }

        private (StreamWriter Input, StreamReader Output) Connect()
        {
            if (_process is { HasExited: false } && _input is { } i && _output is { } o) return (i, o);
            if (provider.Executable is not { } claude) throw new InvalidOperationException("Claude Code is not installed.");
            if (context.Mcp is not { } mcp) throw new InvalidOperationException("Turn on the MCP server (Servers page): Claude Code reaches Cuebot through it.");
            _process = CliProcess.Start(claude, Arguments(context, mcp), context.WorkingDirectory,
                new Dictionary<string, string>
                {
                    [McpEndpoint.TokenVariable] = mcp.Token,
                    // a destructive tool waits for the streamer's Approve
                    ["MCP_TOOL_TIMEOUT"] = ((int)McpEndpoint.ToolTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            _input = new StreamWriter(_process.Input, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _output = new StreamReader(_process.Output, new UTF8Encoding(false));
            return (_input, _output);
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { } process) await process.DisposeAsync().ConfigureAwait(false);
            _process = null;
            _input = null;
            _output = null;
        }
    }
}
