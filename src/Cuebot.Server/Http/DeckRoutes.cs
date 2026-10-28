using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core.Decks;
using Cuebot.Server.Hosting;
using Microsoft.AspNetCore.Http;

namespace Cuebot.Server.Http;

/// <summary>
/// Cuebot Decks on the HTTP server:
/// <list type="bullet">
/// <item><c>GET /deck</c>: the enabled decks; <c>GET /deck/{id}</c>: the deck page (any browser, phone or OBS dock);</item>
/// <item><c>GET /deck/{id}/state</c>: the current view as JSON; <c>GET /deck/{id}/events</c>: server-sent events
/// ("state" with a new view, "notify" with a notification);</item>
/// <item><c>POST /deck/{id}/press</c> <c>{"item":"&lt;id&gt;"}</c> runs a button; <c>POST /deck/{id}/page</c>
/// <c>{"delta":1}</c> or <c>{"page":2}</c> changes page for every open copy of the deck.</item>
/// </list>
/// POSTs follow the server's allowed origins, like the API. Decks are addressed by id, not name.
/// </summary>
internal sealed class DeckRoutes(DeckService decks)
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static bool Matches(string path)
    {
        var p = NetworkPolicy.NormalizePath(path);
        return p.Equals("/deck", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/deck/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task HandleAsync(HttpContext context, bool originAllowed)
    {
        var parts = NetworkPolicy.NormalizePath(context.Request.Path.Value).Trim('/').Split('/');
        var isGet = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var isPost = HttpMethods.IsPost(context.Request.Method);
        if (parts.Length == 1)
        {
            if (!isGet) await Status(context, 405).ConfigureAwait(false);
            else await Html(context, IndexPage()).ConfigureAwait(false);
            return;
        }
        if (!Guid.TryParse(parts[1], out var id) || decks.View(id) is null)
        {
            await Status(context, 404, "No such deck").ConfigureAwait(false);
            return;
        }
        var route = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        switch (route)
        {
            case "" when isGet:
                await Html(context, Page(id)).ConfigureAwait(false);
                return;
            case "state" when isGet:
                await Json(context, 200, decks.View(id)!).ConfigureAwait(false);
                return;
            case "events" when isGet:
                await StreamAsync(context, id).ConfigureAwait(false);
                return;
            case "press" or "page" when isPost:
                if (!originAllowed)
                {
                    await Status(context, 403, "Origin not allowed").ConfigureAwait(false);
                    return;
                }
                await PostAsync(context, id, route).ConfigureAwait(false);
                return;
            case "" or "state" or "events" or "press" or "page":
                await Status(context, 405).ConfigureAwait(false);
                return;
            default:
                await Status(context, 404).ConfigureAwait(false);
                return;
        }
    }

    private async Task PostAsync(HttpContext context, Guid id, string route)
    {
        JsonObject body;
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            body = (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            await Status(context, 400, "Invalid JSON").ConfigureAwait(false);
            return;
        }
        if (route == "press")
        {
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "";
            var ok = Guid.TryParse(Text(body["item"]), out var item) && decks.Press(id, item, client);
            await Status(context, ok ? 204 : 404).ConfigureAwait(false);
            return;
        }
        var deck = id.ToString();
        if (int.TryParse(Text(body["page"]), out var page)) decks.ChangePage(deck, null, page);
        else decks.ChangePage(deck, int.TryParse(Text(body["delta"]), out var delta) ? delta : 1);
        await Status(context, 204).ConfigureAwait(false);
    }

    private static string Text(JsonNode? node) => node is JsonValue v ? v.ToString() : "";

    /// <summary>Server-sent events for one deck until the browser goes away.</summary>
    private async Task StreamAsync(HttpContext context, Guid id)
    {
        var queue = Channel.CreateBounded<DeckMessage>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnMessage(DeckMessage m)
        {
            if (m.DeckId == id) queue.Writer.TryWrite(m);
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        decks.Message += OnMessage;
        var ct = context.RequestAborted;
        try
        {
            if (decks.View(id) is { } first) await WriteAsync(context, "state", first, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(KeepAlive);
                try
                {
                    var message = await queue.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    await WriteAsync(context, message.Kind, message.Data, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The browser closed the page.
        }
        finally
        {
            decks.Message -= OnMessage;
        }
    }

    private static async Task WriteAsync(HttpContext context, string kind, JsonObject data, CancellationToken ct)
    {
        await context.Response.WriteAsync($"event: {kind}\ndata: {data.ToJsonString()}\n\n", ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private string IndexPage()
    {
        var items = string.Concat(decks.EnabledDecks().Select(d =>
            $"<li><a href=\"/deck/{d.Id}\">{WebUtility.HtmlEncode(d.Name)}</a></li>"));
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Cuebot Decks</title><style>body{font-family:system-ui,sans-serif;background:#16161c;color:#eee;padding:24px}a{color:#8ab4ff}</style></head>
            <body><h1>Cuebot Decks</h1><ul>{{(items.Length == 0 ? "<li>No decks yet: create one on Cuebot's Decks page.</li>" : items)}}</ul></body></html>
            """;
    }

    /// <summary>The deck page with its id filled in.</summary>
    internal static string Page(Guid id)
    {
        using var stream = typeof(DeckRoutes).Assembly.GetManifestResourceStream("Cuebot.Server.Decks.deck.html")
                           ?? throw new InvalidOperationException("Missing deck page resource");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("/*CUEBOT_DECK*/\"\"", JsonSerializer.Serialize(id.ToString()), StringComparison.Ordinal);
    }

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
        context.Response.Headers.CacheControl = "no-cache";
        return context.Response.WriteAsync(body.ToJsonString());
    }

    private static Task Status(HttpContext context, int status, string? text = null)
    {
        context.Response.StatusCode = status;
        if (text is null) return Task.CompletedTask;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }
}
