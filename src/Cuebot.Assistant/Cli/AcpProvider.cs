using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Cli;

/// <summary>
/// An AI CLI that speaks the Agent Client Protocol over stdio (Grok <c>agent stdio</c>), with the
/// streamer's own sign-in for that app. Cuebot's MCP server is added to the session (its token goes in the session
/// request, not on a command line); Cuebot offers the agent no file system or terminal, and answers every permission
/// request: Cuebot's own tools are allowed, everything else is refused.
/// </summary>
public sealed class AcpProvider(AcpProvider.Profile profile, Func<AssistantProviderSettings> settings) : IAssistantProvider
{
    /// <param name="Arguments">Arguments that start the ACP agent (before any model argument).</param>
    /// <param name="Environment">Extra environment for the agent process.</param>
    /// <param name="SignedIn">Whether the app has a sign-in on this computer (checked without starting it), with a hint when not.</param>
    /// <param name="Blocked">Why this app can not be used yet: Cuebot only runs an AI CLI it can limit to Cuebot's tools.</param>
    /// <param name="Prepare">Per-conversation setup (environment, session options, clean-up); null for none.</param>
    public sealed record Profile(string Id, string Name, string Command, string[] Arguments, string ModelFlag, string InstallHint,
        IReadOnlyDictionary<string, string> Environment, Func<(bool SignedIn, string Message)> SignedIn, string? Blocked = null,
        Func<AssistantContext, AcpLaunch>? Prepare = null, string[]? ModelsArguments = null);

    /// <summary>Limited to Cuebot by <see cref="GrokIsolation"/> (throwaway home, tool allowlist, the streamer's sign-in read-only).</summary>
    public static readonly Profile Grok = new("grok", "Grok CLI", "grok", ["agent", "stdio"], "--model", "see x.ai/cli",
        new Dictionary<string, string>(), GrokIsolation.SignedIn, Prepare: context => GrokIsolation.Prepare(context), ModelsArguments: ["models"]);

    internal Profile Definition => profile;

    public string Id => profile.Id;
    public string Name => profile.Name;
    public AssistantProviderKind Kind => AssistantProviderKind.Cli;

    private string? Executable => CliLocator.Find(profile.Command, settings().Command);

    public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (profile.Blocked is { } blocked) return Task.FromResult(AssistantProviderStatus.Unavailable(blocked));
        if (Executable is null) return Task.FromResult(AssistantProviderStatus.Unavailable($"{profile.Name} is not installed ({profile.InstallHint}), or set its path"));
        var (signedIn, message) = profile.SignedIn();
        return Task.FromResult(signedIn ? AssistantProviderStatus.Available(message) : AssistantProviderStatus.Unavailable(message));
    }

    public IAssistantSession StartSession(AssistantContext context) =>
        profile.Blocked is { } blocked ? throw new InvalidOperationException(blocked) : new Session(this, context);

    /// <summary>
    /// The models the app lists (<c>grok models</c>). It runs in the same throwaway home as a conversation (the streamer's
    /// sign-in passed in read-only when there is one), so none of their own Grok settings are read or written. Grok 1.0.67
    /// lists its standard models there even for an account that has more; the Model field still takes any id.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (profile.ModelsArguments is not { } args || Executable is not { } executable) return [];
        AcpLaunch? launch = null;
        try
        {
            if (profile.SignedIn().SignedIn && profile.Prepare is { } prepare)
                launch = prepare(new AssistantContext("", null!, null, "", Path.GetTempPath()));   // only the environment is used
        }
        catch (InvalidOperationException)
        {
            launch = null;
        }
        var home = launch is null ? Directory.CreateTempSubdirectory("cuebot-models-").FullName : null;
        try
        {
            var environment = launch?.Environment
                ?? new Dictionary<string, string> { ["GROK_HOME"] = home!, ["GROK_DISABLE_AUTOUPDATER"] = "1" };
            return ParseModelList(await CliProcess.RunToEndAsync(executable, args, ct, environment).ConfigureAwait(false));
        }
        finally
        {
            launch?.Dispose();
            if (home is not null)
            {
                try { Directory.Delete(home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static IReadOnlyList<string> ParseModelList(string output)
    {
        var models = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Default model:", StringComparison.OrdinalIgnoreCase)) models.Insert(0, line["Default model:".Length..].Trim());
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                models.Add(line[2..].Replace("(default)", "", StringComparison.Ordinal).Trim());
        }
        return [.. models.Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    private static string Home => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    private sealed class Session(AcpProvider provider, AssistantContext context) : IAssistantSession
    {
        private readonly HashSet<string> _cuebotTools = [.. context.Tools.List().Select(t => t.Name)];
        private CliProcess? _process;
        private JsonLineRpc? _rpc;
        private AcpLaunch? _launch;
        private string? _sessionId;
        private bool _instructed;

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var rpc = await ConnectAsync(ct).ConfigureAwait(false);
            // ACP has no system prompt: the instructions lead the first message.
            var prompt = _instructed || _launch?.InstructionsInSession == true
                ? text
                : $"<cuebot-instructions>\n{context.Instructions}\n</cuebot-instructions>\n\n{text}";
            _instructed = true;
            var turn = rpc.RequestAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = _sessionId,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }),
            }, CancellationToken.None);
            using var cancel = ct.Register(() => _ = rpc.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = _sessionId }));
            var tools = new Dictionary<string, string>();

            while (true)
            {
                var next = rpc.Incoming.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(turn, next).ConfigureAwait(false) == turn && !rpc.Incoming.TryPeek(out _)) break;
                if (!await next.ConfigureAwait(false)) break;
                while (rpc.Incoming.TryRead(out var message))
                {
                    if (message.IsRequest)
                    {
                        await AnswerAsync(rpc, message, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (message.Method != "session/update" || message.Params["update"] is not JsonObject update) continue;
                    switch (update["sessionUpdate"]?.GetValue<string>())
                    {
                        case "agent_message_chunk":
                            var chunk = (update["content"] as JsonObject)?["text"]?.GetValue<string>();
                            if (chunk is { Length: > 0 }) yield return new AssistantText(chunk);
                            break;
                        case "tool_call":
                            var id = update["toolCallId"]?.GetValue<string>() ?? "";
                            var name = ToolName(update);
                            tools[id] = name;
                            yield return new AssistantToolStarted(id, name);
                            if (Finished(update) is { } done) yield return new AssistantToolFinished(id, name, done);
                            break;
                        case "tool_call_update" when Finished(update) is { } failed:
                            var callId = update["toolCallId"]?.GetValue<string>() ?? "";
                            yield return new AssistantToolFinished(callId, tools.GetValueOrDefault(callId, "tool"), failed);
                            break;
                    }
                }
            }

            JsonNode? result;
            try
            {
                result = await turn.ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new InvalidOperationException($"{provider.Name} stopped: {_process?.ErrorTail}");
            }
            ct.ThrowIfCancellationRequested();
            if (result?["stopReason"]?.GetValue<string>() is "refusal") yield return new AssistantError($"{provider.Name} declined this request.");
            else if (result?["stopReason"]?.GetValue<string>() is "max_tokens") yield return new AssistantError("The answer was cut off (too long).");
        }

        /// <summary>true = failed, false = done, null = still running.</summary>
        private static bool? Finished(JsonObject update) => update["status"]?.GetValue<string>() switch
        {
            "completed" => false,
            "failed" => true,
            _ => null,
        };

        /// <summary>The Cuebot tool a call runs, also when the app wraps MCP calls (Grok's use_tool names it in its input).</summary>
        private string ToolName(JsonObject update)
        {
            var title = update["title"]?.GetValue<string>() ?? "tool";
            var input = update["rawInput"]?.ToJsonString() ?? "";
            return _cuebotTools.FirstOrDefault(t => title.Contains(t, StringComparison.Ordinal))
                   ?? _cuebotTools.FirstOrDefault(t => input.Contains(t, StringComparison.Ordinal))
                   ?? title;
        }

        /// <summary>Permission requests: Cuebot's own tools yes, anything else no. No file system or terminal is offered.</summary>
        private Task AnswerAsync(JsonLineRpc rpc, RpcIncoming request, CancellationToken ct)
        {
            if (request.Method != "session/request_permission") return rpc.RespondErrorAsync(request.Id, -32601, "Not available in Cuebot's assistant", ct);
            var call = request.Params["toolCall"] as JsonObject ?? [];
            var title = call["title"]?.GetValue<string>() ?? "";
            var kind = call["kind"]?.GetValue<string>() ?? "other";
            var cuebot = kind is "other" or "read" or "fetch" or "think" && _cuebotTools.Any(t => title.Contains(t, StringComparison.Ordinal));
            var options = request.Params["options"] as JsonArray ?? [];
            var wanted = cuebot ? new[] { "allow_once", "allow_always" } : ["reject_once", "reject_always"];
            var option = options.OfType<JsonObject>().FirstOrDefault(o => wanted.Contains(o["kind"]?.GetValue<string>()));
            return rpc.RespondAsync(request.Id, option?["optionId"] is { } id
                ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = id.DeepClone() } }
                : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }, ct);
        }

        private async Task<JsonLineRpc> ConnectAsync(CancellationToken ct)
        {
            if (_rpc is { CloseReason: null } open) return open;
            await DisposeAsync().ConfigureAwait(false);
            if (provider.Executable is not { } executable) throw new InvalidOperationException($"{provider.Name} is not installed.");
            if (context.Mcp is not { } mcp) throw new InvalidOperationException($"Turn on the MCP server (Servers page): {provider.Name} reaches Cuebot through it.");
            string[] args = [.. provider.Definition.Arguments, .. context.Model.Length > 0 ? [provider.Definition.ModelFlag, context.Model] : Array.Empty<string>()];
            var launch = _launch = provider.Definition.Prepare?.Invoke(context);
            var environment = new Dictionary<string, string>(provider.Definition.Environment);
            foreach (var (key, value) in launch?.Environment ?? new Dictionary<string, string>()) environment[key] = value;
            _process = CliProcess.Start(executable, args, context.WorkingDirectory, environment);
            var rpc = _rpc = new JsonLineRpc(_process.Output, _process.Input);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var init = await rpc.RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["clientCapabilities"] = new JsonObject
                    {
                        ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                        ["terminal"] = false,
                    },
                    ["clientInfo"] = new JsonObject { ["name"] = "cuebot", ["title"] = "Cuebot", ["version"] = Cuebot.Core.AppVersion.Current },
                }, timeout.Token).ConfigureAwait(false);
                if (init?["agentCapabilities"]?["mcpCapabilities"]?["http"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException($"This version of {provider.Name} can not connect to MCP servers over HTTP; update it.");
                if (launch?.Authenticate == true && (init?["authMethods"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>() is { } method)
                    await rpc.RequestAsync("authenticate", new JsonObject { ["methodId"] = method }, timeout.Token).ConfigureAwait(false);
                var newSession = new JsonObject
                {
                    ["cwd"] = context.WorkingDirectory,
                    ["mcpServers"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "http",
                        ["name"] = McpEndpoint.ServerName,
                        ["url"] = mcp.Url,
                        ["headers"] = new JsonArray(new JsonObject { ["name"] = "Authorization", ["value"] = "Bearer " + mcp.Token }),
                    }),
                };
                if (launch?.SessionMeta is { } meta) newSession["_meta"] = meta.DeepClone();
                var session = await rpc.RequestAsync("session/new", newSession, timeout.Token).ConfigureAwait(false);
                _sessionId = session?["sessionId"]?.GetValue<string>() ?? throw new InvalidOperationException($"{provider.Name} did not start a session.");
                return rpc;
            }
            catch (Exception ex) when (ex is RpcException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                var tail = _process.ErrorTail;
                throw new InvalidOperationException($"{provider.Name} did not start: {ex.Message}{(tail.Length > 0 ? " - " + Last(tail) : "")}");
            }
        }

        private static string Last(string text) => text.Length > 400 ? text[^400..] : text;

        public async ValueTask DisposeAsync()
        {
            if (_rpc is { } rpc) await rpc.DisposeAsync().ConfigureAwait(false);
            if (_process is { } process) await process.DisposeAsync().ConfigureAwait(false);
            _launch?.Dispose();
            _rpc = null;
            _process = null;
            _launch = null;
            _sessionId = null;
            _instructed = false;
        }
    }
}
