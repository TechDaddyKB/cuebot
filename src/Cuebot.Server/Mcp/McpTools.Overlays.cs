using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Mcp;

public static partial class McpTools
{
    private const string OverlayRef = "Overlay id or name";
    private const string LayerRef = "Layer id or name (get_overlay lists them)";

    private static JsonObject PlaceSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "Optional: name, x, y, width, height (overlay pixels), opacity (0-1), rotation (degrees), visible, locked, "
                          + "enter / exit ({name, duration} or just the animation name)",
    };

    /// <summary>Overlays, layers, widgets and their settings, alert box designs, the gallery and StreamElements imports.</summary>
    private static IEnumerable<McpTool> OverlayTools(ApiOperations ops) =>
    [
        new("list_overlays", "List overlays", "Every overlay with its canvas and layers (each layer's type, widget kind, place and look).",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.ListOverlays())),
        new("get_overlay", "Get overlay", "One overlay with its layers.", Schema(["overlay"], ("overlay", Str(OverlayRef))), ReadOnlyHints,
            (a, _) => Done(ops.GetOverlay(ReqStr(a, "overlay")))),
        new("get_overlay_link", "Get overlay link",
            "The link to paste into an OBS browser source for this overlay (with its canvas size). It contains the overlay's secret token.",
            Schema(["overlay"], ("overlay", Str(OverlayRef))), ReadOnlyHints, (a, _) => Done(ops.OverlayLinkOf(ReqStr(a, "overlay")))),
        new("create_overlay", "Create overlay", "Create an empty overlay (a page OBS shows as a browser source).",
            Schema(["name"], ("name", Str("Name")), ("width", Int("Canvas width (default 1920)")), ("height", Int("Canvas height (default 1080)"))), WriteHints,
            (a, _) => Done(ops.CreateOverlay(ReqStr(a, "name"), (int?)OptNum(a, "width") ?? 1920, (int?)OptNum(a, "height") ?? 1080))),
        new("update_overlay", "Rename or resize overlay", "Change an overlay's name or canvas size.",
            Schema(["overlay"], ("overlay", Str(OverlayRef)), ("name", Str("New name")), ("width", Int("Canvas width")), ("height", Int("Canvas height"))),
            WriteHints, (a, _) => Done(ops.UpdateOverlay(ReqStr(a, "overlay"), OptStr(a, "name"), (int?)OptNum(a, "width"), (int?)OptNum(a, "height")))),
        new("delete_overlay", "Delete overlay", "Delete an overlay (its widgets stay in the library). OBS sources using its link stop working.",
            Schema(["overlay"], ("overlay", Str(OverlayRef)), Confirm()), DestructiveHints, (a, _) => Done(ops.DeleteOverlay(ReqStr(a, "overlay"))))
        {
            Confirmation = a => $"Delete the overlay '{ops.FindOverlay(ReqStr(a, "overlay")).Name}'",
        },

        new("list_widget_kinds", "List widget kinds",
            "What can go on an overlay: Cuebot's widgets (alert box, chat box, label, goal, event list, credits, countdown, emote rain, hype cup, "
            + "hype boss, poll, giveaway, media player, charity, products, seasonal) and text, image, video and slideshow layers, plus the animations.",
            Schema([]), ReadOnlyHints, (_, _) => Done(ApiOperations.WidgetKinds())),
        new("add_widget", "Add widget or layer",
            "Add one of Cuebot's widgets (kind from list_widget_kinds) or a text/image/video/slideshow layer to an overlay. settings are its first "
            + "values (a widget's field keys from get_widget, or the layer's settings); place sets where it goes.",
            Schema(["overlay", "kind"], ("overlay", Str(OverlayRef)), ("kind", Str("alertbox, chatbox, label, goal, ..., or text, image, video, slideshow")),
                ("settings", new JsonObject { ["type"] = "object", ["description"] = "First settings, e.g. {\"type\": \"follower\", \"prefix\": \"Latest follower: \"} for a label" }),
                ("place", PlaceSchema())), WriteHints,
            (a, _) => Done(ops.AddLayer(ReqStr(a, "overlay"), ReqStr(a, "kind"), a["settings"] as JsonObject, a["place"] as JsonObject))),
        new("add_custom_widget", "Add custom widget",
            "Add a widget from code (StreamElements widget API: HTML, CSS, JS, fields JSON and data JSON; Streamlabs custom code works too). "
            + "Fields become its settings form; {fieldKey} placeholders in the panes are filled in.",
            Schema(["overlay", "name", "code"], ("overlay", Str(OverlayRef)), ("name", Str("Widget name")),
                ("code", new JsonObject { ["type"] = "object", ["description"] = "{html, css, js, fields, data}: text panes; fields and data are JSON objects as text" }),
                ("place", PlaceSchema())), WriteHints,
            (a, _) => Done(ops.AddCustomWidget(ReqStr(a, "overlay"), ReqStr(a, "name"), a["code"] as JsonObject ?? throw Missing("code"), a["place"] as JsonObject))),
        new("update_layer", "Move or restyle layer",
            "Change a layer's place, size, opacity, rotation, visibility, lock, name or enter/exit animation; for text, image, video and slideshow "
            + "layers also their settings (merged).",
            Schema(["overlay", "layer", "changes"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef)),
                ("changes", new JsonObject { ["type"] = "object", ["description"] = "Fields as in place, plus settings for static layers" })), WriteHints,
            (a, _) => Done(ops.UpdateLayer(ReqStr(a, "overlay"), ReqStr(a, "layer"), a["changes"] as JsonObject ?? throw Missing("changes")))),
        new("remove_layer", "Remove layer", "Take a layer off an overlay (a widget stays in the library).",
            Schema(["overlay", "layer"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef))), WriteHints,
            (a, _) => Done(ops.RemoveLayer(ReqStr(a, "overlay"), ReqStr(a, "layer")))),

        new("get_widget", "Get widget settings",
            "A widget's settings: every field (type, label, options, min/max) with its current value. includeCode adds its HTML/CSS/JS. "
            + "Alert box designs come from get_alert_designs.",
            Schema(["overlay", "layer"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef)), ("includeCode", Bool("Include the code panes"))), ReadOnlyHints,
            (a, _) => Done(ops.GetWidget(ReqStr(a, "overlay"), ReqStr(a, "layer"), OptBool(a, "includeCode") ?? false))),
        new("update_widget_settings", "Change widget settings",
            "Set widget settings by field key (from get_widget), e.g. {\"fontSize\": 40, \"textColor\": \"#ff0\"}. Overlays update at once.",
            Schema(["overlay", "layer", "values"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef)),
                ("values", new JsonObject { ["type"] = "object", ["description"] = "Field key → value" })), WriteHints,
            (a, _) => Done(ops.UpdateWidgetSettings(ReqStr(a, "overlay"), ReqStr(a, "layer"), a["values"] as JsonObject ?? throw Missing("values")))),
        new("update_widget_code", "Change widget code",
            "Replace a widget's code panes: html, css, js, fields (JSON text) and/or cssOverride (CSS applied after the widget's own).",
            Schema(["overlay", "layer", "code"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef)),
                ("code", new JsonObject { ["type"] = "object", ["description"] = "{html, css, js, fields, cssOverride}: only the panes to replace" })), WriteHints,
            (a, _) => Done(ops.UpdateWidgetCode(ReqStr(a, "overlay"), ReqStr(a, "layer"), a["code"] as JsonObject ?? throw Missing("code")))),

        new("get_alert_designs", "Get alert box designs",
            "An alert box's designs: for each alert type (follower, subscriber, tip, cheer, raid, superchat, kicks, redemption, jewels, "
            + "charityCampaignDonation, merch, custom) whether it is on, its design and its variations, plus what each design field means.",
            Schema(["overlay", "layer"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef))), ReadOnlyHints,
            (a, _) => Done(ops.GetAlertDesigns(ReqStr(a, "overlay"), ReqStr(a, "layer")))),
        new("update_alert_designs", "Change alert box designs",
            "Turn alert types on or off and change their designs (fields merged) or variations (replaced). Example: "
            + "{\"types\": {\"raid\": {\"enabled\": true, \"design\": {\"template\": \"{name} raided with {amount}!\", \"sound\": \"chime\"}}}}. "
            + "Try it with emulate_event.",
            Schema(["overlay", "layer"], ("overlay", Str(OverlayRef)), ("layer", Str(LayerRef)),
                ("types", new JsonObject { ["type"] = "object", ["description"] = "Alert type → {enabled, design: {...}, variations: [...]}" }),
                ("randomPick", Bool("Pick a random matching variation instead of the first"))), WriteHints,
            (a, _) => Done(ops.UpdateAlertDesigns(ReqStr(a, "overlay"), ReqStr(a, "layer"), a["types"] as JsonObject, OptBool(a, "randomPick")))),

        new("list_gallery", "List the community gallery", "Overlays and widgets shared in Cuebot's community gallery (each reviewed before it is listed).",
            Schema([]), new McpToolHints(ReadOnly: true, OpenWorld: true), async (_, ct) => await ops.GalleryAsync(ct).ConfigureAwait(false)),
        new("import_gallery_item", "Import from the gallery",
            "Import a gallery item: an overlay item becomes a new overlay; a widget item is added to the given overlay.",
            Schema(["id"], ("id", Str("Item id (list_gallery)")), ("overlay", Str("For widget items: the overlay to add it to"))),
            new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.ImportGalleryItemAsync(ReqStr(a, "id"), OptStr(a, "overlay"), ct).ConfigureAwait(false)),
        new("list_streamelements_overlays", "List StreamElements overlays", "Overlays in the connected StreamElements account (read only).",
            Schema([]), new McpToolHints(ReadOnly: true, OpenWorld: true), async (_, ct) => await ops.StreamElementsOverlaysAsync(ct).ConfigureAwait(false)),
        new("import_streamelements_overlay", "Import a StreamElements overlay",
            "Copy one StreamElements overlay into a new Cuebot overlay (custom widgets, text, images, labels and the alert box). StreamElements is not changed.",
            Schema(["id"], ("id", Str("StreamElements overlay id (list_streamelements_overlays)"))), new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.ImportStreamElementsOverlayAsync(ReqStr(a, "id"), ct).ConfigureAwait(false)),
    ];
}
