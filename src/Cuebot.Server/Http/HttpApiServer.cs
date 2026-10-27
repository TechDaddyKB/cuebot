using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Server.Hosting;
using Cuebot.Server.StreamerBot;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Cuebot.Server.Http;

/// <summary>
/// Streamer.bot-compatible HTTP API (<c>/api/DoAction</c>, <c>/api/GetActions</c>, ...; the un-prefixed paths work
/// too), static files from the configured mappings, and the built-in OBS docks <c>/feed/chat</c> and
/// <c>/feed/events</c>, which connect to the WebSocket server.
/// </summary>
public sealed class HttpApiServer : IAsyncDisposable
{
    /// <summary>Requests the HTTP API does not offer (they need a WebSocket session or authentication).</summary>
    private static readonly HashSet<string> NotOverHttp = new(["SendMessage"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests answered with 204 No Content, as in Streamer.bot.</summary>
    private static readonly HashSet<string> NoContent =
        new(["DoAction", "ClearCredits", "ClearFirstWordsCache", "ExecuteCodeTrigger"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests that run actions; only POST (a JSON body), never a plain GET a web page could trigger.</summary>
    private static readonly HashSet<string> PostOnly = new(["DoAction", "ExecuteCodeTrigger"], StringComparer.OrdinalIgnoreCase);

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly Func<ServerSettings> _settings;
    private readonly StreamerBotRequests _requests;
    private KestrelListener? _listener;

    /// <param name="settings">Read per request, so mapping and origin changes apply immediately.</param>
    public HttpApiServer(Func<ServerSettings> settings, StreamerBotRequests requests)
    {
        _settings = settings;
        _requests = requests;
    }

    public int Port => _listener?.Port ?? 0;
    public bool IsRunning => _listener is not null;

    public async Task StartAsync(IPAddress address, CancellationToken ct = default)
    {
        if (_listener is not null) return;
        _listener = await KestrelListener.StartAsync(address, _settings().Http.Port, HandleAsync, ct).ConfigureAwait(false);
    }

    internal async Task HandleAsync(HttpContext context)
    {
        var settings = _settings();
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var origin = request.Headers.Origin.ToString();
        var originAllowed = NetworkPolicy.IsOriginAllowed(origin, settings.AllowedOrigins, request.Host.Value);
        // Browsers send no Origin on plain cross-site GETs (<img>, links) but do mark them cross-site.
        if (origin.Length == 0 && string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase)
            && !settings.AllowedOrigins.Any(a => a.Trim() == "*"))
        {
            originAllowed = false;
        }
        if (originAllowed && origin.Length > 0)
        {
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
        }

        if (HttpMethods.IsOptions(request.Method))
        {
            if (!originAllowed)
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
            context.Response.StatusCode = 204;
            return;
        }

        if (ApiRequest(path) is { } name)
        {
            if (!originAllowed)
            {
                await Text(context, 403, "Origin not allowed").ConfigureAwait(false);
                return;
            }
            await HandleApiAsync(context, name).ConfigureAwait(false);
            return;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await Text(context, 405, "Method not allowed").ConfigureAwait(false);
            return;
        }

        var normalized = NetworkPolicy.NormalizePath(path);
        if (normalized.Equals("/feed/chat", StringComparison.OrdinalIgnoreCase) || normalized.Equals("/feed/events", StringComparison.OrdinalIgnoreCase))
        {
            await Html(context, Feed(normalized.EndsWith("chat", StringComparison.OrdinalIgnoreCase) ? "chat" : "events", settings)).ConfigureAwait(false);
            return;
        }

        if (ResolveStaticFile(settings.Http.Mappings, path) is { } file)
        {
            context.Response.ContentType = ContentTypes.TryGetContentType(file, out var type) ? type : "application/octet-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(file).ConfigureAwait(false);
            return;
        }

        if (normalized == "/")
        {
            await Html(context, IndexPage).ConfigureAwait(false);
            return;
        }
        await Text(context, 404, "Not found").ConfigureAwait(false);
    }

    /// <summary>The API request name for "/api/Name" or "/Name", or null.</summary>
    internal static string? ApiRequest(string path)
    {
        var p = path.Trim('/');
        if (p.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) p = p[4..];
        if (p.Length == 0 || p.Contains('/')) return null;
        return StreamerBotRequests.IsKnown(p) && !NotOverHttp.Contains(p) ? StreamerBotRequests.Names.First(n => n.Equals(p, StringComparison.OrdinalIgnoreCase)) : null;
    }

    private async Task HandleApiAsync(HttpContext context, string name)
    {
        if (PostOnly.Contains(name) && !HttpMethods.IsPost(context.Request.Method))
        {
            await Json(context, 405, new JsonObject { ["error"] = $"{name} needs POST" }).ConfigureAwait(false);
            return;
        }
        JsonElement body;
        try
        {
            body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await Json(context, 400, new JsonObject { ["error"] = "Invalid JSON" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = name.Equals("DoAction", StringComparison.OrdinalIgnoreCase)
                ? _requests.DoAction(body, "HTTP")
                : await _requests.HandleAsync(name, body, context.RequestAborted).ConfigureAwait(false);
            if (NoContent.Contains(name))
            {
                context.Response.StatusCode = 204;
                return;
            }
            await Json(context, 200, result).ConfigureAwait(false);
        }
        catch (RequestException ex)
        {
            await Json(context, ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ? 404 : 400,
                new JsonObject { ["error"] = ex.Message }).ConfigureAwait(false);
        }
    }

    /// <summary>POST: the JSON body. GET: the query string as an object (values that look like JSON are parsed).</summary>
    private static async Task<JsonElement> ReadBodyAsync(HttpRequest request)
    {
        if (HttpMethods.IsPost(request.Method))
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}").RootElement.Clone();
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        var query = new JsonObject();
        foreach (var (key, value) in request.Query)
        {
            var raw = value.ToString();
            JsonNode? node = raw;
            if (raw.StartsWith('{') || raw.StartsWith('['))
            {
                try { node = JsonNode.Parse(raw); }
                catch (JsonException) { node = raw; }
            }
            query[key] = node;
        }
        // GET /DoAction?id=...&name=... also works.
        if (!query.ContainsKey("action") && (query.ContainsKey("id") || query.ContainsKey("name")))
        {
            query["action"] = new JsonObject { ["id"] = query["id"]?.DeepClone(), ["name"] = query["name"]?.DeepClone() };
        }
        return JsonSerializer.SerializeToElement(query);
    }

    /// <summary>The file a static mapping serves for <paramref name="urlPath"/>, or null. Never leaves the mapped folder.</summary>
    internal static string? ResolveStaticFile(IEnumerable<HttpMapping> mappings, string urlPath)
    {
        var path = "/" + Uri.UnescapeDataString(urlPath).Replace('\\', '/').TrimStart('/');
        foreach (var mapping in mappings.Where(m => m.Folder.Trim().Length > 0).OrderByDescending(m => NetworkPolicy.NormalizePath(m.Path).Length))
        {
            var prefix = NetworkPolicy.NormalizePath(mapping.Path);
            string rest;
            if (prefix == "/") rest = path.TrimStart('/');
            else if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) rest = "";
            else if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) rest = path[(prefix.Length + 1)..];
            else continue;

            var target = mapping.Folder.Trim();
            if (File.Exists(target)) return rest.Length == 0 ? Path.GetFullPath(target) : null;
            if (!Directory.Exists(target)) continue;

            var root = Path.GetFullPath(target);
            var rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, rest.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.Equals(root, comparison) && !full.StartsWith(rootWithSlash, comparison)) return null;
            if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
            return File.Exists(full) ? full : null;
        }
        return null;
    }

    /// <summary>A built-in dock page with the WebSocket server's port and path filled in.</summary>
    internal static string Feed(string kind, ServerSettings settings)
    {
        var config = new JsonObject
        {
            ["port"] = settings.WebSocket.Port,
            ["endpoint"] = NetworkPolicy.NormalizePath(settings.WebSocket.Endpoint),
            ["enabled"] = settings.WebSocket.Enabled,
        };
        return Resource(kind + ".html")
            .Replace("/*CUEBOT_CONFIG*/{}", config.ToJsonString(), StringComparison.Ordinal)
            .Replace("/*CUEBOT_CLIENT*/", Resource("client.js.html"), StringComparison.Ordinal);
    }

    private static string Resource(string name)
    {
        using var stream = typeof(HttpApiServer).Assembly.GetManifestResourceStream("Cuebot.Server.Feeds." + name)
                           ?? throw new InvalidOperationException($"Missing feed resource '{name}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const string IndexPage =
        """
        <!doctype html><html><head><meta charset="utf-8"><title>Cuebot</title>
        <style>body{font-family:system-ui,sans-serif;background:#16161c;color:#eee;padding:24px}a{color:#8ab4ff}</style></head>
        <body><h1>Cuebot HTTP server</h1><ul>
        <li><a href="/feed/chat">/feed/chat</a> - combined chat dock</li>
        <li><a href="/feed/events">/feed/events</a> - activity feed dock</li>
        <li><a href="/api/GetActions">/api/GetActions</a></li></ul></body></html>
        """;

    private static Task Html(HttpContext context, string html)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        return context.Response.WriteAsync(html);
    }

    private static Task Json(HttpContext context, int status, JsonNode body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(body.ToJsonString());
    }

    private static Task Text(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }

    public async ValueTask DisposeAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
    }
}
