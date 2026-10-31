using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Sharing;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Server.Api;

public sealed partial class ApiOperations
{
    /// <summary>The OBS browser source link of an overlay (set by the server manager).</summary>
    public Func<Overlay, string>? OverlayLink { get; init; }

    private OverlayStore OverlayStore => services.Overlays ?? throw ApiException.Unavailable("Overlays are not available");
    private WidgetLibrary Library => services.Widgets ?? throw ApiException.Unavailable("Overlays are not available");
    private WidgetCodeStore CodeStore => services.WidgetCode ?? throw ApiException.Unavailable("Overlays are not available");

    // ---- Overlays --------------------------------------------------------------------------------------------------

    public JsonObject ListOverlays() => new()
    {
        ["overlays"] = new JsonArray([.. OverlayStore.All().OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Select(o => (JsonNode)OverlayJson(o))]),
    };

    public JsonObject GetOverlay(string overlay) => OverlayJson(FindOverlay(overlay));

    /// <summary>The link to paste into an OBS browser source (it contains the overlay's secret token).</summary>
    public JsonObject OverlayLinkOf(string overlay)
    {
        var o = FindOverlay(overlay);
        return new JsonObject
        {
            ["overlay"] = o.Name, ["width"] = o.Width, ["height"] = o.Height,
            ["link"] = OverlayLink?.Invoke(o) ?? throw ApiException.Unavailable("The overlay server is not available"),
            ["note"] = "Add it in OBS as a browser source of this width and height. Anyone with the link can show the overlay.",
        };
    }

    public JsonObject CreateOverlay(string name, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(name)) throw ApiException.BadRequest("The overlay needs a name");
        var overlay = OverlayStore.Create(name.Trim(), Size(width, "width"), Size(height, "height"));
        services.Log.Info(LogSource, $"Overlay '{overlay.Name}' created");
        return OverlayJson(overlay);
    }

    public JsonObject UpdateOverlay(string overlay, string? name, int? width, int? height)
    {
        var o = FindOverlay(overlay);
        OverlayStore.Update(o.Id, x =>
        {
            if (!string.IsNullOrWhiteSpace(name)) x.Name = name.Trim();
            if (width is { } w) x.Width = Size(w, "width");
            if (height is { } h) x.Height = Size(h, "height");
        });
        return GetOverlay(o.Id.ToString());
    }

    public JsonObject DeleteOverlay(string overlay)
    {
        var o = FindOverlay(overlay);
        if (!OverlayStore.Delete(o.Id)) throw ApiException.NotFound($"Overlay '{overlay}' not found");
        services.Log.Info(LogSource, $"Overlay '{o.Name}' deleted");
        return new JsonObject { ["deleted"] = o.Name };
    }

    private static int Size(int value, string what) =>
        value is >= 16 and <= OverlayStore.MaxSize ? value : throw ApiException.BadRequest($"{what} must be 16 to {OverlayStore.MaxSize} pixels");

    internal Overlay FindOverlay(string overlay)
    {
        var text = overlay.Trim();
        var all = OverlayStore.All();
        return (Guid.TryParse(text, out var id) ? all.FirstOrDefault(o => o.Id == id) : null)
               ?? all.FirstOrDefault(o => string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase))
               ?? throw ApiException.NotFound($"Overlay '{text}' not found (list_overlays shows them)");
    }

    private JsonObject OverlayJson(Overlay o) => new()
    {
        ["id"] = o.Id.ToString(),
        ["name"] = o.Name,
        ["width"] = o.Width,
        ["height"] = o.Height,
        ["layers"] = new JsonArray([.. o.Layers.Select(l => (JsonNode)LayerJson(l))]),
    };

    private JsonObject LayerJson(OverlayLayer l)
    {
        var element = l.ElementId is { } id ? services.Elements?.Get(id) : null;
        return new JsonObject
        {
            ["id"] = l.Id.ToString(),
            ["name"] = l.Name,
            ["type"] = l.Type,
            ["widget"] = l.Type != OverlayLayerTypes.Custom ? null : BuiltInWidgets.KindOf(element) ?? (IsChatBox(l) ? "chatbox" : "custom"),
            ["x"] = l.X, ["y"] = l.Y, ["width"] = l.Width, ["height"] = l.Height,
            ["opacity"] = l.Opacity, ["rotation"] = l.Rotation, ["visible"] = l.Visible, ["locked"] = l.Locked,
            ["enter"] = new JsonObject { ["name"] = l.Enter.Name, ["duration"] = l.Enter.Duration },
            ["exit"] = new JsonObject { ["name"] = l.Exit.Name, ["duration"] = l.Exit.Duration },
            ["settings"] = OverlayLayerTypes.Static.Contains(l.Type) ? l.Settings?.DeepClone() : null,
        };
    }

    private bool IsChatBox(OverlayLayer l)
    {
        if (l.ElementId is not { } id) return false;
        var code = CodeStore.Read(id);
        var chat = BuiltInWidgets.ChatBox();
        return code.Js == chat.Js && code.Html == chat.Html;
    }

    // ---- Layers ----------------------------------------------------------------------------------------------------

    /// <summary>What can be added: Cuebot's widgets (with what each is for) and the static layer types.</summary>
    public static JsonObject WidgetKinds() => new()
    {
        ["widgets"] = new JsonArray(
        [
            new JsonObject { ["kind"] = "alertbox", ["name"] = "Alert box", ["about"] = "A design per alert type (follow, sub, tip, cheer, raid...) with variations; edit with get_alert_designs / update_alert_designs" },
            new JsonObject { ["kind"] = "chatbox", ["name"] = "Chat box", ["about"] = "Chat from every platform with badges and emotes (layout \"youtube\" for YouTube popout chat CSS themes)" },
            .. BuiltInWidgets.Kinds.Select(k => (JsonNode)new JsonObject { ["kind"] = k.Id, ["name"] = k.Name }),
            new JsonObject { ["kind"] = "custom", ["name"] = "Custom widget", ["about"] = "Your own HTML/CSS/JS (StreamElements widget API); add with add_custom_widget" },
        ]),
        ["layers"] = new JsonArray(
        [
            new JsonObject { ["type"] = "text", ["settings"] = "text, font (Google font), size, color, weight, align (left/center/right), shadow, background, padding" },
            new JsonObject { ["type"] = "image", ["settings"] = "url (https or an uploaded /assets/... file), fit (contain/cover/fill)" },
            new JsonObject { ["type"] = "video", ["settings"] = "url, fit, loop, muted, volume (0-100)" },
            new JsonObject { ["type"] = "slideshow", ["settings"] = "urls (list), seconds, fit, transition (fade/slide/none)" },
        ]),
        ["animations"] = new JsonObject
        {
            ["enter"] = CatalogJson.Strings(OverlayLayerTypes.EnterAnimations),
            ["exit"] = CatalogJson.Strings(OverlayLayerTypes.ExitAnimations),
        },
    };

    /// <summary>
    /// Adds one of Cuebot's widgets (<paramref name="kind"/>: alertbox, chatbox or a built-in kind) or a static layer
    /// (text, image, video, slideshow); <paramref name="values"/> are its first settings (widget field values or the
    /// layer's settings), <paramref name="place"/> optional x/y/width/height/name.
    /// </summary>
    public JsonObject AddLayer(string overlay, string kind, JsonObject? values, JsonObject? place)
    {
        var o = FindOverlay(overlay);
        kind = kind.Trim().ToLowerInvariant();
        OverlayLayer? layer;
        if (OverlayLayerTypes.Static.Contains(kind))
        {
            var added = OverlayLayerTypes.NewStatic(kind, o);
            if (values is not null) foreach (var (key, value) in values) added.Settings![key] = value?.DeepClone();
            OverlayStore.Update(o.Id, x => x.Layers.Add(added));
            layer = added;
        }
        else
        {
            layer = kind switch
            {
                "alertbox" => Library.AddAlertBox(o.Id),
                "chatbox" => Library.AddChatBox(o.Id, values?["layout"] is JsonValue v && v.TryGetValue<string>(out var layout) ? layout : "cuebot"),
                _ when BuiltInWidgets.For(kind) is not null => Library.AddBuiltIn(o.Id, kind),
                _ => throw ApiException.BadRequest($"Unknown kind '{kind}' (list_widget_kinds shows them; custom widgets use add_custom_widget)"),
            } ?? throw ApiException.NotFound($"Overlay '{overlay}' not found");
            if (values is { Count: > 0 } && kind != "chatbox") SetWidgetValues(layer, values);
        }
        if (place is { Count: > 0 }) ApplyLayer(o.Id, layer.Id, place);
        services.Log.Info(LogSource, $"Added {kind} to overlay '{o.Name}'");
        return LayerResult(o.Id, layer.Id);
    }

    /// <summary>A widget from code: StreamElements-style (or Streamlabs) panes, placed on the overlay.</summary>
    public JsonObject AddCustomWidget(string overlay, string name, JsonObject code, JsonObject? place)
    {
        var o = FindOverlay(overlay);
        var widget = Code(code, WidgetCode.Empty);
        var layer = Library.AddCustomWidget(o.Id, string.IsNullOrWhiteSpace(name) ? "Custom widget" : name.Trim(), widget)
                    ?? throw ApiException.NotFound($"Overlay '{overlay}' not found");
        if (place is { Count: > 0 }) ApplyLayer(o.Id, layer.Id, place);
        return LayerResult(o.Id, layer.Id);
    }

    /// <summary>Changes a layer's place, size, look, name, animations or (static layers) settings.</summary>
    public JsonObject UpdateLayer(string overlay, string layer, JsonObject changes)
    {
        var (o, l) = FindLayer(overlay, layer);
        ApplyLayer(o.Id, l.Id, changes);
        return LayerResult(o.Id, l.Id);
    }

    /// <summary>Takes a layer off the overlay (a widget stays in the library and can be placed again).</summary>
    public JsonObject RemoveLayer(string overlay, string layer)
    {
        var (o, l) = FindLayer(overlay, layer);
        Library.RemoveLayer(o.Id, l.Id);
        return GetOverlay(o.Id.ToString());
    }

    private void ApplyLayer(Guid overlayId, Guid layerId, JsonObject c)
    {
        static double? D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
        static bool? B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        static LayerAnimation? A(JsonNode? n, LayerAnimation current, IReadOnlyList<string> allowed)
        {
            if (n is null) return null;
            var name = n is JsonValue s && s.TryGetValue<string>(out var text) ? text
                : n is JsonObject obj && obj["name"] is JsonValue nv && nv.TryGetValue<string>(out var t) ? t : current.Name;
            if (!allowed.Contains(name)) throw ApiException.BadRequest($"Unknown animation '{name}' ({string.Join(", ", allowed)})");
            var duration = n is JsonObject o ? D(o["duration"]) : null;
            return new LayerAnimation { Name = name, Duration = Math.Clamp(duration ?? current.Duration, 0.1, 10) };
        }
        OverlayStore.Update(overlayId, o =>
        {
            if (o.Layers.FirstOrDefault(x => x.Id == layerId) is not { } l) return;
            if (c["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && name.Trim().Length > 0) l.Name = name.Trim();
            if (D(c["x"]) is { } x) l.X = Math.Clamp(x, -OverlayStore.MaxSize, OverlayStore.MaxSize);
            if (D(c["y"]) is { } y) l.Y = Math.Clamp(y, -OverlayStore.MaxSize, OverlayStore.MaxSize);
            if (D(c["width"]) is { } w) l.Width = Math.Clamp(w, 1, OverlayStore.MaxSize);
            if (D(c["height"]) is { } h) l.Height = Math.Clamp(h, 1, OverlayStore.MaxSize);
            if (D(c["opacity"]) is { } op) l.Opacity = Math.Clamp(op, 0, 1);
            if (D(c["rotation"]) is { } r) l.Rotation = Math.Clamp(r, -360, 360);
            if (B(c["visible"]) is { } visible) l.Visible = visible;
            if (B(c["locked"]) is { } locked) l.Locked = locked;
            if (A(c["enter"], l.Enter, OverlayLayerTypes.EnterAnimations) is { } enter) l.Enter = enter;
            if (A(c["exit"], l.Exit, OverlayLayerTypes.ExitAnimations) is { } exit) l.Exit = exit;
            if (c["settings"] is JsonObject settings && OverlayLayerTypes.Static.Contains(l.Type))
            {
                l.Settings ??= [];
                foreach (var (key, value) in settings) l.Settings[key] = value?.DeepClone();
            }
        });
    }

    private JsonObject LayerResult(Guid overlayId, Guid layerId) =>
        OverlayStore.Get(overlayId)?.Layers.FirstOrDefault(l => l.Id == layerId) is { } l
            ? new JsonObject { ["overlay"] = overlayId.ToString(), ["layer"] = LayerJson(l) }
            : throw ApiException.NotFound("The layer is gone");

    internal (Overlay Overlay, OverlayLayer Layer) FindLayer(string overlay, string layer)
    {
        var o = FindOverlay(overlay);
        var text = layer.Trim();
        var l = (Guid.TryParse(text, out var id) ? o.Layers.FirstOrDefault(x => x.Id == id) : null)
                ?? o.Layers.FirstOrDefault(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase))
                ?? throw ApiException.NotFound($"Overlay '{o.Name}' has no layer '{text}' (get_overlay lists them)");
        return (o, l);
    }

    // ---- Widget settings and code ----------------------------------------------------------------------------------

    /// <summary>
    /// A widget's settings: each field (type, label, options, group) with its current value; with
    /// <paramref name="includeCode"/> also the HTML/CSS/JS panes. The alert box's designs are left out (get_alert_designs).
    /// </summary>
    public JsonObject GetWidget(string overlay, string layer, bool includeCode)
    {
        var (o, l) = FindLayer(overlay, layer);
        var code = WidgetOf(l);
        var fields = WidgetCode.ParseObject(code.Fields);
        var values = code.FieldData();
        var settings = new JsonObject();
        foreach (var (key, field) in fields)
        {
            if (field is not JsonObject f || key == "alerts") continue;
            var type = f["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "text";
            if (type == "hidden" || key.StartsWith("widget", StringComparison.Ordinal)) continue;
            var entry = new JsonObject { ["type"] = type, ["label"] = f["label"]?.DeepClone(), ["group"] = f["group"]?.DeepClone(), ["value"] = values[key]?.DeepClone() };
            foreach (var extra in new[] { "options", "min", "max", "step" })
                if (f[extra] is { } x) entry[extra] = x.DeepClone();
            settings[key] = entry;
        }
        var result = new JsonObject { ["overlay"] = o.Name, ["layer"] = LayerJson(l), ["settings"] = settings };
        if (BuiltInWidgets.IsAlertBox(services.Elements?.Get(l.ElementId!.Value))) result["alertDesigns"] = "Use get_alert_designs";
        if (includeCode)
        {
            result["code"] = new JsonObject
            {
                ["html"] = code.Html, ["css"] = code.Css, ["js"] = code.Js, ["fields"] = code.Fields, ["cssOverride"] = code.CssOverride,
            };
        }
        return result;
    }

    /// <summary>Sets widget settings by field key (merged into the saved values); live overlays reload.</summary>
    public JsonObject UpdateWidgetSettings(string overlay, string layer, JsonObject values)
    {
        var (o, l) = FindLayer(overlay, layer);
        if (values.ContainsKey("alerts")) throw ApiException.BadRequest("Change alert designs with update_alert_designs");
        var unknown = SetWidgetValues(l, values);
        var result = GetWidget(o.Id.ToString(), l.Id.ToString(), includeCode: false);
        if (unknown.Count > 0) result["warnings"] = new JsonArray([.. unknown.Select(k => (JsonNode)$"'{k}' is not one of this widget's fields; it was saved anyway")]);
        return result;
    }

    /// <summary>Replaces the given code panes (html, css, js, fields, cssOverride) of a widget.</summary>
    public JsonObject UpdateWidgetCode(string overlay, string layer, JsonObject panes)
    {
        var (o, l) = FindLayer(overlay, layer);
        var current = WidgetOf(l);
        CodeStore.Write(l.ElementId!.Value, Code(panes, current));
        services.Log.Info(LogSource, $"Widget code of '{l.Name}' on '{o.Name}' changed");
        return GetWidget(o.Id.ToString(), l.Id.ToString(), includeCode: false);
    }

    private WidgetCode WidgetOf(OverlayLayer l) =>
        l.ElementId is { } id ? CodeStore.Read(id) : throw ApiException.BadRequest($"'{l.Name}' is a {l.Type} layer, not a widget; use update_layer");

    /// <summary>Merges values into the widget's saved settings; returns the keys that are not fields of the widget.</summary>
    private List<string> SetWidgetValues(OverlayLayer l, JsonObject values)
    {
        var code = WidgetOf(l);
        var data = WidgetCode.ParseObject(code.Data);
        var fields = WidgetCode.ParseObject(code.Fields);
        var unknown = new List<string>();
        foreach (var (key, value) in values)
        {
            if (!fields.ContainsKey(key)) unknown.Add(key);
            data[key] = value?.DeepClone();
        }
        CodeStore.Write(l.ElementId!.Value, code with { Data = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) });
        return unknown;
    }

    private static WidgetCode Code(JsonObject panes, WidgetCode current)
    {
        string Pane(string key, string fallback) => panes[key] switch
        {
            null => fallback,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonObject obj when key == "fields" => obj.ToJsonString(),
            _ => throw ApiException.BadRequest($"'{key}' must be text"),
        };
        try
        {
            var code = WidgetImport.FromPanes(Pane("html", current.Html), Pane("css", current.Css), Pane("js", current.Js), Pane("fields", current.Fields),
                current == WidgetCode.Empty ? Pane("data", "{}") : current.Data);
            return code with { CssOverride = Pane("cssOverride", current.CssOverride) };
        }
        catch (InvalidDataException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }

    // ---- Alert box designs -----------------------------------------------------------------------------------------

    /// <summary>
    /// The alert box's designs: per alert type whether it is on, its design (template, duration, media, sound, layout,
    /// font, colours, animations, TTS) and its variations; plus what each design field means.
    /// </summary>
    public JsonObject GetAlertDesigns(string overlay, string layer)
    {
        var (o, l) = FindAlertBox(overlay, layer);
        return new JsonObject
        {
            ["overlay"] = o.Name,
            ["layer"] = l.Name,
            ["alerts"] = AlertsOf(l),
            ["designFields"] = AlertDesignHelp(),
        };
    }

    /// <summary>
    /// Changes alert designs: each given type's <c>enabled</c>, <c>design</c> fields (merged) and <c>variations</c>
    /// (replaced when given); <c>randomPick</c> for the whole box.
    /// </summary>
    public JsonObject UpdateAlertDesigns(string overlay, string layer, JsonObject? types, bool? randomPick)
    {
        var (o, l) = FindAlertBox(overlay, layer);
        var alerts = AlertsOf(l);
        var all = alerts["types"] as JsonObject ?? throw ApiException.Unavailable("The alert box's designs are not readable");
        if (randomPick is { } pick) alerts["randomPick"] = pick;
        foreach (var (type, change) in types ?? [])
        {
            if (all[type] is not JsonObject current)
                throw ApiException.BadRequest($"Unknown alert type '{type}' ({string.Join(", ", all.Select(t => t.Key))})");
            if (change is not JsonObject c) throw ApiException.BadRequest($"'{type}' must be an object");
            if (c["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled)) current["enabled"] = enabled;
            if (c["design"] is JsonObject design)
            {
                var target = current["design"] as JsonObject ?? [];
                foreach (var (key, value) in design) target[key] = value?.DeepClone();
                current["design"] = target;
            }
            if (c["variations"] is JsonArray variations) current["variations"] = variations.DeepClone();
        }
        SetWidgetValues(l, new JsonObject { ["alerts"] = alerts });
        services.Log.Info(LogSource, $"Alert designs of '{l.Name}' on '{o.Name}' changed");
        return GetAlertDesigns(o.Id.ToString(), l.Id.ToString());
    }

    private (Overlay, OverlayLayer) FindAlertBox(string overlay, string layer)
    {
        var (o, l) = FindLayer(overlay, layer);
        if (!BuiltInWidgets.IsAlertBox(l.ElementId is { } id ? services.Elements?.Get(id) : null))
            throw ApiException.BadRequest($"'{l.Name}' is not Cuebot's alert box");
        return (o, l);
    }

    private JsonObject AlertsOf(OverlayLayer l) =>
        WidgetOf(l).FieldData()["alerts"]?.DeepClone() as JsonObject ?? throw ApiException.Unavailable("The alert box's designs are not readable");

    private static JsonObject AlertDesignHelp() => new()
    {
        ["template"] = "Headline; tokens {name} {amount} {formattedAmount} {currency} {message} {tier} {sender} {platform} {redemption} {items} {count}",
        ["showMessage"] = "Show the viewer's message under it (true/false)",
        ["duration"] = "Seconds on screen",
        ["media"] = "Image or video URL (https or an uploaded /assets/... file); empty for none",
        ["mediaVolume"] = "0-100",
        ["sound"] = "\"chime\" (built in), \"none\", or a sound URL",
        ["volume"] = "0-100",
        ["layout"] = "above (media above text), side (beside), overlay (text over media)",
        ["font"] = "Google font name",
        ["size"] = "Headline size in px",
        ["color"] = "Text colour",
        ["highlight"] = "Colour of the tokens in the headline",
        ["messageSize"] = "Message size in px",
        ["enter"] = "Enter animation (" + string.Join(", ", OverlayLayerTypes.EnterAnimations) + ")",
        ["exit"] = "Exit animation (" + string.Join(", ", OverlayLayerTypes.ExitAnimations) + ")",
        ["tts"] = "Read it with Cuebot's speech (true/false)",
        ["ttsText"] = "What speech reads, e.g. {name} says {message}",
        ["ttsAlias"] = "Speech voice alias (empty = default)",
        ["variations"] = "List of {name, enabled, param (amount, tier, platform, name, gifted, bulk, currency, message), op (atLeast, exact, between, is, contains), "
                         + "value, value2 (between), chance (0-100), design (fields that differ)}; the first matching one is used",
    };

    // ---- Gallery ---------------------------------------------------------------------------------------------------

    /// <summary>The community gallery's items.</summary>
    public async Task<JsonObject> GalleryAsync(CancellationToken ct)
    {
        var gallery = services.Gallery ?? throw ApiException.Unavailable("The gallery is not available");
        try
        {
            var items = await gallery.ListAsync(ct).ConfigureAwait(false);
            return new JsonObject { ["items"] = JsonSerializer.SerializeToNode(items, JsonOptions) };
        }
        catch (GalleryException ex)
        {
            throw ApiException.Unavailable(ex.Message);
        }
    }

    /// <summary>Imports a gallery item: an overlay item becomes a new overlay; a widget item goes onto <paramref name="overlay"/>.</summary>
    public async Task<JsonObject> ImportGalleryItemAsync(string id, string? overlay, CancellationToken ct)
    {
        var gallery = services.Gallery ?? throw ApiException.Unavailable("The gallery is not available");
        var packages = services.Packages ?? throw ApiException.Unavailable("Packages are not available");
        try
        {
            var item = (await gallery.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => string.Equals(i.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw ApiException.NotFound($"No gallery item '{id}'");
            Guid? target = null;
            if (item.Kind == "widget")
                target = string.IsNullOrWhiteSpace(overlay) ? throw ApiException.BadRequest("A widget item needs the overlay to add it to") : FindOverlay(overlay).Id;
            using var package = await gallery.DownloadAsync(item, ct).ConfigureAwait(false);
            var result = await packages.ImportAsync(package, target, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["imported"] = item.Name, ["kind"] = result.Kind, ["layers"] = result.Layers,
                ["leftOut"] = CatalogJson.Strings(result.LeftOut), ["overlay"] = OverlayJson(result.Overlay),
            };
        }
        catch (GalleryException ex)
        {
            throw ApiException.Unavailable(ex.Message);
        }
        catch (PackageFormatException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }

    // ---- StreamElements overlays -----------------------------------------------------------------------------------

    public async Task<JsonObject> StreamElementsOverlaysAsync(CancellationToken ct)
    {
        var source = StreamElementsSource();
        try
        {
            var list = await source.ListOverlaysAsync(ct).ConfigureAwait(false);
            return new JsonObject { ["overlays"] = JsonSerializer.SerializeToNode(list, JsonOptions) };
        }
        catch (Exception ex) when (ex is HttpRequestException || ex.GetType().Name == "StreamElementsApiException")
        {
            throw ApiException.Unavailable("StreamElements: " + ex.Message);
        }
    }

    public async Task<JsonObject> ImportStreamElementsOverlayAsync(string id, CancellationToken ct)
    {
        var source = StreamElementsSource();
        try
        {
            var json = await source.GetOverlayAsync(id.Trim(), ct).ConfigureAwait(false);
            var result = StreamElementsOverlayImport.Import(json, OverlayStore, Library);
            return new JsonObject
            {
                ["overlay"] = OverlayJson(result.Overlay), ["imported"] = result.Imported, ["skipped"] = CatalogJson.Strings(result.Skipped),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException || ex.GetType().Name == "StreamElementsApiException")
        {
            throw ApiException.Unavailable("StreamElements: " + ex.Message);
        }
    }

    private IStreamElementsOverlaySource StreamElementsSource()
    {
        var source = services.StreamElementsOverlays ?? throw ApiException.Unavailable("StreamElements is not available");
        return source.HasToken ? source : throw ApiException.Unavailable("Connect StreamElements first: paste its JWT token on the Donations page");
    }
}
