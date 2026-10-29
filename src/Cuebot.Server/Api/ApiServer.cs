using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Server.Hosting;
using Cuebot.Server.Mcp;
using Microsoft.AspNetCore.Http;

namespace Cuebot.Server.Api;

/// <summary>
/// The REST API (<c>/api/v1/...</c>) and the MCP Streamable HTTP endpoint (<c>/mcp</c>) on one Kestrel listener.
/// Every request needs the bearer token; browser origins follow the same rules as the other servers. Each half answers
/// 404 while it is switched off.
/// </summary>
public sealed class ApiServer : IAsyncDisposable
{
    public const string Prefix = "/api/v1";
    public const string McpPath = "/mcp";
    private const int MaxEventStreams = 16;

    private readonly Func<ServerSettings> _settings;
    private readonly ServerServices _services;
    private readonly CancellationTokenSource _stopping = new();
    private KestrelListener? _listener;
    private int _eventStreams;

    public ApiServer(Func<ServerSettings> settings, ServerServices services, ApiOperations operations, McpServer mcp)
    {
        _settings = settings;
        _services = services;
        Operations = operations;
        Mcp = mcp;
    }

    public ApiOperations Operations { get; }
    public McpServer Mcp { get; }
    public int Port => _listener?.Port ?? 0;
    public bool IsRunning => _listener is not null;

    public async Task StartAsync(IPAddress address, CancellationToken ct = default)
    {
        if (_listener is not null) return;
        _listener = await KestrelListener.StartAsync(address, _settings().Api.Port, HandleAsync, ct).ConfigureAwait(false);
    }

    internal async Task HandleAsync(HttpContext context)
    {
        var settings = _settings();
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var isMcp = path.TrimEnd('/').Equals(McpPath, StringComparison.OrdinalIgnoreCase);
        var isApi = path.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase);
        context.Response.Headers.CacheControl = "no-store";

        if ((!isMcp || !settings.Mcp.Enabled) && (!isApi || !settings.Api.Enabled))
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "Not found" }).ConfigureAwait(false);
            return;
        }

        // Origins: as the other servers (local pages and native clients yes, other websites only when allowed).
        var origin = request.Headers.Origin.ToString();
        var originAllowed = NetworkPolicy.IsOriginAllowed(origin, settings.AllowedOrigins, request.Host.Value);
        if (origin.Length == 0 && string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase)
            && !settings.AllowedOrigins.Any(a => a.Trim() == "*"))
        {
            originAllowed = false;
        }
        if (originAllowed && origin.Length > 0)
        {
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
            context.Response.Headers.AccessControlExposeHeaders = "Mcp-Session-Id, MCP-Protocol-Version";
        }
        if (HttpMethods.IsOptions(request.Method))
        {
            context.Response.StatusCode = originAllowed ? 204 : 403;
            if (originAllowed)
            {
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, Accept, Mcp-Session-Id, MCP-Protocol-Version, Last-Event-ID";
            }
            return;
        }
        if (!originAllowed)
        {
            await WriteJsonAsync(context, 403, new JsonObject { ["error"] = "Origin not allowed" }).ConfigureAwait(false);
            return;
        }

        var token = ApiToken.FromAuthorizationHeader(request.Headers.Authorization.ToString());
        // An Assistant conversation's own token works on /mcp only.
        var assistant = isMcp ? Mcp.AssistantFor(token) : null;
        if (assistant is not null) context.Items[McpServer.ConfirmItem] = assistant;
        else if (!ApiToken.Matches(settings.Api.Token, token))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Cuebot\"";
            await WriteJsonAsync(context, 401, new JsonObject
            {
                ["error"] = token is null ? "Missing bearer token (Authorization: Bearer <token> from the Servers page)" : "Wrong token",
            }).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        if (isMcp)
        {
            await Mcp.HandleHttpAsync(context, cts.Token).ConfigureAwait(false);
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketAsync(context, path[Prefix.Length..].Trim('/'), cts.Token).ConfigureAwait(false);
            return;
        }

        try
        {
            await RouteAsync(context, path[Prefix.Length..].Trim('/'), cts.Token).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            await WriteJsonAsync(context, ex.Status, ex.ToJson()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Client went away or the server is stopping.
        }
        catch (Exception ex)
        {
            _services.Log.Error("API", $"{request.Method} {path} failed", ex);
            if (!context.Response.HasStarted)
                await WriteJsonAsync(context, 500, new JsonObject { ["error"] = "Internal error: " + ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task RouteAsync(HttpContext context, string route, CancellationToken ct)
    {
        var method = context.Request.Method.ToUpperInvariant();
        var s = route.Length == 0 ? [] : route.Split('/');
        var ops = Operations;
        var q = context.Request.Query;
        string? Q(string name) => q.TryGetValue(name, out var v) ? v.ToString() : null;
        bool? QBool(string name) => bool.TryParse(Q(name), out var b) ? b : null;
        int QInt(string name, int fallback) => int.TryParse(Q(name), out var i) ? i : fallback;

        JsonNode? result;
        var status = 200;
        switch (s.Length > 0 ? s[0].ToLowerInvariant() : "")
        {
            case "status" when s.Length == 1 && method == "GET":
                result = ops.Status();
                break;
            case "openapi.json" when s.Length == 1 && method == "GET":
                result = OpenApiDocument.Build(BaseUrl(context), ops.Status()["app"]!["version"]!.GetValue<string>());
                break;

            case "actions" when s.Length == 1 && method == "GET":
                result = ops.ListActions(Q("group"), Q("search"));
                break;
            case "actions" when s.Length == 1 && method == "POST":
                result = await ops.CreateActionAsync(await BodyAsync(context).ConfigureAwait(false)).ConfigureAwait(false);
                status = 201;
                break;
            case "actions" when s.Length == 2:
                switch (method)
                {
                    case "GET": result = ops.GetAction(s[1]); break;
                    case "PUT": result = await ops.UpdateActionAsync(s[1], await BodyAsync(context).ConfigureAwait(false), merge: false).ConfigureAwait(false); break;
                    case "PATCH": result = await ops.UpdateActionAsync(s[1], await BodyAsync(context).ConfigureAwait(false), merge: true).ConfigureAwait(false); break;
                    case "DELETE": result = await ops.DeleteActionAsync(s[1]).ConfigureAwait(false); break;
                    default: throw MethodNotAllowed();
                }
                break;
            case "actions" when s.Length == 3 && method == "POST":
                switch (s[2].ToLowerInvariant())
                {
                    case "run":
                        var body = await BodyAsync(context).ConfigureAwait(false) as JsonObject ?? [];
                        var wait = body["wait"] is JsonValue w && w.TryGetValue<bool>(out var wv) && wv;
                        var timeout = body["timeoutSeconds"] is JsonValue t && t.TryGetValue<double>(out var tv) ? tv : 30;
                        result = await ops.RunActionAsync(s[1], body["args"], wait, timeout, "API", ct).ConfigureAwait(false);
                        status = wait && result["completed"]?.GetValue<bool>() == true ? 200 : 202;
                        break;
                    case "enable": result = await ops.SetActionEnabledAsync(s[1], true).ConfigureAwait(false); break;
                    case "disable": result = await ops.SetActionEnabledAsync(s[1], false).ConfigureAwait(false); break;
                    default: throw NotFound();
                }
                break;

            case "triggers" when s.Length == 1 && method == "GET": result = ops.ListTriggers(Q("category"), Q("search")); break;
            case "triggers" when s.Length == 2 && method == "GET": result = ops.DescribeTrigger(s[1]); break;
            case "subactions" when s.Length == 1 && method == "GET": result = ops.ListSubActions(Q("category"), Q("search")); break;
            case "subactions" when s.Length == 2 && method == "GET": result = ops.DescribeSubAction(s[1]); break;
            case "scriptfunctions" when s.Length == 1 && method == "GET": result = ops.ListScriptFunctions(Q("search")); break;

            case "commands" when s.Length == 1 && method == "GET": result = ops.ListCommands(); break;
            case "commands" when s.Length == 1 && method == "POST":
                result = ops.CreateCommand(await BodyAsync(context).ConfigureAwait(false));
                status = 201;
                break;
            case "commands" when s.Length == 2:
                result = method switch
                {
                    "GET" => ops.GetCommand(s[1]),
                    "PUT" => ops.UpdateCommand(s[1], await BodyAsync(context).ConfigureAwait(false), merge: false),
                    "PATCH" => ops.UpdateCommand(s[1], await BodyAsync(context).ConfigureAwait(false), merge: true),
                    "DELETE" => ops.DeleteCommand(s[1]),
                    _ => throw MethodNotAllowed(),
                };
                break;

            case "timers" when s.Length == 1 && method == "GET": result = ops.ListTimers(); break;
            case "timers" when s.Length == 1 && method == "POST":
                result = await ops.CreateTimerAsync(await BodyAsync(context).ConfigureAwait(false)).ConfigureAwait(false);
                status = 201;
                break;
            case "timers" when s.Length == 2:
                switch (method)
                {
                    case "GET": result = ops.GetTimer(s[1]); break;
                    case "PUT": result = await ops.UpdateTimerAsync(s[1], await BodyAsync(context).ConfigureAwait(false), merge: false).ConfigureAwait(false); break;
                    case "PATCH": result = await ops.UpdateTimerAsync(s[1], await BodyAsync(context).ConfigureAwait(false), merge: true).ConfigureAwait(false); break;
                    case "DELETE": result = await ops.DeleteTimerAsync(s[1]).ConfigureAwait(false); break;
                    default: throw MethodNotAllowed();
                }
                break;
            case "timers" when s.Length == 3 && method == "POST" && s[2].Equals("state", StringComparison.OrdinalIgnoreCase):
            {
                var body = await BodyAsync(context).ConfigureAwait(false) as JsonObject ?? [];
                bool? enabled = body["enabled"] is JsonValue e && e.TryGetValue<bool>(out var ev) ? ev : null;
                int? interval = body["intervalSeconds"] is JsonValue i && i.TryGetValue<int>(out var iv) ? iv : null;
                result = ops.SetTimerState(s[1], enabled, interval);
                break;
            }

            case "globals" when s.Length == 1 && method == "GET": result = ops.GetGlobals(QBool("persisted")); break;
            case "globals" when s.Length == 2:
                switch (method)
                {
                    case "GET": result = ops.GetGlobal(s[1]); break;
                    case "PUT":
                        var body = await BodyAsync(context).ConfigureAwait(false) as JsonObject
                                   ?? throw ApiException.BadRequest("Body must be {\"value\": ..., \"persisted\": true}");
                        var persisted = body["persisted"] is not JsonValue p || !p.TryGetValue<bool>(out var pv) || pv;
                        result = ops.SetGlobal(s[1], body["value"], persisted);
                        break;
                    case "DELETE": result = ops.DeleteGlobal(s[1]); break;
                    default: throw MethodNotAllowed();
                }
                break;

            case "history" when s.Length == 1 && method == "GET":
                result = ops.History(QInt("limit", 50), Q("action"), QBool("includeArgs") ?? false);
                break;

            case "chat" when s.Length == 1 && method == "GET": result = ops.Chat(QInt("limit", 50)); break;
            case "chat" when s.Length == 1 && method == "POST":
            {
                var body = await BodyAsync(context).ConfigureAwait(false) as JsonObject ?? [];
                result = await ops.SendChatAsync(Text(body["platform"]), Text(body["message"]), ct).ConfigureAwait(false);
                break;
            }

            case "import" when s.Length == 2 && method == "POST" && s[1].Equals("preview", StringComparison.OrdinalIgnoreCase):
            {
                var body = await BodyAsync(context).ConfigureAwait(false) as JsonObject ?? [];
                result = ops.PreviewImport(Text(body["data"]));
                break;
            }
            case "import" when s.Length == 2 && method == "POST" && s[1].Equals("apply", StringComparison.OrdinalIgnoreCase):
            {
                var body = await BodyAsync(context).ConfigureAwait(false) as JsonObject ?? [];
                result = await ops.ApplyImportAsync(Text(body["data"]), Text(body["mode"]), body["items"] as JsonArray).ConfigureAwait(false);
                break;
            }
            case "export" when s.Length == 1 && method == "GET":
                result = ops.Export(Values("actions"), Values("commands"), Values("timers"), Values("globals"), QBool("includeDependencies") ?? true, Q("name"));
                break;

            case "events" when s.Length == 1 && method == "GET":
                await StreamEventsAsync(context, Q("types"), ct).ConfigureAwait(false);
                return;

            case var name when s.Length == 1 && FindWebSocketRoute(name) is not null:
                throw new ApiException(426, $"/api/v1/{name} is a WebSocket endpoint; connect with a WebSocket client");

            default:
                throw IsKnownResource(s) ? MethodNotAllowed() : NotFound();
        }
        await WriteJsonAsync(context, status, result).ConfigureAwait(false);

        IEnumerable<string>? Values(string name) => q.TryGetValue(name, out var v) ? v.Where(x => x is not null).Select(x => x!) : null;
    }

    private static readonly HashSet<string> Resources = new(
        ["status", "openapi.json", "actions", "triggers", "subactions", "scriptfunctions", "commands", "timers", "globals", "history", "chat", "import", "export", "events"],
        StringComparer.OrdinalIgnoreCase);

    private static bool IsKnownResource(string[] segments) => segments.Length is > 0 and <= 3 && Resources.Contains(segments[0]);
    private static ApiException NotFound() => ApiException.NotFound("No such endpoint (see GET /api/v1/openapi.json)");
    private static ApiException MethodNotAllowed() => new(405, "Method not allowed (see GET /api/v1/openapi.json)");

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString();

    private static string BaseUrl(HttpContext context) => $"http://{context.Request.Host.Value}{Prefix}";

    /// <summary>The JSON body, or null when empty. Invalid JSON is a 400.</summary>
    private static async Task<JsonNode?> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest("Invalid JSON: " + ex.Message);
        }
    }

    internal static Task WriteJsonAsync(HttpContext context, int status, JsonNode? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(body?.ToJsonString() ?? "null");
    }

    // ---- Module WebSockets (/api/v1/{route}) ------------------------------------------------------------------------

    private IApiWebSocketRoute? FindWebSocketRoute(string route) =>
        _services.WebSocketRoutes.FirstOrDefault(r => string.Equals(r.Name, route, StringComparison.OrdinalIgnoreCase));

    /// <summary>Upgrades an authorized request for a registered <see cref="IApiWebSocketRoute"/> and runs its connection.</summary>
    private async Task HandleWebSocketAsync(HttpContext context, string route, CancellationToken ct)
    {
        var handler = route.Contains('/') ? null : FindWebSocketRoute(route);
        if (handler is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "No WebSocket endpoint here" }).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = new WebSocketConnection(socket, $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}");
        try
        {
            await handler.OnOpenAsync(connection).ConfigureAwait(false);
            await connection.ReceiveAsync(message => handler.OnMessageAsync(connection, message), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Error("API", $"WebSocket /api/v1/{handler.Name} failed", ex);
        }
        finally
        {
            handler.OnClose(connection);
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- Server-Sent Events ----------------------------------------------------------------------------------------

    private async Task StreamEventsAsync(HttpContext context, string? types, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _eventStreams) > MaxEventStreams)
        {
            Interlocked.Decrement(ref _eventStreams);
            await WriteJsonAsync(context, 429, new JsonObject { ["error"] = "Too many open event streams" }).ConfigureAwait(false);
            return;
        }
        var filters = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        long id = 0;
        void Push(string type, DateTimeOffset time, JsonObject args)
        {
            if (filters.Length > 0 && !filters.Any(f => Matches(f, type))) return;
            var data = new JsonObject { ["type"] = type, ["time"] = time.ToString("o"), ["args"] = args };
            channel.Writer.TryWrite($"id: {Interlocked.Increment(ref id)}\nevent: {type}\ndata: {data.ToJsonString()}\n\n");
        }
        void OnEvent(BotEvent e) => Push(e.Type, e.Time, ApiOperations.Args(e.Args));
        void OnRun(ActionRunRecord r) => Push("action.runCompleted", DateTimeOffset.Now, new JsonObject
        {
            ["actionName"] = r.ActionName,
            ["runId"] = r.RunId.ToString(),
            ["source"] = r.Source,
            ["success"] = r.Success,
            ["state"] = r.State.ToString(),
            ["durationMs"] = (long)r.Duration.TotalMilliseconds,
            ["error"] = r.Error,
        });

        var subscription = _services.Bus.Subscribe(OnEvent);
        _services.Engine.RunCompleted += OnRun;
        try
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await channel.Reader.WaitToReadAsync(heartbeat.Token).ConfigureAwait(false);
                    while (channel.Reader.TryRead(out var message)) await context.Response.WriteAsync(message, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": ping\n\n", ct).ConfigureAwait(false);
                }
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Client disconnected or server stopping.
        }
        finally
        {
            subscription.Dispose();
            _services.Engine.RunCompleted -= OnRun;
            Interlocked.Decrement(ref _eventStreams);
        }
    }

    /// <summary>Exact type (case-insensitive) or a prefix ending in '*'.</summary>
    internal static bool Matches(string filter, string type) =>
        filter.EndsWith('*')
            ? type.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(filter, type, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
    }
}
