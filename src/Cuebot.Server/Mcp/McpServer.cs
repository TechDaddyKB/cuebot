using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Server.Api;
using Microsoft.AspNetCore.Http;

namespace Cuebot.Server.Mcp;

/// <summary>A JSON-RPC error answered to the MCP client.</summary>
public sealed class McpException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ResourceNotFound = -32002;

    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;
}

/// <summary>
/// Cuebot's Model Context Protocol server (JSON-RPC 2.0, protocol revision 2025-06-18; 2025-03-26 and 2024-11-05
/// clients are answered in their revision). Offers tools, resources and prompts over the Streamable HTTP transport at
/// <c>/mcp</c> (see <see cref="HandleHttpAsync"/>); <see cref="McpStdioBridge"/> carries stdio clients to it.
/// </summary>
public sealed class McpServer
{
    public const string LatestProtocolVersion = "2025-06-18";
    public const string SessionHeader = "Mcp-Session-Id";
    public const string ProtocolHeader = "MCP-Protocol-Version";
    private const string LogSource = "MCP";
    private const int MaxSessions = 64;

    /// <summary>Revisions this server can speak; an unknown requested revision is answered with the latest.</summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly ServerServices _services;
    private readonly Func<McpServerSettings> _settings;
    private readonly Dictionary<string, McpTool> _tools;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public McpServer(ApiOperations operations, ServerServices services, Func<McpServerSettings> settings, McpConfirmations? confirmations = null)
    {
        _services = services;
        _settings = settings;
        Tools = McpTools.Create(operations);
        _tools = Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Catalog = new McpCatalog(operations, services);
        Confirmations = confirmations ?? new McpConfirmations();
    }

    public IReadOnlyList<McpTool> Tools { get; }
    public McpCatalog Catalog { get; }
    public McpConfirmations Confirmations { get; }
    public int SessionCount => _sessions.Count;

    public const string Instructions =
        "Cuebot is a streaming bot (Streamer.bot / Speaker.bot replacement). Actions = triggers + sub-actions. Before building an action, "
        + "look up trigger and sub-action types with list_trigger_types / list_subaction_types and their parameters with "
        + "describe_subaction_type; read cuebot://docs/actions and cuebot://docs/variables. All parameter values are strings. "
        + "Ask the user before deleting or overwriting anything; destructive tools may answer with a confirmationToken that must be "
        + "passed back after the user agrees.";

    // ---- JSON-RPC --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Handles one message or a batch and returns the response(s), or null when nothing is to be answered
    /// (notifications and responses).
    /// </summary>
    public async Task<JsonNode?> HandleAsync(JsonNode? message, CancellationToken ct = default)
    {
        if (message is JsonArray batch)
        {
            if (batch.Count == 0) return Error(null, McpException.InvalidRequest, "Empty batch");
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                if (await HandleOneAsync(item, ct).ConfigureAwait(false) is { } response) responses.Add(response);
            }
            return responses.Count == 0 ? null : responses;
        }
        return await HandleOneAsync(message, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> HandleOneAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is not JsonObject request || request["jsonrpc"]?.GetValueKind() != JsonValueKind.String
            || request["jsonrpc"]!.GetValue<string>() != "2.0")
        {
            return Error(null, McpException.InvalidRequest, "Not a JSON-RPC 2.0 message");
        }
        var hasId = request.ContainsKey("id");
        var id = request["id"]?.DeepClone();
        if (request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            // A response to a server request (Cuebot sends none) or garbage.
            return hasId && !request.ContainsKey("result") && !request.ContainsKey("error")
                ? Error(id, McpException.InvalidRequest, "Missing method")
                : null;
        }
        var parameters = request["params"] as JsonObject ?? [];
        if (!hasId)
        {
            // Notifications: notifications/initialized, notifications/cancelled, ... need no answer.
            return null;
        }

        try
        {
            var result = await DispatchAsync(method, parameters, ct).ConfigureAwait(false);
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpException ex)
        {
            return Error(id, ex.Code, ex.Message, ex.ErrorData);
        }
        catch (ApiException ex)
        {
            return Error(id, McpException.InvalidParams, ex.Message, ex.Details?.DeepClone());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _services.Log.Error(LogSource, $"{method} failed", ex);
            return Error(id, McpException.InternalError, ex.Message);
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject p, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize": return Initialize(p);
            case "ping": return new JsonObject();
            case "tools/list": return new JsonObject { ["tools"] = new JsonArray([.. Tools.Select(t => (JsonNode)t.ToJson())]) };
            case "tools/call": return await CallToolAsync(p, ct).ConfigureAwait(false);
            case "resources/list": return Catalog.ListResources();
            case "resources/templates/list": return McpCatalog.ListResourceTemplates();
            case "resources/read":
            {
                var uri = Param(p, "uri");
                try
                {
                    return Catalog.ReadResource(uri) ?? throw new McpException(McpException.ResourceNotFound, "Resource not found", new JsonObject { ["uri"] = uri });
                }
                catch (ApiException ex) when (ex.Status == 404)
                {
                    throw new McpException(McpException.ResourceNotFound, ex.Message, new JsonObject { ["uri"] = uri });
                }
            }
            case "prompts/list": return McpCatalog.ListPrompts();
            case "prompts/get":
            {
                var name = Param(p, "name");
                return Catalog.GetPrompt(name, p["arguments"] as JsonObject ?? [])
                       ?? throw new McpException(McpException.InvalidParams, $"Unknown prompt '{name}'");
            }
            default: throw new McpException(McpException.MethodNotFound, $"Method not found: {method}");
        }
    }

    private static string Param(JsonObject p, string name) =>
        p[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : throw new McpException(McpException.InvalidParams, $"'{name}' is required");

    private static JsonObject Initialize(JsonObject p)
    {
        var requested = p["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var version = SupportedProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "cuebot",
                ["title"] = "Cuebot",
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
            ["instructions"] = Instructions,
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject p, CancellationToken ct)
    {
        var name = Param(p, "name");
        if (!_tools.TryGetValue(name, out var tool)) throw new McpException(McpException.InvalidParams, $"Unknown tool: {name}");
        var args = p["arguments"] as JsonObject ?? [];
        _services.Log.Info(LogSource, $"Tool call: {name}");   // never the argument values
        try
        {
            if (tool.Confirmation is { } describe && _settings().RequireConfirmation)
            {
                var token = McpTools.OptStr(args, McpConfirmations.ArgumentName);
                if (string.IsNullOrEmpty(token))
                {
                    var summary = describe(args);
                    var issued = Confirmations.Issue(name, args);
                    return ToolResult(new JsonObject
                    {
                        ["confirmationRequired"] = true,
                        ["action"] = summary,
                        ["confirmationToken"] = issued,
                        ["expiresInSeconds"] = (int)McpConfirmations.Lifetime.TotalSeconds,
                        ["instructions"] = $"Nothing was done yet. Ask the user to confirm: \"{summary}\". If they agree, call {name} again with "
                                           + $"the same arguments plus \"{McpConfirmations.ArgumentName}\": \"{issued}\".",
                    }, isError: false);
                }
                if (!Confirmations.TryConsume(token, name, args))
                {
                    return ToolResult(new JsonObject
                    {
                        ["error"] = "The confirmation token is invalid, expired, already used or was issued for different arguments. "
                                    + $"Call {name} again without {McpConfirmations.ArgumentName} to get a new one.",
                    }, isError: true);
                }
            }
            var result = await tool.Invoke(args, ct).ConfigureAwait(false);
            return ToolResult(result, isError: false);
        }
        catch (ApiException ex)
        {
            return ToolResult(ex.ToJson(), isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Error(LogSource, $"Tool {name} failed", ex);
            return ToolResult(new JsonObject { ["error"] = ex.Message }, isError: true);
        }
    }

    private static JsonObject ToolResult(JsonNode result, bool isError)
    {
        var json = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString() }),
            ["isError"] = isError,
        };
        if (result is JsonObject structured) json["structuredContent"] = structured.DeepClone();
        return json;
    }

    private static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error };
    }

    // ---- Streamable HTTP transport ---------------------------------------------------------------------------------

    private sealed class Session(string id, string version)
    {
        public string Id { get; } = id;
        public string ProtocolVersion { get; } = version;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// POST: one JSON-RPC message (or batch) → JSON response, or 202 for notifications. An initialize response carries
    /// a new <c>Mcp-Session-Id</c>; later requests may send it (an unknown one is 404, so the client re-initializes).
    /// DELETE ends a session. GET (server-initiated stream) is not offered: 405. Auth and origin are checked by
    /// <see cref="ApiServer"/> before this runs.
    /// </summary>
    internal async Task HandleHttpAsync(HttpContext context, CancellationToken ct)
    {
        var request = context.Request;
        var sessionId = request.Headers[SessionHeader].ToString();
        Session? session = null;
        if (sessionId.Length > 0 && !_sessions.TryGetValue(sessionId, out session))
        {
            await ApiServer.WriteJsonAsync(context, 404, Error(null, McpException.InvalidRequest, "Unknown or expired session; initialize again")).ConfigureAwait(false);
            return;
        }
        if (session is not null) session.LastSeen = DateTimeOffset.UtcNow;

        if (HttpMethods.IsDelete(request.Method))
        {
            if (session is not null) _sessions.TryRemove(session.Id, out _);
            context.Response.StatusCode = session is null ? 400 : 204;
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            context.Response.Headers.Allow = "POST, DELETE";
            await ApiServer.WriteJsonAsync(context, 405, new JsonObject { ["error"] = "Use POST (this server does not open server-to-client streams)" }).ConfigureAwait(false);
            return;
        }

        var protocol = request.Headers[ProtocolHeader].ToString();
        if (protocol.Length > 0 && !SupportedProtocolVersions.Contains(protocol))
        {
            await ApiServer.WriteJsonAsync(context, 400, Error(null, McpException.InvalidRequest, $"Unsupported {ProtocolHeader}: {protocol}")).ConfigureAwait(false);
            return;
        }

        JsonNode? message;
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            message = JsonNode.Parse(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            await ApiServer.WriteJsonAsync(context, 400, Error(null, McpException.ParseError, "Parse error")).ConfigureAwait(false);
            return;
        }

        var response = await HandleAsync(message, ct).ConfigureAwait(false);
        if (IsInitialize(message) && response is JsonObject { } init && init["result"]?["protocolVersion"] is JsonValue version)
        {
            var created = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), version.GetValue<string>());
            PruneSessions();
            _sessions[created.Id] = created;
            context.Response.Headers[SessionHeader] = created.Id;
        }
        if (response is null)
        {
            context.Response.StatusCode = 202;
            return;
        }

        var accept = request.Headers.Accept.ToString();
        if (accept.Length > 0 && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) && !accept.Contains("*/*", StringComparison.Ordinal)
            && accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync($"event: message\ndata: {response.ToJsonString()}\n\n", ct).ConfigureAwait(false);
            return;
        }
        await ApiServer.WriteJsonAsync(context, 200, response).ConfigureAwait(false);
    }

    private static bool IsInitialize(JsonNode? message) =>
        message is JsonObject o && o["method"] is JsonValue m && m.TryGetValue<string>(out var s) && s == "initialize";

    private void PruneSessions()
    {
        if (_sessions.Count < MaxSessions) return;
        foreach (var old in _sessions.Values.OrderBy(s => s.LastSeen).Take(_sessions.Count - MaxSessions + 1).ToArray())
            _sessions.TryRemove(old.Id, out _);
    }
}
