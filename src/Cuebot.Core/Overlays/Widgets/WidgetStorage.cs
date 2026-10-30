using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Overlays.Widgets;

/// <summary>
/// StreamElements' <c>SE_API.store</c> for widgets: values by key, shared by every widget and kept across restarts
/// (widget-store.json; in memory when <c>paths</c> is null). Keys and values are capped so a widget cannot fill the disk.
/// </summary>
public sealed class WidgetKeyValueStore : IDisposable
{
    public const int MaxKeys = 1000;
    public const int MaxValueBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly JsonFile<Dictionary<string, JsonNode?>>? _file;
    private readonly Dictionary<string, JsonNode?> _memory = [];

    public WidgetKeyValueStore(AppPaths? paths, AppLog log)
    {
        if (paths is not null) _file = new JsonFile<Dictionary<string, JsonNode?>>(paths.File("widget-store.json"), log);
    }

    /// <summary>Raised with the key and the new value after a set.</summary>
    public event Action<string, JsonNode?>? Changed;

    private Dictionary<string, JsonNode?> Values => _file?.Value ?? _memory;

    public JsonNode? Get(string key)
    {
        lock (_gate) return Values.TryGetValue(key, out var value) ? value?.DeepClone() : null;
    }

    /// <summary>Stores the value (replacing the old one); false when the key is empty or the value or store too big.</summary>
    public bool Set(string key, JsonNode? value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return false;
        if ((value?.ToJsonString().Length ?? 0) > MaxValueBytes) return false;
        lock (_gate)
        {
            if (!Values.ContainsKey(key) && Values.Count >= MaxKeys) return false;
            Values[key] = value?.DeepClone();
            _file?.ScheduleSave();
        }
        Changed?.Invoke(key, value);
        return true;
    }

    public void Dispose() => _file?.Dispose();
}

/// <summary>Adds custom widgets to overlays: the code goes to the library (an element plus its code folder), a layer uses it.</summary>
public sealed class WidgetLibrary(OverlayStore overlays, ElementStore elements, WidgetCodeStore code)
{
    public const string CustomType = "custom";

    /// <summary>
    /// Creates a custom widget element named <paramref name="name"/> with <paramref name="widget"/>'s code and places it on
    /// the overlay, covering the whole canvas. Returns the new layer, or null when the overlay does not exist.
    /// </summary>
    public OverlayLayer? AddCustomWidget(Guid overlayId, string name, WidgetCode widget, JsonObject? settings = null)
    {
        if (overlays.Get(overlayId) is not { } overlay) return null;
        var element = elements.Add(new OverlayElement { Name = name, Type = CustomType, Settings = settings ?? [] });
        code.Write(element.Id, widget);
        var layer = new OverlayLayer
        {
            Name = element.Name,
            Type = CustomType,
            ElementId = element.Id,
            Width = overlay.Width,
            Height = overlay.Height,
        };
        overlays.Update(overlayId, o => o.Layers.Add(layer));
        return layer;
    }

    /// <summary>
    /// Adds Cuebot's chat box to an overlay: 480 × 600 in the lower left of a landscape canvas, the full width of the
    /// lower half on a vertical one. Returns the layer, or null when the overlay does not exist.
    /// </summary>
    public OverlayLayer? AddChatBox(Guid overlayId, string layout = "cuebot")
    {
        if (overlays.Get(overlayId) is not { } overlay) return null;
        var name = layout == "youtube" ? "Chat box (YouTube popout layout)" : "Chat box";
        if (AddCustomWidget(overlayId, name, BuiltInWidgets.ChatBox(layout)) is not { } layer) return null;
        var vertical = overlay.Height > overlay.Width;
        var (width, height) = vertical ? (overlay.Width - 40.0, overlay.Height / 2.0 - 40) : (Math.Min(480.0, overlay.Width - 40), Math.Min(600.0, overlay.Height - 40));
        MoveLayer(overlayId, layer.Id, 20, overlay.Height - height - 20, width, height);
        return overlays.Get(overlayId)?.Layers.FirstOrDefault(l => l.Id == layer.Id);
    }

    /// <summary>
    /// Adds Cuebot's alert box to an overlay: the upper middle of the canvas (60% of its width, half its height).
    /// Returns the layer, or null when the overlay does not exist.
    /// </summary>
    public OverlayLayer? AddAlertBox(Guid overlayId)
    {
        if (overlays.Get(overlayId) is not { } overlay) return null;
        var settings = new JsonObject { ["builtIn"] = BuiltInWidgets.AlertBoxKind };
        if (AddCustomWidget(overlayId, "Alert box", BuiltInWidgets.AlertBox(), settings) is not { } layer) return null;
        var width = Math.Round(overlay.Width * 0.6);
        var height = Math.Round(overlay.Height * 0.5);
        MoveLayer(overlayId, layer.Id, Math.Round((overlay.Width - width) / 2), Math.Round(overlay.Height * 0.05), width, height);
        return overlays.Get(overlayId)?.Layers.FirstOrDefault(l => l.Id == layer.Id);
    }

    /// <summary>
    /// Adds a built-in widget (<see cref="BuiltInWidgets.Kinds"/>: label, goal, event list, credits, countdown) in a
    /// sensible spot for its kind, scaled to the canvas. Returns the layer, or null for an unknown kind or overlay.
    /// </summary>
    public OverlayLayer? AddBuiltIn(Guid overlayId, string kind)
    {
        if (overlays.Get(overlayId) is not { } overlay || BuiltInWidgets.For(kind) is not { } widget) return null;
        var name = BuiltInWidgets.Kinds.First(k => k.Id == kind).Name;
        if (AddCustomWidget(overlayId, name, widget) is not { } layer) return null;
        double w = overlay.Width, h = overlay.Height, s = Math.Min(w / 1920, h / 1080);
        var (x, y, width, height) = kind switch
        {
            "label" => (40 * s, 40 * s, 700 * s, 70 * s),
            "goal" => (40 * s, 130 * s, 700 * s, 110 * s),
            "eventlist" => (w - 420 * s, 40 * s, 380 * s, 420 * s),
            "countdown" => ((w - 640 * s) / 2, (h - 260 * s) / 2, 640 * s, 260 * s),
            _ => (0.0, 0.0, w, h),   // credits roll over the whole canvas
        };
        MoveLayer(overlayId, layer.Id, Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(height));
        return overlays.Get(overlayId)?.Layers.FirstOrDefault(l => l.Id == layer.Id);
    }

    /// <summary>Removes a layer; its element and code stay in the library.</summary>
    public bool RemoveLayer(Guid overlayId, Guid layerId)
    {
        var removed = false;
        overlays.Update(overlayId, o => removed = o.Layers.RemoveAll(l => l.Id == layerId) > 0);
        return removed;
    }

    /// <summary>Changes a layer's position and size (overlay pixels); false when it does not exist.</summary>
    public bool MoveLayer(Guid overlayId, Guid layerId, double x, double y, double width, double height)
    {
        var found = false;
        overlays.Update(overlayId, o =>
        {
            if (o.Layers.FirstOrDefault(l => l.Id == layerId) is not { } layer) return;
            (layer.X, layer.Y, layer.Width, layer.Height) = (x, y, Math.Max(1, width), Math.Max(1, height));
            found = true;
        });
        return found;
    }

    // ---- Library ----------------------------------------------------------------------------------------------------

    /// <summary>A library element and the overlays that use it.</summary>
    public sealed record LibraryEntry(OverlayElement Element, IReadOnlyList<Overlay> UsedBy);

    /// <summary>Every library element, by name, with the overlays whose layers use it (widgets only; static ones are templates).</summary>
    public IReadOnlyList<LibraryEntry> Library()
    {
        var all = overlays.All();
        return elements.All()
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new LibraryEntry(e, all.Where(o => o.Layers.Any(l => l.ElementId == e.Id)).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Saves a text, image, video or slideshow layer to the library as a template (its settings and size). Adding it
    /// later makes a new layer from a copy. Returns the element, or null when the layer is not a static one.
    /// </summary>
    public OverlayElement? SaveToLibrary(Guid overlayId, Guid layerId)
    {
        if (overlays.Get(overlayId)?.Layers.FirstOrDefault(l => l.Id == layerId) is not { } layer
            || !OverlayLayerTypes.Static.Contains(layer.Type))
            return null;
        return elements.Add(new OverlayElement
        {
            Name = layer.Name,
            Type = layer.Type,
            Settings = new JsonObject
            {
                ["settings"] = layer.Settings?.DeepClone() ?? new JsonObject(),
                ["width"] = layer.Width,
                ["height"] = layer.Height,
            },
        });
    }

    /// <summary>
    /// Places a library element on an overlay: a widget as a layer that shares the element (and its code); a template
    /// as a new layer with a copy of its settings, centred. Returns the layer, or null when either does not exist.
    /// </summary>
    public OverlayLayer? AddFromLibrary(Guid overlayId, Guid elementId)
    {
        if (overlays.Get(overlayId) is not { } overlay || elements.Get(elementId) is not { } element) return null;
        OverlayLayer layer;
        if (element.Type == CustomType)
        {
            layer = new OverlayLayer { Name = element.Name, Type = CustomType, ElementId = element.Id, Width = overlay.Width, Height = overlay.Height };
        }
        else if (OverlayLayerTypes.Static.Contains(element.Type))
        {
            layer = OverlayLayerTypes.NewStatic(element.Type, overlay);
            layer.Name = element.Name;
            if (element.Settings["settings"] is JsonObject settings) layer.Settings = settings.DeepClone().AsObject();
            var width = Math.Min(Number(element.Settings["width"], layer.Width), overlay.Width);
            var height = Math.Min(Number(element.Settings["height"], layer.Height), overlay.Height);
            (layer.Width, layer.Height) = (width, height);
            (layer.X, layer.Y) = (Math.Round((overlay.Width - width) / 2), Math.Round((overlay.Height - height) / 2));
        }
        else
        {
            return null;
        }
        overlays.Update(overlayId, o => o.Layers.Add(layer));
        return layer;
    }

    /// <summary>A copy of an element under a new name ("… copy"), with its own copy of the code for widgets.</summary>
    public OverlayElement? Duplicate(Guid elementId)
    {
        if (elements.Get(elementId) is not { } element) return null;
        var copy = elements.Add(new OverlayElement { Name = element.Name + " copy", Type = element.Type, Settings = element.Settings });
        if (element.Type == CustomType) code.Write(copy.Id, code.Read(element.Id));
        return copy;
    }

    public bool Rename(Guid elementId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var renamed = elements.Update(elementId, e => e.Name = name.Trim());
        if (renamed)
        {
            // Layers that use a widget show its name; keep them in step.
            foreach (var overlay in overlays.All().Where(o => o.Layers.Any(l => l.ElementId == elementId)))
                overlays.Update(overlay.Id, o => o.Layers.Where(l => l.ElementId == elementId).ToList().ForEach(l => l.Name = name.Trim()));
        }
        return renamed;
    }

    /// <summary>
    /// Deletes an element (and a widget's code) that no overlay uses. Returns the overlays still using it, which must
    /// lose those layers first; empty when deleted.
    /// </summary>
    public IReadOnlyList<Overlay> Delete(Guid elementId)
    {
        var users = overlays.All().Where(o => o.Layers.Any(l => l.ElementId == elementId)).ToArray();
        if (users.Length > 0) return users;
        if (elements.Delete(elementId)) code.Delete(elementId);
        return [];
    }

    private static double Number(JsonNode? node, double fallback) => Events.OverlayAlerts.NumberOf(node, fallback);

    /// <summary>The code a layer runs: its element's, or empty for layers without one.</summary>
    public WidgetCode CodeFor(OverlayLayer layer) => layer.ElementId is { } id ? code.Read(id) : WidgetCode.Empty;
}
