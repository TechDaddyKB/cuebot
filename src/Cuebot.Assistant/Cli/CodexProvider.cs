using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Cli;

/// <summary>
/// OpenAI Codex through its app server (<c>codex app-server</c>, the protocol the Codex IDE extensions use), with the
/// streamer's own Codex sign-in: Sign in with ChatGPT starts Codex's browser login. Each conversation runs a Codex
/// process restricted to Cuebot: only Cuebot's MCP server (the streamer's other Codex MCP servers are switched off for
/// that process), no shell, file, browser, image or web tools, read-only sandbox, no approvals, not saved to Codex's
/// history. The streamer's Codex configuration is not changed.
/// </summary>
public sealed class CodexProvider(Func<AssistantProviderSettings> settings) : IAssistantProvider
{
    /// <summary>Codex features that give the model tools beyond Cuebot's (codex features list).</summary>
    internal static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "apps", "plugins", "remote_plugin", "browser_use", "browser_use_external", "in_app_browser",
        "computer_use", "image_generation", "view_image", "multi_agent", "skill_search", "tool_suggest", "hooks",
    ];

    private CodexConnection? _login;

    public string Id => "codex";
    public string Name => "Codex (ChatGPT sign-in)";
    public AssistantProviderKind Kind => AssistantProviderKind.Cli;
    public bool CanSignIn => true;

    internal string? Executable => CliLocator.Find("codex", settings().Command);

    /// <summary>
    /// Installed and signed in? Reads only which kind of sign-in Codex saved (auth.json's auth_mode and whether tokens or a
    /// key are present), never the values, and starts nothing.
    /// </summary>
    public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (Executable is null) return Task.FromResult(AssistantProviderStatus.Unavailable("Codex is not installed (npm install -g @openai/codex), or set its path"));
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        try
        {
            var auth = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
            if (auth?["tokens"] is JsonObject { Count: > 0 })
                return Task.FromResult(AssistantProviderStatus.Available("Signed in with ChatGPT"));
            if (auth?["OPENAI_API_KEY"] is JsonValue)
                return Task.FromResult(AssistantProviderStatus.Available("Signed in to Codex with an API key"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // not signed in yet
        }
        return Task.FromResult(AssistantProviderStatus.Unavailable("Not signed in: use Sign in (Sign in with ChatGPT)"));
    }

    /// <summary>Starts Codex's Sign in with ChatGPT; the returned URL opens in the browser and Codex finishes the login.</summary>
    public async Task<string?> SignInAsync(CancellationToken ct = default)
    {
        if (Executable is not { } codex) throw new InvalidOperationException("Codex is not installed.");
        if (_login is { } old) await old.DisposeAsync().ConfigureAwait(false);
        var login = _login = await CodexConnection.StartAsync(codex, [], Path.GetTempPath(), null, ct).ConfigureAwait(false);
        var started = await login.Rpc.RequestAsync("account/login/start", new JsonObject { ["type"] = "chatgpt" }, ct).ConfigureAwait(false);
        // Codex runs the browser callback; keep it alive until it reports the result (or 10 minutes).
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await foreach (var message in login.Rpc.Incoming.ReadAllAsync(timeout.Token).ConfigureAwait(false))
                    if (message.Method == "account/login/completed") break;
            }
            catch (OperationCanceledException)
            {
            }
            if (ReferenceEquals(_login, login)) _login = null;
            await login.DisposeAsync().ConfigureAwait(false);
        }, CancellationToken.None);
        return started?["authUrl"]?.GetValue<string>();
    }

    public IAssistantSession StartSession(AssistantContext context) => new Session(this, context);

    private sealed class Session(CodexProvider provider, AssistantContext context) : IAssistantSession
    {
        private CodexConnection? _codex;
        private string? _threadId;

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var codex = await ConnectAsync(ct).ConfigureAwait(false);
            var turn = await codex.Rpc.RequestAsync("turn/start", new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            }, ct).ConfigureAwait(false);
            var turnId = turn?["turn"]?["id"]?.GetValue<string>();
            string? lastMessage = null;
            using var interrupt = ct.Register(() => _ = codex.Rpc.NotifyAsync("turn/interrupt", new JsonObject { ["threadId"] = _threadId, ["turnId"] = turnId }));

            await foreach (var message in codex.Rpc.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (message.IsRequest)
                {
                    await Refuse(codex.Rpc, message, ct).ConfigureAwait(false);
                    continue;
                }
                var p = message.Params;
                switch (message.Method)
                {
                    case "item/agentMessage/delta" when p["delta"]?.GetValue<string>() is { Length: > 0 } delta:
                        if (p["itemId"]?.GetValue<string>() is var itemId && itemId != lastMessage)
                        {
                            if (lastMessage is not null) yield return new AssistantText("\n\n");   // a new message in the same turn
                            lastMessage = itemId;
                        }
                        yield return new AssistantText(delta);
                        break;
                    case "item/started" when p["item"] is JsonObject { } item && item["type"]?.GetValue<string>() == "mcpToolCall":
                        yield return new AssistantToolStarted(item["id"]?.GetValue<string>() ?? "", item["tool"]?.GetValue<string>() ?? "tool");
                        break;
                    case "item/completed" when p["item"] is JsonObject { } item && item["type"]?.GetValue<string>() == "mcpToolCall":
                        yield return new AssistantToolFinished(item["id"]?.GetValue<string>() ?? "", item["tool"]?.GetValue<string>() ?? "tool",
                            item["status"]?.GetValue<string>() == "failed" || item["error"] is JsonObject);
                        break;
                    case "error" when p["willRetry"]?.GetValue<bool>() != true:
                        yield return new AssistantError("Codex: " + (p["error"]?["message"]?.GetValue<string>() ?? "error"));
                        break;
                    case "turn/completed" when turnId is null || TurnId(p) == turnId:
                        if (p["turn"]?["status"]?.GetValue<string>() == "failed")
                            yield return new AssistantError("Codex: " + (p["turn"]?["error"]?["message"]?.GetValue<string>() ?? "the turn failed"));
                        yield break;
                }
            }
            throw new InvalidOperationException("Codex stopped: " + (codex.Rpc.CloseReason ?? "") + " " + codex.Process?.ErrorTail);
        }

        private async Task<CodexConnection> ConnectAsync(CancellationToken ct)
        {
            if (_codex is { } open && open.Rpc.CloseReason is null) return open;
            if (_codex is { } closed) await closed.DisposeAsync().ConfigureAwait(false);
            if (provider.Executable is not { } executable) throw new InvalidOperationException("Codex is not installed.");
            if (context.Mcp is not { } mcp) throw new InvalidOperationException("Turn on the MCP server (Servers page): Codex reaches Cuebot through it.");

            // Which MCP servers the streamer configured, so this process can switch them off.
            string[] others;
            await using (var probe = await CodexConnection.StartAsync(executable, [], context.WorkingDirectory, null, ct).ConfigureAwait(false))
            {
                var config = await probe.Rpc.RequestAsync("config/read", new JsonObject(), ct).ConfigureAwait(false);
                others = [.. (config?["config"]?["mcp_servers"] as JsonObject ?? []).Select(kv => kv.Key).Where(k => k != McpEndpoint.ServerName)];
            }

            List<string> args = [];
            foreach (var feature in DisabledFeatures) args.AddRange(["--disable", feature]);
            foreach (var other in others) args.AddRange(["-c", $"mcp_servers.{TomlKey(other)}.enabled=false"]);
            args.AddRange(["-c", $"mcp_servers.{McpEndpoint.ServerName}.url=\"{mcp.Url}\"",
                           "-c", $"mcp_servers.{McpEndpoint.ServerName}.bearer_token_env_var=\"{McpEndpoint.TokenVariable}\"",
                           "-c", "web_search=\"disabled\""]);
            var codex = _codex = await CodexConnection.StartAsync(executable, args, context.WorkingDirectory,
                new Dictionary<string, string> { [McpEndpoint.TokenVariable] = mcp.Token }, ct).ConfigureAwait(false);
            var thread = await codex.Rpc.RequestAsync("thread/start", new JsonObject
            {
                ["cwd"] = context.WorkingDirectory,
                ["sandbox"] = "read-only",
                ["approvalPolicy"] = "never",
                ["developerInstructions"] = context.Instructions,
                ["ephemeral"] = true,
                ["model"] = context.Model.Length > 0 ? context.Model : null,
            }, ct).ConfigureAwait(false);
            _threadId = thread?["thread"]?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Codex did not start a conversation.");
            return codex;
        }

        private static string? TurnId(JsonObject p) => p["turn"]?["id"]?.GetValue<string>();

        private static string TomlKey(string key) =>
            key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>Codex asks before commands, file changes and MCP form input; Cuebot's assistant allows none of them.</summary>
        private static Task Refuse(JsonLineRpc rpc, RpcIncoming request, CancellationToken ct) => request.Method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" =>
                rpc.RespondAsync(request.Id, new JsonObject { ["decision"] = "decline" }, ct),
            "execCommandApproval" or "applyPatchApproval" => rpc.RespondAsync(request.Id, new JsonObject { ["decision"] = "denied" }, ct),
            "mcpServer/elicitation/request" => rpc.RespondAsync(request.Id, new JsonObject { ["action"] = "decline" }, ct),
            _ => rpc.RespondErrorAsync(request.Id, -32601, "Not available in Cuebot's assistant", ct),
        };

        public async ValueTask DisposeAsync()
        {
            if (_codex is { } codex) await codex.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>A <c>codex app-server</c> process with the handshake done.</summary>
internal sealed class CodexConnection(CliProcess? process, JsonLineRpc rpc) : IAsyncDisposable
{
    public CliProcess? Process { get; } = process;
    public JsonLineRpc Rpc { get; } = rpc;

    public static async Task<CodexConnection> StartAsync(string executable, IEnumerable<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        var process = CliProcess.Start(executable, ["app-server", .. args], workingDirectory, environment);
        var connection = new CodexConnection(process, new JsonLineRpc(process.Output, process.Input, jsonRpcField: false));
        try
        {
            await connection.HandshakeAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var tail = process.ErrorTail;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Codex did not start: {ex.Message}{(tail.Length > 0 ? " - " + tail : "")}");
        }
    }

    public async Task HandshakeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await Rpc.RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "cuebot", ["title"] = "Cuebot", ["version"] = typeof(CodexConnection).Assembly.GetName().Version?.ToString(3) },
        }, timeout.Token).ConfigureAwait(false);
        await Rpc.NotifyAsync("initialized", null, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Rpc.DisposeAsync().ConfigureAwait(false);
        if (Process is not null) await Process.DisposeAsync().ConfigureAwait(false);
    }
}
