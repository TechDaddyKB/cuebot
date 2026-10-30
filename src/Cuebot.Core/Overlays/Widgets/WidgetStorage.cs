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
    public OverlayLayer? AddCustomWidget(Guid overlayId, string name, WidgetCode widget)
    {
        if (overlays.Get(overlayId) is not { } overlay) return null;
        var element = elements.Add(new OverlayElement { Name = name, Type = CustomType });
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

    /// <summary>The code a layer runs: its element's, or empty for layers without one.</summary>
    public WidgetCode CodeFor(OverlayLayer layer) => layer.ElementId is { } id ? code.Read(id) : WidgetCode.Empty;
}
