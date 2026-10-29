using System.Text.Json.Nodes;
using Cuebot.Server.Mcp;

namespace Cuebot.Assistant;

/// <summary>A tool the AI may call: one of Cuebot's MCP tools.</summary>
public sealed record AssistantToolDefinition(string Name, string Description, JsonObject InputSchema);

/// <summary>A tool call's answer for the AI, as JSON text.</summary>
public sealed record AssistantToolResult(string Content, bool IsError);

/// <summary>
/// Cuebot's MCP tools called in process (API-key providers). Calls go through <see cref="McpServer"/>, so tool-call
/// logging and the confirmation rule for destructive tools are the same as for MCP clients: when a tool answers with a
/// confirmation token, <paramref name="confirm"/> asks the streamer and the call is repeated with the token only if
/// they approve.
/// </summary>
public sealed class AssistantTools(McpServer mcp)
{
    /// <summary>
    /// MCP clients read Cuebot's guides as resources; API-key providers only have tools, so this one reads them.
    /// </summary>
    public const string ReadDocTool = "read_cuebot_doc";

    private static readonly string[] Docs = [McpCatalog.ActionsDocUri, McpCatalog.VariablesUri];

    public IReadOnlyList<AssistantToolDefinition> List() =>
    [
        .. mcp.Tools.Select(t => new AssistantToolDefinition(t.Name, t.Description, (JsonObject)t.InputSchema.DeepClone())),
        new AssistantToolDefinition(ReadDocTool,
            $"Read one of Cuebot's guides: {McpCatalog.ActionsDocUri} (how actions, triggers and sub-actions work) or "
            + $"{McpCatalog.VariablesUri} (variables and %arguments%). Read them before building actions.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["uri"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Docs.Select(d => (JsonNode)d)]) },
                },
                ["required"] = new JsonArray("uri"),
                ["additionalProperties"] = false,
            }),
    ];

    /// <param name="confirm">Asks the streamer about a destructive call (tool, summary) and answers true to go ahead.</param>
    public async Task<AssistantToolResult> CallAsync(string name, JsonObject arguments, Func<string, string, Task<bool>> confirm,
        CancellationToken ct = default)
    {
        if (name == ReadDocTool) return await ReadDocAsync(arguments, ct).ConfigureAwait(false);
        var result = await CallOnceAsync(name, arguments, ct).ConfigureAwait(false);
        if (result["structuredContent"] is JsonObject { } structured && structured["confirmationRequired"]?.GetValue<bool>() == true)
        {
            var summary = structured["action"]?.GetValue<string>() ?? name;
            if (!await confirm(name, summary).ConfigureAwait(false))
                return new AssistantToolResult(new JsonObject { ["declined"] = true, ["message"] = $"The streamer declined: {summary}. Nothing was done." }
                    .ToJsonString(), IsError: false);
            var confirmed = (JsonObject)arguments.DeepClone();
            confirmed[McpConfirmations.ArgumentName] = structured["confirmationToken"]?.DeepClone();
            result = await CallOnceAsync(name, confirmed, ct).ConfigureAwait(false);
        }
        var text = result["content"] is JsonArray { Count: > 0 } content
            ? string.Concat(content.Select(c => c?["text"]?.GetValue<string>()))
            : result.ToJsonString();
        return new AssistantToolResult(text, result["isError"]?.GetValue<bool>() == true);
    }

    private async Task<AssistantToolResult> ReadDocAsync(JsonObject arguments, CancellationToken ct)
    {
        var uri = arguments["uri"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        if (!Docs.Contains(uri, StringComparer.OrdinalIgnoreCase))
            return new AssistantToolResult(new JsonObject { ["error"] = $"Unknown guide. Use one of: {string.Join(", ", Docs)}" }.ToJsonString(), true);
        var response = await mcp.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/read",
            ["params"] = new JsonObject { ["uri"] = uri },
        }, ct).ConfigureAwait(false);
        var text = response?["result"]?["contents"] is JsonArray { Count: > 0 } contents ? contents[0]?["text"]?.GetValue<string>() : null;
        return text is null
            ? new AssistantToolResult(new JsonObject { ["error"] = response?["error"]?["message"]?.DeepClone() ?? "Not found" }.ToJsonString(), true)
            : new AssistantToolResult(text, false);
    }

    private async Task<JsonObject> CallOnceAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var response = await mcp.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() },
        }, ct).ConfigureAwait(false);
        if (response?["error"] is JsonObject error)
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new JsonObject { ["error"] = error["message"]?.DeepClone() }.ToJsonString() }),
                ["isError"] = true,
            };
        return response?["result"] as JsonObject ?? new JsonObject { ["isError"] = true };
    }
}

/// <summary>Runs one tool call inside a session's update stream: started, any confirmation, finished.</summary>
internal static class ToolCalls
{
    /// <param name="result">Receives the tool's answer once the stream ends.</param>
    public static async IAsyncEnumerable<AssistantUpdate> RunAsync(AssistantTools tools, string callId, string name, JsonObject arguments,
        System.Runtime.CompilerServices.StrongBox<AssistantToolResult?> result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new AssistantToolStarted(callId, name);
        var confirmations = System.Threading.Channels.Channel.CreateUnbounded<AssistantConfirmation>();
        AssistantConfirmation? pending = null;
        using var cancel = ct.Register(() => pending?.Deny());   // a stopped turn must not leave the call waiting
        var call = tools.CallAsync(name, arguments, (tool, summary) =>
        {
            var confirmation = new AssistantConfirmation(tool, summary);
            pending = confirmation;
            confirmations.Writer.TryWrite(confirmation);
            return confirmation.Answer;
        }, ct);
        while (true)
        {
            var waiting = confirmations.Reader.WaitToReadAsync(ct).AsTask();
            if (await Task.WhenAny(call, waiting).ConfigureAwait(false) == call) break;
            await waiting.ConfigureAwait(false);   // surfaces cancellation
            while (confirmations.Reader.TryRead(out var confirmation)) yield return confirmation;
        }
        result.Value = await call.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();   // a stop while waiting for the streamer ends the turn, not just the call
        yield return new AssistantToolFinished(callId, name, result.Value.IsError);
    }
}
