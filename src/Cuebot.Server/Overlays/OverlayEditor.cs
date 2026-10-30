using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Widgets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Cuebot.Server.Overlays;

/// <summary>
/// The web widget editor on the overlay server's main port, opened with the editor token (not an overlay link):
/// <list type="bullet">
/// <item><c>GET /edit/{overlayId}/{layerId}</c>: the editor page (settings form from FIELDS, code panes, CSS override,
/// preview and per-platform Emulate).</item>
/// <item><c>GET|POST /edit/api/{overlayId}/{layerId}</c>: the widget's code, or saving it (which reloads its overlays).</item>
/// <item><c>POST .../preview</c>: a draft document for the preview, served briefly from the widget port at <c>/p/{id}</c>.</item>
/// <item><c>POST .../request</c>: the preview's SE_API requests.</item>
/// <item><c>POST /edit/api/emulate</c>: sample events for one platform, converted like real ones but sent nowhere.</item>
/// <item><c>POST /edit/api/assets?name=...</c>: uploads a file for image, video and sound fields.</item>
/// </list>
/// Every POST must come from the editor's own page (same origin).
/// </summary>
internal sealed class OverlayEditor(Func<ServerSettings> settings, ServerServices services)
{
    public const int MaxCodeBytes = WidgetCode.MaxBytes + 1024 * 1024;
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(10);
    private const int MaxDrafts = 50;

    private readonly ConcurrentDictionary<string, (string Html, DateTimeOffset Expires)> _drafts = new();

    /// <summary>A draft preview document by id, or null when unknown or expired.</summary>
    public string? Draft(string id) =>
        _drafts.TryGetValue(id, out var draft) && draft.Expires > DateTimeOffset.UtcNow ? draft.Html : null;

    public async Task<bool> TryHandleAsync(HttpContext context, string[] segments)
    {
        if (segments is not ["edit", ..]) return false;
        var request = context.Request;
        var response = context.Response;
        if (!Authorized(request))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }
        var isPost = HttpMethods.IsPost(request.Method);
        if (isPost && !SameOrigin(request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }
        if (!isPost && !HttpMethods.IsGet(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return true;
        }

        switch (segments)
        {
            case ["edit", var o, var l] when !isPost && Find(o, l) is (var overlay, var layer):
                await WritePageAsync(context, overlay, layer).ConfigureAwait(false);
                return true;
            case ["edit", "api", "emulate"] when isPost:
                await JsonAsync(context, Emulate(await ReadJsonAsync(context).ConfigureAwait(false))).ConfigureAwait(false);
                return true;
            case ["edit", "api", "assets"] when isPost:
                await UploadAsync(context).ConfigureAwait(false);
                return true;
            case ["edit", "api", var o, var l] when Find(o, l) is (var overlay, var layer):
                if (isPost) await SaveAsync(context, layer).ConfigureAwait(false);
                else await JsonAsync(context, CodeJson(layer)).ConfigureAwait(false);
                return true;
            case ["edit", "api", var o, var l, "preview"] when isPost && Find(o, l) is (_, _):
                await JsonAsync(context, Preview(ReadCode(await ReadJsonAsync(context).ConfigureAwait(false)))).ConfigureAwait(false);
                return true;
            case ["edit", "api", var o, var l, "request"] when isPost && Find(o, l) is (_, _):
                await AnswerAsync(context).ConfigureAwait(false);
                return true;
        }
        response.StatusCode = StatusCodes.Status404NotFound;
        return true;
    }

    private bool Authorized(HttpRequest request)
    {
        var expected = settings().Overlays.EditorToken;
        var candidate = request.Query["editor"].ToString();
        if (expected.Length == 0 || candidate.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }

    private static bool SameOrigin(HttpRequest request) =>
        string.Equals(request.Headers.Origin.ToString(), $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);

    private (Overlay Overlay, OverlayLayer Layer)? Find(string overlayId, string layerId)
    {
        if (!Guid.TryParse(overlayId, out var o) || !Guid.TryParse(layerId, out var l)) return null;
        if (services.Overlays?.Get(o) is not { } overlay || overlay.Layers.FirstOrDefault(x => x.Id == l) is not { } layer) return null;
        return (overlay, layer);
    }

    private async Task WritePageAsync(HttpContext context, Overlay overlay, OverlayLayer layer)
    {
        var config = new JsonObject
        {
            ["overlayId"] = overlay.Id.ToString("D"),
            ["layerId"] = layer.Id.ToString("D"),
            ["name"] = layer.Name,
            ["overlayName"] = overlay.Name,
            ["width"] = layer.Width,
            ["height"] = layer.Height,
            ["editor"] = settings().Overlays.EditorToken,
            ["widgetPort"] = WidgetPort(),
            ["emulate"] = JsonSerializer.SerializeToNode(OverlayEmulator.Kinds),
        };
        var page = OverlayServer.Resource("editor.html")
            .Replace("/*CUEBOT_EDITOR*/{}", OverlayServer.ScriptJson(config), StringComparison.Ordinal)
            .Replace("{{title}}", WebUtility.HtmlEncode(layer.Name), StringComparison.Ordinal);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(page).ConfigureAwait(false);
    }

    /// <summary>Set by the server once the widget port is bound.</summary>
    public Func<int> WidgetPort { get; set; } = () => 0;

    private JsonObject CodeJson(OverlayLayer layer)
    {
        var code = services.Widgets?.CodeFor(layer) ?? WidgetCode.Empty;
        return new JsonObject
        {
            ["name"] = layer.Name,
            ["html"] = code.Html,
            ["css"] = code.Css,
            ["js"] = code.Js,
            ["fields"] = code.Fields,
            ["data"] = code.Data,
            ["cssOverride"] = code.CssOverride,
        };
    }

    private static WidgetCode ReadCode(JsonObject? json)
    {
        string Get(string key) => json?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
        return WidgetImport.FromPanes(Get("html"), Get("css"), Get("js"), Get("fields"), Get("data")) with { CssOverride = Get("cssOverride") };
    }

    private async Task SaveAsync(HttpContext context, OverlayLayer layer)
    {
        WidgetCode code;
        try
        {
            code = ReadCode(await ReadJsonAsync(context).ConfigureAwait(false));
        }
        catch (InvalidDataException ex)
        {
            await JsonAsync(context, new JsonObject { ["error"] = ex.Message }, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }
        if (layer.ElementId is not { } element || services.WidgetCode is not { } store)
        {
            await JsonAsync(context, new JsonObject { ["error"] = "This layer has no widget code" }, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }
        store.Write(element, code);
        await JsonAsync(context, new JsonObject { ["saved"] = true }).ConfigureAwait(false);
    }

    /// <summary><c>{src, fieldData, widgetDuration}</c> for a draft; the draft id is the only key to it.</summary>
    private JsonObject Preview(WidgetCode code)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, draft) in _drafts)
            if (draft.Expires <= now) _drafts.TryRemove(key, out _);
        while (_drafts.Count >= MaxDrafts && _drafts.Keys.FirstOrDefault() is { } oldest) _drafts.TryRemove(oldest, out _);

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _drafts[id] = (OverlayWidgetHost.Document(code), now + DraftLifetime);
        var json = OverlayWidgetHost.Settings(code);
        json["src"] = "/p/" + id;
        return json;
    }

    private JsonObject Emulate(JsonObject? json)
    {
        var platform = json?["platform"]?.GetValue<string>() ?? "";
        var kind = json?["kind"]?.GetValue<string>() ?? "";
        var messages = new JsonArray();
        if (OverlayEmulator.Create(platform, kind) is { } e && services.OverlayBridge is { } bridge)
        {
            foreach (var (listener, data) in bridge.Convert(e))
                messages.Add(new JsonObject { ["listener"] = listener, ["event"] = data?.DeepClone() });
        }
        return new JsonObject { ["messages"] = messages };
    }

    private async Task AnswerAsync(HttpContext context)
    {
        var json = await ReadJsonAsync(context).ConfigureAwait(false);
        try
        {
            var result = OverlayWidgetHost.Answer(json?["request"]?.GetValue<string>() ?? "", json?["data"] as JsonObject, services);
            await JsonAsync(context, new JsonObject { ["result"] = result }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            await JsonAsync(context, new JsonObject { ["error"] = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task UploadAsync(HttpContext context)
    {
        if (services.OverlayAssets is not { } assets)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = AssetStore.MaxBytes;
        try
        {
            var name = await assets.AddAsync(context.Request.Body, context.Request.Query["name"].ToString(), context.RequestAborted).ConfigureAwait(false);
            await JsonAsync(context, new JsonObject { ["url"] = "/assets/" + name }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            await JsonAsync(context, new JsonObject { ["error"] = ex.Message }, StatusCodes.Status400BadRequest).ConfigureAwait(false);
        }
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxCodeBytes;
        try
        {
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task JsonAsync(HttpContext context, JsonObject json, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(json.ToJsonString()).ConfigureAwait(false);
    }
}
