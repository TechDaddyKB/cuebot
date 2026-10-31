using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Overlays.Widgets;

/// <summary>An overlay in a StreamElements account (from the overlay list).</summary>
public sealed record StreamElementsOverlaySummary(string Id, string Name, int Width, int Height);

/// <summary>Reads overlays from the connected StreamElements account (the Donations StreamElements connection).</summary>
public interface IStreamElementsOverlaySource
{
    /// <summary>True when a StreamElements JWT token is set.</summary>
    bool HasToken { get; }

    Task<IReadOnlyList<StreamElementsOverlaySummary>> ListOverlaysAsync(CancellationToken ct);

    /// <summary>One overlay as StreamElements stores it: <c>{name, settings: {width, height}, widgets: [...]}</c>.</summary>
    Task<JsonObject> GetOverlayAsync(string id, CancellationToken ct);
}

/// <summary>What an import made: the new overlay, how many widgets came across, and what was left out (and why).</summary>
public sealed record StreamElementsImportResult(Overlay Overlay, int Imported, IReadOnlyList<string> Skipped);

/// <summary>
/// Turns a StreamElements overlay into a Cuebot one: custom widgets keep their code and settings, text layers become
/// text layers, image and video layers become image and video layers, "latest follower"-style labels become Cuebot
/// labels, and the alert box becomes Cuebot's alert box with each alert type's design and variations. Positions, sizes,
/// stacking order, opacity, rotation and visibility come along. Other widgets are listed as skipped. Media stays on
/// StreamElements' CDN (the overlay loads it from there).
/// </summary>
public static partial class StreamElementsOverlayImport
{
    /// <summary>Overlays in a StreamElements overlay list response (<c>{docs: [...]}</c> or a bare array).</summary>
    public static IReadOnlyList<StreamElementsOverlaySummary> ParseList(JsonNode? list)
    {
        var docs = list is JsonObject o ? o["docs"] as JsonArray : list as JsonArray;
        return (docs ?? []).OfType<JsonObject>()
            .Select(d => new StreamElementsOverlaySummary(Str(d["_id"]), Str(d["name"]) is { Length: > 0 } n ? n : "Overlay",
                Int(d["settings"]?["width"], 1920), Int(d["settings"]?["height"], 1080)))
            .Where(s => s.Id.Length > 0)
            .ToArray();
    }

    /// <summary>Creates the overlay in <paramref name="overlays"/> with its widgets placed through <paramref name="library"/>.</summary>
    public static StreamElementsImportResult Import(JsonObject se, OverlayStore overlays, WidgetLibrary library)
    {
        var name = Str(se["name"]) is { Length: > 0 } n ? n : "StreamElements overlay";
        var overlay = overlays.Create(name, Math.Clamp(Int(se["settings"]?["width"], 1920), 16, 7680),
            Math.Clamp(Int(se["settings"]?["height"], 1080), 16, 7680));
        var imported = 0;
        var skipped = new List<string>();
        var widgets = (se["widgets"] as JsonArray ?? []).OfType<JsonObject>()
            .Select((w, i) => (Widget: w, Order: i))
            .OrderBy(x => Number(x.Widget["css"]?["z-index"], 0)).ThenBy(x => x.Order)
            .Select(x => x.Widget);
        foreach (var widget in widgets)
        {
            var type = Str(widget["type"]);
            var label = Str(widget["name"]) is { Length: > 0 } wn ? wn : type;
            try
            {
                if (Place(widget, type, overlay, overlays, library) is { } layer)
                {
                    Finish(widget, overlay, layer, overlays);
                    imported++;
                }
                else skipped.Add($"{label} ({SkipReason(type)})");
            }
            catch (InvalidDataException ex)
            {
                skipped.Add($"{label} ({ex.Message})");
            }
        }
        return new StreamElementsImportResult(overlays.Get(overlay.Id) ?? overlay, imported, skipped);
    }

    private static string SkipReason(string type) => type switch
    {
        "" => "no type",
        _ when type.EndsWith("-goal", StringComparison.Ordinal) => "goals are not imported yet; add a Cuebot goal",
        _ => "not supported",
    };

    private static OverlayLayer? Place(JsonObject w, string type, Overlay overlay, OverlayStore overlays, WidgetLibrary library)
    {
        var variables = w["variables"] as JsonObject;
        if (variables is not null && (variables.ContainsKey("html") || variables.ContainsKey("js")))
            return Custom(w, variables, overlay, library);
        switch (type)
        {
            case "se-widget-alert-box":
                return library.AddAlertBox(overlay.Id, AlertData(variables ?? []));
            case "text":
                return Static(overlays, overlay, OverlayLayerTypes.Text, TextSettings(w["text"] as JsonObject));
            case "image":
            case "video":
                var url = Str(w[type]?["src"]) is { Length: > 0 } src ? src : Str(w["variables"]?["src"]);
                if (url.Length == 0) return null;
                var settings = type == "image"
                    ? new JsonObject { ["url"] = url, ["fit"] = "contain" }
                    : new JsonObject { ["url"] = url, ["fit"] = "contain", ["loop"] = true, ["muted"] = false, ["volume"] = (int)Math.Round(Number(w["video"]?["volume"], 0.5) * 100) };
                return Static(overlays, overlay, type == "image" ? OverlayLayerTypes.Image : OverlayLayerTypes.Video, settings);
        }
        var listener = Str(w["listener"]) is { Length: > 0 } l ? l : Str(variables?["listener"]);
        if (LabelData(listener, w["text"] as JsonObject, variables) is { } data)
            return library.AddBuiltIn(overlay.Id, "label", data);
        return null;
    }

    private static OverlayLayer? Custom(JsonObject w, JsonObject v, Overlay overlay, WidgetLibrary library)
    {
        var fields = v["fields"] switch
        {
            JsonValue s when s.GetValueKind() == JsonValueKind.String => s.GetValue<string>(),
            JsonObject o => o.ToJsonString(),
            _ => "{}",
        };
        var data = v["fieldData"] is JsonObject fd ? fd.ToJsonString() : "{}";
        var code = WidgetImport.FromPanes(Str(v["html"]), Str(v["css"]), Str(v["js"]), fields, data);
        var name = Str(w["name"]) is { Length: > 0 } n ? n : "StreamElements widget";
        return library.AddCustomWidget(overlay.Id, name, code);
    }

    private static OverlayLayer Static(OverlayStore overlays, Overlay overlay, string type, JsonObject settings)
    {
        var layer = OverlayLayerTypes.NewStatic(type, overlay);
        layer.Settings = settings;
        overlays.Update(overlay.Id, o => o.Layers.Add(layer));
        return layer;
    }

    /// <summary>Position, size, opacity, rotation and visibility from the widget's <c>css</c>; "auto" sizes get a fitting default.</summary>
    private static void Finish(JsonObject w, Overlay overlay, OverlayLayer placed, OverlayStore overlays)
    {
        var css = w["css"] as JsonObject ?? [];
        var fontSize = Number(w["text"]?["css"]?["font-size"], 32);
        var x = Pixels(css["left"]) ?? placed.X;
        var y = Pixels(css["top"]) ?? placed.Y;
        // "auto" sizes: a label is as wide as a line of its text; anything else keeps the size it was placed with.
        var label = placed.Type == OverlayLayerTypes.Custom && w["text"] is JsonObject && Str(w["type"]) != "se-widget-alert-box"
                    && w["variables"]?["html"] is null;
        var width = Pixels(css["width"]) ?? (label ? Math.Max(100, Math.Min(700, overlay.Width - x)) : placed.Width);
        var height = Pixels(css["height"]) ?? (label ? Math.Max(20, Math.Round(fontSize * 1.6)) : placed.Height);
        var rotation = Rotate().Match(Str(css["transform"])) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var name = Str(w["name"]) is { Length: > 0 } n ? n : null;
        overlays.Update(overlay.Id, o =>
        {
            if (o.Layers.FirstOrDefault(l => l.Id == placed.Id) is not { } layer) return;
            (layer.X, layer.Y, layer.Width, layer.Height) = (Math.Round(x, 2), Math.Round(y, 2), Math.Max(1, Math.Round(width, 2)), Math.Max(1, Math.Round(height, 2)));
            layer.Opacity = Math.Clamp(Number(css["opacity"], 1), 0, 1);
            layer.Rotation = rotation;
            layer.Visible = w["visible"] is not JsonValue v || !v.TryGetValue<bool>(out var visible) || visible;
            layer.Locked = w["locked"] is JsonValue lk && lk.TryGetValue<bool>(out var locked) && locked;
            if (name is not null) layer.Name = name;
        });
    }

    // ---- Text and labels -----------------------------------------------------------------------------------------

    internal static JsonObject TextSettings(JsonObject? text)
    {
        var css = text?["css"] as JsonObject ?? [];
        return new JsonObject
        {
            ["text"] = Str(text?["value"]),
            ["font"] = Str(css["font-family"]) is { Length: > 0 } f ? f : "Inter",
            ["size"] = (int)Math.Round(Number(css["font-size"], 32)),
            ["color"] = Str(css["color"]) is { Length: > 0 } c ? c : "#ffffff",
            ["weight"] = Weight(css["font-weight"]),
            ["align"] = Str(css["text-align"]) is "center" or "right" ? Str(css["text-align"]) : "left",
            ["shadow"] = text?["enableShadow"] is JsonValue s && s.TryGetValue<bool>(out var shadow) && shadow,
            ["background"] = "",
            ["padding"] = 0,
        };
    }

    /// <summary>
    /// A Cuebot label's settings for a StreamElements label (<c>follower-latest</c>, <c>tip-session</c>, ...), or null
    /// when the listener is not one.
    /// </summary>
    internal static JsonObject? LabelData(string listener, JsonObject? text, JsonObject? variables)
    {
        var m = LabelListener().Match(listener);
        if (!m.Success) return null;
        var type = m.Groups["type"].Value switch
        {
            "subscriber-new" or "subscriber-resub" or "subscriber-gifted" or "subscriber-alltime-gifter" => "subscriber",
            "sponsor" => "subscriber",
            var t => t,
        };
        if (!LabelTypes.Contains(type)) return null;
        var period = m.Groups["period"].Value;
        var template = Str(text?["value"]);
        var money = type is "tip" or "superchat" or "charityCampaignDonation" or "merch";
        var (show, labelPeriod) = period switch
        {
            "latest" => (template.Contains("{amount}", StringComparison.Ordinal) ? "latestAmount" : "latest", "session"),
            "recent" => ("recent", "session"),
            "count" => ("count", "session"),
            _ => (money || type == "cheer" ? "amount" : "count", period),
        };
        var tokens = TemplateToken().Matches(template);
        var prefix = tokens.Count > 0 ? template[..tokens[0].Index] : template;
        var suffix = tokens.Count > 0 ? template[(tokens[^1].Index + tokens[^1].Length)..] : "";
        var noData = Str(variables?["noData"]);
        if (prefix.Length > 0 && noData.StartsWith(prefix, StringComparison.Ordinal)) noData = noData[prefix.Length..];
        var css = text?["css"] as JsonObject ?? [];
        var color = Str(css["color"]) is { Length: > 0 } c ? c : "#ffffff";
        return new JsonObject
        {
            ["type"] = type,
            ["show"] = show,
            ["period"] = labelPeriod,
            ["prefix"] = prefix,
            ["suffix"] = suffix,
            ["emptyText"] = noData.Length > 0 ? noData : "—",
            ["fontFamily"] = Str(css["font-family"]) is { Length: > 0 } f ? f : "Inter",
            ["fontSize"] = (int)Math.Round(Number(css["font-size"], 32)),
            ["fontWeight"] = Weight(css["font-weight"]),
            ["textColor"] = color,
            ["valueColor"] = color,
            ["align"] = Str(css["text-align"]) is "center" or "right" ? Str(css["text-align"]) : "left",
            ["shadow"] = text?["enableShadow"] is JsonValue s && s.TryGetValue<bool>(out var shadow) && shadow,
            ["flash"] = false,
        };
    }

    private static readonly HashSet<string> LabelTypes = new(StringComparer.Ordinal)
    {
        "follower", "subscriber", "tip", "cheer", "raid", "superchat", "kicks", "jewels", "charityCampaignDonation", "merch", "redemption",
    };

    // ---- Alert box ------------------------------------------------------------------------------------------------

    /// <summary>StreamElements alert type → Cuebot's.</summary>
    private static readonly Dictionary<string, string> AlertTypes = new(StringComparer.Ordinal)
    {
        ["follower"] = "follower", ["subscriber"] = "subscriber", ["tip"] = "tip", ["cheer"] = "cheer", ["raid"] = "raid",
        ["merch"] = "merch", ["charityCampaignDonation"] = "charityCampaignDonation", ["superchat"] = "superchat",
        ["sponsor"] = "subscriber", ["redemption"] = "redemption",
    };

    /// <summary>
    /// The alert box's saved data (<c>{alerts: {...}}</c>): Cuebot's default designs with each StreamElements alert type
    /// it knows replaced by that type's design and variations.
    /// </summary>
    internal static JsonObject AlertData(JsonObject variables)
    {
        var fields = WidgetCode.ParseObject(BuiltInWidgets.AlertBox().Fields);
        var alerts = fields["alerts"]?["value"]?.DeepClone() as JsonObject ?? new JsonObject { ["version"] = 1, ["types"] = new JsonObject() };
        var types = alerts["types"] as JsonObject ?? [];
        alerts["types"] = types;
        var widgetAnimation = variables["animation"] as JsonObject;
        foreach (var (seType, value) in variables)
        {
            if (!AlertTypes.TryGetValue(seType, out var type) || value is not JsonObject se) continue;
            var defaults = types[type]?["design"] as JsonObject;
            var design = Design(se, widgetAnimation, defaults);
            var variations = new JsonArray();
            foreach (var v in (se["variations"] as JsonArray ?? []).OfType<JsonObject>()
                         .OrderByDescending(v => Number(v["requirement"], 0)))
            {
                if (Variation(v, design) is { } variation) variations.Add(variation);
            }
            types[type] = new JsonObject
            {
                ["enabled"] = se["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var enabled) || enabled,
                ["design"] = design,
                ["variations"] = variations,
            };
        }
        return new JsonObject { ["alerts"] = alerts };
    }

    private static JsonObject? Variation(JsonObject v, JsonObject baseDesign)
    {
        var condition = Str(v["condition"]).ToUpperInvariant();
        var requirement = v["requirement"];
        var (param, op, value) = Str(v["type"]) switch
        {
            "gift" => ("gifted", "is", (JsonNode?)true),
            "communityGift" => ("bulk", "is", (JsonNode?)true),
            "tier" => ("tier", "is", (JsonNode?)Str(requirement)),
            "amount" or "" => ("amount", condition switch { "EXACT" => "exact", _ => "atLeast" }, (JsonNode?)Number(requirement, 0)),
            _ => ("", "", null),
        };
        if (param.Length == 0) return null;
        return new JsonObject
        {
            ["name"] = Str(v["name"]) is { Length: > 0 } n ? n : "Variation",
            ["enabled"] = v["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var enabled) || enabled,
            ["param"] = param,
            ["op"] = op,
            ["value"] = value,
            ["chance"] = (int)Math.Round(Number(v["chance"], 100)),
            ["design"] = v["settings"] is JsonObject s ? Design(s, null, baseDesign) : baseDesign.DeepClone(),
        };
    }

    private static readonly HashSet<string> Enter = new(OverlayLayerTypes.EnterAnimations, StringComparer.Ordinal);
    private static readonly HashSet<string> Exit = new(OverlayLayerTypes.ExitAnimations, StringComparer.Ordinal);

    internal static JsonObject Design(JsonObject se, JsonObject? widgetAnimation, JsonObject? defaults)
    {
        var design = defaults?.DeepClone() as JsonObject ?? [];
        var text = se["text"] as JsonObject;
        var css = text?["css"] as JsonObject ?? [];
        if (text?["message"] is JsonValue) design["template"] = Template(Str(text["message"]));
        if (se["duration"] is JsonValue) design["duration"] = Number(se["duration"], 8);
        var graphics = se["graphics"] as JsonObject;
        design["media"] = Str(graphics?["src"]);
        design["mediaVolume"] = (int)Math.Round(Number(graphics?["volume"], 0.5) * 100);
        var sound = Str(se["audio"]?["src"]);
        design["sound"] = sound.Length > 0 ? sound : "none";
        design["volume"] = (int)Math.Round(Number(se["audio"]?["volume"], 0.6) * 100);
        design["layout"] = Str(se["layout"]) switch
        {
            "row" or "side" => "side",
            var l when l.Contains("overlay", StringComparison.Ordinal) || l.Contains("over", StringComparison.Ordinal) => "overlay",
            _ => "above",
        };
        if (Str(css["font-family"]) is { Length: > 0 } font) design["font"] = font;
        if (css["font-size"] is JsonValue) design["size"] = (int)Math.Round(Number(css["font-size"], 42));
        if (Str(css["color"]) is { Length: > 0 } color) design["color"] = color;
        if (Str(css["highlights"]?["color"]) is { Length: > 0 } highlight) design["highlight"] = highlight;
        if (css["message"]?["font-size"] is JsonValue) design["messageSize"] = (int)Math.Round(Number(css["message"]?["font-size"], 26));
        design["enter"] = Animation(se["animation"]?["in"], widgetAnimation?["in"], Enter, "fadeIn");
        design["exit"] = Animation(se["animation"]?["out"], widgetAnimation?["out"], Exit, "fadeOut");
        design["tts"] = se["tts"]?["enabled"] is JsonValue t && t.TryGetValue<bool>(out var tts) && tts;
        design["showMessage"] = true;
        return design;
    }

    /// <summary>StreamElements' alert text in Cuebot's tokens: <c>{currency}{amount}</c> is the formatted amount.</summary>
    internal static string Template(string message) =>
        message.Replace("{currency}{amount}", "{formattedAmount}", StringComparison.Ordinal)
            .Replace("{amount}{currency}", "{formattedAmount}", StringComparison.Ordinal);

    private static string Animation(JsonNode? own, JsonNode? widget, HashSet<string> allowed, string fallback)
    {
        var name = Str(own);
        if (name.Length == 0 || name == "none") name = Str(widget);
        if (name.Length == 0) return fallback;
        return allowed.Contains(name) ? name : fallback;
    }

    // ---- Values ---------------------------------------------------------------------------------------------------

    private static string Weight(JsonNode? weight) => Str(weight) switch
    {
        "bold" or "bolder" => "700",
        "normal" or "lighter" or "" => "400",
        var w when int.TryParse(w, out var n) => n >= 800 ? "900" : n >= 700 ? "700" : n >= 600 ? "600" : "400",
        _ => "700",
    };

    internal static string Str(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToJsonString() : ""
        : "";

    private static double Number(JsonNode? node, double fallback)
    {
        if (node is not JsonValue v) return fallback;
        if (v.GetValueKind() == JsonValueKind.Number) return v.GetValue<double>();
        return double.TryParse(Str(v).Replace("px", "", StringComparison.Ordinal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static int Int(JsonNode? node, int fallback) => (int)Math.Round(Number(node, fallback));

    /// <summary>A CSS length in pixels ("12.5px" or a number); null for "auto", percentages and anything else.</summary>
    private static double? Pixels(JsonNode? node)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number) return v.GetValue<double>();
        var s = Str(node).Trim();
        if (s.EndsWith("px", StringComparison.Ordinal)) s = s[..^2];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [GeneratedRegex(@"^(?<type>[A-Za-z]+(?:-(?:new|resub|gifted|alltime-gifter))?)-(?<period>latest|session|week|month|total|count|recent)$")]
    private static partial Regex LabelListener();

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex TemplateToken();

    [GeneratedRegex(@"rotate\(\s*(-?[0-9.]+)deg\s*\)")]
    private static partial Regex Rotate();
}
