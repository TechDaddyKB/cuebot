using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Net;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Cuebot.Server.Overlays;

/// <summary>
/// Serves Cuebot overlays to OBS browser sources:
/// <list type="bullet">
/// <item><c>GET /o/{id}?token=...</c>: the overlay page.</item>
/// <item><c>GET /o/{id}/ws?token=...</c>: its WebSocket; first a "hello" with the overlay, then everything the
/// <see cref="OverlayFeed"/> publishes for it, in order. Widgets' <c>SE_API</c> requests come back over it.</item>
/// <item><c>GET /o/{id}/l/{layerId}?token=...</c>: a layer's widget settings and the address of its document (JSON).</item>
/// <item><c>/edit/...?editor=...</c>: the widget editor (see <see cref="OverlayEditor"/>).</item>
/// <item><c>/design/...?editor=...</c>: the overlay designer (see <see cref="OverlayDesigner"/>).</item>
/// <item>On the widget port, <c>GET /w/{id}/{layerId}?key=...</c>: the widget document, which the page shows in a frame.
/// The widget port is another web origin, so a widget cannot reach the page, its token or Cuebot's API; its key, derived
/// from the overlay token, opens only that layer. <c>/runtime</c> and <c>/assets</c> are served there too.</item>
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
        ["overlay.js", "overlay.css", "se-widget.js", "jquery-3.7.1.min.js", "editor.js", "editor.css", "designer.js", "designer.css", "alerts.js", "alerts.css", .. Cuebot.Core.Overlays.Events.BuiltInBadges.Names.Select(n => $"badge-{n}.svg")], StringComparer.Ordinal);

    private readonly Func<ServerSettings> _settings;
    private readonly ServerServices _services;
    private readonly ConcurrentDictionary<string, OverlayClient> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private KestrelListener? _listener;
    private KestrelListener? _widgetListener;
    private readonly OverlayEditor _editor;
    private readonly OverlayDesigner _designer;
    private IDisposable? _feed;
    private int _disposed;
    private Action<Guid>? _overlayChanged;
    private Action<Guid>? _codeChanged;
    private Action<string, JsonNode?>? _storeChanged;
    private Action<Cuebot.Core.Actions.GlobalVariableChange>? _globalChanged;

    /// <param name="settings">Read per request, so origin changes apply immediately.</param>
    public OverlayServer(Func<ServerSettings> settings, ServerServices services)
    {
        _settings = settings;
        _services = services;
        _editor = new OverlayEditor(settings, services) { WidgetPort = () => WidgetPort };
        _designer = new OverlayDesigner(settings, services);
    }

    public int Port => _listener?.Port ?? 0;

    /// <summary>The bound widget port (another origin for widget frames).</summary>
    public int WidgetPort => _widgetListener?.Port ?? 0;
    public bool IsRunning => _listener is not null;

    /// <summary>Connected overlay pages.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>Raised (on a connection's task) when a page connects or disconnects.</summary>
    public event Action? ClientsChanged;

    public async Task StartAsync(IPAddress address, CancellationToken ct = default)
    {
        if (_listener is not null) return;
        _listener = await KestrelListener.StartAsync(address, _settings().Overlays.Port, HandleAsync, ct).ConfigureAwait(false);
        try
        {
            _widgetListener = await KestrelListener.StartAsync(address, _settings().Overlays.WidgetPort, HandleWidgetAsync, ct).ConfigureAwait(false);
        }
        catch
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
            throw;
        }
        _feed = _services.OverlayFeed?.Subscribe(Deliver);
        WatchChanges();
    }

    /// <summary>
    /// Overlay and widget changes reload the pages they affect; SE_API.store changes become "kvstore:update" and numeric
    /// global variable changes "bot:counter" events, as StreamElements sends them.
    /// </summary>
    private void WatchChanges()
    {
        if (_services.OverlayFeed is not { } feed) return;
        if (_services.Overlays is { } overlays)
        {
            _overlayChanged = id => feed.Publish(OverlayMessage.Command("reload", id));
            overlays.Changed += _overlayChanged;
        }
        if (_services.WidgetCode is { } code && _services.Overlays is { } store)
        {
            _codeChanged = elementId =>
            {
                foreach (var overlay in store.All().Where(o => o.Layers.Any(l => l.ElementId == elementId)))
                    feed.Publish(OverlayMessage.Command("reload", overlay.Id));
            };
            code.Changed += _codeChanged;
        }
        if (_services.WidgetStore is { } kv)
        {
            _storeChanged = (key, value) => feed.Publish(OverlayMessage.Event("kvstore:update",
                new JsonObject { ["data"] = new JsonObject { ["key"] = "customWidget." + key, ["value"] = value?.DeepClone() } }));
            kv.Changed += _storeChanged;
        }
        _globalChanged = change =>
        {
            if (change.NewValue is not (long or int or decimal or double)) return;
            feed.Publish(OverlayMessage.Event("bot:counter", new JsonObject
            {
                ["counter"] = change.Name,
                ["value"] = Convert.ToDouble(change.NewValue, CultureInfo.InvariantCulture),
            }));
        };
        try { _services.Globals.VariableChanged += _globalChanged; }
        catch (InvalidOperationException) { _globalChanged = null; }   // no globals (tests without a provider)
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

        var segments = (request.Path.Value ?? "/").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (await _editor.TryHandleAsync(context, segments).ConfigureAwait(false)) return;
        if (await _designer.TryHandleAsync(context, segments).ConfigureAwait(false)) return;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        switch (segments)
        {
            case ["o", var id] when Authorize(id, request) is { } overlay:
                await WritePageAsync(context, overlay, WidgetPort).ConfigureAwait(false);
                return;
            case ["o", var id, "ws"] when Authorize(id, request) is { } overlay:
                await AcceptAsync(context, overlay).ConfigureAwait(false);
                return;
            case ["o", var id, "l", var layerId] when Authorize(id, request) is { } overlay
                                                    && overlay.Layers.FirstOrDefault(l => l.Id.ToString("D") == layerId) is { } layer:
                await WriteLayerAsync(context, overlay, layer).ConfigureAwait(false);
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

    private static async Task WritePageAsync(HttpContext context, Overlay overlay, int widgetPort)
    {
        var config = new JsonObject
        {
            ["id"] = overlay.Id.ToString("D"),
            ["name"] = overlay.Name,
            ["width"] = overlay.Width,
            ["height"] = overlay.Height,
            ["token"] = overlay.Token,
            ["layers"] = new JsonArray([.. overlay.Layers.Select(OverlayWidgetHost.LayerConfig)]),
            ["widgetPort"] = widgetPort,
        };
        var page = Resource("overlay.html")
            .Replace("/*CUEBOT_OVERLAY*/{}", ScriptJson(config), StringComparison.Ordinal)
            .Replace("{{title}}", WebUtility.HtmlEncode(overlay.Name), StringComparison.Ordinal);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(page).ConfigureAwait(false);
    }

    /// <summary><c>{src, fieldData, widgetDuration}</c>: the document's path on the widget port and the widget's settings.</summary>
    private async Task WriteLayerAsync(HttpContext context, Overlay overlay, OverlayLayer layer)
    {
        var code = _services.Widgets?.CodeFor(layer) ?? WidgetCode.Empty;
        var json = OverlayWidgetHost.Settings(code);
        json["src"] = $"/w/{overlay.Id:D}/{layer.Id:D}?key={OverlayWidgetHost.FrameKey(overlay.Token, layer.Id)}";
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(json.ToJsonString()).ConfigureAwait(false);
    }

    /// <summary>The widget port: widget documents (by frame key), runtime files and assets; nothing else.</summary>
    internal async Task HandleWidgetAsync(HttpContext context)
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
            case ["p", var draftId] when _editor.Draft(draftId) is { } draft:
                response.ContentType = "text/html; charset=utf-8";
                response.Headers.CacheControl = "no-store";
                await response.WriteAsync(draft).ConfigureAwait(false);
                return;
            case ["w", var id, var layerId] when WidgetLayer(id, layerId, request.Query["key"].ToString()) is { } layer:
                response.ContentType = "text/html; charset=utf-8";
                response.Headers.CacheControl = "no-store";
                await response.WriteAsync(OverlayWidgetHost.Document(_services.Widgets?.CodeFor(layer) ?? WidgetCode.Empty)).ConfigureAwait(false);
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

    private OverlayLayer? WidgetLayer(string overlayId, string layerId, string key)
    {
        if (!Guid.TryParse(overlayId, out var id) || !Guid.TryParse(layerId, out var layer)) return null;
        if (_services.Overlays?.Get(id) is not { } overlay) return null;
        var expected = OverlayWidgetHost.FrameKey(overlay.Token, layer);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(key.Length == expected.Length ? key : expected + "x")))
            return null;
        return overlay.Layers.FirstOrDefault(l => l.Id == layer);
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
        client.Outbox.Enqueue(Hello(overlay, _services.SessionData, _services.AlertQueue, _services.Polls, _services.Giveaways, _services.Media));
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

    /// <summary>
    /// The overlay, plus the current stream totals and recent alerts (StreamElements' <c>session.data</c> and
    /// <c>recents</c>), whether the alert queue is paused or muted (<c>alerts {paused, muted}</c>) and the latest poll
    /// and prediction from each source (<c>polls</c>), the current giveaway (<c>giveaway</c>, null when none) and media
    /// requests (<c>media</c>).
    /// </summary>
    private static OverlayMessage Hello(Overlay overlay, Cuebot.Core.Overlays.Session.SessionDataStore? session,
        OverlayAlertQueue? alerts, Cuebot.Core.Polls.PollTracker? polls, Cuebot.Core.Giveaways.Giveaways? giveaways,
        Cuebot.Core.Media.MediaRequests? media) => new(new JsonObject
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
        ["alerts"] = alerts?.Snapshot() ?? new JsonObject { ["paused"] = false, ["muted"] = false },
        ["polls"] = polls?.Snapshot() ?? new JsonArray(),
        ["giveaway"] = giveaways?.Snapshot(),
        ["media"] = media?.Snapshot(),
    }.ToJsonString(), overlay.Id);

    /// <summary>
    /// Pings get pongs; widget requests (<c>{"type":"request", id, layer, request, data}</c>) get
    /// <c>{"type":"response", id, layer, result}</c> or <c>{..., error}</c>, to that page only.
    /// </summary>
    private async Task OnMessageAsync(OverlayClient client, string message)
    {
        if (message == "ping")
        {
            client.Outbox.Enqueue(new OverlayMessage("\"pong\"", client.OverlayId, Droppable: true));
            return;
        }
        JsonObject? json;
        try { json = JsonNode.Parse(message) as JsonObject; }
        catch (JsonException) { return; }
        if (json?["type"]?.GetValue<string>() != "request") return;

        var reply = new JsonObject { ["type"] = "response", ["id"] = json["id"]?.DeepClone(), ["layer"] = json["layer"]?.DeepClone() };
        try
        {
            var layerId = json["layer"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
            var layer = _services.Overlays?.Get(client.OverlayId)?.Layers.FirstOrDefault(l => l.Id.ToString("D") == layerId);
            reply["result"] = await OverlayWidgetHost.AnswerAsync(json["request"]?.GetValue<string>() ?? "", json["data"] as JsonObject, layer, _services)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            reply["error"] = ex.Message;
        }
        client.Outbox.Enqueue(new OverlayMessage(reply.ToJsonString(), client.OverlayId));
    }

    internal static string Resource(string name)
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
        if (_overlayChanged is not null && _services.Overlays is { } overlays) overlays.Changed -= _overlayChanged;
        if (_codeChanged is not null && _services.WidgetCode is { } code) code.Changed -= _codeChanged;
        if (_storeChanged is not null && _services.WidgetStore is { } kv) kv.Changed -= _storeChanged;
        if (_globalChanged is not null) _services.Globals.VariableChanged -= _globalChanged;
        _feed = null;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_clients.Values.Select(c => c.Connection.CloseAsync())).ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        if (_widgetListener is not null) await _widgetListener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        _widgetListener = null;
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
