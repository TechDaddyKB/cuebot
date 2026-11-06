using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Core.Storage;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Uploads;
using static Cuebot.Portal.Overlays.OverlayValues;

namespace Cuebot.Portal.Overlays;

/// <summary>
/// Overlays and widgets in the portal (docs/MOD_PORTAL_PLAN.md, phase 6). With <see cref="PortalPermission.EditOverlays"/>
/// a mod moves, resizes, shows and hides layers, edits text, image, video and slideshow layers and widget settings
/// (alert designs included); with <see cref="PortalPermission.CreateOverlays"/> also adds and removes layers and
/// overlays. Widget code, CSS and hidden settings are never theirs to change. Files are uploads or files the
/// overlays already use, never addresses on the internet. Changes are "overlay" and "widget" change areas, so
/// approval and undo work as for actions. Overlay links (their tokens) never reach the portal.
/// </summary>
public sealed class PortalOverlays(OverlayStore overlays, ElementStore elements, WidgetCodeStore code, AssetStore assets,
    PortalChanges changes, PortalMediaStore? media = null)
{
    public const int MaxOverlays = 100;
    public const int MaxLayers = 100;
    public const int MaxSlides = 50;
    public const int MaxName = 100;

    private static readonly string[] Fits = ["contain", "cover", "fill"];
    private static readonly string[] Aligns = ["left", "center", "right"];
    private static readonly string[] Weights = ["100", "200", "300", "400", "500", "600", "700", "800", "900", "normal", "bold"];
    private static readonly string[] Transitions = ["fade", "none"];
    private static readonly string[] AlertLayouts = ["above", "side", "overlay"];
    private static readonly string[] Images = ["png", "jpg", "jpeg", "gif", "webp", "avif", "apng"];
    private static readonly string[] Videos = ["mp4", "webm", "mov"];
    private static readonly string[] Sounds = ["mp3", "ogg", "oga", "wav", "m4a", "flac", "opus"];

    /// <summary>Widget field types a mod may set (others, such as hidden and button, are the streamer's).</summary>
    private static readonly HashSet<string> EditableTypes =
        ["text", "textfield", "number", "slider", "colorpicker", "dropdown", "checkbox", "googleFont", "image-input", "video-input", "sound-input"];

    /// <summary>Types whose values are text pasted into the code, so where the widget pastes them matters.</summary>
    private static readonly HashSet<string> PastedTypes = ["text", "textfield", "colorpicker", "googleFont", "image-input", "video-input", "sound-input"];

    private readonly Dictionary<Guid, string> _uploadAssets = [];

    public static bool Handles(string op) => op.StartsWith("overlays.", StringComparison.Ordinal) || op == "widgets.save";

    public async Task<JsonObject> HandleAsync(PortalMember member, PortalRole? role, string op, JsonObject request)
    {
        if (!PortalPolicy.Has(role, PortalPermission.EditOverlays) && !PortalPolicy.Has(role, PortalPermission.CreateOverlays))
            return Error("Your role does not include overlays.");
        var create = PortalPolicy.Has(role, PortalPermission.CreateOverlays);
        return op switch
        {
            "overlays.list" => List(create),
            "overlays.get" => Get(request, create),
            "overlays.save" => await SaveAsync(member, role, request).ConfigureAwait(false),
            "overlays.create" when create => await CreateAsync(member, request).ConfigureAwait(false),
            "overlays.delete" when create => await DeleteAsync(member, request).ConfigureAwait(false),
            "widgets.save" when PortalPolicy.Has(role, PortalPermission.EditOverlays) => await SaveWidgetAsync(member, request).ConfigureAwait(false),
            "overlays.create" or "overlays.delete" or "widgets.save" => Error("Your role does not include this."),
            _ => Error($"Unknown request '{op}'."),
        };
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    private JsonObject List(bool create) => Ok(new JsonObject
    {
        ["overlays"] = new JsonArray([.. overlays.All().OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).Select(o => (JsonNode)new JsonObject
        {
            ["id"] = o.Id.ToString(), ["name"] = o.Name, ["width"] = o.Width, ["height"] = o.Height, ["layers"] = o.Layers.Count,
        })]),
        ["canCreate"] = create,
    });

    private JsonObject Get(JsonObject request, bool create)
    {
        if (!Guid.TryParse((string?)request["id"], out var id) || overlays.Get(id) is not { } overlay) return NotFound();
        var all = overlays.All();
        var layers = new JsonArray();
        foreach (var layer in overlay.Layers)
        {
            var node = (JsonObject)PortalChanges.ToNode(layer);
            if (layer.Settings is { } s && OverlayLayerTypes.Static.Contains(layer.Type)) node["settings"] = StaticToPortal(layer.Type, s);
            else node.Remove("settings");
            layers.Add(node);
        }
        var widgets = new JsonArray();
        foreach (var elementId in overlay.Layers.Where(l => l.Type == OverlayLayerTypes.Custom && l.ElementId is not null).Select(l => l.ElementId!.Value).Distinct())
            if (elements.Get(elementId) is { } element) widgets.Add(WidgetInfo(element, all));
        return Ok(new JsonObject
        {
            ["overlay"] = new JsonObject
            {
                ["id"] = overlay.Id.ToString(), ["name"] = overlay.Name, ["width"] = overlay.Width, ["height"] = overlay.Height, ["layers"] = layers,
            },
            ["widgets"] = widgets,
            ["canCreate"] = create,
            ["enter"] = new JsonArray([.. OverlayLayerTypes.EnterAnimations.Select(a => (JsonNode)a)]),
            ["exit"] = new JsonArray([.. OverlayLayerTypes.ExitAnimations.Select(a => (JsonNode)a)]),
            ["add"] = create ? AddCatalog() : null,
        });
    }

    /// <summary>What a mod who may create can add: the static layers, Cuebot's widgets and widgets already in the library.</summary>
    private JsonObject AddCatalog() => new()
    {
        ["static"] = new JsonArray([.. OverlayLayerTypes.Static.Order().Select(t => (JsonNode)t)]),
        ["widgets"] = new JsonArray([.. NewWidgetKinds().Select(k => (JsonNode)new JsonObject { ["kind"] = k.Kind, ["name"] = k.Name })]),
        ["library"] = new JsonArray([.. elements.All().Where(e => e.Type == OverlayLayerTypes.Custom).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => (JsonNode)new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name })]),
    };

    private JsonObject WidgetInfo(OverlayElement element, IReadOnlyList<Overlay> all)
    {
        var widget = code.Read(element.Id);
        var values = widget.FieldData();
        var fields = new JsonArray();
        foreach (var (key, f) in WidgetCode.ParseObject(widget.Fields))
        {
            if (f is not JsonObject field) continue;
            var type = String(field["type"]) ?? "";
            if (type is "hidden" or "button") continue;
            var why = !EditableTypes.Contains(type) ? "Only the streamer can change this kind of setting."
                : PastedTypes.Contains(type) ? UnsafePlacement(widget, key) : null;
            var value = values[key]?.DeepClone();
            if (MediaKinds(type) is { } && String(value) is { } url) value = MediaToPortal(url);
            fields.Add(new JsonObject
            {
                ["key"] = key, ["type"] = type, ["label"] = String(field["label"]) ?? key, ["group"] = String(field["group"]) ?? "",
                ["value"] = value, ["options"] = field["options"]?.DeepClone(), ["min"] = field["min"]?.DeepClone(), ["max"] = field["max"]?.DeepClone(),
                ["step"] = field["step"]?.DeepClone(), ["editable"] = why is null, ["why"] = why,
            });
        }
        var info = new JsonObject
        {
            ["id"] = element.Id.ToString(),
            ["name"] = element.Name,
            ["kind"] = BuiltInWidgets.KindOf(element),
            ["usedBy"] = new JsonArray([.. all.Where(o => o.Layers.Any(l => l.ElementId == element.Id)).Select(o => (JsonNode)o.Name)]),
            ["fields"] = fields,
        };
        if (BuiltInWidgets.IsAlertBox(element) && values["alerts"] is JsonObject alerts) info["alerts"] = AlertsMedia(alerts.DeepClone().AsObject(), MediaToPortal);
        return info;
    }

    // ---- Overlays ---------------------------------------------------------------------------------------------------

    private async Task<JsonObject> CreateAsync(PortalMember member, JsonObject request)
    {
        if (overlays.All().Count >= MaxOverlays) return Error($"There are already {MaxOverlays} overlays.");
        var name = (String(request["name"]) ?? "").Trim();
        if (name.Length is 0 or > MaxName) return Error("Give the overlay a name (up to 100 characters).");
        var width = (int)Math.Clamp(Number(request["width"]) ?? 1920, OverlayStore.MinSize, OverlayStore.MaxSize);
        var height = (int)Math.Clamp(Number(request["height"]) ?? 1080, OverlayStore.MinSize, OverlayStore.MaxSize);
        var overlay = new Overlay { Name = name, Width = width, Height = height, Token = OverlayStore.NewToken() };
        var result = await changes.SubmitAsync(member, OverlayArea.AreaName, overlay.Id, OverlayArea.Node(overlay)).ConfigureAwait(false);
        var answer = Submitted(result);
        answer["id"] = overlay.Id.ToString();
        return answer;
    }

    private async Task<JsonObject> DeleteAsync(PortalMember member, JsonObject request)
    {
        if (!Guid.TryParse((string?)request["id"], out var id) || overlays.Get(id) is null) return NotFound();
        return Submitted(await changes.SubmitAsync(member, OverlayArea.AreaName, id, null).ConfigureAwait(false));
    }

    private async Task<JsonObject> SaveAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (request["overlay"] is not JsonObject node || !Guid.TryParse(String(node["id"]), out var id) || overlays.Get(id) is not { } before)
            return NotFound();
        var create = PortalPolicy.Has(role, PortalPermission.CreateOverlays);
        if (!PortalPolicy.Has(role, PortalPermission.EditOverlays) && !create) return Error("Your role does not include this.");
        var problems = new List<string>();
        var after = Json.Clone(before);

        if (String(node["name"]) is { } name && name.Trim() != before.Name)
        {
            if (name.Trim().Length is 0 or > MaxName || name.Any(char.IsControl)) problems.Add("Give the overlay a name (up to 100 characters).");
            else after.Name = name.Trim();
        }
        var width = Number(node["width"]);
        var height = Number(node["height"]);
        if ((width is { } w && (int)w != before.Width) || (height is { } h && (int)h != before.Height))
        {
            if (!create) problems.Add("Only roles that may create overlays can change an overlay's size.");
            else
            {
                after.Width = (int)Math.Clamp(width ?? before.Width, OverlayStore.MinSize, OverlayStore.MaxSize);
                after.Height = (int)Math.Clamp(height ?? before.Height, OverlayStore.MinSize, OverlayStore.MaxSize);
            }
        }

        var layers = new List<OverlayLayer>();
        var seen = new HashSet<Guid>();
        foreach (var item in node["layers"] as JsonArray ?? [])
        {
            if (item is not JsonObject l) continue;
            var existing = Guid.TryParse(String(l["id"]), out var layerId) ? before.Layers.FirstOrDefault(x => x.Id == layerId) : null;
            if (existing is not null && !seen.Add(existing.Id)) continue;
            if (existing is null)
            {
                if (!create) { problems.Add("Only roles that may create overlays can add layers."); continue; }
                if (await NewLayerAsync(after, l, problems).ConfigureAwait(false) is { } added)
                {
                    seen.Add(added.Id);
                    layers.Add(added);
                }
                continue;
            }
            var copy = Json.Clone(existing);
            await ApplyLayerAsync(copy, l, problems).ConfigureAwait(false);
            layers.Add(copy);
        }
        if (before.Layers.Any(l => !seen.Contains(l.Id)) && !create) problems.Add("Only roles that may create overlays can remove layers.");
        if (layers.Count > MaxLayers) problems.Add($"An overlay can have up to {MaxLayers} layers.");
        if (problems.Count > 0) return Refused(problems);
        after.Layers = layers;
        var value = OverlayArea.Node(after);
        if (JsonNode.DeepEquals(value, OverlayArea.Node(before))) return Unchanged();
        return Submitted(await changes.SubmitAsync(member, OverlayArea.AreaName, id, value).ConfigureAwait(false));
    }

    /// <summary>A layer a mod adds: a static one, a widget already in the library, or a new one of Cuebot's widgets.</summary>
    private async Task<OverlayLayer?> NewLayerAsync(Overlay overlay, JsonObject node, List<string> problems)
    {
        var type = String(node["type"]) ?? "";
        OverlayLayer layer;
        if (OverlayLayerTypes.Static.Contains(type)) layer = OverlayLayerTypes.NewStatic(type, overlay);
        else if (type == OverlayLayerTypes.Custom && Guid.TryParse(String(node["elementId"]), out var elementId)
                 && elements.Get(elementId) is { Type: OverlayLayerTypes.Custom } element)
            layer = Centred(new OverlayLayer { Name = element.Name, Type = type, ElementId = element.Id }, overlay, overlay.Width, overlay.Height);
        else if (type == OverlayLayerTypes.Custom && String(node["widget"]) is { } kind && NewWidgetKinds().FirstOrDefault(k => k.Kind == kind) is { Kind: not null } k)
            // The element is made when the change goes live (OverlayArea), so a rejected change leaves nothing behind.
            layer = Centred(new OverlayLayer { Name = k.Name, Type = type, ElementId = Guid.NewGuid(), Settings = new JsonObject { [OverlayArea.NewWidget] = kind } },
                overlay, 640, 360);
        else
        {
            problems.Add("That kind of layer cannot be added from the portal.");
            return null;
        }
        await ApplyLayerAsync(layer, node, problems).ConfigureAwait(false);
        return layer;
    }

    private static OverlayLayer Centred(OverlayLayer layer, Overlay overlay, double width, double height)
    {
        (layer.Width, layer.Height) = (Math.Min(width, overlay.Width), Math.Min(height, overlay.Height));
        (layer.X, layer.Y) = (Math.Round((overlay.Width - layer.Width) / 2), Math.Round((overlay.Height - layer.Height) / 2));
        return layer;
    }

    /// <summary>Applies the position, look and (for static layers) settings a mod sent; type, widget and group stay.</summary>
    private async Task ApplyLayerAsync(OverlayLayer layer, JsonObject node, List<string> problems)
    {
        if (String(node["name"]) is { } name && name.Trim() != layer.Name)
        {
            if (name.Trim().Length is 0 or > MaxName || name.Any(char.IsControl)) problems.Add("Layer names can be up to 100 characters.");
            else layer.Name = name.Trim();
        }
        double Clamp(JsonNode? n, double current, double min, double max) => Number(n) is { } d ? Math.Clamp(d, min, max) : current;
        layer.X = Clamp(node["x"], layer.X, -OverlayStore.MaxSize, OverlayStore.MaxSize);
        layer.Y = Clamp(node["y"], layer.Y, -OverlayStore.MaxSize, OverlayStore.MaxSize);
        layer.Width = Clamp(node["width"], layer.Width, 1, OverlayStore.MaxSize);
        layer.Height = Clamp(node["height"], layer.Height, 1, OverlayStore.MaxSize);
        layer.Opacity = Clamp(node["opacity"], layer.Opacity, 0, 1);
        layer.Rotation = Clamp(node["rotation"], layer.Rotation, -360, 360);
        layer.Visible = Bool(node["visible"]) ?? layer.Visible;
        layer.Locked = Bool(node["locked"]) ?? layer.Locked;
        layer.Enter = Animation(node["enter"], layer.Enter, OverlayLayerTypes.EnterAnimations, problems);
        layer.Exit = Animation(node["exit"], layer.Exit, OverlayLayerTypes.ExitAnimations, problems);
        if (OverlayLayerTypes.Static.Contains(layer.Type) && node["settings"] is JsonObject settings)
            layer.Settings = await StaticSettingsAsync(layer.Type, layer.Settings ?? [], settings, problems).ConfigureAwait(false);
    }

    private static LayerAnimation Animation(JsonNode? node, LayerAnimation current, IReadOnlyList<string> allowed, List<string> problems)
    {
        if (node is not JsonObject a) return current;
        var name = String(a["name"]) ?? current.Name;
        if (!allowed.Contains(name)) { problems.Add($"'{name}' is not an animation layers can use."); name = current.Name; }
        return new LayerAnimation { Name = name, Duration = Math.Clamp(Number(a["duration"]) ?? current.Duration, 0.1, 10) };
    }

    /// <summary>A static layer's settings after a mod's edit: changed keys are checked one by one, the rest kept.</summary>
    private async Task<JsonObject> StaticSettingsAsync(string type, JsonObject current, JsonObject incoming, List<string> problems)
    {
        var result = current.DeepClone().AsObject();
        foreach (var (key, raw) in incoming)
        {
            JsonNode? value = raw?.DeepClone();
            if ((type, key) is (OverlayLayerTypes.Image, "url") or (OverlayLayerTypes.Video, "url"))
                value = await MediaFromPortalAsync(String(value), type == OverlayLayerTypes.Image ? Images : Videos, problems).ConfigureAwait(false);
            else if ((type, key) is (OverlayLayerTypes.Slideshow, "urls") && value is JsonArray urls)
            {
                if (urls.Count > MaxSlides) { problems.Add($"A slideshow can have up to {MaxSlides} pictures."); continue; }
                var list = new JsonArray();
                foreach (var u in urls) list.Add(await MediaFromPortalAsync(String(u), Images, problems).ConfigureAwait(false));
                value = list;
            }
            if (JsonNode.DeepEquals(current[key], value)) continue;
            var ok = (type, key) switch
            {
                (OverlayLayerTypes.Text, "text") => String(value) is { Length: <= MaxText } t && !t.Any(c => char.IsControl(c) && c != '\n'),
                (OverlayLayerTypes.Text, "font") => String(value) is { } f && IsFont(f),
                (OverlayLayerTypes.Text, "size") => Number(value) is >= 1 and <= 1000,
                (OverlayLayerTypes.Text, "color") => String(value) is { } c && IsColor(c),
                (OverlayLayerTypes.Text, "weight") => Weights.Contains(String(value) ?? Number(value)?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (OverlayLayerTypes.Text, "align") => Aligns.Contains(String(value)),
                (OverlayLayerTypes.Text, "shadow") => Bool(value) is not null,
                (OverlayLayerTypes.Text, "background") => String(value) is { } b && (b.Length == 0 || IsColor(b)),
                (OverlayLayerTypes.Text, "padding") => Number(value) is >= 0 and <= 500,
                (OverlayLayerTypes.Image or OverlayLayerTypes.Video, "url") => value is not null,
                (OverlayLayerTypes.Image or OverlayLayerTypes.Video or OverlayLayerTypes.Slideshow, "fit") => Fits.Contains(String(value)),
                (OverlayLayerTypes.Video, "loop" or "muted") => Bool(value) is not null,
                (OverlayLayerTypes.Video, "volume") => Number(value) is >= 0 and <= 100,
                (OverlayLayerTypes.Slideshow, "urls") => value is JsonArray a && a.All(x => x is not null),
                (OverlayLayerTypes.Slideshow, "seconds") => Number(value) is >= 1 and <= 3600,
                (OverlayLayerTypes.Slideshow, "transition") => Transitions.Contains(String(value)),
                _ => false,
            };
            if (ok) result[key] = value;
            else if (value is not null || key is not ("url" or "urls")) problems.Add($"The {type} layer's '{key}' setting cannot be set to that.");
        }
        return result;
    }

    private JsonObject StaticToPortal(string type, JsonObject settings)
    {
        var copy = settings.DeepClone().AsObject();
        if (type is OverlayLayerTypes.Image or OverlayLayerTypes.Video && String(copy["url"]) is { } url) copy["url"] = MediaToPortal(url);
        if (type == OverlayLayerTypes.Slideshow && copy["urls"] is JsonArray urls)
            copy["urls"] = new JsonArray([.. urls.Select(u => (JsonNode?)(String(u) is { } s ? MediaToPortal(s) : u?.DeepClone()))]);
        return copy;
    }

    // ---- Widgets ----------------------------------------------------------------------------------------------------

    private async Task<JsonObject> SaveWidgetAsync(PortalMember member, JsonObject request)
    {
        if (!Guid.TryParse((string?)request["id"], out var id) || elements.Get(id) is not { Type: OverlayLayerTypes.Custom } element) return NotFound();
        if (request["data"] is not JsonObject incoming) return Error("No settings given.");
        var widget = code.Read(id);
        var fields = WidgetCode.ParseObject(widget.Fields);
        var values = widget.FieldData();
        var data = WidgetCode.ParseObject(widget.Data);
        var problems = new List<string>();
        foreach (var (key, raw) in incoming)
        {
            var field = fields[key] as JsonObject;
            var type = String(field?["type"]) ?? "";
            if (key == "alerts" && BuiltInWidgets.IsAlertBox(element) && values["alerts"] is JsonObject currentAlerts)
            {
                if (await AlertsAsync(currentAlerts, raw, problems).ConfigureAwait(false) is { } alerts && !JsonNode.DeepEquals(alerts, currentAlerts))
                    data["alerts"] = alerts;
                continue;
            }
            JsonNode? value = raw?.DeepClone();
            if (MediaKinds(type) is { } kinds) value = await MediaFromPortalAsync(String(value), kinds, problems).ConfigureAwait(false);
            if (JsonNode.DeepEquals(values[key], value)) continue;
            var label = String(field?["label"]) ?? key;
            if (field is null || !EditableTypes.Contains(type)) { problems.Add($"'{label}' is not a setting mods can change."); continue; }
            if (PastedTypes.Contains(type) && UnsafePlacement(widget, key) is { } why) { problems.Add($"'{label}': {why}"); continue; }
            if (FieldValue(field, type, value, out var problem) is { } clean) data[key] = clean;
            else problems.Add($"'{label}': {problem}");
        }
        if (problems.Count > 0) return Refused(problems);
        if (JsonNode.DeepEquals(data, WidgetCode.ParseObject(widget.Data))) return Unchanged();
        var after = new JsonObject { ["id"] = id.ToString(), ["name"] = element.Name, ["data"] = data };
        return Submitted(await changes.SubmitAsync(member, WidgetArea.AreaName, id, after).ConfigureAwait(false));
    }

    /// <summary>A widget field value checked against its field, or null with the reason.</summary>
    private static JsonNode? FieldValue(JsonObject field, string type, JsonNode? value, out string? problem)
    {
        problem = null;
        switch (type)
        {
            case "text" or "textfield":
                if (String(value) is not { } text) break;
                return CleanText(text, type == "textfield", out problem) is { } clean ? JsonValue.Create(clean) : null;
            case "number" or "slider":
                if (Number(value) is not { } n) break;
                if (Number(field["min"]) is { } min && n < min || Number(field["max"]) is { } max && n > max) { problem = "That number is out of range."; return null; }
                return JsonValue.Create(n);
            case "colorpicker":
                if (String(value) is { } c && IsColor(c)) return JsonValue.Create(c.Trim());
                problem = "That is not a colour.";
                return null;
            case "dropdown":
                if (field["options"] is JsonObject options && value is JsonValue && options.ContainsKey(value.ToString())) return value.DeepClone();
                problem = "Pick one of the choices.";
                return null;
            case "checkbox":
                if (Bool(value) is { } b) return JsonValue.Create(b);
                break;
            case "googleFont":
                if (String(value) is { } f && IsFont(f)) return JsonValue.Create(f);
                problem = "That is not a font name.";
                return null;
            case "image-input" or "video-input" or "sound-input":
                return value;   // already resolved to an upload or a file the overlays use
        }
        problem ??= "That value does not fit this setting.";
        return null;
    }

    /// <summary>
    /// The alert box's designs after a mod's edit: each alert type's on/off and design, and each variation's on/off and
    /// design. Adding, removing or changing when a variation is picked stays with the streamer.
    /// </summary>
    private async Task<JsonObject?> AlertsAsync(JsonObject current, JsonNode? incoming, List<string> problems)
    {
        if (incoming is not JsonObject alerts) { problems.Add("The alert designs could not be read."); return null; }
        var result = current.DeepClone().AsObject();
        foreach (var (key, value) in alerts)
        {
            if (key == "types") continue;
            if (key == "randomPick" && Bool(value) is { } random) result[key] = random;
            else if (!JsonNode.DeepEquals(current[key], value)) problems.Add("Only the streamer can change how alerts are picked.");
        }
        if (alerts["types"] is not JsonObject types || result["types"] is not JsonObject resultTypes) return result;
        foreach (var (type, node) in types)
        {
            if (resultTypes[type] is not JsonObject target || node is not JsonObject t) { problems.Add($"There is no '{type}' alert."); continue; }
            if (Bool(t["enabled"]) is { } enabled) target["enabled"] = enabled;
            if (target["design"] is JsonObject design && t["design"] is JsonObject d) target["design"] = await DesignAsync(design, d, problems).ConfigureAwait(false);
            if (t["variations"] is not JsonArray variations) continue;
            if (target["variations"] is not JsonArray targetVariations || targetVariations.Count != variations.Count)
            {
                problems.Add("Only the streamer can add or remove alert variations.");
                continue;
            }
            for (var i = 0; i < variations.Count; i++)
            {
                if (targetVariations[i] is not JsonObject tv || variations[i] is not JsonObject v) continue;
                foreach (var (vk, vv) in v)
                {
                    if (vk == "enabled" && Bool(vv) is { } on) tv["enabled"] = on;
                    else if (vk == "design" && tv["design"] is JsonObject vd && vv is JsonObject nd) tv["design"] = await DesignAsync(vd, nd, problems).ConfigureAwait(false);
                    else if (vk is not ("enabled" or "design") && !JsonNode.DeepEquals(tv[vk], vv)) problems.Add("Only the streamer can change when a variation is used.");
                }
            }
        }
        return result;
    }

    private async Task<JsonObject> DesignAsync(JsonObject current, JsonObject incoming, List<string> problems)
    {
        var result = current.DeepClone().AsObject();
        foreach (var (key, raw) in incoming)
        {
            JsonNode? value = raw?.DeepClone();
            if (key == "media") value = await MediaFromPortalAsync(String(value), [.. Images, .. Videos], problems).ConfigureAwait(false);
            else if (key == "sound" && String(value) is not ("chime" or "none" or "" or null)) value = await MediaFromPortalAsync(String(value), Sounds, problems).ConfigureAwait(false);
            if (JsonNode.DeepEquals(current[key], value)) continue;
            var ok = key switch
            {
                "template" or "ttsText" => String(value) is { Length: <= 300 } t && !t.Any(char.IsControl),
                "ttsAlias" => String(value) is { Length: <= 60 } a && !a.Any(char.IsControl),
                "showMessage" or "tts" => Bool(value) is not null,
                "duration" => Number(value) is >= 1 and <= 120,
                "mediaVolume" or "volume" => Number(value) is >= 0 and <= 100,
                "size" => Number(value) is >= 8 and <= 300,
                "messageSize" => Number(value) is >= 8 and <= 200,
                "media" or "sound" => value is not null,
                "layout" => AlertLayouts.Contains(String(value)),
                "font" => String(value) is { } f && IsFont(f),
                "color" or "highlight" => String(value) is { } c && IsColor(c),
                "enter" => OverlayLayerTypes.EnterAnimations.Contains(String(value) ?? ""),
                "exit" => OverlayLayerTypes.ExitAnimations.Contains(String(value) ?? ""),
                _ => false,
            };
            if (ok) result[key] = value;
            else if (value is not null || key is not ("media" or "sound")) problems.Add($"The alert's '{key}' cannot be set to that.");
        }
        return result;
    }

    private static JsonObject AlertsMedia(JsonObject alerts, Func<string, string> map)
    {
        foreach (var (_, t) in alerts["types"] as JsonObject ?? [])
        {
            Map(t?["design"] as JsonObject);
            foreach (var v in t?["variations"] as JsonArray ?? []) Map(v?["design"] as JsonObject);
        }
        return alerts;

        void Map(JsonObject? design)
        {
            if (design is null) return;
            foreach (var key in new[] { "media", "sound" })
                if (String(design[key]) is { Length: > 0 } url && url is not ("chime" or "none")) design[key] = map(url);
        }
    }

    // ---- Files ------------------------------------------------------------------------------------------------------

    private static string[]? MediaKinds(string fieldType) => fieldType switch
    {
        "image-input" => Images,
        "video-input" => Videos,
        "sound-input" => Sounds,
        _ => null,
    };

    /// <summary>"upload:&lt;id&gt;" for a file the overlays use that came from an upload; other values as they are.</summary>
    private string MediaToPortal(string url)
    {
        if (media is null || !url.StartsWith("/assets/", StringComparison.Ordinal)) return url;
        var name = url["/assets/".Length..];
        foreach (var item in media.Items)
            if (UploadAsset(item) == name) return PortalFiles.Prefix + item.Id;
        return url;
    }

    /// <summary>
    /// A file value from the portal: empty, an upload (copied to the overlay files on first use) or a file the overlays
    /// already have, of the right type. Anything else (an internet address, a path) is refused: null and a problem.
    /// </summary>
    private async Task<JsonNode?> MediaFromPortalAsync(string? value, string[] extensions, List<string> problems)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return JsonValue.Create("");
        if (value.StartsWith(PortalFiles.Prefix, StringComparison.Ordinal) && Guid.TryParse(value[PortalFiles.Prefix.Length..], out var uploadId)
            && media?.Get(uploadId) is { } item)
        {
            var extension = Path.GetExtension(item.File).TrimStart('.').ToLowerInvariant();
            if (!extensions.Contains(extension)) { problems.Add($"'{item.Name}' is not the right kind of file for that."); return null; }
            await using var file = File.OpenRead(media.FullPath(item));
            return JsonValue.Create("/assets/" + await assets.AddAsync(file, item.File).ConfigureAwait(false));
        }
        if (value.StartsWith("/assets/", StringComparison.Ordinal) && assets.PathOf(value["/assets/".Length..]) is not null
            && extensions.Contains(Path.GetExtension(value).TrimStart('.').ToLowerInvariant()))
            return JsonValue.Create(value);
        problems.Add("Pick one of the uploads (Uploads page): mods cannot use other files or internet addresses in overlays.");
        return null;
    }

    /// <summary>The overlay file name an upload has (or would have) once copied: SHA-256 of its content plus its type.</summary>
    private string? UploadAsset(PortalMediaItem item)
    {
        lock (_uploadAssets)
            if (_uploadAssets.TryGetValue(item.Id, out var known)) return known;
        string name;
        try
        {
            using var file = File.OpenRead(media!.FullPath(item));
            name = Convert.ToHexStringLower(SHA256.HashData(file)) + Path.GetExtension(item.File).ToLowerInvariant();
        }
        catch (IOException) { return null; }
        lock (_uploadAssets) _uploadAssets[item.Id] = name;
        return name;
    }

    /// <summary>Cuebot's own widgets a mod may add: the chat box, the alert box and the built-in kinds.</summary>
    internal static IEnumerable<(string Kind, string Name)> NewWidgetKinds() =>
        [("chatbox", "Chat box"), (BuiltInWidgets.AlertBoxKind, "Alert box"), .. BuiltInWidgets.Kinds.Select(k => (k.Id, k.Name))];

    private static JsonObject Submitted((ChangeResult Result, PortalChange Change) r) => Ok(new JsonObject
    {
        ["change"] = r.Change.Id.ToString(),
        ["status"] = r.Result == ChangeResult.Pending ? "pending" : "live",
    });

    /// <summary>Nothing differed from what is there, so no change was recorded.</summary>
    private static JsonObject Unchanged() => Ok(new JsonObject { ["status"] = "unchanged" });

    private static JsonObject Ok(JsonObject body)
    {
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject NotFound() => Error("Not found, or not something your role can see.");

    private static JsonObject Refused(IReadOnlyList<string> problems) => new()
    {
        ["ok"] = false,
        ["error"] = string.Join(" ", problems.Distinct()),
        ["problems"] = new JsonArray([.. problems.Distinct().Select(p => (JsonNode)p)]),
    };
}

/// <summary>Overlays as a change area. The value carries the token, so an undone delete keeps its OBS link; the portal never sees it.</summary>
public sealed class OverlayArea(OverlayStore overlays, ElementStore elements, WidgetCodeStore code) : IPortalArea
{
    public const string AreaName = "overlay";

    /// <summary>A new widget layer's settings key naming which of Cuebot's widgets to make when the change goes live.</summary>
    public const string NewWidget = "widget";

    public string Name => AreaName;

    public JsonNode? Current(Guid id) => overlays.Get(id) is { } o ? Node(o) : null;

    public string NameOf(JsonNode value) => (string?)value["name"] ?? "";

    public Task ApplyAsync(Guid id, JsonNode? value)
    {
        if (value is null)
        {
            overlays.Delete(id);
            return Task.CompletedTask;
        }
        var overlay = PortalChanges.FromNode<Overlay>(value);
        overlay.Id = id;
        foreach (var layer in overlay.Layers.Where(l => l.Type == OverlayLayerTypes.Custom && l.ElementId is not null))
        {
            if (elements.Get(layer.ElementId!.Value) is not null || OverlayValues.String(layer.Settings?[NewWidget]) is not { } kind) continue;
            var (widget, settings) = kind switch
            {
                "chatbox" => (BuiltInWidgets.ChatBox(), new JsonObject()),
                BuiltInWidgets.AlertBoxKind => (BuiltInWidgets.AlertBox(), new JsonObject { ["builtIn"] = kind }),
                _ => (BuiltInWidgets.For(kind), new JsonObject { ["builtIn"] = kind }),
            };
            if (widget is null) continue;
            elements.Add(new OverlayElement { Id = layer.ElementId.Value, Name = layer.Name, Type = OverlayLayerTypes.Custom, Settings = settings });
            code.Write(layer.ElementId.Value, widget);
        }
        overlays.Put(overlay);
        return Task.CompletedTask;
    }

    /// <summary>An overlay as a change value (its token included, its timestamps left out so they never look like a conflict).</summary>
    public static JsonNode Node(Overlay overlay)
    {
        var node = (JsonObject)PortalChanges.ToNode(overlay);
        node.Remove("created");
        node.Remove("modified");
        return node;
    }
}

/// <summary>A widget's settings (its DATA) as a change area; its code is never part of it.</summary>
public sealed class WidgetArea(ElementStore elements, WidgetCodeStore code) : IPortalArea
{
    public const string AreaName = "widget";

    public string Name => AreaName;

    public JsonNode? Current(Guid id) => elements.Get(id) is { Type: OverlayLayerTypes.Custom } e
        ? new JsonObject { ["id"] = id.ToString(), ["name"] = e.Name, ["data"] = WidgetCode.ParseObject(code.Read(id).Data) }
        : null;

    public string NameOf(JsonNode value) => (string?)value["name"] ?? "";

    public Task ApplyAsync(Guid id, JsonNode? value)
    {
        if (value?["data"] is JsonObject data && elements.Get(id) is not null) code.Write(id, code.Read(id) with { Data = data.ToJsonString() });
        return Task.CompletedTask;
    }
}
