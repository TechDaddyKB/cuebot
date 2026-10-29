using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Cli;

/// <summary>
/// Google's Antigravity CLI (<c>agy</c>) in print mode with stream-json input and output, with the streamer's own
/// Antigravity sign-in. Each conversation runs one process limited to Cuebot by <see cref="AntigravityIsolation"/>.
/// </summary>
public sealed class AntigravityProvider(Func<AssistantProviderSettings> settings, Func<IReadOnlyList<string>?> gateCommand) : IAssistantProvider
{
    public string Id => "antigravity";
    public string Name => "Antigravity CLI";
    public AssistantProviderKind Kind => AssistantProviderKind.Cli;

    private string? Executable => CliLocator.Find("agy", settings().Command);

    private IReadOnlyList<string>? Gate => gateCommand();

    public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (Executable is null) return Task.FromResult(AssistantProviderStatus.Unavailable("Antigravity CLI is not installed (antigravity.google), or set its path"));
        if (Gate is null) return Task.FromResult(AssistantProviderStatus.Unavailable("Cuebot can not find its own program to check Antigravity's tool calls"));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Task.FromResult(AssistantProviderStatus.Unavailable("Not available on Windows yet: Cuebot's tool check for Antigravity runs through sh"));
        // Antigravity keeps its sign-in in the system keyring, so it is checked when the conversation starts.
        return Task.FromResult(AssistantProviderStatus.Available("Uses your Antigravity sign-in"));
    }

    public IAssistantSession StartSession(AssistantContext context) => new Session(this, context);

    private sealed class Session(AntigravityProvider provider, AssistantContext context) : IAssistantSession
    {
        private CliProcess? _process;
        private StreamWriter? _input;
        private StreamReader? _output;
        private AntigravityWorkspace? _workspace;

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var (input, output) = Connect();
            await input.WriteLineAsync(new JsonObject
            {
                ["event"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
            }.ToJsonString()).ConfigureAwait(false);

            // print mode has no interrupt message: a stop ends this process, and the next message starts a new conversation
            using var cancel = ct.Register(() => _ = DisposeAsync().AsTask());
            var lastStep = -1;
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
                if (message?["event"]?.GetValue<string>() == "result")
                {
                    var result = message["result"] as JsonObject;
                    if (result?["status"]?.GetValue<string>() is { } status && status != "SUCCESS")
                        yield return new AssistantError("Antigravity: " + (result["error"]?.ToString() is { Length: > 0 } e ? e : status.ToLowerInvariant()));
                    yield break;
                }
                if (message?["step_update"] is not JsonObject step) continue;
                var index = step["step_index"]?.GetValue<int>() ?? 0;
                switch (step["step_type"]?.GetValue<string>())
                {
                    case "agent_response":
                        var delta = step["text_delta"]?.GetValue<string>();
                        if (delta is not { Length: > 0 }) break;
                        if (index != lastStep && lastStep >= 0) yield return new AssistantText("\n\n");
                        lastStep = index;
                        yield return new AssistantText(delta);
                        break;
                    case "tool":
                        var tool = step["tool_info"]?["parameters"]?["ToolName"]?.GetValue<string>() ?? step["tool_name"]?.GetValue<string>() ?? "tool";
                        var id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        // Antigravity reads an MCP tool's description before calling it; only the calls themselves are shown.
                        if (step["tool_name"]?.GetValue<string>() != "call_mcp_tool") break;
                        switch (step["state"]?.GetValue<string>())
                        {
                            case "ACTIVE" when index != lastStep:
                                lastStep = index;
                                yield return new AssistantToolStarted(id, tool);
                                break;
                            case "DONE":
                                yield return new AssistantToolFinished(id, tool, false);
                                break;
                            case "ERROR":
                                yield return new AssistantToolFinished(id, tool, true);
                                break;
                        }
                        break;
                }
            }
            ct.ThrowIfCancellationRequested();
            var tail = _process?.ErrorTail ?? "";
            await DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Antigravity stopped" + (tail.Length > 0 ? ": " + (tail.Length > 400 ? tail[^400..] : tail) : "."));
        }

        private (StreamWriter Input, StreamReader Output) Connect()
        {
            if (_process is { HasExited: false } && _input is { } i && _output is { } o) return (i, o);
            if (provider.Executable is not { } agy) throw new InvalidOperationException("Antigravity CLI is not installed.");
            if (context.Mcp is not { } mcp) throw new InvalidOperationException("Turn on the MCP server (Servers page): Antigravity reaches Cuebot through it.");
            var gate = provider.Gate ?? throw new InvalidOperationException("Cuebot can not find its own program to check Antigravity's tool calls.");
            _workspace = AntigravityIsolation.Prepare(context, mcp, gate);
            List<string> args =
            [
                "-p", "", "--input-format", "stream-json", "--output-format", "stream-json", "--disable-slash-commands",
                // Print mode can not ask, so Cuebot's own server is granted for this session only (never saved); the gate
                // still denies every other tool.
                "--settings", AntigravityIsolation.SessionSettings(),
            ];
            if (context.Model.Length > 0) args.AddRange(["--model", context.Model]);
            _process = CliProcess.Start(agy, args, _workspace.Directory);
            _input = new StreamWriter(_process.Input, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _output = new StreamReader(_process.Output, new UTF8Encoding(false));
            return (_input, _output);
        }

        public async ValueTask DisposeAsync()
        {
            var process = _process;
            _process = null;
            _input = null;
            _output = null;
            if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
            _workspace?.Dispose();
            _workspace = null;
        }
    }
}
