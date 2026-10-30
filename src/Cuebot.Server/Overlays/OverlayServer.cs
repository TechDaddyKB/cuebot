using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Cuebot.Server.Overlays;

/// <summary>
/// Serves Cuebot overlays to OBS browser sources:
/// <list type="bullet">
/// <item><c>GET /o/{id}?token=...</c>: the overlay page.</item>
/// <item><c>GET /o/{id}/ws?token=...</c>: its WebSocket; first a "hello" with the overlay, then everything the
/// <see cref="OverlayFeed"/> publishes for it, in order.</item>
/// <item><c>GET /runtime/{file}</c>: Cuebot's overlay scripts, styles and built-in badge icons.</item>
/// <item><c>GET /assets/{sha256}.{ext}</c>: files from the <see cref="AssetStore"/>.</item>
/// </list>
/// A wrong or missing token answers 404, the same as an unknown overlay, so overlay ids cannot be probed. Pages are
/// sent with <c>Referrer-Policy: no-referrer</c> so the token in their URL never reaches sites widgets load from.
/// </summary>
public sealed class OverlayServer : IAsyncDisposable
{
    private const string LogSource = "Overlays";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Runtime files served at /runtime (embedded resources "Cuebot.Server.Overlays.{name}").</summary>
    private static readonly HashSet<string> RuntimeFiles = new(
        ["overlay.js", "overlay.css", .. Cuebot.Core.Overlays.Events.BuiltInBadges.Names.Select(n => $"badge-{n}.svg")], StringComparer.Ordinal);

    private readonly Func<ServerSettings> _settings;
    private readonly ServerServices _services;
    private readonly ConcurrentDictionary<string, OverlayClient> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private KestrelListener? _listener;
    private IDisposable? _feed;
    private int _disposed;

    /// <param name="settings">Read per request, so origin changes apply immediately.</param>
    public OverlayServer(Func<ServerSettings> settings, ServerServices services)
    {
        _settings = settings;
        _services = services;
    }

    public int Port => _listener?.Port ?? 0;
    public bool IsRunning => _listener is not null;

    /// <summary>Connected overlay pages.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>Raised (on a connection's task) when a page connects or disconnects.</summary>
    public event Action? ClientsChanged;

    public async Task StartAsync(IPAddress address, CancellationToken ct = default)
    {
        if (_listener is not null) return;
        _listener = await KestrelListener.StartAsync(address, _settings().Overlays.Port, HandleAsync, ct).ConfigureAwait(false);
        _feed = _services.OverlayFeed?.Subscribe(Deliver);
    }

    /// <summary>The page's path and query for an overlay (relative to the server's base URL).</summary>
    public static string PathFor(Overlay overlay) => $"/o/{overlay.Id:D}?token={Uri.EscapeDataString(overlay.Token)}";

    private void Deliver(OverlayMessage message)
    {
        foreach (var client in _clients.Values)
        {
            if (message.OverlayId is null || message.OverlayId == client.OverlayId) client.Outbox.Enqueue(message);
        }
    }

    internal async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var segments = (request.Path.Value ?? "/").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (segments)
        {
            case ["o", var id] when Authorize(id, request) is { } overlay:
                await WritePageAsync(context, overlay).ConfigureAwait(false);
                return;
            case ["o", var id, "ws"] when Authorize(id, request) is { } overlay:
                await AcceptAsync(context, overlay).ConfigureAwait(false);
                return;
            case ["runtime", var file] when RuntimeFiles.Contains(file):
                await WriteRuntimeAsync(context, file).ConfigureAwait(false);
                return;
            case ["assets", var name] when _services.OverlayAssets?.PathOf(name) is { } path:
                await WriteAssetAsync(context, path).ConfigureAwait(false);
                return;
            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private Overlay? Authorize(string id, HttpRequest request) =>
        Guid.TryParse(id, out var guid) ? _services.Overlays?.Authorize(guid, request.Query["token"].ToString()) : null;

    private static async Task WritePageAsync(HttpContext context, Overlay overlay)
    {
        var config = new JsonObject
        {
            ["id"] = overlay.Id.ToString("D"),
            ["name"] = overlay.Name,
            ["width"] = overlay.Width,
            ["height"] = overlay.Height,
            ["token"] = overlay.Token,
        };
        var page = Resource("overlay.html")
            .Replace("/*CUEBOT_OVERLAY*/{}", ScriptJson(config), StringComparison.Ordinal)
            .Replace("{{title}}", WebUtility.HtmlEncode(overlay.Name), StringComparison.Ordinal);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(page).ConfigureAwait(false);
    }

    /// <summary>JSON safe inside a &lt;script&gt; element (no "&lt;/script&gt;" or comment openers).</summary>
    internal static string ScriptJson(JsonNode node) =>
        node.ToJsonString().Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal);

    private static async Task WriteRuntimeAsync(HttpContext context, string file)
    {
        context.Response.ContentType = Path.GetExtension(file) switch
        {
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "text/css; charset=utf-8",
        };
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(Resource(file)).ConfigureAwait(false);
    }

    private static async Task WriteAssetAsync(HttpContext context, string path)
    {
        var response = context.Response;
        response.ContentType = ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";
        // Content-addressed names never change content.
        response.Headers.CacheControl = "public, max-age=31536000, immutable";
        // Opened directly (e.g. an SVG), an asset may not run script on the overlay server's origin.
        response.Headers.ContentSecurityPolicy = "sandbox";
        await response.SendFileAsync(path).ConfigureAwait(false);
    }

    private async Task AcceptAsync(HttpContext context, Overlay overlay)
    {
        var request = context.Request;
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        // The page is served from this server, so its own origin is always fine (also by host name on a LAN).
        var origin = request.Headers.Origin.ToString();
        var sameOrigin = origin.Length > 0 && string.Equals(origin, $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);
        if (!sameOrigin && !NetworkPolicy.IsOriginAllowed(origin, _settings().AllowedOrigins, request.Host.Value))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = new WebSocketConnection(socket, $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}");
        using var client = new OverlayClient(overlay.Id, connection);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, context.RequestAborted);
        client.Outbox.Enqueue(Hello(overlay, _services.SessionData));
        _clients[connection.Id] = client;
        ClientsChanged?.Invoke();
        var sending = client.SendLoopAsync(linked.Token);
        try
        {
            await connection.ReceiveAsync(message => OnMessageAsync(client, message), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(connection.Id, out _);
            ClientsChanged?.Invoke();
            await linked.CancelAsync().ConfigureAwait(false);
            try { await sending.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The overlay, plus the current stream totals and recent alerts (StreamElements' <c>session.data</c> and <c>recents</c>).</summary>
    private static OverlayMessage Hello(Overlay overlay, Cuebot.Core.Overlays.Session.SessionDataStore? session) => new(new JsonObject
    {
        ["type"] = "hello",
        ["overlay"] = new JsonObject
        {
            ["id"] = overlay.Id.ToString("D"),
            ["name"] = overlay.Name,
            ["width"] = overlay.Width,
            ["height"] = overlay.Height,
        },
        ["session"] = session?.Snapshot() ?? new JsonObject(),
        ["recents"] = session?.Recents() ?? new JsonArray(),
    }.ToJsonString(), overlay.Id);

    /// <summary>Pages send nothing Cuebot acts on yet (widget requests come with the widget runtime); pings get pongs.</summary>
    private static Task OnMessageAsync(OverlayClient client, string message)
    {
        if (message == "ping") client.Outbox.Enqueue(new OverlayMessage("\"pong\"", client.OverlayId, Droppable: true));
        return Task.CompletedTask;
    }

    private static string Resource(string name)
    {
        using var stream = typeof(OverlayServer).Assembly.GetManifestResourceStream("Cuebot.Server.Overlays." + name)
                           ?? throw new InvalidOperationException($"Missing overlay resource '{name}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _feed?.Dispose();
        _feed = null;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_clients.Values.Select(c => c.Connection.CloseAsync())).ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        _stopping.Dispose();
    }

    /// <summary>One connected overlay page and the messages waiting for it.</summary>
    private sealed class OverlayClient(Guid overlayId, WebSocketConnection connection) : IDisposable
    {
        public Guid OverlayId { get; } = overlayId;
        public WebSocketConnection Connection { get; } = connection;
        public OverlayOutbox Outbox { get; } = new();

        public async Task SendLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var message in await Outbox.TakeAllAsync(ct).ConfigureAwait(false))
                {
                    if (!await Connection.SendAsync(message.Json, ct).ConfigureAwait(false)) return;
                }
            }
        }

        public void Dispose() => Outbox.Dispose();
    }
}
