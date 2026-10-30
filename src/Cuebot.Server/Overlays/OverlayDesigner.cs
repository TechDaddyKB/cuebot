using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Cuebot.Server.Overlays;

/// <summary>
/// The web overlay designer on the overlay server's main port, opened with the editor token:
/// <list type="bullet">
/// <item><c>GET /design/{overlayId}</c>: the designer (canvas with the live overlay, layers, properties).</item>
/// <item><c>GET|POST /design/api/{overlayId}</c>: the overlay with its layers and the element library, or saving the
/// overlay's name, canvas and layers (checked: known types, existing elements, sane sizes and animations).</item>
/// <item><c>POST .../add</c> <c>{type, elementId?}</c>: a new text, image, video or slideshow layer, a chat box, or a
/// layer using a library element.</item>
/// <item><c>POST .../import?name=...</c>: a widget zip (raw body) as a new custom widget layer.</item>
/// <item><c>POST .../save-to-library</c> <c>{layerId}</c>: a text, image, video or slideshow layer as a library template.</item>
/// <item><c>POST /design/api/library/{elementId}/rename|duplicate|delete</c>: library upkeep (delete only when no
/// overlay uses the element).</item>
/// </list>
/// Uploads for image and video layers use the widget editor's <c>/edit/api/assets</c>. Every POST must come from the
/// designer page's own origin.
/// </summary>
internal sealed class OverlayDesigner(Func<ServerSettings> settings, ServerServices services)
{
    public async Task<bool> TryHandleAsync(HttpContext context, string[] segments)
    {
        if (segments is not ["design", ..]) return false;
        var request = context.Request;
        var response = context.Response;
        if (!OverlayEditor.Authorized(settings(), request) || services.Overlays is not { } overlays)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }
        var isPost = HttpMethods.IsPost(request.Method);
        if (isPost && !OverlayEditor.SameOrigin(request))
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
            case ["design", var id] when !isPost && Get(id) is { } overlay:
                await WritePageAsync(context, overlay).ConfigureAwait(false);
                return true;
            case ["design", "api", var id] when Get(id) is { } overlay:
                if (isPost)
                {
                    var error = Save(overlay.Id, await OverlayEditor.ReadJsonAsync(context).ConfigureAwait(false));
                    if (error is not null)
                    {
                        await OverlayEditor.JsonAsync(context, new JsonObject { ["error"] = error }, StatusCodes.Status400BadRequest).ConfigureAwait(false);
                        return true;
                    }
                }
                await OverlayEditor.JsonAsync(context, State(overlays.Get(overlay.Id)!)).ConfigureAwait(false);
                return true;
            case ["design", "api", var id, "add"] when isPost && Get(id) is { } overlay:
                var json = await OverlayEditor.ReadJsonAsync(context).ConfigureAwait(false);
                var added = Add(overlay, json?["type"]?.GetValue<string>() ?? "", json?["elementId"]?.GetValue<string>());
                await OverlayEditor.JsonAsync(context, added is null
                    ? new JsonObject { ["error"] = "That cannot be added" }
                    : new JsonObject { ["layer"] = added.Value.ToString("D"), ["state"] = State(overlays.Get(overlay.Id)!) },
                    added is null ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK).ConfigureAwait(false);
                return true;
            case ["design", "api", "library", var elementId, var action] when isPost && Guid.TryParse(elementId, out var element):
                await LibraryAsync(context, element, action).ConfigureAwait(false);
                return true;
            case ["design", "api", var id, "save-to-library"] when isPost && Get(id) is { } overlay:
                var body = await OverlayEditor.ReadJsonAsync(context).ConfigureAwait(false);
                var saved = Guid.TryParse(body?["layerId"]?.GetValue<string>(), out var layerId) ? services.Widgets?.SaveToLibrary(overlay.Id, layerId) : null;
                await OverlayEditor.JsonAsync(context, saved is null
                    ? new JsonObject { ["error"] = "Only text, image, video and slideshow layers are saved this way; widgets are in the library already" }
                    : new JsonObject { ["element"] = saved.Id.ToString("D"), ["state"] = State(overlays.Get(overlay.Id)!) },
                    saved is null ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK).ConfigureAwait(false);
                return true;
            case ["design", "api", var id, "import"] when isPost && Get(id) is { } overlay:
                await ImportAsync(context, overlay).ConfigureAwait(false);
                return true;
        }
        response.StatusCode = StatusCodes.Status404NotFound;
        return true;
    }

    private Overlay? Get(string id) => Guid.TryParse(id, out var guid) ? services.Overlays?.Get(guid) : null;

    private async Task WritePageAsync(HttpContext context, Overlay overlay)
    {
        var config = new JsonObject
        {
            ["overlayId"] = overlay.Id.ToString("D"),
            ["editor"] = settings().Overlays.EditorToken,
            ["preview"] = OverlayServer.PathFor(overlay),
            ["enterAnimations"] = new JsonArray([.. OverlayLayerTypes.EnterAnimations.Select(a => (JsonNode)a)]),
            ["exitAnimations"] = new JsonArray([.. OverlayLayerTypes.ExitAnimations.Select(a => (JsonNode)a)]),
        };
        var page = OverlayServer.Resource("designer.html")
            .Replace("/*CUEBOT_DESIGNER*/{}", OverlayServer.ScriptJson(config), StringComparison.Ordinal)
            .Replace("{{title}}", WebUtility.HtmlEncode(overlay.Name), StringComparison.Ordinal);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(page).ConfigureAwait(false);
    }

    /// <summary>The overlay (without its token) and the element library, for the designer.</summary>
    private JsonObject State(Overlay overlay) => new()
    {
        ["overlay"] = new JsonObject
        {
            ["id"] = overlay.Id.ToString("D"),
            ["name"] = overlay.Name,
            ["width"] = overlay.Width,
            ["height"] = overlay.Height,
            ["layers"] = JsonSerializer.SerializeToNode(overlay.Layers, Json.Options),
            ["preview"] = OverlayServer.PathFor(overlay),
        },
        ["elements"] = new JsonArray([.. (services.Widgets?.Library() ?? []).Select(entry => (JsonNode)new JsonObject
        {
            ["id"] = entry.Element.Id.ToString("D"),
            ["name"] = entry.Element.Name,
            ["type"] = entry.Element.Type,
            ["usedBy"] = new JsonArray([.. entry.UsedBy.Select(o => (JsonNode)o.Name)]),
        })]),
    };

    private async Task LibraryAsync(HttpContext context, Guid elementId, string action)
    {
        var widgets = services.Widgets;
        var json = await OverlayEditor.ReadJsonAsync(context).ConfigureAwait(false);
        string? error = null;
        switch (action)
        {
            case "rename" when widgets is not null:
                if (!widgets.Rename(elementId, json?["name"]?.GetValue<string>() ?? "")) error = "Give it a name";
                break;
            case "duplicate" when widgets is not null:
                if (widgets.Duplicate(elementId) is null) error = "That element no longer exists";
                break;
            case "delete" when widgets is not null:
                var users = widgets.Delete(elementId);
                if (users.Count > 0) error = "Still used on " + string.Join(", ", users.Select(o => o.Name)) + "; remove it from there first";
                break;
            default:
                error = "Unknown library action";
                break;
        }
        // The designer's own overlay comes along, so the dialog and the canvas stay in step.
        var overlay = Get(context.Request.Query["overlay"].ToString());
        var reply = overlay is null ? new JsonObject() : State(overlay);
        if (error is not null) reply["error"] = error;
        await OverlayEditor.JsonAsync(context, reply, error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest).ConfigureAwait(false);
    }

    /// <summary>Saves name, canvas and layers; returns an error message, or null when saved.</summary>
    internal string? Save(Guid overlayId, JsonObject? json)
    {
        if (json?["layers"] is not JsonArray array) return "The layers are missing";
        List<OverlayLayer> layers;
        try
        {
            layers = array.Deserialize<List<OverlayLayer>>(Json.Options) ?? [];
        }
        catch (JsonException ex)
        {
            return "The layers could not be read: " + ex.Message;
        }
        var elements = (services.Elements?.All() ?? []).Select(e => e.Id).ToHashSet();
        var clean = new List<OverlayLayer>();
        var seen = new HashSet<Guid>();
        foreach (var layer in layers)
        {
            if (layer.Id == Guid.Empty || !seen.Add(layer.Id)) layer.Id = Guid.NewGuid();
            if (layer.Type == OverlayLayerTypes.Custom)
            {
                if (layer.ElementId is not { } element || !elements.Contains(element)) continue;   // unknown widget: dropped
            }
            else if (!OverlayLayerTypes.Static.Contains(layer.Type))
            {
                continue;
            }
            else
            {
                layer.ElementId = null;
            }
            layer.Name = string.IsNullOrWhiteSpace(layer.Name) ? layer.Type : layer.Name.Trim();
            layer.Width = Math.Clamp(layer.Width, 1, OverlayStore.MaxSize);
            layer.Height = Math.Clamp(layer.Height, 1, OverlayStore.MaxSize);
            layer.X = Math.Clamp(layer.X, -OverlayStore.MaxSize, OverlayStore.MaxSize);
            layer.Y = Math.Clamp(layer.Y, -OverlayStore.MaxSize, OverlayStore.MaxSize);
            layer.Opacity = Math.Clamp(layer.Opacity, 0, 1);
            layer.Rotation = Math.Clamp(layer.Rotation, -360, 360);
            layer.Enter = Animation(layer.Enter, OverlayLayerTypes.EnterAnimations);
            layer.Exit = Animation(layer.Exit, OverlayLayerTypes.ExitAnimations);
            clean.Add(layer);
        }
        var groups = clean.Where(l => l.GroupId is not null).GroupBy(l => l.GroupId).Where(g => g.Count() < 2).SelectMany(g => g);
        foreach (var lone in groups) lone.GroupId = null;   // a group of one is no group

        var width = json["width"]?.GetValue<int>();
        var height = json["height"]?.GetValue<int>();
        var name = json["name"]?.GetValue<string>();
        services.Overlays!.Update(overlayId, o =>
        {
            o.Layers = clean;
            if (width is { } w) o.Width = w;
            if (height is { } h) o.Height = h;
            if (!string.IsNullOrWhiteSpace(name)) o.Name = name;
        });
        return null;
    }

    private static LayerAnimation Animation(LayerAnimation? animation, IReadOnlyList<string> allowed) => new()
    {
        Name = animation is not null && allowed.Contains(animation.Name) ? animation.Name : "none",
        Duration = Math.Clamp(animation?.Duration ?? 1, 0.1, 10),
    };

    /// <summary>Adds a layer; returns its id, or null when the type or element is unknown.</summary>
    private Guid? Add(Overlay overlay, string type, string? elementId)
    {
        if (OverlayLayerTypes.Static.Contains(type))
        {
            var layer = OverlayLayerTypes.NewStatic(type, overlay);
            services.Overlays!.Update(overlay.Id, o => o.Layers.Add(layer));
            return layer.Id;
        }
        if (services.Widgets is not { } widgets) return null;
        switch (type)
        {
            case "chatbox":
                return widgets.AddChatBox(overlay.Id)?.Id;
            case "chatbox-youtube":
                return widgets.AddChatBox(overlay.Id, "youtube")?.Id;
            case BuiltInWidgets.AlertBoxKind:
                return widgets.AddAlertBox(overlay.Id)?.Id;
            case var kind when BuiltInWidgets.Kinds.Any(k => k.Id == kind):
                return widgets.AddBuiltIn(overlay.Id, kind)?.Id;
            case "element" when Guid.TryParse(elementId, out var id):
                return widgets.AddFromLibrary(overlay.Id, id)?.Id;
            default:
                return null;
        }
    }

    private async Task ImportAsync(HttpContext context, Overlay overlay)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = WidgetCode.MaxBytes * 2L;
        try
        {
            using var copy = new MemoryStream();
            await context.Request.Body.CopyToAsync(copy, context.RequestAborted).ConfigureAwait(false);
            copy.Position = 0;
            var code = WidgetImport.FromZip(copy);
            var name = Path.GetFileNameWithoutExtension(context.Request.Query["name"].ToString()).Replace('_', ' ').Trim();
            var layer = services.Widgets?.AddCustomWidget(overlay.Id, name.Length > 0 ? name : "Widget", code);
            await OverlayEditor.JsonAsync(context, new JsonObject
            {
                ["layer"] = layer?.Id.ToString("D"),
                ["state"] = State(services.Overlays!.Get(overlay.Id)!),
            }).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            await OverlayEditor.JsonAsync(context, new JsonObject { ["error"] = ex.Message }, StatusCodes.Status400BadRequest).ConfigureAwait(false);
        }
    }
}
